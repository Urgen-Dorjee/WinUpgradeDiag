using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;

namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>
    /// Pulls the event IDs DESIGN.md §4.1 names — System 41, 1001, 1074, 6008 and Service
    /// Control Manager; Application 1000, 1001 — for a bounded look-back window.
    /// </summary>
    public sealed class EventLogCollector
    {
        public const int DefaultLookbackDays = 14;
        public const int DefaultMaxEventsPerQuery = 500;

        private static readonly string[] SystemQueries =
        {
            "*[System[(EventID=41 or EventID=1001 or EventID=1074 or EventID=6008)]]",
            "*[System[Provider[@Name='Service Control Manager'] and (EventID=7031 or EventID=7034 or EventID=7036)]]"
        };

        private static readonly string[] ApplicationQueries =
        {
            "*[System[(EventID=1000 or EventID=1001)]]"
        };

        public EventLogCollectionResult Collect(int lookbackDays = DefaultLookbackDays, int maxPerQuery = DefaultMaxEventsPerQuery)
        {
            var result = new EventLogCollectionResult();
            var since = DateTime.UtcNow.AddDays(-lookbackDays);

            foreach (var q in SystemQueries)
            {
                ReadInto(result, "System", q, since, maxPerQuery);
            }
            foreach (var q in ApplicationQueries)
            {
                ReadInto(result, "Application", q, since, maxPerQuery);
            }

            result.Events.Sort((a, b) => Nullable.Compare(a.TimeCreatedUtc, b.TimeCreatedUtc));
            return result;
        }

        private static void ReadInto(EventLogCollectionResult result, string logName, string xpath, DateTime sinceUtc, int max)
        {
            try
            {
                var query = new EventLogQuery(logName, PathType.LogName, xpath) { ReverseDirection = true };
                using (var reader = new EventLogReader(query))
                {
                    int count = 0;
                    EventRecord record;
                    while (count < max && (record = reader.ReadEvent()) != null)
                    {
                        using (record)
                        {
                            var created = record.TimeCreated?.ToUniversalTime();
                            if (created.HasValue && created.Value < sinceUtc)
                            {
                                break; // reading newest-first; everything after is older
                            }

                            result.Events.Add(new EventRecordInfo
                            {
                                LogName = logName,
                                ProviderName = record.ProviderName,
                                EventId = record.Id,
                                TimeCreatedUtc = created,
                                Level = SafeLevel(record),
                                Message = SafeMessage(record)
                            });
                            count++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add(logName + " (" + xpath + "): " + ex.Message);
            }
        }

        private static string SafeLevel(EventRecord record)
        {
            try { return record.LevelDisplayName; } catch (Exception) { return record.Level?.ToString(); }
        }

        private static string SafeMessage(EventRecord record)
        {
            try { return record.FormatDescription(); } catch (Exception) { return null; }
        }
    }

    public sealed class EventLogCollectionResult
    {
        public List<EventRecordInfo> Events { get; } = new List<EventRecordInfo>();
        public List<string> Errors { get; } = new List<string>();
    }
}
