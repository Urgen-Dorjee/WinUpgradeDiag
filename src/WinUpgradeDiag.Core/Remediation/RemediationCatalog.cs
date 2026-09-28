using System;
using System.Collections.Generic;
using System.Linq;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Orchestration;

namespace WinUpgradeDiag.Core.Remediation
{
    /// <summary>
    /// Maps a finding onto the recovery script that fixes it, filling in the parameters from what
    /// the diagnostic already collected.
    /// <para>
    /// The parameter-filling is the point. Today a technician recovering an interrupted download
    /// runs one script to list the cache, reads a multi-gigabyte row off a table by eye, copies a
    /// content id, and pastes it into a second script. Every one of those steps is a chance to
    /// delete the wrong thing on a machine that is already broken. The tool has the cache in front
    /// of it, so it can simply say which id.
    /// </para>
    /// </summary>
    public static class RemediationCatalog
    {
        /// <summary>The folder the recovery scripts live in, relative to the tool.</summary>
        public const string ScriptFolderName = "Script";

        /// <summary>
        /// The action that addresses <paramref name="findingId"/>, or null if that finding has no
        /// scripted fix. <paramref name="context"/> supplies the parameter values.
        /// </summary>
        public static RemediationAction For(string findingId, DiagnosticContext context)
        {
            if (string.IsNullOrEmpty(findingId) || context == null)
            {
                return null;
            }

            var state = context.SystemState ?? new SystemState();

            switch (findingId)
            {
                case "TS-001": return ForOrphanedTaskSequence(state);
                case "CT-002": return ForStaleCache(state);
                case "SU-006": return ForLowDiskSpace(state);
                case "SU-003": return ForStaleProgress();
                default: return null;
            }
        }

        // ---------------------------------------------------------------- orphaned task sequence

        /// <summary>
        /// The stuck "Installing..." case. Which script depends on whether the interruption
        /// happened during the download or during Setup — the cache tells us which.
        /// </summary>
        private static RemediationAction ForOrphanedTaskSequence(SystemState state)
        {
            var cache = state.CcmCache;
            var partialDownload = WasInterruptedDownloading(state);

            if (!partialDownload)
            {
                // Setup phase: install.wim is already downloaded and verified, so the cache is left
                // alone and only the orphaned execution request is cleared.
                return new RemediationAction(
                    "FIX-B",
                    "Fix-B-SetupInterrupted.ps1",
                    "Clear the orphaned task sequence, keep the downloaded image",
                    "The image was already downloaded when the machine was interrupted, so only the stale execution " +
                    "request needs clearing. The multi-gigabyte download is kept, so the retry does not start from zero.",
                    RemediationRisk.Disruptive,
                    new[]
                    {
                        "Stop the ConfigMgr client service (CcmExec).",
                        "Delete the CCM_TSExecutionRequest instances from WMI.",
                        "Delete C:\\_SMSTaskSequence.",
                        "Start CcmExec and wait for it to settle.",
                        "Trigger a machine policy refresh.",
                        "Leave the client cache untouched."
                    },
                    new[]
                    {
                        "TSManager, SetupHost and setupprep must all be stopped.",
                        "The machine must have booted normally back into Windows."
                    },
                    Command("Fix-B-SetupInterrupted.ps1"));
            }

            var package = cache?.LikelyOsUpgradePackage;
            if (package == null)
            {
                return new RemediationAction(
                    "FIX-A",
                    "Fix-A-DownloadInterrupted.ps1",
                    "Clear the orphaned task sequence and discard the partial download",
                    "The machine was interrupted while downloading the image, and a partial download cannot resume.",
                    RemediationRisk.Destructive,
                    new[]
                    {
                        "Stop CcmExec, clear the execution request, delete C:\\_SMSTaskSequence.",
                        "Delete the partial content through the client so WMI and disk stay in step.",
                        "Trigger a machine policy refresh."
                    },
                    new[] { "TSManager, SetupHost and setupprep must all be stopped." },
                    commandLine: null,
                    blockedReason:
                        cache == null || !cache.Available
                            ? "The client cache could not be read, so the content id to delete is unknown. " +
                              "Run List-CcmCache.ps1 and pass the multi-gigabyte item's ContentId to Fix-A."
                            : "No cached item is large enough to be the OS image, so there is no partial download " +
                              "to discard. Fix-B is probably the right script — confirm with List-CcmCache.ps1.");
            }

            return new RemediationAction(
                "FIX-A",
                "Fix-A-DownloadInterrupted.ps1",
                "Clear the orphaned task sequence and discard the partial download",
                "The machine was interrupted while downloading the image. Content downloaded inside a task sequence " +
                "does not resume, so the partial copy is removed and the retry downloads it again from the start.",
                RemediationRisk.Destructive,
                new[]
                {
                    "Stop the ConfigMgr client service (CcmExec).",
                    "Delete the CCM_TSExecutionRequest instances from WMI.",
                    "Delete C:\\_SMSTaskSequence.",
                    "Start CcmExec and wait for it to settle.",
                    "Delete cache item " + package.ContentId + " (" + package.SizeText + ") through the client.",
                    "Trigger a machine policy refresh."
                },
                new[]
                {
                    "TSManager, SetupHost and setupprep must all be stopped.",
                    "Content id " + package.ContentId + " must still be the OS upgrade package.",
                    "The retry will download " + package.SizeText + " again."
                },
                Command("Fix-A-DownloadInterrupted.ps1", "-ContentId " + package.ContentId));
        }

