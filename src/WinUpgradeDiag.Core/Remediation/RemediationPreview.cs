using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using WinUpgradeDiag.Core.Collect;

namespace WinUpgradeDiag.Core.Remediation
{
    /// <summary>What one thing a tool would touch looks like right now.</summary>
    public sealed class PreviewTarget
    {
        public PreviewTarget(string what, string state, bool willChange, string effect)
        {
            What = what;
            State = state;
            WillChange = willChange;
            Effect = effect;
        }

        /// <summary>The thing itself: a service, a WMI class, a folder.</summary>
        public string What { get; }

        /// <summary>Its state as observed a moment ago.</summary>
        public string State { get; }

        /// <summary>False when the target is already absent, so the step is a no-op.</summary>
        public bool WillChange { get; }

        /// <summary>What running the tool would do to it.</summary>
        public string Effect { get; }

        public string StatusText => WillChange ? "WILL CHANGE" : "no change";
    }

    /// <summary>The result of previewing a tool without running it.</summary>
    public sealed class PreviewReport
    {
        public PreviewReport(
            ToolDefinition tool, IReadOnlyList<PreviewTarget> targets, string summary, string caveat)
        {
            Tool = tool;
            Targets = targets ?? new List<PreviewTarget>();
            Summary = summary;
            Caveat = caveat;
        }

        public ToolDefinition Tool { get; }
        public IReadOnlyList<PreviewTarget> Targets { get; }

        /// <summary>One line: how much this would actually do.</summary>
        public string Summary { get; }

        /// <summary>What the preview cannot promise, or null.</summary>
        public string Caveat { get; }

        public int ChangeCount => Targets.Count(t => t.WillChange);
        public bool HasCaveat => !string.IsNullOrWhiteSpace(Caveat);
    }

    /// <summary>
    /// Reports what a tool would affect, by inspecting the machine rather than by running anything.
    /// <para>
    /// Most of these scripts have no <c>-WhatIf</c>, so a "dry run" that claimed to simulate them
    /// would be fiction. What is honest — and more useful — is to look at each thing the script
    /// touches and say what is there now: whether the orphaned execution request actually exists,
    /// whether the folder it would delete is present and how large, whether the cache item is the
    /// one you think it is. That turns "trust me" into something checkable before committing.
    /// </para>
    /// </summary>
    public sealed class RemediationPreview
    {
        private readonly CcmWmiCollector _wmi = new CcmWmiCollector();

        public PreviewReport Build(ToolDefinition tool, string parameterValue)
        {
            if (tool == null)
            {
                throw new ArgumentNullException(nameof(tool));
            }

            switch (tool.Id)
            {
                case "FIX-A": return PreviewDownloadFix(tool, parameterValue);
                case "FIX-B": return PreviewTaskSequenceClear(tool);
                case "FIX-C": return PreviewCacheFix(tool);
                case "FIX-D": return PreviewSetupLeftovers(tool);
                case "RESET-TS": return PreviewHistoryReset(tool, parameterValue);
                case "REPAIR-CLIENT": return PreviewClientRepair(tool);
                case "REMOVE-LEFTOVERS": return PreviewRemoveLeftovers(tool);
                default:
                    return new PreviewReport(
                        tool,
                        new List<PreviewTarget>(),
                        "This tool only reads the machine, so there is nothing to preview.",
                        null);
            }
        }

        // ---------------------------------------------------------------- shared targets

        private PreviewTarget ExecutionRequestTarget()
        {
            var info = _wmi.GetOrphanedTaskSequenceInfo();

            if (info.Error != null)
            {
                return new PreviewTarget(
                    "WMI: CCM_TSExecutionRequest", "could not be read (" + info.Error + ")", true,
                    "Any instances found would be deleted.");
            }

            return info.ExecutionRequestExists
                ? new PreviewTarget(
                    "WMI: CCM_TSExecutionRequest",
                    "present" + (info.PackageId != null ? " for package " + info.PackageId : ""),
                    true,
                    "Would be deleted, which is what releases Software Center.")
                : new PreviewTarget(
                    "WMI: CCM_TSExecutionRequest", "not present", false,
                    "Nothing to delete — this machine has no stuck execution request.");
        }

