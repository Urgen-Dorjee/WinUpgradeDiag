using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Discovery;
using WinUpgradeDiag.Core.IO;
using WinUpgradeDiag.Core.Orchestration;

namespace WinUpgradeDiag.Core.Rules
{
    /// <summary>Where a timeline entry came from, so the view can group and filter.</summary>
    public enum TimelineSource
    {
        /// <summary>A Windows event log record.</summary>
        WindowsEvent,

        /// <summary>The last time a discovered log was written — when that phase stopped working.</summary>
        LogActivity,

        /// <summary>An upgrade working folder's timestamp.</summary>
        UpgradeFolder,

        /// <summary>A process that is running now, and when it started.</summary>
        Process
    }

    /// <summary>One dated thing that happened, normalised so unlike sources can be merged.</summary>
    public sealed class TimelineEntry
    {
        public TimelineEntry(
            DateTime timestampUtc, TimelineSource source, string label, string detail, Severity severity)
        {
            TimestampUtc = timestampUtc;
            Source = source;
            Label = label;
            Detail = detail;
            Severity = severity;
        }

        public DateTime TimestampUtc { get; }
        public TimelineSource Source { get; }

        /// <summary>Short description: "Unexpected shutdown", "setupact.log last written".</summary>
        public string Label { get; }

        /// <summary>The supporting text — event message, file path, and so on.</summary>
        public string Detail { get; }

        public Severity Severity { get; }

        public string SourceText
        {
            get
            {
                switch (Source)
                {
                    case TimelineSource.WindowsEvent: return "Event log";
                    case TimelineSource.LogActivity: return "Log activity";
                    case TimelineSource.UpgradeFolder: return "Folder";
                    case TimelineSource.Process: return "Process";
                    default: return Source.ToString();
                }
            }
        }

        public string SeverityText => Severity.ToString();

        public string LocalTimeText =>
            TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Merges everything with a timestamp onto one ordered list.
    /// <para>
    /// DESIGN.md §4.4 wants the failure bracketed: the last thing the task sequence logged, the
    /// event that ended it, and the crash if there was one. Without a single ordered view a
    /// technician has to hold three logs' clocks in their head, which is the manual work this tool
    /// exists to remove.
    /// </para>
    /// </summary>
    public static class TimelineBuilder
    {
        /// <summary>Event IDs that say something happened <em>to</em> the machine rather than in it.</summary>
        private static readonly Dictionary<int, string> NotableEvents = new Dictionary<int, string>
        {
            { 41, "Machine restarted without shutting down cleanly (power loss, hang, or crash)" },
            { 1001, "Crash recorded (bugcheck or application error)" },
            { 1074, "Shutdown or restart initiated — the message names who or what started it" },
            { 6008, "Previous shutdown was unexpected" },
            { 7031, "A service terminated unexpectedly" },
            { 7034, "A service terminated unexpectedly" },
            { 1000, "An application crashed" }
        };

        public static IReadOnlyList<TimelineEntry> Build(DiagnosticContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            var state = context.SystemState ?? new SystemState();
            var entries = new List<TimelineEntry>();

            entries.AddRange(FromEvents(state));
            entries.AddRange(FromLogs(context));
            entries.AddRange(FromFolders(state));
            entries.AddRange(FromProcesses(state));

            // Newest first: on a machine that failed hours ago, the relevant end of the timeline is
            // the recent one.
            return entries
                .Where(e => e.TimestampUtc != default(DateTime) && e.TimestampUtc.Year > 2000)
                .OrderByDescending(e => e.TimestampUtc)
                .ToList();
        }

        private static IEnumerable<TimelineEntry> FromEvents(SystemState state)
        {
            foreach (var record in state.Events ?? new List<EventRecordInfo>())
            {
                if (!record.TimeCreatedUtc.HasValue)
                {
                    continue;
                }

                string meaning;
                var notable = NotableEvents.TryGetValue(record.EventId, out meaning);

                var severity = notable
                    ? (record.EventId == 41 || record.EventId == 1001 || record.EventId == 6008
                        ? Severity.Critical
                        : Severity.Warning)
                    : Severity.Info;

                var label = notable
                    ? meaning
                    : record.ProviderName + " event " + record.EventId.ToString(CultureInfo.InvariantCulture);

                yield return new TimelineEntry(
                    record.TimeCreatedUtc.Value,
                    TimelineSource.WindowsEvent,
                    label,
                    Trim(record.Message) ?? (record.LogName + " / " + record.ProviderName + " / id " + record.EventId),
                    severity);
            }
        }

        private static IEnumerable<TimelineEntry> FromLogs(DiagnosticContext context)
        {
            foreach (var entry in context.Manifest.Where(m => m.Exists && m.LastWriteTimeUtc.HasValue))
            {
                // A log's last write is when that component stopped doing anything — often the
                // closest thing to a time of death.
                var severity = entry.Source.HighValue ? Severity.Warning : Severity.Info;

                yield return new TimelineEntry(
                    entry.LastWriteTimeUtc.Value,
                    TimelineSource.LogActivity,
                    entry.Source.DisplayName + " last written",
                    entry.ResolvedPath + (entry.SizeKnown ? "  (" + LogSearchView.FormatSize(entry.SizeBytes) + ")" : ""),
                    severity);
            }
        }

        private static IEnumerable<TimelineEntry> FromFolders(SystemState state)
        {
            foreach (var folder in state.UpgradeFolders ?? new List<UpgradeFolderInfo>())
            {
                if (!folder.Exists || !folder.LastWriteTimeUtc.HasValue)
                {
                    continue;
                }

                yield return new TimelineEntry(
                    folder.LastWriteTimeUtc.Value,
                    TimelineSource.UpgradeFolder,
                    "Upgrade folder last changed",
                    folder.Path,
                    Severity.Info);
            }
        }

        private static IEnumerable<TimelineEntry> FromProcesses(SystemState state)
        {
            var snapshot = state.Processes;
            if (snapshot == null)
            {
                yield break;
            }

            foreach (var process in snapshot.Processes.Where(p => p.IsRunning && p.StartTimeUtc.HasValue))
            {
                yield return new TimelineEntry(
                    process.StartTimeUtc.Value,
                    TimelineSource.Process,
                    process.Name + " started and is still running",
                    "pid " + (process.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"),
                    Severity.Info);
            }
        }

        private static string Trim(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return null;
            }

            var single = message.Replace("\r", " ").Replace("\n", " ").Trim();
            while (single.Contains("  "))
            {
                single = single.Replace("  ", " ");
            }

            return single.Length > 300 ? single.Substring(0, 300) + "…" : single;
        }
    }
}
