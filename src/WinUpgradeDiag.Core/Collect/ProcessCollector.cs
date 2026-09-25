using System;
using System.Collections.Generic;
using System.Diagnostics;

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

        public ProcessSnapshot Collect()
        {
            var results = new List<ProcessInfo>();

            foreach (var name in WatchedProcessNames)
            {
                results.Add(CollectOne(name));
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

                var first = matches[0];
                DateTime? startTime;
                try
                {
                    startTime = first.StartTime.ToUniversalTime();
                }
                catch (Exception)
                {
                    // Access to StartTime can be denied even when the process is visible.
                    startTime = null;
                }

                return new ProcessInfo(name, true, first.Id, startTime);
            }
            finally
            {
                foreach (var p in matches)
                {
                    p.Dispose();
                }
            }
        }
    }
}
