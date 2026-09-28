using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Discovery;
using WinUpgradeDiag.Core.Orchestration;
using Xunit;

namespace WinUpgradeDiag.Tests.Orchestration
{
    /// <summary>
    /// The run must always come back with either a verdict or a stated reason there is none.
    /// A blank "did not complete" tells a technician nothing and hides the evidence that was
    /// collected before the failure.
    /// </summary>
    public class DiagnosticRunnerTests
    {
        [Fact]
        public void A_normal_run_produces_a_verdict()
        {
            var context = new DiagnosticRunner(() => new List<LogSource>()).Run();

            Assert.NotNull(context.Verdict);
            Assert.Null(context.VerdictFailure);
            Assert.Null(context.CollectionFailure);
            Assert.False(context.Cancelled);
        }

        [Fact]
        public void A_run_always_explains_itself_one_way_or_the_other()
        {
            var context = new DiagnosticRunner(() => new List<LogSource>()).Run();

            var explained = context.Verdict != null
                            || context.Cancelled
                            || context.VerdictFailure != null
                            || context.CollectionFailure != null;

            Assert.True(explained, "the run produced neither a verdict nor a reason for not having one");
        }

        [Fact]
        public void A_source_provider_that_throws_does_not_lose_the_whole_run()
        {
            var context = new DiagnosticRunner(() => throw new InvalidOperationException("catalogue exploded")).Run();

            // Discovery failed, but the run still returned and said why.
            Assert.Contains("catalogue exploded", context.CollectionFailure);
            Assert.NotEqual(default(DateTime), context.FinishedAtUtc);
        }

        [Fact]
        public void Cancelling_before_the_run_starts_is_reported_as_cancelled_not_as_a_failure()
        {
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();

                var context = new DiagnosticRunner(() => new List<LogSource>()).Run(null, cts.Token);

                Assert.True(context.Cancelled);
                Assert.Null(context.VerdictFailure);
            }
        }

        [Fact]
        public void A_cancelled_run_keeps_whatever_it_had_already_collected()
        {
            // Cancel once discovery has been asked for, so collection is under way.
            using (var cts = new CancellationTokenSource())
            {
                var runner = new DiagnosticRunner(() =>
                {
                    cts.Cancel();
                    return new List<LogSource>();
                });

                var context = runner.Run(null, cts.Token);

                Assert.True(context.Cancelled);
                // The point of the separation: the run object still exists and is inspectable.
                Assert.NotEqual(default(DateTime), context.StartedAtUtc);
                Assert.NotEqual(default(DateTime), context.FinishedAtUtc);
            }
        }

        [Fact]
        public void The_finish_time_is_always_recorded_even_on_a_bad_run()
        {
            var context = new DiagnosticRunner(() => throw new Exception("boom")).Run();

            Assert.True(context.FinishedAtUtc >= context.StartedAtUtc);
        }

        [Fact]
        public void Partial_results_are_flagged_so_the_ui_can_offer_them()
        {
            var context = new DiagnosticRunner(() => new List<LogSource>()).Run();

            // A real machine always yields at least system state.
            Assert.True(context.HasPartialResults);
        }

        [Fact]
        public void The_cpu_sample_only_watches_setups_own_workers()
        {
            // TrustedInstaller and TiWorker are busy on any machine installing updates. Sampling
            // them stalled collection for two seconds on healthy machines and proved nothing.
            Assert.Equal(new[] { "SetupHost", "setupprep" }, ProcessCollector.UpgradeWorkerNames.ToArray());
        }

        [Fact]
        public void A_machine_with_no_setup_running_is_collected_without_any_sampling_delay()
        {
            var started = DateTime.UtcNow;

            var snapshot = new ProcessCollector().CollectWithCpuSample(TimeSpan.FromSeconds(2));

            // No Setup worker alive, so the window must be skipped entirely.
            Assert.NotNull(snapshot);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1.5),
                "collection paused for the CPU sample even though no Setup worker was running");
        }
    }
}
