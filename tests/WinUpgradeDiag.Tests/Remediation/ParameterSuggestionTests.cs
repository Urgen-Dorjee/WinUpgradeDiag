using System;
using System.Collections.Generic;
using System.Linq;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Orchestration;
using WinUpgradeDiag.Core.Remediation;
using Xunit;

namespace WinUpgradeDiag.Tests.Remediation
{
    /// <summary>
    /// Every parameterised tool asked the operator for a value the run had already collected, and
    /// one of them offered the wrong answer as its default. A technician standing at a broken
    /// machine should be correcting a prefilled box, not researching what to put in an empty one.
    /// </summary>
    public class ParameterSuggestionTests
    {
        private static ToolDefinition Tool(string id)
        {
            return ToolCatalog.All.Single(t => t.Id == id);
        }

        private static DiagnosticContext ContextWith(SystemState state)
        {
            return new DiagnosticContext
            {
                ToolVersion = "0.1.0-test",
                StartedAtUtc = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc),
                Manifest = new LogManifestEntryList(),
                SystemState = state
            };
        }

        /// <summary>Empty manifest, since none of these suggestions read logs.</summary>
        private sealed class LogManifestEntryList : List<WinUpgradeDiag.Core.Discovery.LogManifestEntry>
        {
        }

        // ---------------------------------------------------------------- the contradiction

        /// <summary>
        /// Both ComputerName tools reach a DIFFERENT machine over the network, and their own help
        /// says "not this machine" — while the prompt arrived prefilled with this machine's name.
        /// The default was the one answer guaranteed to be wrong.
        /// </summary>
        [Theory]
        [InlineData("GET-PROGRESS")]
        [InlineData("WATCH-SMSTS")]
        public void A_remote_tool_does_not_offer_this_machine_as_the_default(string id)
        {
            var tool = Tool(id);
            var suggested = ParameterSuggestion.For(tool, ContextWith(new SystemState()));

            Assert.NotEqual(Environment.MachineName, suggested);
            Assert.True(string.IsNullOrEmpty(suggested));

            // And the box is not left bare: it says whose name to type.
            var where = ParameterSuggestion.WhereToFind(tool, ContextWith(new SystemState()));
            Assert.Contains("not this one", where, StringComparison.OrdinalIgnoreCase);
        }

        // ---------------------------------------------------------------- what is known

        [Fact]
        public void The_content_id_comes_from_the_largest_cached_item()
        {
            var state = new SystemState
            {
                CcmCache = new CcmCacheSnapshot
                {
                    Elements = new[]
                    {
                        new CcmCacheElement { ContentId = "ABC00001", SizeKilobytes = 4_096 },
                        // The OS image: nothing else on the machine is this size.
                        new CcmCacheElement { ContentId = "ABC00123", SizeKilobytes = 5_200_000 },
                        new CcmCacheElement { ContentId = "ABC00002", SizeKilobytes = 51_200 }
                    }
                }
            };

            Assert.Equal("ABC00123", ParameterSuggestion.For(Tool("FIX-A"), ContextWith(state)));
        }

        [Fact]
        public void The_task_sequence_package_id_comes_from_the_execution_request()
        {
            var state = new SystemState
            {
                TaskSequenceExecutionRequest = OrphanedTaskSequenceInfo.Found("ABC00456", "ABC20001")
            };

            Assert.Equal("ABC00456", ParameterSuggestion.For(Tool("RESET-TS"), ContextWith(state)));
        }

        // ---------------------------------------------------------------- what is not known

        /// <summary>
        /// A wrong package id handed to a destructive script is worse than an empty box, so a
        /// value that cannot be established stays empty rather than becoming a plausible guess.
        /// </summary>
        [Fact]
        public void An_unreadable_cache_suggests_nothing_rather_than_guessing()
        {
            var state = new SystemState
            {
                CcmCache = new CcmCacheSnapshot { Error = "The client cache could not be read." }
            };

            Assert.Equal(string.Empty, ParameterSuggestion.For(Tool("FIX-A"), ContextWith(state)));
        }

        [Fact]
        public void No_diagnostic_run_at_all_suggests_nothing_and_does_not_throw()
        {
            foreach (var tool in ToolCatalog.All.Where(t => t.RequiresParameter))
            {
                var ex = Record.Exception(() => ParameterSuggestion.For(tool, null));
                Assert.Null(ex);
            }
        }

        /// <summary>
        /// An empty box with an example of somebody else's site is a dead end. Every parameter
        /// that can come back empty has to say where the value is found.
        /// </summary>
        [Fact]
        public void Every_parameterised_tool_says_where_to_find_its_value()
        {
            var parameterised = ToolCatalog.All.Where(t => t.RequiresParameter).ToList();

            Assert.NotEmpty(parameterised);

            foreach (var tool in parameterised)
            {
                var where = ParameterSuggestion.WhereToFind(tool, ContextWith(new SystemState()));

                Assert.False(string.IsNullOrWhiteSpace(where),
                    tool.Id + " (" + tool.ParameterName + ") has no guidance for an empty box.");
            }
        }

        [Fact]
        public void A_tool_that_takes_no_parameter_is_asked_nothing()
        {
            var noParameter = ToolCatalog.All.First(t => !t.RequiresParameter);

            Assert.Equal(string.Empty, ParameterSuggestion.For(noParameter, ContextWith(new SystemState())));
            Assert.Null(ParameterSuggestion.WhereToFind(noParameter, ContextWith(new SystemState())));
        }

        /// <summary>
        /// A value the operator typed is a deliberate correction and must survive a later run.
        /// The refresh only fills boxes that are still empty, which is asserted here at the level
        /// the rule actually lives rather than through the view model.
        /// </summary>
        [Fact]
        public void A_suggestion_is_only_offered_when_there_is_something_to_suggest()
        {
            var empty = ContextWith(new SystemState());

            foreach (var tool in ToolCatalog.All.Where(t => t.RequiresParameter))
            {
                var suggested = ParameterSuggestion.For(tool, empty);

                // Nothing collected means nothing offered - never a placeholder that looks real.
                Assert.True(string.IsNullOrEmpty(suggested),
                    tool.Id + " invented " + suggested + " from an empty diagnostic.");
            }
        }

        /// <summary>
        /// The two id parameters are different things and get confused, which is why the help says
        /// so. Guard that they are never sourced from the same place.
        /// </summary>
        [Fact]
        public void The_content_id_and_the_task_sequence_id_are_not_interchangeable()
        {
            var state = new SystemState
            {
                CcmCache = new CcmCacheSnapshot
                {
                    Elements = new[] { new CcmCacheElement { ContentId = "ABC00123", SizeKilobytes = 5_200_000 } }
                },
                TaskSequenceExecutionRequest = OrphanedTaskSequenceInfo.Found("ABC00456", "ABC20001")
            };

            var context = ContextWith(state);

            Assert.Equal("ABC00123", ParameterSuggestion.For(Tool("FIX-A"), context));
            Assert.Equal("ABC00456", ParameterSuggestion.For(Tool("RESET-TS"), context));
        }
    }
}
