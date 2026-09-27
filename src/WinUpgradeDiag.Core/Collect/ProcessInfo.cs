using System;
using System.Globalization;

namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>
    /// Whether a process this tool cares about is alive right now, since when, and — when it was
    /// sampled — whether it is actually doing anything.
    /// <para>
    /// That last part is the whole point for a machine sitting at "99%". A progress bar is not
    /// evidence: the number comes from the task sequence, not from Setup, and it saturates long
    /// before the work finishes. Whether <c>SetupHost</c> is burning CPU is evidence.
    /// </para>
    /// </summary>
    public sealed class ProcessInfo
    {
        public ProcessInfo(
            string name,
            bool isRunning,
            int? processId,
            DateTime? startTimeUtc,
            TimeSpan? totalProcessorTime = null,
            long? workingSetBytes = null,
            int instanceCount = 0,
            double? cpuPercent = null)
        {
            Name = name;
            IsRunning = isRunning;
            ProcessId = processId;
            StartTimeUtc = startTimeUtc;
            TotalProcessorTime = totalProcessorTime;
            WorkingSetBytes = workingSetBytes;
            InstanceCount = instanceCount;
            CpuPercent = cpuPercent;
        }

        public string Name { get; }
        public bool IsRunning { get; }
        public int? ProcessId { get; }
        public DateTime? StartTimeUtc { get; }

        /// <summary>CPU consumed since the process started, across all its threads.</summary>
        public TimeSpan? TotalProcessorTime { get; }

        public long? WorkingSetBytes { get; }

        /// <summary>How many copies are running. Setup can legitimately have more than one.</summary>
        public int InstanceCount { get; }

        /// <summary>
        /// CPU used during a short sampling window, as a percentage of one core, or null if no
        /// sample was taken. Above <see cref="BusyCpuPercent"/> the process is clearly working;
        /// near zero it is idle or blocked.
        /// </summary>
        public double? CpuPercent { get; }

        /// <summary>Sustained CPU above this over the sample window means real work is happening.</summary>
        public const double BusyCpuPercent = 2.0;

        public bool IsBusy => CpuPercent.HasValue && CpuPercent.Value >= BusyCpuPercent;

        public string CpuText =>
            CpuPercent.HasValue
                ? CpuPercent.Value.ToString("0.#", CultureInfo.CurrentCulture) + "%"
                : string.Empty;

        public string RunningFor =>
            StartTimeUtc.HasValue
                ? Describe(DateTime.UtcNow - StartTimeUtc.Value)
                : string.Empty;

        /// <summary>
        /// A duration the way a person would say it: "45 minutes", "2 hours 35 minutes". Findings
        /// quote elapsed times constantly, and "01:35:00" makes a technician do arithmetic under
        /// pressure.
        /// </summary>
        public static string Describe(TimeSpan span)
        {
            if (span < TimeSpan.Zero)
            {
                span = TimeSpan.Zero;
            }

            if (span.TotalMinutes < 1)
            {
                return "less than a minute";
            }
            if (span.TotalHours < 1)
            {
                return ((int)span.TotalMinutes) + " minute" + (((int)span.TotalMinutes) == 1 ? "" : "s");
            }
            if (span.TotalDays < 1)
            {
                var hours = (int)span.TotalHours;
                var minutes = span.Minutes;
                return hours + " hour" + (hours == 1 ? "" : "s") +
                       (minutes > 0 ? " " + minutes + " minute" + (minutes == 1 ? "" : "s") : "");
            }

            var days = (int)span.TotalDays;
            return days + " day" + (days == 1 ? "" : "s");
        }
    }
}
