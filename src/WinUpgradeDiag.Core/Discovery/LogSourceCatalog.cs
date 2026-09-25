using System;
using System.Collections.Generic;
using System.IO;

namespace WinUpgradeDiag.Core.Discovery
{
    /// <summary>
    /// The full set of candidate log locations from docs/DESIGN.md §4.1. Building the list is
    /// pure path resolution; whether anything exists at those paths is for
    /// <see cref="LogManifestBuilder"/> to find out.
    /// </summary>
    public static class LogSourceCatalog
    {
        public static IReadOnlyList<LogSource> GetDefaultSources()
        {
            var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            var windir = Environment.GetEnvironmentVariable("WINDIR") ?? Path.Combine(systemDrive, "Windows");
            var ccmLogDir = CcmLogDirectoryResolver.Resolve();

            var sources = new List<LogSource>();

            // --- Task sequence: collect every smsts.log location; they are different runs. ---
            sources.Add(new LogSource(
                "ts-smsts-ccm-smstslog", LogSourceCategory.TaskSequence,
                "Task sequence log (CCM SMSTSLog)",
                "The primary smsts.log location once the client relocates it out of ccmsetup.",
                Path.Combine(ccmLogDir, "SMSTSLog"),
                LogSourceKind.DirectoryGlob, "smsts*.log"));

            sources.Add(new LogSource(
                "ts-smsts-taskseq", LogSourceCategory.TaskSequence,
                "Task sequence log (bare-metal / pre-client staging area)",
                "Used before the CCM client is fully relocated, or for OSD-style sequences.",
                Path.Combine(systemDrive + Path.DirectorySeparatorChar, "_SMSTaskSequence", "Logs", "Smstslog"),
                LogSourceKind.DirectoryGlob, "smsts*.log"));

            sources.Add(new LogSource(
                "ts-smsts-ccm-root", LogSourceCategory.TaskSequence,
                "Task sequence log (CCM Logs root)",
                "smsts.log sometimes starts directly under the client log root before relocation.",
                ccmLogDir,
                LogSourceKind.DirectoryGlob, "smsts*.log"));

            // --- Windows Setup: current attempt ---
            var pantherCurrent = Path.Combine(systemDrive + Path.DirectorySeparatorChar, "$WINDOWS.~BT", "Sources", "Panther");
            sources.Add(new LogSource(
                "setup-current-act", LogSourceCategory.SetupCurrent, "setupact.log (current attempt)",
                "Windows Setup's own log for the in-progress or most recent attempt.",
                Path.Combine(pantherCurrent, "setupact.log")));
            sources.Add(new LogSource(
                "setup-current-err", LogSourceCategory.SetupCurrent, "setuperr.log (current attempt)",
                "Errors only, same phase as setupact.log.",
                Path.Combine(pantherCurrent, "setuperr.log")));

            // --- Windows Setup: rollback — highest value, most often missed ---
            var rollback = Path.Combine(systemDrive + Path.DirectorySeparatorChar, "$WINDOWS.~BT", "Sources", "Rollback");
            sources.Add(new LogSource(
                "setup-rollback-act", LogSourceCategory.SetupRollback, "setupact.log (rollback)",
                "What Setup was doing right before it decided to revert to Windows 10.",
                Path.Combine(rollback, "setupact.log"), highValue: true));
            sources.Add(new LogSource(
                "setup-rollback-apilog", LogSourceCategory.SetupRollback, "setupapi.dev.log (rollback)",
                "Device/driver install activity during the failed upgrade attempt.",
                Path.Combine(rollback, "setupapi.dev.log"), highValue: true));
            sources.Add(new LogSource(
                "setup-rollback-dmp", LogSourceCategory.SetupRollback, "setupmem.dmp (rollback)",
                "Present only if the machine bugchecked during the upgrade.",
                Path.Combine(rollback, "setupmem.dmp"), highValue: true));
            sources.Add(new LogSource(
                "setup-rollback-evtx", LogSourceCategory.SetupRollback, "Rollback event logs (*.evtx)",
                "Exported event logs captured at rollback time.",
                rollback, LogSourceKind.DirectoryGlob, "*.evtx", highValue: true));

            // --- Windows Setup: completed successfully at some point ---
            var pantherCompleted = Path.Combine(windir, "Panther");
            sources.Add(new LogSource(
                "setup-completed-act", LogSourceCategory.SetupCompleted, "setupact.log (completed)",
                "Left behind by a Setup run that finished, successfully or not.",
                Path.Combine(pantherCompleted, "setupact.log")));
            sources.Add(new LogSource(
                "setup-completed-err", LogSourceCategory.SetupCompleted, "setuperr.log (completed)",
                "Errors only, same phase as the completed setupact.log.",
                Path.Combine(pantherCompleted, "setuperr.log")));

            // --- Previous OS, if a rollback already happened and Windows.old exists ---
            sources.Add(new LogSource(
                "previous-os-panther", LogSourceCategory.PreviousOs, "setupact.log (Windows.old)",
                "Setup's own log carried over from the previous installation.",
                Path.Combine(systemDrive + Path.DirectorySeparatorChar, "Windows.old", "Windows", "Panther", "setupact.log")));

            // --- Downlevel Windows Update communication ---
            sources.Add(new LogSource(
                "mosetup-bluebox", LogSourceCategory.DownlevelServicing, "BlueBox.log",
                "Downlevel Windows Update client's conversation with Setup.",
                Path.Combine(windir, "Logs", "Mosetup", "BlueBox.log")));

            // --- Servicing ---
            sources.Add(new LogSource(
                "servicing-cbs", LogSourceCategory.Servicing, "CBS.log",
                "Component-based servicing log; component store corruption shows up here.",
                Path.Combine(windir, "Logs", "CBS", "CBS.log")));
            sources.Add(new LogSource(
                "servicing-dism", LogSourceCategory.Servicing, "dism.log",
                "DISM operations against the image/component store.",
                Path.Combine(windir, "Logs", "DISM", "dism.log")));

            // --- Client install ---
            sources.Add(new LogSource(
                "client-ccmsetup", LogSourceCategory.ClientInstall, "ccmsetup.log",
                "The ConfigMgr client's own install/repair log — a repair here can kill TSManager mid-sequence.",
                Path.Combine(windir, "ccmsetup", "Logs", "ccmsetup.log")));

            // --- Other CCM client logs relevant to content, execution, and app enforcement ---
            foreach (var name in new[]
                     {
                         "execmgr.log", "CAS.log", "ContentTransferManager.log",
                         "DataTransferService.log", "AppEnforce.log", "PolicyAgent.log"
                     })
            {
                sources.Add(new LogSource(
                    "client-other-" + name.ToLowerInvariant(), LogSourceCategory.ClientOther, name,
                    "ConfigMgr client log relevant to content delivery, execution, or app enforcement.",
                    Path.Combine(ccmLogDir, name)));
            }

            // --- Crash dumps ---
            sources.Add(new LogSource(
                "crash-memory-dmp", LogSourceCategory.CrashDump, "MEMORY.DMP",
                "Full kernel/complete memory dump from a bugcheck, if configured and present.",
                Path.Combine(windir, "MEMORY.DMP")));
            sources.Add(new LogSource(
                "crash-minidumps", LogSourceCategory.CrashDump, "Minidumps",
                "Small memory dumps, one per bugcheck.",
                Path.Combine(windir, "Minidump"), LogSourceKind.DirectoryGlob, "*.dmp"));

            return sources;
        }
    }
}
