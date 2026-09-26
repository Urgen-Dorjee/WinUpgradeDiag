using System;
using System.Collections.Generic;
using System.Linq;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Discovery;
using WinUpgradeDiag.Core.Orchestration;
using WinUpgradeDiag.Core.Rules;
using WinUpgradeDiag.Tests.Support;
using Xunit;

namespace WinUpgradeDiag.Tests.Rules
{
    public class TimelineBuilderTests
    {
        private static DateTime At(int hour, int minute) =>
            new DateTime(2026, 9, 18, hour, minute, 0, DateTimeKind.Utc);

        [Fact]
        public void Entries_from_unlike_sources_are_merged_newest_first()
        {
            using (var tmp = new TempDirectory())
            {
                var log = tmp.File("setupact.log", "x", At(14, 5));
                var source = new LogSource("s", LogSourceCategory.SetupCurrent, "setupact.log", "", log);

                var context = new DiagnosticContext
                {
                    Manifest = new LogManifestBuilder().Build(new[] { source }),
                    SystemState = new SystemState
                    {
                        Events = new List<EventRecordInfo>
                        {
                            new EventRecordInfo { LogName = "System", EventId = 41, TimeCreatedUtc = At(14, 10), Message = "Power lost" },
                            new EventRecordInfo { LogName = "System", EventId = 1074, TimeCreatedUtc = At(13, 0), Message = "Restart by user" }
                        },
                        UpgradeFolders = new List<UpgradeFolderInfo>
                        {
                            new UpgradeFolderInfo { Path = @"C:\$WINDOWS.~BT", Exists = true, LastWriteTimeUtc = At(14, 8) }
                        }
                    }
                };

                var timeline = TimelineBuilder.Build(context);

                var times = timeline.Select(e => e.TimestampUtc).ToList();
                Assert.Equal(times.OrderByDescending(t => t).ToList(), times);
                Assert.Equal(At(14, 10), times.First());

                // All four source kinds that had data are represented.
                Assert.Contains(timeline, e => e.Source == TimelineSource.WindowsEvent);
                Assert.Contains(timeline, e => e.Source == TimelineSource.LogActivity);
                Assert.Contains(timeline, e => e.Source == TimelineSource.UpgradeFolder);
            }
        }

        [Fact]
        public void An_unclean_restart_is_marked_critical_and_explained_in_plain_words()
        {
            var context = new DiagnosticContext
            {
                SystemState = new SystemState
                {
                    Events = new List<EventRecordInfo>
                    {
                        new EventRecordInfo { LogName = "System", EventId = 41, TimeCreatedUtc = At(14, 10), Message = "The system has rebooted without cleanly shutting down first." }
                    }
                }
            };

            var entry = TimelineBuilder.Build(context).Single();

            Assert.Equal(Severity.Critical, entry.Severity);
            // The label has to say what it means, not just quote the event id.
            Assert.Contains("without shutting down cleanly", entry.Label);
        }

        [Fact]
        public void A_shutdown_request_is_a_warning_because_it_names_who_ended_it()
        {
            var context = new DiagnosticContext
            {
                SystemState = new SystemState
                {
                    Events = new List<EventRecordInfo>
                    {
                        new EventRecordInfo { LogName = "System", EventId = 1074, TimeCreatedUtc = At(13, 0), Message = "Process X initiated restart" }
                    }
                }
            };

            var entry = TimelineBuilder.Build(context).Single();

            Assert.Equal(Severity.Warning, entry.Severity);
            Assert.Contains("names who", entry.Label);
        }

        [Fact]
        public void Routine_events_stay_informational_so_the_notable_ones_stand_out()
        {
            var context = new DiagnosticContext
            {
                SystemState = new SystemState
                {
                    Events = new List<EventRecordInfo>
                    {
                        new EventRecordInfo { LogName = "System", ProviderName = "Service Control Manager", EventId = 7036, TimeCreatedUtc = At(12, 0), Message = "A service entered the running state." }
                    }
                }
            };

            Assert.Equal(Severity.Info, TimelineBuilder.Build(context).Single().Severity);
        }

        [Fact]
        public void A_running_process_contributes_its_start_time()
        {
            var context = new DiagnosticContext
            {
                SystemState = new SystemState
                {
                    Processes = new ProcessSnapshot(new List<ProcessInfo>
                    {
                        new ProcessInfo("SetupHost", true, 4242, At(13, 30)),
                        new ProcessInfo("TSManager", false, null, null)
                    })
                }
            };

            var entry = TimelineBuilder.Build(context).Single();

            Assert.Equal(TimelineSource.Process, entry.Source);
            Assert.Contains("SetupHost", entry.Label);
            Assert.Contains("4242", entry.Detail);
        }

        [Fact]
        public void Undated_records_are_dropped_rather_than_shown_at_the_epoch()
        {
            var context = new DiagnosticContext
            {
                SystemState = new SystemState
                {
                    Events = new List<EventRecordInfo>
                    {
                        new EventRecordInfo { LogName = "System", EventId = 41, TimeCreatedUtc = null },
                        new EventRecordInfo { LogName = "System", EventId = 41, TimeCreatedUtc = At(14, 0) }
                    }
                }
            };

            Assert.Single(TimelineBuilder.Build(context));
        }

        [Fact]
        public void A_long_event_message_is_shortened_so_the_grid_stays_readable()
        {
            var context = new DiagnosticContext
            {
                SystemState = new SystemState
                {
                    Events = new List<EventRecordInfo>
                    {
                        new EventRecordInfo
                        {
                            LogName = "Application", EventId = 1000, TimeCreatedUtc = At(14, 0),
                            Message = new string('x', 900)
                        }
                    }
                }
            };

            var entry = TimelineBuilder.Build(context).Single();

            Assert.True(entry.Detail.Length < 400);
            Assert.EndsWith("…", entry.Detail);
        }

        [Fact]
        public void An_empty_machine_yields_an_empty_timeline_rather_than_throwing()
        {
            Assert.Empty(TimelineBuilder.Build(new DiagnosticContext()));
        }

        [Fact]
        public void A_null_context_is_rejected()
        {
            Assert.Throws<ArgumentNullException>(() => TimelineBuilder.Build(null));
        }
    }
}
