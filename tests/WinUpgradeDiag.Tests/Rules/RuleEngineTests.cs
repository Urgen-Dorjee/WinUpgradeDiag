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
    public class RuleEngineTests
    {
        private static DiagnosticContext Context(
            SystemState state = null, IReadOnlyList<LogManifestEntry> manifest = null)
        {
            return new DiagnosticContext
            {
                ToolVersion = "test",
                StartedAtUtc = new DateTime(2026, 9, 18, 14, 0, 0, DateTimeKind.Utc),
                Manifest = manifest ?? new List<LogManifestEntry>(),
                SystemState = state ?? Healthy()
            };
        }

        /// <summary>A machine with nothing wrong: elevated, plenty of space, healthy disk.</summary>
        private static SystemState Healthy()
        {
            return new SystemState
            {
                MachineName = "WS-TEST-0042",
                IsElevated = true,
                Os = new OsIdentity { ProductName = "Windows 10 Pro", CurrentBuildNumber = "26200" },
                SystemDriveFreeBytes = 200L * 1024 * 1024 * 1024,
                SystemDriveTotalBytes = 500L * 1024 * 1024 * 1024,
                PendingReboot = new PendingRebootState(),
                Processes = new ProcessSnapshot(new List<ProcessInfo>
                {
                    new ProcessInfo("TSManager", false, null, null),
                    new ProcessInfo("SetupHost", false, null, null)
                }),
                StorageHealth = new List<StorageHealthInfo>
                {
                    new StorageHealthInfo { FriendlyName = "NVMe disk", HealthStatus = "Healthy" }
                }
            };
        }

        private static ProcessSnapshot Running(params string[] names)
        {
            var all = new[] { "TSManager", "SetupHost", "setupprep", "CcmExec" };
            return new ProcessSnapshot(all
                .Select(n => new ProcessInfo(n, names.Contains(n, StringComparer.OrdinalIgnoreCase), 1234, null))
                .ToList());
        }

        // ---------------------------------------------------------------- the safety rule

        [Fact]
        public void A_live_upgrade_outranks_everything_and_never_advises_cleanup()
        {
            var state = Healthy();
            state.Processes = Running("TSManager", "SetupHost");
            // Also leave a stale-looking WMI lock; the live engine must still win.
            state.TaskSequenceExecutionRequest = OrphanedTaskSequenceInfo.Found("ABC00123", "ABC20001");

            var verdict = new RuleEngine().Evaluate(Context(state));

            Assert.Equal(VerdictKind.InProgress, verdict.Kind);
            Assert.Equal("TS-002", verdict.TopFinding.Id);
            // The orphaned-task-sequence rule must NOT fire while the engine is alive.
            Assert.DoesNotContain(verdict.Findings, f => f.Id == "TS-001");
            Assert.Contains("Do not clear", verdict.TopFinding.Action, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void An_orphaned_task_sequence_needs_both_a_wmi_lock_and_a_dead_engine()
        {
            var state = Healthy();
            state.Processes = Running(); // nothing alive
            state.TaskSequenceExecutionRequest = OrphanedTaskSequenceInfo.Found("ABC00123", "ABC20001");

            var verdict = new RuleEngine().Evaluate(Context(state));

            var finding = verdict.Findings.Single(f => f.Id == "TS-001");
            Assert.Equal(Severity.Critical, finding.Severity);
            Assert.Equal(Confidence.High, finding.Confidence);
            Assert.True(finding.HasCommand);
            Assert.Contains(finding.Evidence, e => e.Text.Contains("ABC00123"));
        }

        [Fact]
        public void A_wmi_lock_alone_with_no_evidence_of_a_dead_engine_is_not_called_orphaned()
        {
            var state = Healthy();
            state.Processes = null; // process state unknown
            state.TaskSequenceExecutionRequest = OrphanedTaskSequenceInfo.Found("ABC00123", null);

            var verdict = new RuleEngine().Evaluate(Context(state));

            Assert.DoesNotContain(verdict.Findings, f => f.Id == "TS-001");
        }

        // ---------------------------------------------------------------- hardware precedence

        [Fact]
        public void Failing_hardware_outranks_software_findings()
        {
            var state = Healthy();
            state.Processes = Running();
            state.TaskSequenceExecutionRequest = OrphanedTaskSequenceInfo.Found("ABC00123", null);
            state.StorageHealth = new List<StorageHealthInfo>
            {
                new StorageHealthInfo { FriendlyName = "Failing SSD", HealthStatus = "Unhealthy" }
            };

            var verdict = new RuleEngine().Evaluate(Context(state));

            // RULES.md precedence: hardware first, because everything downstream is a symptom.
            Assert.StartsWith("HW-", verdict.TopFinding.Id, StringComparison.Ordinal);
            Assert.Equal(VerdictKind.CauseIdentified, verdict.Kind);
        }

        [Fact]
        public void Uncorrected_media_errors_are_reported_as_failing_hardware()
        {
            var state = Healthy();
            state.StorageHealth = new List<StorageHealthInfo>
            {
                new StorageHealthInfo
                {
                    FriendlyName = "SSD", HealthStatus = "Healthy",
                    ReadErrorsUncorrected = 4, WriteErrorsUncorrected = 0
                }
            };

            var verdict = new RuleEngine().Evaluate(Context(state));

            var finding = verdict.Findings.Single(f => f.Id == "HW-002");
            Assert.Equal(Severity.Critical, finding.Severity);
            Assert.Contains(finding.Evidence, e => e.Text.Contains("ReadErrorsUncorrected = 4"));
        }

        [Fact]
        public void High_wear_is_a_warning_not_a_verdict()
        {
            var state = Healthy();
            state.StorageHealth = new List<StorageHealthInfo>
            {
                new StorageHealthInfo { FriendlyName = "SSD", HealthStatus = "Healthy", Wear = 95 }
            };

            var verdict = new RuleEngine().Evaluate(Context(state));

            Assert.Equal(Severity.Warning, verdict.Findings.Single(f => f.Id == "HW-003").Severity);
            Assert.NotEqual(VerdictKind.CauseIdentified, verdict.Kind);
        }

        // ---------------------------------------------------------------- rollback and space

        [Fact]
        public void Rollback_logs_on_disk_produce_a_critical_finding()
        {
            using (var tmp = new TempDirectory())
            {
                var path = tmp.File(Path.Combine("Rollback", "setupact.log"), "reverting\n");
                var source = new LogSource("rb", LogSourceCategory.SetupRollback, "setupact.log (rollback)", "", path, highValue: true);
                var manifest = new LogManifestBuilder().Build(new[] { source });

                var verdict = new RuleEngine().Evaluate(Context(Healthy(), manifest));

                var finding = verdict.Findings.Single(f => f.Id == "RB-001");
                Assert.Equal(Severity.Critical, finding.Severity);
                Assert.Equal(VerdictKind.CauseIdentified, verdict.Kind);
                Assert.True(finding.HasEvidence);
            }
        }

        [Fact]
        public void Low_disk_space_is_called_out_as_a_cause_in_its_own_right()
        {
            var state = Healthy();
            state.SystemDriveFreeBytes = 3L * 1024 * 1024 * 1024;

            var verdict = new RuleEngine().Evaluate(Context(state));

            var finding = verdict.Findings.Single(f => f.Id == "SU-006");
            Assert.Equal(Severity.Critical, finding.Severity);
            Assert.Contains("3.0 GB", finding.Meaning);
        }

        [Fact]
        public void Ample_disk_space_produces_no_finding()
        {
            var verdict = new RuleEngine().Evaluate(Context(Healthy()));

            Assert.DoesNotContain(verdict.Findings, f => f.Id == "SU-006");
        }

        // ---------------------------------------------------------------- honest non-answers

        [Fact]
        public void A_clean_elevated_machine_says_so_plainly_instead_of_inventing_a_cause()
        {
            var verdict = new RuleEngine().Evaluate(Context(Healthy()));

            Assert.Equal(VerdictKind.NoFailureFound, verdict.Kind);
            Assert.Equal(0, verdict.CriticalCount);
            // The machine in Healthy() has build 26200, so the headline should name Windows 11.
            Assert.Contains("Windows 11", verdict.Headline);
        }

        [Fact]
        public void An_unelevated_run_with_nothing_readable_refuses_to_conclude()
        {
            var state = Healthy();
            state.IsElevated = false;

            var verdict = new RuleEngine().Evaluate(Context(state));

            Assert.Equal(VerdictKind.InsufficientEvidence, verdict.Kind);
            Assert.Contains(verdict.Gaps, g => g.IndexOf("administrator", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void Unreadable_protected_logs_are_reported_as_a_gap_not_ignored()
        {
            var state = Healthy();
            var source = new LogSource("rb", LogSourceCategory.SetupRollback, "setupact.log (rollback)", "",
                @"C:\$WINDOWS.~BT\Sources\Rollback\setupact.log", highValue: true);
            var manifest = new List<LogManifestEntry>
            {
                LogManifestEntry.PresentButUnreadable(source, source.Path, "Access denied")
            };

            var verdict = new RuleEngine().Evaluate(Context(state, manifest));

            Assert.Contains(verdict.Gaps, g => g.Contains("could not be read"));
        }

        // ---------------------------------------------------------------- evidence discipline

        [Fact]
        public void Every_finding_carries_quotable_evidence()
        {
            var state = Healthy();
            state.Processes = Running();
            state.TaskSequenceExecutionRequest = OrphanedTaskSequenceInfo.Found("ABC00123", null);
            state.SystemDriveFreeBytes = 1L * 1024 * 1024 * 1024;
            state.PendingReboot = new PendingRebootState { WindowsUpdate = true };
            state.MemoryIntegrityEnabled = true;
            state.StorageHealth = new List<StorageHealthInfo>
            {
                new StorageHealthInfo { FriendlyName = "SSD", HealthStatus = "Warning", Wear = 92 }
            };

            var verdict = new RuleEngine().Evaluate(Context(state));

            Assert.NotEmpty(verdict.Findings);
            // AGENTS.md: "a verdict with no quotable evidence is a bug".
            Assert.All(verdict.Findings, f =>
            {
                Assert.True(f.HasEvidence, f.Id + " has no evidence");
                Assert.All(f.Evidence, e => Assert.False(string.IsNullOrWhiteSpace(e.Text)));
                Assert.False(string.IsNullOrWhiteSpace(f.Meaning), f.Id + " has no meaning");
                Assert.False(string.IsNullOrWhiteSpace(f.Action), f.Id + " has no action");
            });
        }

        [Fact]
        public void Findings_are_ranked_most_severe_first()
        {
            var state = Healthy();
            state.SystemDriveFreeBytes = 1L * 1024 * 1024 * 1024; // critical
            state.PendingReboot = new PendingRebootState { WindowsUpdate = true }; // warning
            state.MemoryIntegrityEnabled = true; // info

            var verdict = new RuleEngine().Evaluate(Context(state));

            var severities = verdict.Findings.Select(f => (int)f.Severity).ToList();
            Assert.Equal(severities.OrderByDescending(s => s).ToList(), severities);
        }

        [Fact]
        public void A_null_context_is_rejected_rather_than_producing_an_empty_verdict()
        {
            Assert.Throws<ArgumentNullException>(() => new RuleEngine().Evaluate(null));
        }
    }
}