        private static PreviewTarget ServiceTarget(string serviceName, string effect)
        {
            string state;
            try
            {
                using (var service = new ServiceController(serviceName))
                {
                    state = service.Status.ToString().ToLowerInvariant();
                }
            }
            catch (Exception)
            {
                return new PreviewTarget("Service: " + serviceName, "not installed", false,
                    "No ConfigMgr client on this machine.");
            }

            return new PreviewTarget("Service: " + serviceName, state, true, effect);
        }

        private static PreviewTarget FolderTarget(string path, string effect)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return new PreviewTarget("Folder: " + path, "not present", false, "Nothing to delete.");
                }

                var size = DirectorySize(path);
                return new PreviewTarget(
                    "Folder: " + path,
                    size.HasValue ? "present, about " + Format(size.Value) : "present",
                    true,
                    effect);
            }
            catch (Exception ex)
            {
                return new PreviewTarget("Folder: " + path, "could not be inspected (" + ex.Message + ")", true, effect);
            }
        }

        /// <summary>
        /// Approximate size, capped so previewing never becomes an expensive directory walk on a
        /// folder with hundreds of thousands of files.
        /// </summary>
        private static long? DirectorySize(string path)
        {
            try
            {
                long total = 0;
                var counted = 0;
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch (Exception)
                    {
                        // Skip what cannot be measured.
                    }

                    if (++counted >= 20000)
                    {
                        break;
                    }
                }
                return total;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---------------------------------------------------------------- per-tool previews

        private PreviewReport PreviewTaskSequenceClear(ToolDefinition tool)
        {
            var targets = new List<PreviewTarget>
            {
                ServiceTarget("CcmExec", "Would be stopped, then started again."),
                ExecutionRequestTarget(),
                FolderTarget(@"C:\_SMSTaskSequence", "Would be deleted."),
                new PreviewTarget("Client cache", "untouched by this tool", false,
                    "The downloaded image is kept, so a retry does not start from zero.")
            };

            return new PreviewReport(tool, targets, Summarise(targets), null);
        }

        private PreviewReport PreviewDownloadFix(ToolDefinition tool, string contentId)
        {
            var targets = new List<PreviewTarget>
            {
                ServiceTarget("CcmExec", "Would be stopped, then started again."),
                ExecutionRequestTarget(),
                FolderTarget(@"C:\_SMSTaskSequence", "Would be deleted.")
            };

            var cache = _wmi.GetCacheSnapshot();
            if (!cache.Available)
            {
                targets.Add(new PreviewTarget(
                    "Cache item " + (contentId ?? "(none given)"), "cache could not be read", true,
                    "The script would attempt the delete anyway."));
            }
            else
            {
                var match = cache.Elements.FirstOrDefault(e =>
                    string.Equals(e.ContentId, contentId, StringComparison.OrdinalIgnoreCase));

                targets.Add(match != null
                    ? new PreviewTarget(
                        "Cache item " + match.ContentId,
                        match.SizeText + " at " + match.Location,
                        true,
                        "Would be deleted. The retry re-downloads " + match.SizeText + ".")
                    : new PreviewTarget(
                        "Cache item " + (contentId ?? "(none given)"),
                        "no cache item with that id",
                        false,
                        "The delete would find nothing. Check the id against Show downloaded content."));
            }

            var wrongId = targets.Any(t => t.What.StartsWith("Cache item", StringComparison.Ordinal) && !t.WillChange);
            return new PreviewReport(
                tool, targets, Summarise(targets),
                wrongId ? "The content id does not match anything cached on this machine — check it before running." : null);
        }

        private PreviewReport PreviewCacheFix(ToolDefinition tool)
        {
            var targets = new List<PreviewTarget>
            {
                ServiceTarget("CcmExec", "Would be stopped, then started again."),
                ExecutionRequestTarget(),
                FolderTarget(@"C:\_SMSTaskSequence", "Would be deleted.")
            };

            var cache = _wmi.GetCacheSnapshot();
            if (!cache.Available)
            {
                targets.Add(new PreviewTarget("Stale cache records", "cache could not be read", true,
                    "Any records pointing at missing folders would be removed."));
            }
            else
            {
                var stale = cache.StaleElements;
                targets.Add(stale.Count > 0
                    ? new PreviewTarget("Stale cache records", stale.Count + " record(s) point at missing folders", true,
                        "Would be removed so the client downloads the content again.")
                    : new PreviewTarget("Stale cache records", "none", false, "Nothing to clean up."));
            }

            return new PreviewReport(tool, targets, Summarise(targets), null);
        }

        private PreviewReport PreviewSetupLeftovers(ToolDefinition tool)
        {
            var windowsOld = Directory.Exists(@"C:\Windows.old");

            var targets = new List<PreviewTarget>
            {
                FolderTarget(@"C:\$WINDOWS.~BT", "Would be deleted after its logs are copied to C:\\Temp."),
                new PreviewTarget(
                    @"Folder: C:\Windows.old",
                    windowsOld ? "present" : "not present",
                    false,
                    windowsOld
                        ? "The script refuses to run while this exists — use the reclaim-space tool instead."
                        : "Absent, so this script will proceed.")
            };

            return new PreviewReport(
                tool, targets, Summarise(targets),
                windowsOld
                    ? "C:\\Windows.old is present, so this script will stop without doing anything. " +
                      "This machine may have upgraded successfully."
                    : "Setup logs are copied to C:\\Temp first, but export this tool's evidence bundle " +
                      "if the failure still needs investigating.");
        }

        private static PreviewReport PreviewHistoryReset(ToolDefinition tool, string packageId)
        {
            var key = @"HKLM\SOFTWARE\Microsoft\SMS\Mobile Client\Software Distribution\Execution History\System\" +
                      (packageId ?? "(none given)");

            var exists = false;
            try
            {
                using (var hive = Microsoft.Win32.RegistryKey.OpenBaseKey(
                           Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Default))
                using (var subKey = hive.OpenSubKey(
                           @"SOFTWARE\Microsoft\SMS\Mobile Client\Software Distribution\Execution History\System\" + packageId))
                {
                    exists = subKey != null;
                }
            }
            catch (Exception)
            {
                // Treated as absent; the script reports the same thing.
            }

            var targets = new List<PreviewTarget>
            {
                exists
                    ? new PreviewTarget("Registry: " + key, "present", true, "Would be deleted.")
                    : new PreviewTarget("Registry: " + key, "not present", false,
                        "Nothing to delete — check the package id is the task sequence's, not the content id."),
                ServiceTarget("CcmExec", "Would be restarted.")
            };

            return new PreviewReport(
                tool, targets, Summarise(targets),
                exists ? null : "No execution history exists for that package id on this machine.");
        }

        private static PreviewReport PreviewClientRepair(ToolDefinition tool)
        {
            var repair = @"C:\Windows\CCM\ccmrepair.exe";
            var present = File.Exists(repair);

            var targets = new List<PreviewTarget>
            {
                present
                    ? new PreviewTarget("ConfigMgr client repair", "ccmrepair.exe present", true,
                        "Would start a client repair, which hands off to ccmsetup and can take several minutes.")
                    : new PreviewTarget("ConfigMgr client repair", "ccmrepair.exe not found", false,
                        "The script would stop; the client may need a full reinstall from the site server.")
            };

            return new PreviewReport(tool, targets, Summarise(targets), null);
        }

        private static PreviewReport PreviewRemoveLeftovers(ToolDefinition tool)
        {
            var targets = new List<PreviewTarget>
            {
                FolderTarget(@"C:\Windows.old", "Would be retired through DISM together with $WINDOWS.~BT."),
                FolderTarget(@"C:\$WINDOWS.~BT", "Would be retired together with Windows.old."),
                FolderTarget(@"C:\$WINDOWS.~WS", "Would be deleted; it has no rollback role."),
                FolderTarget(@"C:\$GetCurrent", "Would be deleted; it has no rollback role.")
            };

            return new PreviewReport(
                tool, targets, Summarise(targets),
                "This retires the rollback option permanently: \"Go back\" disappears from Settings. " +
                "Windows removes Windows.old by itself ten days after an upgrade, so this is often unnecessary.");
        }

        // ---------------------------------------------------------------- helpers

        private static string Summarise(IReadOnlyList<PreviewTarget> targets)
        {
            var changes = targets.Count(t => t.WillChange);

            if (changes == 0)
            {
                return "Nothing on this machine would change — every target this tool touches is already absent.";
            }

            return changes + " of " + targets.Count + " target(s) would be changed. " +
                   "The rest are already absent.";
        }

        private static string Format(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return value.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
        }
    }
}
