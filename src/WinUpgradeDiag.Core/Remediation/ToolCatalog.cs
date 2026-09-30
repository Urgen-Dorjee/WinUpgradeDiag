using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WinUpgradeDiag.Core.Remediation
{
    /// <summary>
    /// One entry in the technician's toolbox: a recovery script, what it is for, what it costs to
    /// get wrong, and whether it needs an argument the operator must supply.
    /// </summary>
    public sealed class ToolDefinition
    {
        public ToolDefinition(
            string id,
            string scriptName,
            string title,
            string purpose,
            RemediationRisk risk,
            string category,
            IReadOnlyList<string> steps,
            IReadOnlyList<string> preconditions,
            string parameterName = null,
            string parameterPrompt = null,
            bool requiresLocalMachine = true,
            string parameterExample = null,
            string parameterHelp = null,
            bool runsUntilStopped = false,
            string acknowledgement = null)
        {
            Id = id;
            ScriptName = scriptName;
            Title = title;
            Purpose = purpose;
            Risk = risk;
            Category = category;
            Steps = steps ?? new List<string>();
            Preconditions = preconditions ?? new List<string>();
            ParameterName = parameterName;
            ParameterPrompt = parameterPrompt;
            RequiresLocalMachine = requiresLocalMachine;
            ParameterExample = parameterExample;
            ParameterHelp = parameterHelp;
            RunsUntilStopped = runsUntilStopped;
            Acknowledgement = acknowledgement;
        }

        public string Id { get; }
        public string ScriptName { get; }
        public string Title { get; }
        public string Purpose { get; }
        public RemediationRisk Risk { get; }

        /// <summary>Menu grouping: "Diagnose" or "Recover".</summary>
        public string Category { get; }

        public IReadOnlyList<string> Steps { get; }
        public IReadOnlyList<string> Preconditions { get; }

        /// <summary>Script parameter that must be supplied, or null if it takes none.</summary>
        public string ParameterName { get; }

        public string ParameterPrompt { get; }

        /// <summary>
        /// A specimen value, shown as placeholder text. The shape of these values is the part
        /// operators get wrong, and a wrong id here is fed straight to a destructive script.
        /// </summary>
        public string ParameterExample { get; }

        /// <summary>Where to find the value, for the ones that are not obvious.</summary>
        public string ParameterHelp { get; }

        public bool RequiresParameter => !string.IsNullOrEmpty(ParameterName);

        /// <summary>False for the scripts that target another machine over WinRM.</summary>
        public bool RequiresLocalMachine { get; }

        /// <summary>
        /// Anything that changes the machine must not run while an upgrade is in flight. Read-only
        /// tools are always safe to run.
        /// </summary>
        public bool BlockedByLiveUpgrade => Risk != RemediationRisk.ReadOnly;

        public string RiskText
        {
            get
            {
                switch (Risk)
                {
                    case RemediationRisk.ReadOnly: return "Read-only";
                    case RemediationRisk.Disruptive: return "Disruptive";
                    case RemediationRisk.Destructive: return "Destructive";
                    default: return Risk.ToString();
                }
            }
        }

        public string RiskSeverity
        {
            get
            {
                switch (Risk)
                {
                    case RemediationRisk.Destructive: return "Critical";
                    case RemediationRisk.Disruptive: return "Warning";
                    default: return "Info";
                }
            }
        }

        /// <summary>
        /// True for a tool that streams until the operator stops it rather than finishing on its
        /// own. Watch-Smsts.ps1 ends in a tailing read that never returns; without this the runner
        /// would sit on it until the timeout expired and then kill it, which looks like a hang.
        /// </summary>
        public bool RunsUntilStopped { get; }

        /// <summary>
        /// A statement the operator must tick before the action can run, or null.
        /// <para>
        /// Used where typing the script name is not enough on its own — retiring a rollback depends
        /// on a fact the tool cannot observe, namely that someone has actually checked the machine
        /// works. Making that an explicit claim is the difference between a warning and a decision.
        /// </para>
        /// </summary>
        public string Acknowledgement { get; }

        public bool RequiresAcknowledgement => !string.IsNullOrWhiteSpace(Acknowledgement);

        /// <summary>Destructive actions require the operator to type a confirmation word.</summary>
        public bool RequiresTypedConfirmation => Risk == RemediationRisk.Destructive;
    }

    /// <summary>
    /// Every recovery script the tool knows how to launch, described so the operator can see what
    /// it does before it runs rather than after.
    /// </summary>
    public static class ToolCatalog
    {
        private const string LiveUpgradeGuard =
            "TSManager, SetupHost and setupprep must all be stopped — never run a fix on a live upgrade.";

        private const string BootedNormally =
            "The machine must have booted normally back into Windows.";

        public static IReadOnlyList<ToolDefinition> All { get; } = new List<ToolDefinition>
        {
            // ---------------------------------------------------------------- diagnose
            new ToolDefinition(
                "CHECK-STATE", "Check-UpgradeState.ps1",
                "Check upgrade state",
                "Reports whether a task sequence is running or orphaned, plus disk, folder and Setup progress. Changes nothing.",
                RemediationRisk.ReadOnly, "Diagnose",
                new[] { "Report process, WMI, folder, disk and Setup-progress state." },
                new string[0]),

            new ToolDefinition(
                "REBUILD-CLIENT", "Rebuild-CcmClient.ps1",
                "Rebuild a broken ConfigMgr client",
                "For a client whose WMI provider will not load: the Configuration Manager applet will not " +
                "open, Software Center is empty, and CcmExec starts then stops. Removes the client " +
                "completely and reinstalls it with the management point set explicitly. Try the " +
                "lighter \"repair the ConfigMgr client\" first — this is what to do when that fails.",
                RemediationRisk.Destructive, "Fix",
                new[]
                {
                    "Stop and delete CcmExec, ccmsetup, smstsmgr and CmRcService.",
                    "Run ccmsetup.exe /uninstall.",
                    "Remove the root\\ccm, root\\ccmvdi, root\\smsdm and root\\cimv2\\sms WMI namespaces.",
                    "Delete C:\\Windows\\CCM, ccmcache and SMSCFG.ini.",
                    "Delete the CCM, CCMSetup and SMS registry keys and the SMS certificate store.",
                    "Remove the ccmsetup retry task, which would otherwise re-run the failed install.",
                    "Reinstall with /mp:, SMSSITECODE= and SMSMP=, then wait for CcmExec to come up."
                },
                new[]
                {
                    "No task sequence or Windows Setup may be running. The script refuses if one is.",
                    "Repair the ConfigMgr client should have been tried first and failed.",
                    "C:\\Windows\\ccmsetup must hold the client installer.",
                    "The client identity is reset, so the machine may appear twice in the console until the duplicate ages out."
                },
                parameterName: "Target",
                parameterPrompt: "Site code and management point, as SITE/mp.fqdn",
                parameterExample: "ABC/mp01.contoso.com",
                parameterHelp:
                    "Both values are needed. SMSMP= is what assigns the client to a management point; " +
                    "/mp: only says where to download the installer from, which is why a client installed " +
                    "with /mp: alone returns success and then never registers.",
                acknowledgement:
                    "I have confirmed this machine's client is broken, not merely unregistered, and that " +
                    "a duplicate device record in the console is acceptable."),
            new ToolDefinition(
                "LIST-CACHE", "List-CcmCache.ps1",
                "List client cache",
                "Lists cached content with sizes, and flags records pointing at folders that no longer exist. Use it to find the OS package's ContentId.",
                RemediationRisk.ReadOnly, "Diagnose",
                new[] { "Enumerate CacheInfoEx and report size, location and whether each folder still exists." },
                new string[0]),

            new ToolDefinition(
                "GET-PROGRESS", "Get-UpgradeProgress.ps1",
                "Check a remote machine's Setup progress",
                "Answers the \"stuck at 99%?\" question for another machine over WinRM, without unlocking it.",
                RemediationRisk.ReadOnly, "Diagnose",
                new[] { "Query processes, Setup progress and setupact.log write time on the target machine." },
                new[] { "WinRM must be reachable on the target machine." },
                parameterName: "ComputerName",
                parameterPrompt: "Machine name to query",
                requiresLocalMachine: false,
                parameterExample: "e.g. PC001 or WS-1234",
                parameterHelp: "The NetBIOS or DNS name of the machine that is stuck. Not this machine."),

            new ToolDefinition(
                "WATCH-SMSTS", "Watch-Smsts.ps1",
                "Watch a remote machine's task sequence log",
                "Tails smsts.log over the admin share so progress can be watched without unlocking or rebooting the machine.",
                RemediationRisk.ReadOnly, "Diagnose",
                new[]
                {
                    "Locate smsts.log on the target machine.",
                    "Stream it continuously — this keeps running until you press Cancel."
                },
                new[] { "The admin share (\\\\machine\\c$) must be reachable." },
                parameterName: "ComputerName",
                parameterPrompt: "Machine name to watch",
                requiresLocalMachine: false,
                parameterExample: "e.g. PC001 or WS-1234",
                parameterHelp: "The machine whose task sequence you want to watch, reached over its admin share.",
                runsUntilStopped: true),

            // ---------------------------------------------------------------- recover
            new ToolDefinition(
                "FIX-B", "Fix-B-SetupInterrupted.ps1",
                "Fix: interrupted during Setup (keeps the download)",
                "For a machine interrupted during \"Windows upgrade progress: xx%\". Clears the orphaned task sequence and leaves the downloaded image alone, so the retry does not start from zero.",
                RemediationRisk.Disruptive, "Recover",
                new[]
                {
                    "Stop the ConfigMgr client service (CcmExec).",
                    "Delete the CCM_TSExecutionRequest instances from WMI.",
                    "Delete C:\\_SMSTaskSequence.",
                    "Start CcmExec and wait for it to settle.",
                    "Trigger a machine policy refresh.",
                    "Leave the client cache untouched."
                },
                new[] { LiveUpgradeGuard, BootedNormally }),

            new ToolDefinition(
                "FIX-A", "Fix-A-DownloadInterrupted.ps1",
                "Fix: interrupted while downloading (discards the download)",
                "For a machine interrupted during \"Downloading install.wim\". Content downloaded inside a task sequence cannot resume, so the partial copy is deleted and the retry downloads it again.",
                RemediationRisk.Destructive, "Recover",
                new[]
                {
                    "Stop CcmExec, clear the execution request, delete C:\\_SMSTaskSequence.",
                    "Delete the named cache element through the client.",
                    "Start CcmExec and trigger a machine policy refresh."
                },
                new[]
                {
                    LiveUpgradeGuard,
                    BootedNormally,
                    "The retry will download the whole image again — several gigabytes."
                },
                parameterName: "ContentId",
                parameterPrompt: "ContentId of the OS package (the multi-gigabyte item)",
                parameterExample: "e.g. ABC00123",
                parameterHelp: "Run \"List client cache\" first: it is the item several gigabytes in size. " +
                               "A diagnostic run fills this in automatically when it can identify the package."),

            new ToolDefinition(
                "FIX-C", "Fix-C-CacheCleared.ps1",
                "Fix: ccmcache was cleared or deleted",
                "For a machine whose cache was emptied, especially by hand in Explorer, leaving WMI records pointing at folders that are gone.",
                RemediationRisk.Destructive, "Recover",
                new[]
                {
                    "Stop CcmExec, clear the execution request, delete C:\\_SMSTaskSequence.",
                    "Recreate C:\\Windows\\ccmcache if the folder itself was deleted.",
                    "Remove cache records whose folder no longer exists.",
                    "Start CcmExec and trigger a machine policy refresh."
                },
                new[] { LiveUpgradeGuard, BootedNormally, "The retry will download the image again from the start." }),

            new ToolDefinition(
                "FIX-D", "Fix-D-SetupLeftovers.ps1",
                "Fix: remove a half-built Setup folder",
                "For a retry that fails immediately inside Setup, or a full disk. Saves the Setup logs first, then deletes C:\\$WINDOWS.~BT.",
                RemediationRisk.Destructive, "Recover",
                new[]
                {
                    "Copy setup*.log and smsts.log to a dated folder under C:\\Temp.",
                    "Take ownership of C:\\$WINDOWS.~BT and grant Administrators access.",
                    "Delete C:\\$WINDOWS.~BT."
                },
                new[]
                {
                    LiveUpgradeGuard,
                    "C:\\Windows.old must NOT exist. If it does, this machine may have upgraded successfully and \"Reclaim space after a successful upgrade\" is the correct tool instead.",
                    "Export this tool's evidence bundle first if the failure still needs investigating — this deletes the Setup logs from their original location."
                }),

            new ToolDefinition(
                "RESET-TS", "Reset-TSHistory.ps1",
                "Fix: clear a deployment's run history",
                "For when Software Center still refuses to start the task sequence after another fix has run.",
                RemediationRisk.Disruptive, "Recover",
                new[]
                {
                    "Delete the execution history registry key for the given package.",
                    "Restart CcmExec and trigger a machine policy refresh."
                },
                new[] { LiveUpgradeGuard, "This is the TASK SEQUENCE package id, not the OS package's content id." },
                parameterName: "TsPackageId",
                parameterPrompt: "Task sequence package ID",
                parameterExample: "e.g. ABC00456",
                parameterHelp: "The TASK SEQUENCE package id from execmgr.log or the ConfigMgr console. " +
                               "This is NOT the content id used by the download fix."),

            new ToolDefinition(
                "REPAIR-CLIENT", "Repair-CcmClient.ps1",
                "Fix: repair the ConfigMgr client",
                "Last resort before escalating. Runs ccmrepair, which hands off to ccmsetup and can take several minutes.",
                RemediationRisk.Disruptive, "Recover",
                new[] { "Start ccmrepair.exe and report the tail of ccmsetup.log." },
                new[] { LiveUpgradeGuard, "Run a Setup-interrupted fix afterwards, then retry the deployment." }),

            new ToolDefinition(
                "REMOVE-LEFTOVERS", "Remove-UpgradeLeftovers.ps1",
                "Reclaim space after a successful upgrade",
                "Retires Windows.old and C:\\$WINDOWS.~BT together through DISM. They are one rollback set — removing either alone breaks \"Go back\" while the other keeps wasting space.",
                RemediationRisk.Destructive, "Recover",
                new[]
                {
                    "Report how many days of rollback remain.",
                    "Run DISM /Online /Remove-OSUninstall.",
                    "Remove the $WINDOWS.~WS and $GetCurrent staging folders."
                },
                new[]
                {
                    "The user must have confirmed the upgraded machine is working.",
                    "\"Go back\" will no longer be available afterwards. This cannot be undone.",
                    "C:\\Windows.old must exist — without it there is nothing to reclaim.",
                    "This machine must have upgraded successfully, not rolled back.",
                    "Windows removes Windows.old by itself ten days after an upgrade, so this is often unnecessary."
                },
                acknowledgement:
                    "I have confirmed with the user that this machine is working correctly after the upgrade, " +
                    "and that they will not need to roll back.")
        };

        public static ToolDefinition ById(string id)
        {
            return All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public static IEnumerable<ToolDefinition> InCategory(string category)
        {
            return All.Where(t => string.Equals(t.Category, category, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Whether the catalogue's scripts can actually be run.
        /// <para>
        /// Lives here because the UI used to answer this for itself, by looking only for a file on
        /// disk. The runner had always preferred the embedded copy, so on the single-file build the
        /// two disagreed: the Tools tab opened with "the recovery scripts were not found" while
        /// every tool underneath ran perfectly. One answer, in the layer that owns the scripts.
        /// </para>
        /// </summary>
        /// <param name="scriptFolder">Optional folder override; may be null or empty.</param>
        public static bool ScriptsAvailable(string scriptFolder)
        {
            return ScriptsEmbedded || ScriptsInFolder(scriptFolder);
        }

        /// <summary>True when this build carries the scripts inside it. The normal deployment.</summary>
        public static bool ScriptsEmbedded => EmbeddedScriptProvider.Contains(MarkerScript);

        /// <summary>True when the given folder holds the scripts.</summary>
        public static bool ScriptsInFolder(string scriptFolder)
        {
            if (string.IsNullOrWhiteSpace(scriptFolder))
            {
                return false;
            }

            try
            {
                return File.Exists(Path.Combine(scriptFolder, MarkerScript));
            }
            catch (Exception)
            {
                // An unusable path is "no", never an exception out of a property the UI binds to.
                return false;
            }
        }

        /// <summary>The script every other tool sits beside; used to recognise the set.</summary>
        public const string MarkerScript = "Check-UpgradeState.ps1";

        /// <summary>
        /// Finds the folder holding the recovery scripts: beside the executable first, then walking
        /// up the tree, which is what makes it work from both a deployed copy and a dev build.
        /// Returns null when the scripts are not present, so the UI can say so rather than fail
        /// at launch time.
        /// </summary>
        public static string LocateScriptFolder(string startDirectory = null)
        {
            var start = startDirectory ?? AppDomain.CurrentDomain.BaseDirectory;

            try
            {
                var dir = new DirectoryInfo(start);
                for (int depth = 0; dir != null && depth < 8; depth++, dir = dir.Parent)
                {
                    var match = FindScriptsUnder(dir.FullName);
                    if (match != null)
                    {
                        return match;
                    }
                }
            }
            catch (Exception)
            {
                // A missing or unreadable path is reported as "not found", never thrown.
            }

            return null;
        }

        private static string FindScriptsUnder(string root)
        {
            try
            {
                // The marker is the state-check script: every other tool sits beside it.
                if (File.Exists(Path.Combine(root, MarkerScript)))
                {
                    return root;
                }

                var scriptDir = Path.Combine(root, RemediationCatalog.ScriptFolderName);
                if (!Directory.Exists(scriptDir))
                {
                    return null;
                }

                if (File.Exists(Path.Combine(scriptDir, "Check-UpgradeState.ps1")))
                {
                    return scriptDir;
                }

                // The published set nests one or two folders deep inside Script\.
                return Directory
                    .EnumerateDirectories(scriptDir, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(d => File.Exists(Path.Combine(d, "Check-UpgradeState.ps1")));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
