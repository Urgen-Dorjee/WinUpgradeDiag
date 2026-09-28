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
    /// The same hexadecimal code means different things in different logs. Matching every signature
    /// against every log produced confident ConfigMgr findings on machines with no ConfigMgr —
    /// a "task sequence step failed" verdict from a Windows Setup log on an unmanaged machine.
    /// </summary>
    public class SignatureScopingTests
    {
        private static DiagnosticContext ContextWithLog(
            TempDirectory tmp, LogSourceCategory category, string content)
        {
            var path = tmp.File("scoped-" + category + ".log", content);
            var source = new LogSource(category.ToString(), category, category + " log", "", path);

            return new DiagnosticContext
            {
                Manifest = new LogManifestBuilder().Build(new[] { source }),
                SystemState = new SystemState
                {
                    IsElevated = true,
                    PendingReboot = new PendingRebootState(),
                    SystemDriveFreeBytes = 200L * 1024 * 1024 * 1024,
                    Processes = new ProcessSnapshot(new List<ProcessInfo>
                    {
                        new ProcessInfo("TSManager", false, null, null),
                        new ProcessInfo("SetupHost", false, null, null)
                    }),
                    Os = new OsIdentity { ProductName = "Windows 10 Enterprise", CurrentBuildNumber = "19045" }
                }
            };
        }

        [Fact]
        public void A_generic_error_in_a_setup_log_is_not_reported_as_a_task_sequence_failure()
        {
            using (var tmp = new TempDirectory())
            {
                // 0x80004005 means "unspecified error" across the whole of Windows. In a Setup log
                // it is background noise; only smsts.log makes it a failed task sequence step.
                var context = ContextWithLog(tmp, LogSourceCategory.SetupCompleted,
                    "2026-09-18 12:10:17, Error  SP  Operation failed: 0x80004005\r\n");

                var verdict = new RuleEngine().Evaluate(context);

                Assert.DoesNotContain(verdict.Findings, f => f.Id == "TS-004");
            }
        }

        [Fact]
        public void The_same_code_in_a_task_sequence_log_is_reported()
        {
            using (var tmp = new TempDirectory())
            {
                var context = ContextWithLog(tmp, LogSourceCategory.TaskSequence,
                    "<![LOG[Step failed 0x80004005]LOG]!><time=\"12:10:17\" component=\"TSManager\">\r\n");

                var verdict = new RuleEngine().Evaluate(context);

                Assert.Contains(verdict.Findings, f => f.Id == "TS-004");
            }
        }

        [Fact]
        public void A_file_not_found_code_in_a_setup_log_is_not_a_configmgr_cache_problem()
        {
            using (var tmp = new TempDirectory())
            {
                // This is the exact false positive seen in the field: a ConfigMgr content finding,
                // with a destructive cache-clearing fix attached, on a machine with no client.
                var context = ContextWithLog(tmp, LogSourceCategory.SetupCompleted,
                    "2026-09-18 12:10:17, Error  MIG  Could not open file: 0x80070002\r\n");

                var verdict = new RuleEngine().Evaluate(context);

                Assert.DoesNotContain(verdict.Findings, f => f.Id == "CT-002");
            }
        }

        [Fact]
        public void A_servicing_code_is_still_found_in_a_servicing_log()
        {
            using (var tmp = new TempDirectory())
            {
                var context = ContextWithLog(tmp, LogSourceCategory.Servicing,
                    "2026-09-18 12:10:17, Error CBS Failed to resolve package: 0x800F0922\r\n");

                var verdict = new RuleEngine().Evaluate(context);

                Assert.Contains(verdict.Findings, f => f.Id == "CB-001");
            }
        }

        [Fact]
        public void A_rollback_code_is_still_found_in_a_setup_log()
        {
            using (var tmp = new TempDirectory())
            {
                var context = ContextWithLog(tmp, LogSourceCategory.SetupRollback,
                    "2026-09-18 12:10:17, Error  MOUPG  Operation failed: 0xC1900101-0x20017\r\n");

                var verdict = new RuleEngine().Evaluate(context);

                Assert.Contains(verdict.Findings, f => f.Id == "RB-002");
            }
        }

        [Fact]
        public void A_servicing_log_is_never_scanned_for_task_sequence_codes()
        {
            using (var tmp = new TempDirectory())
            {
                var context = ContextWithLog(tmp, LogSourceCategory.Servicing,
                    "0x80004005 0x80070002 DRIVER_PNP_WATCHDOG 0xC1900101\r\n");

                var verdict = new RuleEngine().Evaluate(context);

                // Only the servicing signature may match here, whatever else is in the file.
                Assert.DoesNotContain(verdict.Findings, f => f.Id == "TS-004");
                Assert.DoesNotContain(verdict.Findings, f => f.Id == "CT-002");
                Assert.DoesNotContain(verdict.Findings, f => f.Id == "BC-001");
                Assert.DoesNotContain(verdict.Findings, f => f.Id == "RB-002");
            }
        }

        // ---------------------------------------------------------------- the catalogue contract

        [Fact]
        public void Every_signature_declares_where_it_applies()
        {
            var unscoped = ErrorSignatureCatalog.All
                .Where(s => s.AppliesTo.Count == 0)
                .Select(s => s.RuleId)
                .ToList();

            // An unscoped signature matches everywhere, which is how the false positives happened.
            Assert.True(unscoped.Count == 0, "unscoped: " + string.Join(", ", unscoped));
        }

        [Fact]
        public void Configmgr_signatures_are_confined_to_configmgr_logs()
        {
            foreach (var id in new[] { "CT-002", "TS-004" })
            {
                var signature = ErrorSignatureCatalog.All.Single(s => s.RuleId == id);

                Assert.False(signature.Covers(LogSourceCategory.SetupCurrent), id);
                Assert.False(signature.Covers(LogSourceCategory.SetupCompleted), id);
                Assert.False(signature.Covers(LogSourceCategory.Servicing), id);
                Assert.True(signature.Covers(LogSourceCategory.TaskSequence), id);
            }
        }

        [Fact]
        public void Setup_signatures_are_confined_to_setup_logs()
        {
            foreach (var id in new[] { "SU-004", "SU-005", "RB-002" })
            {
                var signature = ErrorSignatureCatalog.All.Single(s => s.RuleId == id);

                Assert.True(signature.Covers(LogSourceCategory.SetupRollback), id);
                Assert.False(signature.Covers(LogSourceCategory.Servicing), id);
                Assert.False(signature.Covers(LogSourceCategory.ClientOther), id);
            }
        }
    }
}
