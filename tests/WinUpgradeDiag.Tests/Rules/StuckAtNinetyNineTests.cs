using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Discovery;
using WinUpgradeDiag.Core.Orchestration;
using WinUpgradeDiag.Core.Rules;
using WinUpgradeDiag.Tests.Support;
using Xunit;

namespace WinUpgradeDiag.Tests.Rules
{
    /// <summary>
    /// The "stuck at 99%" question, which is the one a technician standing in front of the machine
    /// actually asks. The advice inverts between the two answers — wait, or stop waiting and go
    /// looking — so getting the distinction wrong is worse than saying nothing.
    /// </summary>
    public class StuckAtNinetyNineTests
    {
        private static ProcessSnapshot Setup(bool running, double? cpuPercent = null, DateTime? startedUtc = null)
        {
            return new ProcessSnapshot(new List<ProcessInfo>
            {
                new ProcessInfo("TSManager", false, null, null),
                new ProcessInfo("SetupHost", running, running ? 4242 : (int?)null,
                    startedUtc ?? (running ? DateTime.UtcNow.AddHours(-2) : (DateTime?)null),
                    totalProcessorTime: running ? TimeSpan.FromMinutes(20) : (TimeSpan?)null,
                    workingSetBytes: running ? 300L * 1024 * 1024 : (long?)null,
                    instanceCount: running ? 1 : 0,
                    cpuPercent: cpuPercent)
            });
        }

        /// <summary>A manifest whose in-progress Setup log was last written <paramref name="ago"/> ago.</summary>
        private static IReadOnlyList<LogManifestEntry> SetupLogWritten(TempDirectory tmp, TimeSpan ago)
        {
            var path = tmp.File("setupact.log", "…", DateTime.UtcNow - ago);
            var source = new LogSource("cur", LogSourceCategory.SetupCurrent, "setupact.log (current attempt)", "", path);
            return new LogManifestBuilder().Build(new[] { source });
        }

        private static DiagnosticContext Context(
            ProcessSnapshot processes, IReadOnlyList<LogManifestEntry> manifest, int? progressPercent)
        {
            return new DiagnosticContext
            {
                Manifest = manifest ?? new List<LogManifestEntry>(),
                SystemState = new SystemState
                {
                    IsElevated = true,
                    Processes = processes,
                    SetupProgressPercent = progressPercent,
                    PendingReboot = new PendingRebootState(),
                    SystemDriveFreeBytes = 200L * 1024 * 1024 * 1024,
                    SystemDriveTotalBytes = 500L * 1024 * 1024 * 1024,
                    Os = new OsIdentity { ProductName = "Windows 10 Enterprise", CurrentBuildNumber = "19045" }
                }
            };
        }

        [Fact]
        public void A_recently_written_log_means_the_upgrade_is_working_even_at_99_percent()
        {
            using (var tmp = new TempDirectory())
            {
                var context = Context(Setup(running: true), SetupLogWritten(tmp, TimeSpan.FromMinutes(1)), 99);

                var verdict = new RuleEngine().Evaluate(context);

                Assert.Equal(VerdictKind.InProgress, verdict.Kind);
                var finding = verdict.Findings.Single(f => f.Id == "SU-001");
                Assert.Equal(Severity.Info, finding.Severity);
                Assert.Equal(Confidence.High, finding.Confidence);
                Assert.Contains("still working", finding.Title);
                Assert.Contains("Leave it alone", finding.Action);
            }
        }

        [Fact]
        public void Cpu_activity_alone_is_enough_to_call_it_working_when_the_log_is_quiet()
        {
            using (var tmp = new TempDirectory())
            {
                // Log quiet for 45 minutes, but SetupHost is clearly burning CPU: applying an image
                // is a single long operation that produces no log output while it runs.
                var context = Context(Setup(running: true, cpuPercent: 40.0),
                                      SetupLogWritten(tmp, TimeSpan.FromMinutes(45)), 99);

                var verdict = new RuleEngine().Evaluate(context);

                Assert.Contains(verdict.Findings, f => f.Id == "SU-001");
                Assert.DoesNotContain(verdict.Findings, f => f.Id == "SU-002");
                Assert.Equal(VerdictKind.InProgress, verdict.Kind);
            }
        }

