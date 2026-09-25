using System.Collections.Generic;
using System.Linq;

namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>
    /// A point-in-time snapshot of the processes that decide whether an upgrade is live.
    /// Distinguishing "TSManager alive" from "orphaned" is AGENTS.md's first acceptance test —
    /// this snapshot is what makes that distinction possible.
    /// </summary>
    public sealed class ProcessSnapshot
    {
        public IReadOnlyList<ProcessInfo> Processes { get; }

        public ProcessSnapshot(IReadOnlyList<ProcessInfo> processes)
        {
            Processes = processes;
        }

        public bool IsRunning(string name)
        {
            return Processes.Any(p => p.IsRunning && string.Equals(p.Name, name, System.StringComparison.OrdinalIgnoreCase));
        }
    }
}
