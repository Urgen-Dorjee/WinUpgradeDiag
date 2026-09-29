using System.Collections.Generic;
using System.Linq;
using WinUpgradeDiag.Core.Discovery;

namespace WinUpgradeDiag.Core.Rules
{
    /// <summary>
    /// A string worth looking for in a log, and what finding it means. Data, not code — AGENTS.md
    /// requires rules to be data rather than <c>if</c> statements scattered through the collectors.
    /// </summary>
    public sealed class ErrorSignature
    {
        public ErrorSignature(
            string ruleId,
            string pattern,
            string title,
            Severity severity,
            Confidence confidence,
            string meaning,
            string action,
            string command = null,
            IReadOnlyList<LogSourceCategory> appliesTo = null,
            IReadOnlyList<string> corroborate = null)
        {
            Corroborate = corroborate ?? new List<string>();
            RuleId = ruleId;
            Pattern = pattern;
            Title = title;
            Severity = severity;
            Confidence = confidence;
            Meaning = meaning;
            Action = action;
            Command = command;
            AppliesTo = appliesTo ?? new List<LogSourceCategory>();
        }

        public string RuleId { get; }

        /// <summary>Case-insensitive substring searched for in the log.</summary>
        public string Pattern { get; }

        public string Title { get; }
        public Severity Severity { get; }
        public Confidence Confidence { get; }
        public string Meaning { get; }
        public string Action { get; }
        public string Command { get; }

        /// <summary>
        /// Log categories in which this code carries the meaning described above. Empty means any.
        /// <para>
        /// Scoping matters because the same hexadecimal code means different things in different
        /// logs. 0x80004005 is "unspecified error" across the whole of Windows; in smsts.log it is
        /// a failed task sequence step, and in a Windows Setup log it is background noise. Matching
        /// it everywhere produced confident ConfigMgr findings on machines with no ConfigMgr.
        /// </para>
        /// </summary>
        public IReadOnlyList<LogSourceCategory> AppliesTo { get; }

        /// <summary>
        /// Words that must also appear on the matching line for the signature's meaning to hold.
        /// Empty means the code alone is enough.
        /// <para>
        /// Category scoping is not always enough on its own. 0x80070002 is ERROR_FILE_NOT_FOUND,
        /// and in smsts.log it genuinely does mean missing content — but it is also what a failed
        /// registry read returns. A real report told a technician to clear the ConfigMgr content
        /// cache on the strength of "GetTsRegValue() is unsuccessful. 0x80070002", which is a
        /// registry call and says nothing whatever about content on disk.
        /// </para>
        /// </summary>
        public IReadOnlyList<string> Corroborate { get; }

        /// <summary>Whether this signature's meaning holds for a log of the given category.</summary>
        public bool Covers(LogSourceCategory category)
        {
            return AppliesTo.Count == 0 || AppliesTo.Contains(category);
        }