        /// <summary>
        /// Whether the interruption looks like it happened during the download rather than during
        /// Setup. Setup having started at all leaves <c>$WINDOWS.~BT</c> behind; if that is absent
        /// but a large cache item is present, the machine never got past downloading.
        /// </summary>
        private static bool WasInterruptedDownloading(SystemState state)
        {
            var setupStarted = (state.UpgradeFolders ?? new List<UpgradeFolderInfo>())
                .Any(f => f.Exists && f.Path != null &&
                          f.Path.IndexOf("$WINDOWS.~BT", StringComparison.OrdinalIgnoreCase) >= 0);

            return !setupStarted;
        }

        // ---------------------------------------------------------------- cache problems

        private static RemediationAction ForStaleCache(SystemState state)
        {
            var stale = state.CcmCache?.StaleElements ?? new List<CcmCacheElement>();

            return new RemediationAction(
                "FIX-C",
                "Fix-C-CacheCleared.ps1",
                "Remove cache records pointing at folders that no longer exist",
                stale.Count > 0
                    ? stale.Count + " cache record(s) point at folders that are gone — the signature of a cache " +
                      "deleted by hand rather than through the client. The client still believes that content is " +
                      "present, so it will not download it again."
                    : "Clears stale cache records and the orphaned task sequence, then lets the content download again.",
                RemediationRisk.Destructive,
                new[]
                {
                    "Stop CcmExec, clear the execution request, delete C:\\_SMSTaskSequence.",
                    "Recreate C:\\Windows\\ccmcache if the folder itself was deleted.",
                    "Remove cache records whose folder no longer exists.",
                    "Start CcmExec and trigger a machine policy refresh."
                },
                new[]
                {
                    "TSManager, SetupHost and setupprep must all be stopped.",
                    "The retry will download the image again from the start."
                },
                Command("Fix-C-CacheCleared.ps1"));
        }

        // ---------------------------------------------------------------- disk space

        private static RemediationAction ForLowDiskSpace(SystemState state)
        {
            var hasWindowsOld = (state.UpgradeFolders ?? new List<UpgradeFolderInfo>())
                .Any(f => f.Exists && f.Path != null &&
                          f.Path.EndsWith("Windows.old", StringComparison.OrdinalIgnoreCase));

            if (hasWindowsOld)
            {
                // Windows.old present means this machine may have upgraded successfully. Removing
                // the half-built Setup folder is the wrong script here, and the scripts themselves
                // refuse in this state.
                return new RemediationAction(
                    "REMOVE-LEFTOVERS",
                    "Remove-UpgradeLeftovers.ps1",
                    "Reclaim space from a completed upgrade",
                    "C:\\Windows.old is present, so this machine may have upgraded successfully. Windows.old and " +
                    "$WINDOWS.~BT are one rollback set; removing either alone breaks \"Go back\" while the other " +
                    "keeps wasting space. The supported DISM route retires both together.",
                    RemediationRisk.Destructive,
                    new[]
                    {
                        "Report how many days of rollback remain.",
                        "Run DISM /Online /Remove-OSUninstall, retiring Windows.old and $WINDOWS.~BT together.",
                        "Remove the $WINDOWS.~WS and $GetCurrent staging folders."
                    },
                    new[]
                    {
                        "The user must have confirmed the upgraded machine is working.",
                        "\"Go back\" will no longer be available afterwards — this cannot be undone.",
                        "Windows removes Windows.old by itself ten days after an upgrade, so this is often unnecessary."
                    },
                    Command("Remove-UpgradeLeftovers.ps1", "-WhatIf"));
            }

            return new RemediationAction(
                "FIX-D",
                "Fix-D-SetupLeftovers.ps1",
                "Save the Setup logs, then remove the half-built Setup folder",
                "Frees the space a failed attempt is holding, after copying the Setup logs somewhere safe so the " +
                "reason for the failure survives the cleanup.",
                RemediationRisk.Destructive,
                new[]
                {
                    "Copy setup*.log and smsts.log to a dated folder under C:\\Temp.",
                    "Take ownership of C:\\$WINDOWS.~BT and grant Administrators access.",
                    "Delete C:\\$WINDOWS.~BT."
                },
                new[]
                {
                    "TSManager, SetupHost and setupprep must all be stopped.",
                    "C:\\Windows.old must NOT exist — if it does, this machine may have upgraded successfully " +
                    "and Remove-UpgradeLeftovers.ps1 is the correct script instead.",
                    "Export the evidence bundle from this tool first if the failure still needs investigating."
                },
                Command("Fix-D-SetupLeftovers.ps1"));
        }

        // ---------------------------------------------------------------- stale progress

        private static RemediationAction ForStaleProgress()
        {
            return new RemediationAction(
                "CHECK-STATE",
                "Check-UpgradeState.ps1",
                "Confirm the machine's task sequence state before doing anything",
                "Setup has already exited, so the next question is whether the task sequence closed cleanly or is " +
                "stuck. This reports that without changing anything.",
                RemediationRisk.ReadOnly,
                new[] { "Report process, WMI, folder and disk state, then say whether a task sequence is orphaned." },
                new string[0],
                Command("Check-UpgradeState.ps1"));
        }

        // ---------------------------------------------------------------- helpers

        private static string Command(string scriptName, string arguments = null)
        {
            var call = ".\\" + scriptName + (string.IsNullOrEmpty(arguments) ? "" : " " + arguments);
            return "powershell -ExecutionPolicy Bypass -File " + call;
        }
    }
}
