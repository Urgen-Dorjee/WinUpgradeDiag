using System;

namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>Whether a process this tool cares about is alive right now, and since when.</summary>
    public sealed class ProcessInfo
    {
        public string Name { get; }
        public bool IsRunning { get; }
        public int? ProcessId { get; }
        public DateTime? StartTimeUtc { get; }

        public ProcessInfo(string name, bool isRunning, int? processId, DateTime? startTimeUtc)
        {
            Name = name;
            IsRunning = isRunning;
            ProcessId = processId;
            StartTimeUtc = startTimeUtc;
        }
    }
}
