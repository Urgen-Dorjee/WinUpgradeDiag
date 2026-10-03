using System;
using System.Collections.Generic;
using System.Linq;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Orchestration;
using WinUpgradeDiag.Core.Remediation;
using WinUpgradeDiag.Core.Rules;
using Xunit;

namespace WinUpgradeDiag.Tests.Remediation
{
    /// <summary>
    /// The Tools tab listed twelve options flat, six of them beginning "Fix:" and separated only by
    /// a parenthetical — "interrupted during Setup (keeps the download)" against "interrupted while
    /// downloading (discards the download)". Picking between those is the diagnosis, which is the
    /// one thing a technician arrives without, and the run has already done it.
    /// </summary>
    public class ToolRelevanceTests
    {
        private static ToolDefinition Tool(string id)
        {
            return ToolCatalog.All.Single(t => t.Id == id);
        }

        private static DiagnosticContext ContextWith(SystemState state, Verdict verdict = null)
        {
            return new DiagnosticContext
            {
                ToolVersion = "0.1.0-test",
                StartedAtUtc = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc),
                Manifest = new List<WinUpgradeDiag.Core.Discovery.LogManifestEntry>(),
                SystemState = state,
                Verdict = verdict
            };
        }

        private static UpgradeFolderInfo Folder(string path, bool exists)
        {
            return new UpgradeFolderInfo { Path = path, Exists = exists };
        }

        // ---------------------------------------------------------------- ruled out

