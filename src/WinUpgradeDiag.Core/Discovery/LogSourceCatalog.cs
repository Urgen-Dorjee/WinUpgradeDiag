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
            // Searched recursively. Setup writes the failed attempt's driver logs into a setupapi
            // subfolder of Rollback, not next to setupact.log, so a fixed path at the top of the
            // folder reported them absent on the very machines that needed them - including one
            // that had bugchecked with DRIVER_PNP_WATCHDOG, whose answer is in exactly that file.
            sources.Add(new LogSource(
                "setup-rollback-apilog", LogSourceCategory.SetupRollback, "setupapi logs (rollback)",
                "Device and driver install activity from the failed attempt, including any install " +
                "that was cut off when the machine crashed.",
                rollback, LogSourceKind.DirectoryGlob, "setupapi*.log", highValue: true, recursive: true));
            sources.Add(new LogSource(
                "setup-current-apilog", LogSourceCategory.SetupCurrent, "setupapi logs (current attempt)",
                "Device and driver install activity for the attempt in progress.",
                pantherCurrent, LogSourceKind.DirectoryGlob, "setupapi*.log", highValue: true, recursive: true));
            sources.Add(new LogSource(
                "setup-rollback-dmp", LogSourceCategory.SetupRollback, "setupmem.dmp (rollback)",
                "Present only if the machine bugchecked during the upgrade.",
                Path.Combine(rollback, "setupmem.dmp"), highValue: true));
            sources.Add(new LogSource(
                "setup-rollback-evtx", LogSourceCategory.SetupRollback, "Rollback event logs (*.evtx)",
                "Exported event logs captured at rollback time.",
                rollback, LogSourceKind.DirectoryGlob, "*.evtx", highValue: true));

            // --- Windows' own diagnosis ---
            //
            // Since Windows 10 2004, Setup runs Microsoft's SetupDiag automatically when an upgrade
            // fails and writes its conclusion here. After a later successful upgrade the old one
            // moves under Windows.old.
            sources.Add(new LogSource(
                "setupdiag-results", LogSourceCategory.SetupRollback, "SetupDiag results",
                "Microsoft's own analysis of the failed upgrade, written by Windows Setup when it failed.",
                Path.Combine(windir, "Logs", "SetupDiag", "SetupDiagResults.xml"), highValue: true));
            sources.Add(new LogSource(
                "setupdiag-results-old", LogSourceCategory.PreviousOs, "SetupDiag results (Windows.old)",
                "SetupDiag's analysis from before the most recent upgrade.",
                Path.Combine(systemDrive + Path.DirectorySeparatorChar, "Windows.old", "Windows", "Logs", "SetupDiag",
                    "SetupDiagResults.xml")));

            // --- Driver installation, the live record ---
            //
            // This is where a PnP watchdog failure is written down, and it was not looked at. The
            // catalogue knew one setupapi.dev.log, the copy inside the Rollback folder, which is
            // frequently not there — so on a machine that bugchecked on a driver the tool had no
            // driver log at all, while the authoritative one sat unread in the INF directory. The
            // report then told the technician to go and check setupapi.dev.log, a file it had just
            // listed as not present.
            var infDir = Path.Combine(windir, "INF");
            sources.Add(new LogSource(
                "driver-inf-dev", LogSourceCategory.SetupCurrent, "setupapi.dev.log (device installs)",
                "Every driver install and start this Windows installation has performed, with the " +
                "result of each. The record of the driver that stalls during an upgrade.",
                Path.Combine(infDir, "setupapi.dev.log"), highValue: true));
            sources.Add(new LogSource(
                "driver-inf-app", LogSourceCategory.Servicing, "setupapi.app.log (application installs)",
                "Application-side counterpart to setupapi.dev.log.",
                Path.Combine(infDir, "setupapi.app.log")));

            // --- Windows Setup: completed successfully at some point ---
            var pantherCompleted = Path.Combine(windir, "Panther");
            sources.Add(new LogSource(
                "setup-completed-act", LogSourceCategory.SetupCompleted, "setupact.log (completed)",
                "Left behind by a Setup run that finished, successfully or not.",
                Path.Combine(pantherCompleted, "setupact.log")));
            sources.Add(new LogSource(
                "setup-completed-apilog", LogSourceCategory.SetupCompleted, "setupapi.dev.log (completed)",
                "Driver activity from a Setup run that finished.",
                Path.Combine(pantherCompleted, "setupapi.dev.log")));
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
                         "DataTransferService.log", "AppEnforce.log", "PolicyAgent.log",
                         // Client health rather than content: these two say why a client that
                         // installed successfully never registers with a site.
                         "ClientIDManagerStartup.log", "LocationServices.log",
                         "ClientLocation.log", "CcmExec.log", "CcmRepair.log"
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