        [Fact]
        public void A_long_silent_log_with_an_idle_process_is_called_blocked()
        {
            using (var tmp = new TempDirectory())
            {
                var context = Context(Setup(running: true, cpuPercent: 0.1),
                                      SetupLogWritten(tmp, TimeSpan.FromMinutes(90)), 99);

                var verdict = new RuleEngine().Evaluate(context);

                var finding = verdict.Findings.Single(f => f.Id == "SU-002");
                Assert.Equal(Severity.Critical, finding.Severity);
                Assert.Equal(Confidence.Medium, finding.Confidence);
                Assert.Contains("blocked", finding.Title);

                // The verdict must stop saying "wait" and start saying "go and look".
                Assert.Equal(VerdictKind.CauseIdentified, verdict.Kind);
                Assert.Contains("Do not restart yet", finding.Action);
            }
        }

        [Fact]
        public void Without_a_cpu_sample_a_silent_log_is_only_low_confidence()
        {
            using (var tmp = new TempDirectory())
            {
                // No CPU measurement: silence alone is weaker evidence, and must be reported as such.
                var context = Context(Setup(running: true, cpuPercent: null),
                                      SetupLogWritten(tmp, TimeSpan.FromMinutes(90)), 99);

                var verdict = new RuleEngine().Evaluate(context);

                Assert.Equal(Confidence.Low, verdict.Findings.Single(f => f.Id == "SU-002").Confidence);
            }
        }

        [Fact]
        public void A_few_quiet_minutes_is_normal_and_says_so()
        {
            using (var tmp = new TempDirectory())
            {
                var context = Context(Setup(running: true, cpuPercent: 0.2),
                                      SetupLogWritten(tmp, TimeSpan.FromMinutes(12)), 99);

                var verdict = new RuleEngine().Evaluate(context);

                var finding = verdict.Findings.Single(f => f.Id == "SU-001");
                Assert.Equal(Severity.Info, finding.Severity);
                Assert.Equal(Confidence.Medium, finding.Confidence);
                Assert.Contains("re-run this diagnostic in ten minutes", finding.Action, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void A_percentage_with_no_setup_running_is_a_stale_dialog_not_progress()
        {
            var context = Context(Setup(running: false), null, 99);

            var verdict = new RuleEngine().Evaluate(context);

            var finding = verdict.Findings.Single(f => f.Id == "SU-003");
            Assert.Equal(Severity.Critical, finding.Severity);
            Assert.Equal(Confidence.High, finding.Confidence);
            Assert.Contains("stale", finding.Meaning);
            Assert.Contains("Do not keep waiting", finding.Action);
            Assert.Contains(finding.Evidence, e => e.Text.Contains("99%"));
        }

        [Fact]
        public void No_setup_and_no_reported_percentage_produces_no_progress_finding()
        {
            var context = Context(Setup(running: false), null, null);

            var verdict = new RuleEngine().Evaluate(context);

            Assert.DoesNotContain(verdict.Findings, f => f.Id.StartsWith("SU-00", StringComparison.Ordinal)
                                                        && f.Id != "SU-006");
        }

        [Fact]
        public void Setup_running_with_no_readable_log_admits_it_cannot_tell()
        {
            var context = Context(Setup(running: true), null, 99);

            var verdict = new RuleEngine().Evaluate(context);

            var finding = verdict.Findings.Single(f => f.Id == "SU-002");
            Assert.Equal(Confidence.Low, finding.Confidence);
            Assert.Contains("could not be read", finding.Title);
            Assert.Contains("elevated", finding.Action);
        }

        [Fact]
        public void The_running_upgrade_still_suppresses_cleanup_advice()
        {
            using (var tmp = new TempDirectory())
            {
                var context = Context(Setup(running: true), SetupLogWritten(tmp, TimeSpan.FromMinutes(1)), 99);
                context.SystemState.TaskSequenceExecutionRequest =
                    OrphanedTaskSequenceInfo.Found("ABC00123", "ABC20001");

                var verdict = new RuleEngine().Evaluate(context);

                // A live upgrade must never be reported as an orphaned task sequence.
                Assert.DoesNotContain(verdict.Findings, f => f.Id == "TS-001");
                Assert.Equal(VerdictKind.InProgress, verdict.Kind);
            }
        }

        [Theory]
        [InlineData(0, "less than a minute")]
        [InlineData(1, "1 minute")]
        [InlineData(45, "45 minutes")]
        [InlineData(60, "1 hour")]
        [InlineData(155, "2 hours 35 minutes")]
        public void Durations_read_the_way_a_person_would_say_them(int minutes, string expected)
        {
            Assert.Equal(expected, ProcessInfo.Describe(TimeSpan.FromMinutes(minutes)));
        }
    }
}
