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
            string acknowledgement = null,
            string useWhen = null)
        {
            UseWhen = useWhen;
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

        /// <summary>
        /// The area the problem is in — Inspect, Downloads and Software Center, Windows upgrade,
        /// ConfigMgr client. Grouping by area is how a technician thinks about a fault ("it's the
        /// client", "it's the download"); the old Diagnose / Fix / Recover split grouped by what the
        /// script does, and nobody could say why rebuilding the client was "Fix" while clearing a
        /// stuck upgrade was "Recover".
        /// </summary>
        public string Category { get; }

        /// <summary>
        /// The symptom, in the words of someone looking at the screen, that says this is the tool.
        /// <para>
        /// The single most important line on the card. Tools used to be named after their causes
        /// and told apart by a parenthetical — "interrupted during Setup (keeps the download)"
        /// against "interrupted while downloading (discards the download)" — which only helps a
        /// reader who already knows the diagnosis. A technician knows what they can see.
        /// </para>
        /// </summary>
        public string UseWhen { get; }

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
        /// <summary>
        /// Always false. Kept so the shape of the confirmation is explicit rather than implied.
        /// <para>
        /// Destructive tools used to demand the script name be typed. The dialog printed that name
        /// on the line directly above the box, so the exercise was transcription: copy eighteen
        /// characters from one line to the next. That is friction without safety — it proves the
        /// operator can read, not that they have decided anything. The gate that works is the
        /// acknowledgement, which states the specific irreversible consequence and makes the
        /// operator claim something the tool cannot check for itself.
        /// </para>
        /// </summary>
        public bool RequiresTypedConfirmation => false;
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

        /// <summary>The order tools are presented in. Every tool must appear exactly once.</summary>
        public static readonly IReadOnlyList<string> DisplayOrder = new[]
        {
            "CHECK-STATE", "RUN-SETUPDIAG", "LIST-CACHE", "GET-PROGRESS", "WATCH-SMSTS", "FIX-E", "FIX-C", "RESET-TS", "FIX-B", "FIX-A", "FIX-D", "REMOVE-LEFTOVERS", "REPAIR-CLIENT", "REBUILD-CLIENT"
        };

        private static IReadOnlyList<ToolDefinition> Ordered()
        {
            return Definitions
                .OrderBy(t =>
                {
                    var index = Array.IndexOf((string[])DisplayOrder, t.Id);
                    return index < 0 ? int.MaxValue : index;
                })
                .ToList();
        }

        private static readonly IReadOnlyList<ToolDefinition> Definitions = new List<ToolDefinition>
        {
            // ---------------------------------------------------------------- diagnose
            new ToolDefinition(
                "CHECK-STATE", "Check-UpgradeState.ps1",
                "Check whether an upgrade is still running",
                "Reports whether a task sequence is running or orphaned, plus disk space, upgrade folders and Setup progress. Changes nothing.",
                RemediationRisk.ReadOnly, "Inspect",
                new[] { "Report process, WMI, folder, disk and Setup-progress state." },
                new string[0],
                useWhen: "You can't tell whether an upgrade is still working, has stopped, or never started."),

            new ToolDefinition(
                "REBUILD-CLIENT", "Rebuild-CcmClient.ps1",
                "Reinstall the ConfigMgr client",
                "Removes the client completely - services, WMI namespaces, files, registry and certificates - and reinstalls it with the site and management point set explicitly.",
                RemediationRisk.Destructive, "ConfigMgr client",
                new[]
                {
                    "Stop and delete CcmExec, ccmsetup, smstsmgr and CmRcService.",
                    "Skip ccmsetup.exe /uninstall, which returns 1612 once its MSI source folder is gone.",
                    "Remove the root\\ccm, root\\ccmvdi, root\\smsdm and root\\cimv2\\sms WMI namespaces.",
                    "Delete C:\\Windows\\CCM, ccmcache and SMSCFG.ini.",
                    "Delete the CCM, CCMSetup and SMS registry keys and the SMS certificate store.",
                    "Remove the ccmsetup retry task, which would otherwise re-run the failed install.",
                    "Stop after the cleanup by default, so the machine can be rebooted before the install.",
                    "Reinstall with /mp:, SMSSITECODE= and SMSMP= and no /forceinstall, then wait for CcmExec."
                },
                new[]
                {
                    "No task sequence or Windows Setup may be running. The script refuses if one is.",
                    "Repair the ConfigMgr client should have been tried first and failed.",
                    "C:\\Windows\\ccmsetup must hold the client installer.",
                    "The client identity is reset, so the machine may appear twice in the console until the duplicate ages out."
                },
                parameterName: "Target",
                parameterPrompt: "Type your site code, a slash, then your management point",
                parameterExample: "ABC/mp01.contoso.com",
                parameterHelp:
                    "Filled in from this machine where it could be worked out - check it is right. " +
                    "The site code is the three characters your site is known by; the management point " +
                    "is a server name. Both appear in ccmsetup.log, and whoever owns ConfigMgr will " +
                    "know them. They are needed separately because SMSMP= is what assigns the client " +
                    "to a management point, while /mp: only says where to download the installer from.",
                acknowledgement:
                    "I have confirmed this machine's client is broken, not merely unregistered, and that " +
                    "a duplicate device record in the console is acceptable.",
                useWhen: "Configuration Manager will not open, or its service starts and then stops. Use when Repair has not worked."),
            new ToolDefinition(
                "RUN-SETUPDIAG", "Run-SetupDiag.ps1",
                "Ask Windows why the upgrade failed",
                "Shows the result Windows recorded when the upgrade failed, then runs Microsoft's SetupDiag on " +
                "this PC's upgrade logs if a copy signed by Microsoft is found here.",
                RemediationRisk.ReadOnly, "Inspect",
                new[]
                {
                    "Show the SetupDiag result Windows saved when the upgrade failed, if there is one.",
                    "Look for setupdiag.exe in Setup's folder, under Windows.old, and in the client cache.",
                    "Check the copy is validly signed by Microsoft, and refuse to run it otherwise.",
                    "Run it on this PC's upgrade logs with Microsoft telemetry off, and show what it found."
                },
                new[]
                {
                    "Only a copy signed by Microsoft is ever run. Nothing is downloaded.",
                    "SetupDiag writes its result file to the temp folder and may record it under " +
                    "HKLM\\SYSTEM\\Setup. It changes nothing else."
                },
                useWhen: "An upgrade failed or rolled back and you want Microsoft's own analysis of the logs, " +
                         "including any driver it blames."),
            new ToolDefinition(
                "LIST-CACHE", "List-CcmCache.ps1",
                "Show downloaded content",
                "Lists everything in the client cache with its size, and flags records that point at folders which no longer exist.",
                RemediationRisk.ReadOnly, "Inspect",
                new[] { "Enumerate CacheInfoEx and report size, location and whether each folder still exists." },
                new string[0],
                useWhen: "You need to see what this PC has downloaded and how large it is, or find a package's content ID."),

            new ToolDefinition(
                "GET-PROGRESS", "Get-UpgradeProgress.ps1",
                "Check upgrade progress on another PC",
                "Reads Setup progress, the running upgrade processes and log activity on another PC over WinRM, without signing in to it.",
                RemediationRisk.ReadOnly, "Inspect",
                new[] { "Query processes, Setup progress and setupact.log write time on the target machine." },
                new[] { "WinRM must be reachable on the target machine." },
                parameterName: "ComputerName",
                parameterPrompt: "Machine name to query",
                requiresLocalMachine: false,
                parameterExample: "e.g. PC001 or WS-1234",
                parameterHelp: "The NetBIOS or DNS name of the machine that is stuck. Not this machine.",
                useWhen: "A user says their PC is stuck near 99% and you can't get to it."),

            new ToolDefinition(
                "WATCH-SMSTS", "Watch-Smsts.ps1",
                "Follow a deployment live on another PC",
                "Shows the remote PC's task sequence log as it is written, over the admin share, without signing in or restarting it.",
                RemediationRisk.ReadOnly, "Inspect",
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
                runsUntilStopped: true,
                useWhen: "You want to watch a task sequence run step by step on a remote PC."),

            // ---------------------------------------------------------------- recover
            new ToolDefinition(
                "FIX-B", "Fix-B-SetupInterrupted.ps1",
                "Reset an upgrade interrupted during Setup",
                "Clears the orphaned task sequence so the upgrade can be retried, and keeps the downloaded Windows image so the retry does not start from zero.",
                RemediationRisk.Disruptive, "Windows upgrade",
                new[]
                {
                    "Stop the ConfigMgr client service (CcmExec).",
                    "Delete the CCM_TSExecutionRequest instances from WMI.",
                    "Delete C:\\_SMSTaskSequence.",
                    "Start CcmExec and wait for it to settle.",
                    "Trigger a machine policy refresh.",
                    "Leave the client cache untouched."
                },
                new[] { LiveUpgradeGuard, BootedNormally },
                useWhen: "The PC restarted part way through the upgrade and Software Center shows \"Installing\u2026\" forever. Keeps the downloaded files."),

            new ToolDefinition(
                "FIX-A", "Fix-A-DownloadInterrupted.ps1",
                "Reset an upgrade interrupted during download",
                "Clears the orphaned task sequence and deletes the partial Windows image, which cannot resume, so the retry downloads it in full.",
                RemediationRisk.Destructive, "Windows upgrade",
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
                acknowledgement:
                    "I accept that the partial download is discarded and the whole image is " +
                    "downloaded again from the start.",
                parameterName: "ContentId",
                parameterPrompt: "ContentId of the OS package (the multi-gigabyte item)",
                parameterExample: "e.g. ABC00123",
                parameterHelp: "Run \"Show downloaded content\" first: it is the item several gigabytes in size. " +
                               "A diagnostic run fills this in automatically when it can identify the package.",
                useWhen: "The upgrade stopped while it was still downloading the Windows image. Deletes the partial download."),

            new ToolDefinition(
                "FIX-C", "Fix-C-CacheCleared.ps1",
                "Repair the cache after it was deleted by hand",
                "Clears the client's records of cached content whose folders no longer exist, recreates the cache folder if it is gone, and refreshes policy so the content downloads again.",
                RemediationRisk.Destructive, "Downloads and Software Center",
                new[]
                {
                    "Stop CcmExec, clear the execution request, delete C:\\_SMSTaskSequence.",
                    "Recreate C:\\Windows\\ccmcache if the folder itself was deleted.",
                    "Remove cache records whose folder no longer exists.",
                    "Start CcmExec and trigger a machine policy refresh."
                },
                new[] { LiveUpgradeGuard, BootedNormally, "The retry will download the image again from the start." },
                acknowledgement:
                    "I accept that the client content cache is rebuilt and the image is downloaded " +
                    "again from the start.",
                useWhen: "Someone deleted C:\\Windows\\ccmcache in Explorer, and downloads now fail or never start."),

            new ToolDefinition(
                "FIX-D", "Fix-D-SetupLeftovers.ps1",
                "Remove files left by a failed upgrade",
                "Saves the Setup logs, then deletes C:\\$WINDOWS.~BT so the next attempt starts clean and the space is freed.",
                RemediationRisk.Destructive, "Windows upgrade",
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
                },
                acknowledgement:
                    "I have confirmed this machine is not part way through an upgrade and this folder " +
                    "is left over from an attempt that already ended.",
                useWhen: "A previous upgrade failed and left C:\\$WINDOWS.~BT behind, and the next attempt will not start."),

            new ToolDefinition(
                "FIX-E", "Fix-E-StuckDownload.ps1",
                "Clear a stuck download",
                "Cancels the download jobs holding the percentage, clears cache records for folders that are gone, then restarts the client and asks Software Center to re-evaluate, so the download starts again from the beginning.",
                RemediationRisk.Destructive, "Downloads and Software Center",
                new[]
                {
                    "Report the BITS, DataTransferService and ContentTransferManager jobs in flight.",
                    "Stop CcmExec.",
                    "Cancel the BITS transfers that hold the percentage.",
                    "Remove the DataTransferService and ContentTransferManager job records.",
                    "Remove cache records pointing at folders that no longer exist.",
                    "Start CcmExec, then trigger Machine Policy and Application Deployment Evaluation."
                },
                new[]
                {
                    LiveUpgradeGuard,
                    "The ConfigMgr client must be installed. The script refuses otherwise.",
                    "Any download in progress for any deployment is cancelled, not just the stuck one.",
                    "The content downloads again from the start."
                },
                acknowledgement:
                    "I accept that every download in progress on this machine is cancelled and will " +
                    "start again from the beginning.",
                useWhen: "An item in Software Center has sat at the same percentage for hours and Cancel does nothing."),

            new ToolDefinition(
                "RESET-TS", "Reset-TSHistory.ps1",
                "Let a deployment run again",
                "Finds the task sequence whose last run failed on this PC, clears its run history and refreshes policy, so Software Center offers it again.",
                RemediationRisk.Disruptive, "Downloads and Software Center",
                new[]
                {
                    "Find the task sequence whose last recorded run failed, and name it.",
                    "Delete that task sequence's execution history. Ones that last succeeded are left alone.",
                    "Restart CcmExec and trigger a machine policy refresh."
                },
                new[]
                {
                    LiveUpgradeGuard,
                    "If several task sequences failed, only the most recent is reset; the others are listed."
                },
                useWhen: "You've fixed the cause, but Software Center still won't start the deployment again."),

            new ToolDefinition(
                "REPAIR-CLIENT", "Repair-CcmClient.ps1",
                "Repair the ConfigMgr client",
                "Runs ccmrepair, which repairs the client in place without removing it. Can take several minutes.",
                RemediationRisk.Disruptive, "ConfigMgr client",
                new[] { "Start ccmrepair.exe and report the tail of ccmsetup.log." },
                new[] { LiveUpgradeGuard, "Run a Setup-interrupted fix afterwards, then retry the deployment." },
                useWhen: "The client is installed and partly working, but actions fail or policy is not arriving. Try this first."),

            new ToolDefinition(
                "REMOVE-LEFTOVERS", "Remove-UpgradeLeftovers.ps1",
                "Free disk space after a successful upgrade",
                "Removes Windows.old and C:\\$WINDOWS.~BT together through DISM. They are one rollback set, so removing only one breaks \"Go back\" while the other keeps using space.",
                RemediationRisk.Destructive, "Windows upgrade",
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
                    "and that they will not need to roll back.",
                useWhen: "The upgrade worked, the user is happy with it, and you need the space Windows.old is taking.")
        };

        /// <summary>
        /// The catalogue in display order: read-only inspection first, then by area, least drastic
        /// first within each. Group order on the Tools tab follows from this.
        /// </summary>
        public static IReadOnlyList<ToolDefinition> All { get; } = Ordered();

        /// <summary>
        /// One line under each group heading, saying what the tools in it have in common and how
        /// careful to be. A heading alone does not tell someone that everything under "Inspect"
        /// is safe to click.
        /// </summary>
        public static string GroupDescription(string group)
        {
            switch (group)
            {
                case "Inspect":
                    return "Read-only. These look and report; they never change anything on this PC.";
                case "Downloads and Software Center":
                    return "For deployments that are stuck, will not download, or will not start again.";
                case "Windows upgrade":
                    return "For a Windows 10 to 11 upgrade that was interrupted, failed, or has finished.";
                case "ConfigMgr client":
                    return "For when the ConfigMgr client itself is broken. Try Repair before Reinstall.";
                default:
                    return null;
            }
        }

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
