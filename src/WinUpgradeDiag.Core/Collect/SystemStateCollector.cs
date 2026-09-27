using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Threading;

namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>
    /// Runs every live-state collector and assembles a <see cref="SystemState"/>. Each step is
    /// isolated so one failing collector costs only its own section of the report.
    /// </summary>
    public sealed class SystemStateCollector
    {
        public SystemState Collect(IProgress<string> progress = null, CancellationToken cancellationToken = default(CancellationToken))
        {
            var state = new SystemState
            {
                MachineName = Environment.MachineName,
                CollectedAtUtc = DateTime.UtcNow
            };

            Step(state, progress, cancellationToken, "Checking elevation", () => state.IsElevated = IsElevated());

            var registry = new RegistryStateCollector();
            Step(state, progress, cancellationToken, "Reading OS identity", () => state.Os = registry.GetOsIdentity());
            Step(state, progress, cancellationToken, "Reading Setup progress", () => state.SetupProgressPercent = registry.GetSetupProgressPercent());
            Step(state, progress, cancellationToken, "Reading pending-reboot flags", () => state.PendingReboot = registry.GetPendingReboot());
            Step(state, progress, cancellationToken, "Reading Secure Boot state", () => state.SecureBootEnabled = registry.GetSecureBootEnabled());
            Step(state, progress, cancellationToken, "Reading Memory Integrity state", () => state.MemoryIntegrityEnabled = registry.GetHvciEnabled());

            Step(state, progress, cancellationToken, "Checking free space and upgrade folders", () => CollectFileSystem(state));

            Step(state, progress, cancellationToken, "Checking upgrade processes", () =>
            {
                // Samples CPU only when an upgrade worker is actually alive, so a machine that has
                // already failed costs nothing, and one sitting at 99% gets the measurement that
                // decides whether it is working or wedged.
                state.Processes = new ProcessCollector()
                    .CollectWithCpuSample(ProcessCollector.DefaultCpuSampleWindow, cancellationToken);
            });

            Step(state, progress, cancellationToken, "Querying task sequence execution request (WMI)", () =>
            {
                state.TaskSequenceExecutionRequest = new CcmWmiCollector().GetOrphanedTaskSequenceInfo();
                if (state.TaskSequenceExecutionRequest.Error != null)
                {
                    state.CollectionErrors.Add("CCM_TSExecutionRequest: " + state.TaskSequenceExecutionRequest.Error);
                }
            });

            Step(state, progress, cancellationToken, "Reading storage health (WMI)", () =>
            {
                state.StorageHealth = new StorageHealthCollector().Collect();
                foreach (var disk in state.StorageHealth)
                {
                    if (disk.Error != null)
                    {
                        state.CollectionErrors.Add(disk.Error);
                    }
                }
            });

            Step(state, progress, cancellationToken, "Listing filter drivers", () => state.FilterDrivers = new FilterDriverCollector().Collect());

            Step(state, progress, cancellationToken, "Reading System and Application event logs", () =>
            {
                var events = new EventLogCollector().Collect();
                state.Events = events.Events;
                state.CollectionErrors.AddRange(events.Errors);
            });

            return state;
        }

        private static void Step(SystemState state, IProgress<string> progress, CancellationToken cancellationToken, string label, Action action)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(label);
            try
            {
                action();
            }
            catch (Exception ex)
            {
                state.CollectionErrors.Add(label + ": " + ex.Message);
            }
        }

        private static bool IsElevated()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        private static void CollectFileSystem(SystemState state)
        {
            var systemDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + Path.DirectorySeparatorChar;
            var windir = Environment.GetEnvironmentVariable("WINDIR") ?? Path.Combine(systemDrive, "Windows");

            try
            {
                var drive = new DriveInfo(systemDrive);
                state.SystemDriveFreeBytes = drive.AvailableFreeSpace;
                state.SystemDriveTotalBytes = drive.TotalSize;
            }
            catch (Exception ex)
            {
                state.CollectionErrors.Add("Free space: " + ex.Message);
            }

            var folders = new List<UpgradeFolderInfo>();
            foreach (var path in new[]
                     {
                         Path.Combine(systemDrive, "$WINDOWS.~BT"),
                         Path.Combine(systemDrive, "$WINDOWS.~WS"),
                         Path.Combine(systemDrive, "Windows.old"),
                         Path.Combine(systemDrive, "_SMSTaskSequence"),
                         Path.Combine(windir, "CCM")
                     })
            {
                var info = new UpgradeFolderInfo { Path = path };
                try
                {
                    info.Exists = Directory.Exists(path);
                    if (info.Exists)
                    {
                        info.LastWriteTimeUtc = Directory.GetLastWriteTimeUtc(path);
                    }
                }
                catch (Exception)
                {
                    // Existence is still useful even if the timestamp is not readable.
                }
                folders.Add(info);
            }
            state.UpgradeFolders = folders;
        }
    }
}