        [Fact]
        public void Reclaiming_space_is_ruled_out_when_there_is_nothing_to_reclaim()
        {
            var state = new SystemState
            {
                UpgradeFolders = new[] { Folder(@"C:\Windows.old", false) }
            };

            var standing = ToolRelevance.For(Tool("REMOVE-LEFTOVERS"), ContextWith(state));

            Assert.Equal(ToolStanding.NotApplicable, standing.Standing);
            Assert.Contains("no C:\\Windows.old", standing.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void Discarding_a_download_is_ruled_out_when_the_cache_holds_no_image()
        {
            var state = new SystemState
            {
                CcmCache = new CcmCacheSnapshot
                {
                    Elements = new CcmCacheElement[0]
                }
            };

            var standing = ToolRelevance.For(Tool("FIX-A"), ContextWith(state));

            Assert.Equal(ToolStanding.NotApplicable, standing.Standing);
            Assert.Contains("no partial download", standing.Reason, StringComparison.Ordinal);
        }

        /// <summary>
        /// Repairing a client that answers queries restarts it and achieves nothing, which is worse
        /// than useless on a machine somebody is waiting to use.
        /// </summary>
        [Fact]
        public void Repairing_a_healthy_client_is_ruled_out_and_says_why()
        {
            var state = new SystemState
            {
                CcmClient = new CcmClientHealthInfo
                {
                    ClientFolderExists = true,
                    CcmNamespaceResponds = true,
                    SiteCode = "JMH"
                }
            };

            var standing = ToolRelevance.For(Tool("REPAIR-CLIENT"), ContextWith(state));

            Assert.Equal(ToolStanding.NotApplicable, standing.Standing);
            Assert.Contains("JMH", standing.Reason, StringComparison.Ordinal);
            Assert.Contains("healthy", standing.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void Rebuilding_is_ruled_out_when_no_client_is_installed()
        {
            var state = new SystemState
            {
                CcmClient = new CcmClientHealthInfo { ClientFolderExists = false }
            };

            var standing = ToolRelevance.For(Tool("REBUILD-CLIENT"), ContextWith(state));

            Assert.Equal(ToolStanding.NotApplicable, standing.Standing);
            Assert.Contains("not installed", standing.Reason, StringComparison.Ordinal);
        }

        // ---------------------------------------------------------------- recommended

        /// <summary>
        /// Driven through the real engine rather than by hand-attaching a remediation: the
        /// attachment happens inside Evaluate, so going round it would test a path that does not
        /// exist in the application.
        /// </summary>
        [Fact]
        public void The_tool_a_finding_prescribes_is_recommended_and_sorts_first()
        {
            var state = new SystemState
            {
                IsElevated = true,
                // TS-001 is "the engine is gone but its execution request survives", so the
                // process snapshot showing TSManager stopped is half the rule.
                Processes = new ProcessSnapshot(new List<ProcessInfo>
                {
                    new ProcessInfo("TSManager", false, null, null),
                    new ProcessInfo("SetupHost", false, null, null)
                }),
                TaskSequenceExecutionRequest = OrphanedTaskSequenceInfo.Found("JMH00456", "JMH20001"),
                UpgradeFolders = new[] { Folder(@"C:\$WINDOWS.~BT", true) },
                PendingReboot = new PendingRebootState(),
                SystemDriveFreeBytes = 200L * 1024 * 1024 * 1024
            };

            var context = ContextWith(state);
            context.Verdict = new RuleEngine().Evaluate(context);

            var prescribed = context.Verdict.Findings
                .Where(f => f.Remediation != null)
                .Select(f => f.Remediation.ScriptName)
                .FirstOrDefault();

            Assert.True(prescribed != null,
                "The engine attached no remediation, so there is nothing for the Tools tab to point at.");

            var tool = ToolCatalog.All.Single(t =>
                string.Equals(t.ScriptName, prescribed, StringComparison.OrdinalIgnoreCase));

            var standing = ToolRelevance.For(tool, context);
            Assert.Equal(ToolStanding.Recommended, standing.Standing);
            Assert.Contains("The diagnosis points here", standing.Reason, StringComparison.Ordinal);

            // And it leads the list, which is the whole point.
            var ordered = ToolRelevance.Order(ToolCatalog.All, context);
            Assert.Equal(tool.Id, ordered[0].Id);
        }

        // ---------------------------------------------------------------- honesty

        /// <summary>
        /// Before any evidence exists, everything is simply available. Guessing a recommendation
        /// from nothing would be the same failure as inventing a content id.
        /// </summary>
        [Fact]
        public void With_no_diagnostic_run_nothing_is_recommended_or_ruled_out()
        {
            foreach (var tool in ToolCatalog.All)
            {
                var standing = ToolRelevance.For(tool, null);

                Assert.Equal(ToolStanding.Available, standing.Standing);
                Assert.Null(standing.Reason);
            }
        }

        /// <summary>
        /// A folder nobody looked at is not evidence of absence. Ruling a tool out on an
        /// unexamined folder would tell a technician something untrue about their machine.
        /// </summary>
        [Fact]
        public void An_unexamined_folder_does_not_rule_anything_out()
        {
            var state = new SystemState { UpgradeFolders = new UpgradeFolderInfo[0] };

            Assert.Equal(ToolStanding.Available, ToolRelevance.For(Tool("REMOVE-LEFTOVERS"), ContextWith(state)).Standing);
            Assert.Equal(ToolStanding.Available, ToolRelevance.For(Tool("FIX-D"), ContextWith(state)).Standing);
        }

        [Fact]
        public void An_unreadable_cache_does_not_rule_out_the_download_fix()
        {
            var state = new SystemState
            {
                CcmCache = new CcmCacheSnapshot { Error = "The client cache could not be read." }
            };

            Assert.Equal(ToolStanding.Available, ToolRelevance.For(Tool("FIX-A"), ContextWith(state)).Standing);
        }

        /// <summary>
        /// Nothing is removed from the list. A tool ruled out keeps its place with the reason,
        /// because the reason is itself a finding, and because a technician who disagrees with the
        /// reasoning must still be able to act.
        /// </summary>
        [Fact]
        public void Ordering_hides_nothing()
        {
            var state = new SystemState
            {
                UpgradeFolders = new[] { Folder(@"C:\Windows.old", false), Folder(@"C:\$WINDOWS.~BT", false) },
                CcmClient = new CcmClientHealthInfo { ClientFolderExists = false }
            };

            var ordered = ToolRelevance.Order(ToolCatalog.All, ContextWith(state));

            Assert.Equal(ToolCatalog.All.Count, ordered.Count);
            Assert.Equal(
                ToolCatalog.All.Select(t => t.Id).OrderBy(id => id, StringComparer.Ordinal),
                ordered.Select(t => t.Id).OrderBy(id => id, StringComparer.Ordinal));
        }

        [Fact]
        public void Tools_that_cannot_apply_sort_to_the_bottom()
        {
            var state = new SystemState
            {
                UpgradeFolders = new[] { Folder(@"C:\Windows.old", false) }
            };

            var ordered = ToolRelevance.Order(ToolCatalog.All, ContextWith(state));
            var context = ContextWith(state);

            var lastApplicable = ordered
                .Select((t, i) => new { t, i })
                .Where(x => !ToolRelevance.For(x.t, context).IsNotApplicable)
                .Max(x => x.i);

            var firstRuledOut = ordered
                .Select((t, i) => new { t, i })
                .Where(x => ToolRelevance.For(x.t, context).IsNotApplicable)
                .Min(x => x.i);

            Assert.True(firstRuledOut > lastApplicable,
                "A tool that cannot apply appeared above one that can.");
        }
    }
}