        /// <summary>Whether a line that contains the pattern also supports the claimed meaning.</summary>
        public bool Corroborated(string line)
        {
            if (Corroborate.Count == 0)
            {
                return true;
            }

            return line != null && Corroborate.Any(
                word => line.IndexOf(word, System.StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }

    /// <summary>
    /// The signatures the engine scans logs for, drawn from docs/RULES.md.
    /// </summary>
    public static class ErrorSignatureCatalog
    {
        public static IReadOnlyList<ErrorSignature> All { get; } = new List<ErrorSignature>
        {
            // --- Setup exit codes -------------------------------------------------------------
            new ErrorSignature(
                "SU-004", "0xC1900210",
                "Compatibility scan passed",
                Severity.Info, Confidence.High,
                "0xC1900210 means Setup found no compatibility issues. Despite looking like an error code, this is a success result.",
                "Not a problem. Keep reading — if the upgrade still failed, the cause is elsewhere.",
                appliesTo: new[] { LogSourceCategory.SetupCurrent, LogSourceCategory.SetupRollback, LogSourceCategory.SetupCompleted, LogSourceCategory.PreviousOs }),

            new ErrorSignature(
                "SU-005", "0xC1900208",
                "Upgrade blocked by an incompatible application or driver",
                Severity.Critical, Confidence.High,
                "Setup stopped because something installed on this machine is not compatible with the target build. Setup names the blocker in its compatibility data.",
                "Open the CompatData*.xml files in the Panther folder and setuperr.log to find the blocking application or driver, then remove or update it before retrying.",
                appliesTo: new[] { LogSourceCategory.SetupCurrent, LogSourceCategory.SetupRollback, LogSourceCategory.SetupCompleted, LogSourceCategory.PreviousOs }),

            new ErrorSignature(
                "RB-002", "0xC1900101",
                "Upgrade rolled back to the previous Windows version",
                Severity.Critical, Confidence.Medium,
                "0xC1900101 is Setup's generic rollback code. The extend code that follows it identifies the phase that failed (SAFE_OS, MIGRATE_DATA, SECOND_BOOT, or sysprep), and a driver is the usual cause.",
                "Read the extend code after the dash to identify the phase, then check setupapi.dev.log in the Rollback folder for the device or driver that failed to install.",
                appliesTo: new[] { LogSourceCategory.SetupCurrent, LogSourceCategory.SetupRollback, LogSourceCategory.SetupCompleted, LogSourceCategory.PreviousOs }),

            new ErrorSignature(
                "CB-001", "0x800F0922",
                "Servicing stack or component store problem",
                Severity.Critical, Confidence.Medium,
                "The component store could not be serviced. This is a different failure track from a driver rollback and usually needs the component store repaired first.",
                "Repair the component store, then re-run the upgrade.",
                "DISM /Online /Cleanup-Image /RestoreHealth && sfc /scannow",
                appliesTo: new[] { LogSourceCategory.Servicing, LogSourceCategory.SetupCurrent, LogSourceCategory.SetupRollback }),

            new ErrorSignature(
                "CT-002", "0x80070002",
                "Content referenced but missing on disk",
                Severity.Warning, Confidence.Medium,
                "A file or folder the task sequence expected was not found. This usually means a cache record points at content that was deleted by hand.",
                "Clear the stale cache records from the ConfigMgr client and let it download the content again.",
                appliesTo: new[] { LogSourceCategory.TaskSequence, LogSourceCategory.ClientOther },
                // The bare code is also what a failed registry read returns; require the line to
                // be about content before telling anyone to clear the content cache.
                // Deliberately narrow. "package" and "path" both appear in registry lines about the
                // task sequence package key, which is how a registry delete failure was reported as
                // missing content on disk.
                corroborate: new[]
                {
                    "content", "ccmcache", "download", "cache location", "source path",
                    "file not found", "could not find file", "directory", "folder"
                }),

            new ErrorSignature(
                "TS-004", "0x80004005",
                "A task sequence step failed with a generic error",
                Severity.Warning, Confidence.Low,
                "0x80004005 is an unspecified failure. On its own it names nothing; it matters when it lands on the same step as a crash or driver failure.",
                "Identify the step that returned it in smsts.log, then look at that step's own installer log.",
                appliesTo: new[] { LogSourceCategory.TaskSequence },
                // "Unable to load profiler: 0x80004005" is logged by the client on startup and has
                // nothing to do with a failed step. Require the line to be about one.
                // "install" alone matches the component name InstallSoftware on the client's
                // startup chatter, which is not a step failure at all.
                corroborate: new[]
                {
                    "failed to run", "the action", "instruction", "step ", "return code",
                    "exit code", "execution failed", "smsswd", "tsmanager", "failed to execute"
                }),

            // --- Bugchecks --------------------------------------------------------------------
            new ErrorSignature(
                "BC-001", "DRIVER_PNP_WATCHDOG",
                "Machine bugchecked: a driver stalled a PnP operation",
                Severity.Critical, Confidence.High,
                "A driver failed to complete a plug-and-play operation inside the watchdog timeout and crashed the machine. This is typical of a filter driver during device re-enumeration in an upgrade.",
                "Identify the driver from the crash dump, then verify the vendor supports it on the target build. Piloting with that driver's install step disabled confirms causality.",
                appliesTo: new[] { LogSourceCategory.SetupCurrent, LogSourceCategory.SetupRollback, LogSourceCategory.TaskSequence }),

            new ErrorSignature(
                "BC-002", "CLOCK_WATCHDOG_TIMEOUT",
                "Machine bugchecked: clock watchdog timeout",
                Severity.Critical, Confidence.Medium,
                "A processor did not respond to an interrupt in time. Usually a driver or firmware problem rather than failing hardware.",
                "Identify the driver from the crash dump and check the machine's BIOS/firmware level.",
                appliesTo: new[] { LogSourceCategory.SetupCurrent, LogSourceCategory.SetupRollback, LogSourceCategory.TaskSequence }),

            new ErrorSignature(
                "BC-003", "DRIVER_POWER_STATE_FAILURE",
                "Machine bugchecked: a driver hung on a power transition",
                Severity.Critical, Confidence.Medium,
                "A driver did not complete a power state change, which commonly happens on the reboots an in-place upgrade performs.",
                "Identify the driver from the crash dump and update or remove it before retrying.",
                appliesTo: new[] { LogSourceCategory.SetupCurrent, LogSourceCategory.SetupRollback, LogSourceCategory.TaskSequence }),

            new ErrorSignature(
                "BC-002b", "DPC_WATCHDOG_VIOLATION",
                "Machine bugchecked: DPC watchdog violation",
                Severity.Critical, Confidence.Medium,
                "A deferred procedure call ran too long, which is nearly always a driver defect and often a storage driver.",
                "Identify the driver from the crash dump; check the storage controller driver first.",
                appliesTo: new[] { LogSourceCategory.SetupCurrent, LogSourceCategory.SetupRollback, LogSourceCategory.TaskSequence }),

            // --- Setup's own error vocabulary -------------------------------------------------
            new ErrorSignature(
                "SU-007", "MIG_ROLLBACK",
                "Setup rolled the migration back",
                Severity.Critical, Confidence.Medium,
                "The migration phase reverted, so user state and settings were restored to the previous installation rather than carried forward.",
                "Check the Rollback folder's setupact.log for the operation immediately before this line.",
                appliesTo: new[] { LogSourceCategory.SetupCurrent, LogSourceCategory.SetupRollback, LogSourceCategory.SetupCompleted, LogSourceCategory.PreviousOs }),

            new ErrorSignature(
                "SU-008", "CSetupHost::Execute result = 0x",
                "Setup host reported its final result",
                Severity.Info, Confidence.High,
                "This line carries Setup's overall exit code, which identifies how the attempt ended.",
                "Read the code on this line; 0 means success, anything else is the failure to chase.",
                appliesTo: new[] { LogSourceCategory.SetupCurrent, LogSourceCategory.SetupRollback, LogSourceCategory.SetupCompleted, LogSourceCategory.PreviousOs })
        };
    }
}
