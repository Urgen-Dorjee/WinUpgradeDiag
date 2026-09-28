using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>
    /// Checks the handful of processes whose presence or absence decides between "upgrade is
    /// live" and "task sequence is orphaned" (AGENTS.md acceptance test #1) or "Setup is working,
    /// not stuck" (acceptance test #2).
    /// </summary>
    public sealed class ProcessCollector
    {
        public static readonly IReadOnlyList<string> WatchedProcessNames = new[]
        {
            "TSManager", "SetupHost", "setupprep", "CcmExec", "TrustedInstaller"
        };

        /// <summary>
        /// The processes whose CPU activity decides whether Setup is working or wedged.
        /// <para>
        /// Deliberately only Setup's own workers. TrustedInstaller and TiWorker are busy on any
        /// machine that is merely installing updates, so including them made the sample fire — and
        /// stall collection for two seconds — on ordinary healthy machines, while telling us
        /// nothing about an upgrade.
        /// </para>
        /// </summary>
        public static readonly IReadOnlyList<string> UpgradeWorkerNames = new[]
        {
            "SetupHost", "setupprep"
        };

        /// <summary>
        /// Long enough for a busy process to accumulate measurable CPU, short enough that a
        /// technician does not notice the pause.
        /// </summary>
        public static readonly TimeSpan DefaultCpuSampleWindow = TimeSpan.FromSeconds(2);

        public ProcessSnapshot Collect()
        {
            return new ProcessSnapshot(WatchedProcessNames.Select(CollectOne).ToList());
        }

        /// <summary>
        /// Collects, then — only if an upgrade worker is actually running — measures CPU over a
        /// short window so the rules can tell a working Setup from a wedged one.
        /// <para>
        /// The sample is skipped entirely when nothing relevant is alive, so the usual case where
        /// the machine has already failed costs nothing.
        /// </para>
        /// </summary>
        public ProcessSnapshot CollectWithCpuSample(
            TimeSpan window, CancellationToken cancellationToken = default(CancellationToken))
        {
            var first = WatchedProcessNames.ToDictionary(n => n, CollectOne, StringComparer.OrdinalIgnoreCase);

            var worthSampling = UpgradeWorkerNames
                .Where(n => first.ContainsKey(n) && first[n].IsRunning)
                .ToList();

            if (worthSampling.Count == 0 || window <= TimeSpan.Zero)
            {
                return new ProcessSnapshot(WatchedProcessNames.Select(n => first[n]).ToList());
            }

            var before = worthSampling.ToDictionary(n => n, n => first[n].TotalProcessorTime, StringComparer.OrdinalIgnoreCase);
            var clockStart = DateTime.UtcNow;

            cancellationToken.WaitHandle.WaitOne(window);
            if (cancellationToken.IsCancellationRequested)
            {
                return new ProcessSnapshot(WatchedProcessNames.Select(n => first[n]).ToList());
            }

            var elapsed = DateTime.UtcNow - clockStart;
            var results = new List<ProcessInfo>();

            foreach (var name in WatchedProcessNames)
            {
                var original = first[name];
                if (!before.ContainsKey(name) || !original.IsRunning)
                {
                    results.Add(original);
                    continue;
                }

                var after = CollectOne(name);
                double? percent = null;

                var start = before[name];
                if (start.HasValue && after.TotalProcessorTime.HasValue && elapsed > TimeSpan.Zero)
                {
                    var used = after.TotalProcessorTime.Value - start.Value;
                    if (used >= TimeSpan.Zero)
                    {
                        // Percent of a single core. A multi-threaded worker can legitimately exceed
                        // 100, which is itself useful information.
                        percent = used.TotalMilliseconds / elapsed.TotalMilliseconds * 100.0;
                    }
                }

                results.Add(new ProcessInfo(
                    after.Name, after.IsRunning, after.ProcessId, after.StartTimeUtc,
                    after.TotalProcessorTime, after.WorkingSetBytes, after.InstanceCount, percent));
            }

            return new ProcessSnapshot(results);
        }

        private static ProcessInfo CollectOne(string name)
        {
            Process[] matches;
            try
            {
                matches = Process.GetProcessesByName(name);
            }
            catch (Exception)
            {
                // Degrade, never crash: report "not observed" rather than fail the whole collection.
                return new ProcessInfo(name, false, null, null);
            }

            try
            {
                if (matches.Length == 0)
                {
                    return new ProcessInfo(name, false, null, null);
                }

                // Setup can run more than one copy. Report the busiest, since that is the one doing
                // the work, and carry the count so the report does not imply there was only one.
                var primary = matches
                    .OrderByDescending(p => SafeCpu(p) ?? TimeSpan.Zero)
                    .First();

                return new ProcessInfo(
                    name,
                    true,
                    SafeId(primary),
                    SafeStart(primary),
                    SafeCpu(primary),
                    SafeWorkingSet(primary),
                    matches.Length);
            }
            finally
            {
                foreach (var p in matches)
                {
                    p.Dispose();
                }
            }
        }

        // Each of these can throw if the process exits mid-read or access is denied, which is
        // normal for a process owned by SYSTEM; a missing number is better than a failed run.
        private static int? SafeId(Process p)
        {
            try { return p.Id; } catch (Exception) { return null; }
        }

        private static DateTime? SafeStart(Process p)
        {
            try { return p.StartTime.ToUniversalTime(); } catch (Exception) { return null; }
        }

        private static TimeSpan? SafeCpu(Process p)
        {
            try { return p.TotalProcessorTime; } catch (Exception) { return null; }
        }

        private static long? SafeWorkingSet(Process p)
        {
            try { return p.WorkingSet64; } catch (Exception) { return null; }
        }
    }
}
