using System;
using WinUpgradeDiag.Core.Collect;
using Xunit;

namespace WinUpgradeDiag.Tests.Collect
{
    /// <summary>
    /// The report names the task sequence that last failed on this PC, so it must pick the one
    /// that failed most recently, and never one that succeeded.
    /// </summary>
    public class TaskSequenceHistoryTests
    {
        private static TaskSequenceRun Run(string id, string state, string started, string name = null)
        {
            return new TaskSequenceRun
            {
                PackageId = id,
                Name = name,
                State = state,
                Started = TaskSequenceHistoryReader.ParseTime(started)
            };
        }

        [Fact]
        public void The_most_recent_failure_is_chosen()
        {
            var chosen = TaskSequenceHistoryReader.Choose(new[]
            {
                Run("ABC00010", "Failure", "2026-08-01 09:00:00"),
                Run("ABC00456", "Failure", "2026-09-25 14:30:00", "Windows 11 24H2 In-place Upgrade"),
                Run("ABC00020", "Failure", "2026-09-02 11:00:00")
            });

            Assert.Equal("ABC00456", chosen.PackageId);
        }

        /// <summary>Clearing a success would let a required deployment run a second time.</summary>
        [Fact]
        public void A_task_sequence_that_succeeded_is_never_chosen()
        {
            var chosen = TaskSequenceHistoryReader.Choose(new[]
            {
                Run("ABC00010", "Success", "2026-09-30 09:00:00"),
                Run("ABC00456", "Failure", "2026-09-25 14:30:00")
            });

            Assert.Equal("ABC00456", chosen.PackageId);
        }

        [Fact]
        public void Nothing_is_chosen_when_nothing_failed()
        {
            Assert.Null(TaskSequenceHistoryReader.Choose(new[] { Run("ABC00010", "Success", "2026-09-30 09:00:00") }));
            Assert.Null(TaskSequenceHistoryReader.Choose(null));
        }

        [Fact]
        public void The_run_is_described_by_name_so_the_operator_can_recognise_it()
        {
            var run = Run("ABC00456", "Failure", "2026-09-25 14:30:00", "Windows 11 24H2 In-place Upgrade");
            run.ExitCode = "2147942402";

            var text = run.Describe();

            Assert.Contains("\"Windows 11 24H2 In-place Upgrade\" (ABC00456)", text);
            Assert.Contains("2026-09-25 14:30", text);
            Assert.Contains("failure with code 2147942402", text);
        }
    }
}
