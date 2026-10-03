using System;
using System.Linq;
using WinUpgradeDiag.Core.Remediation;
using Xunit;

namespace WinUpgradeDiag.Tests.Remediation
{
    /// <summary>
    /// Holds the Tools tab to the standard someone who has never seen it needs: pick the right tool
    /// from the list without already knowing the diagnosis.
    /// <para>
    /// The catalogue used to fail that. Six tools began "Fix:" and were told apart by a
    /// parenthetical; tools were named after causes ("ccmcache was cleared or deleted") rather than
    /// actions; the group a tool sat in depended on an unexplained split between "Fix" and
    /// "Recover"; and the most visible line under each name was a script filename. These tests
    /// encode the rules that replaced all of that, so the list cannot drift back.
    /// </para>
    /// </summary>
    public class ToolNamingTests
    {
        private static readonly string[] Groups =
        {
            "Inspect", "Downloads and Software Center", "Windows upgrade", "ConfigMgr client"
        };

        [Fact]
        public void Every_tool_says_when_to_use_it()
        {
            foreach (var tool in ToolCatalog.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(tool.UseWhen), tool.Id + " has no 'use when'.");

                // A symptom is a sentence about what someone can see, not a label.
                Assert.True(tool.UseWhen.Length >= 40,
                    tool.Id + " 'use when' is too short to describe a symptom: " + tool.UseWhen);
            }
        }

        [Fact]
        public void No_two_tools_share_a_name()
        {
            var duplicates = ToolCatalog.All
                .GroupBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.True(duplicates.Count == 0, "Shared names: " + string.Join(", ", duplicates));
        }

        /// <summary>
        /// "Fix:" on six tools told nobody which one; and a name whose meaning lives in brackets is
        /// a name that only works for someone who already knows the answer.
        /// </summary>
        [Fact]
        public void Names_are_actions_that_stand_on_their_own()
        {
            foreach (var tool in ToolCatalog.All)
            {
                Assert.False(tool.Title.StartsWith("Fix:", StringComparison.OrdinalIgnoreCase),
                    tool.Id + " uses the generic 'Fix:' prefix: " + tool.Title);

                Assert.False(tool.Title.Contains("("),
                    tool.Id + " depends on a parenthetical to be understood: " + tool.Title);

                // Sentence case, starting with the action.
                Assert.True(char.IsUpper(tool.Title[0]), tool.Id + " should start with a capital.");
                Assert.True(tool.Title.Length <= 52,
                    tool.Id + " name is too long to scan in a list: " + tool.Title);
            }
        }

        /// <summary>
        /// The two upgrade resets were the clearest case of a name that needed its parenthetical.
        /// The word that tells them apart now sits in the name itself.
        /// </summary>
        [Fact]
        public void The_two_upgrade_resets_differ_in_their_names_not_their_footnotes()
        {
            var setup = ToolCatalog.ById("FIX-B");
            var download = ToolCatalog.ById("FIX-A");

            Assert.Contains("Setup", setup.Title, StringComparison.Ordinal);
            Assert.Contains("download", download.Title, StringComparison.Ordinal);
            Assert.NotEqual(setup.Title, download.Title);
        }

        [Fact]
        public void Every_tool_sits_in_a_named_group_that_explains_itself()
        {
            foreach (var tool in ToolCatalog.All)
            {
                Assert.Contains(tool.Category, Groups);
                Assert.False(string.IsNullOrWhiteSpace(ToolCatalog.GroupDescription(tool.Category)),
                    tool.Category + " has no description under its heading.");
            }
        }

        /// <summary>
        /// "Inspect" promises the group is safe to click. That promise has to be true.
        /// </summary>
        [Fact]
        public void Everything_under_Inspect_is_read_only_and_everything_read_only_is_under_Inspect()
        {
            foreach (var tool in ToolCatalog.All)
            {
                var inInspect = tool.Category == "Inspect";
                var readOnly = tool.Risk == RemediationRisk.ReadOnly;

                Assert.True(inInspect == readOnly,
                    tool.Id + " is " + tool.Risk + " but sits under " + tool.Category + ".");
            }
        }

        /// <summary>
        /// Read-only first, then by area, and within each area the gentlest option before the
        /// drastic one — so the eye meets Repair before Reinstall.
        /// </summary>
        [Fact]
        public void The_list_reads_from_safe_to_drastic()
        {
            var all = ToolCatalog.All.ToList();

            // Groups appear in the intended order and are contiguous.
            var groupSequence = all.Select(t => t.Category)
                .Where((c, i) => i == 0 || c != all[i - 1].Category)
                .ToList();
            Assert.Equal(Groups, groupSequence);

            Assert.True(
                all.FindIndex(t => t.Id == "REPAIR-CLIENT") < all.FindIndex(t => t.Id == "REBUILD-CLIENT"),
                "Repair must come before Reinstall.");
        }

        /// <summary>
        /// "Use when" is the symptom; "what it does" is the action. When the second was written as
        /// "For a machine whose..." the card said the same thing twice and never said what the
        /// tool would do.
        /// </summary>
        [Fact]
        public void What_it_does_describes_the_action_not_the_symptom()
        {
            foreach (var tool in ToolCatalog.All)
            {
                Assert.False(tool.Purpose.StartsWith("For ", StringComparison.Ordinal),
                    tool.Id + " describes a symptom where it should describe an action: " + tool.Purpose);
            }
        }

        /// <summary>
        /// The client repair was described as a "last resort before escalating" while being placed,
        /// and labelled, as the thing to try first. A tool's own description must not contradict
        /// where it sits.
        /// </summary>
        [Fact]
        public void Repair_is_presented_consistently_as_the_first_thing_to_try()
        {
            var repair = ToolCatalog.ById("REPAIR-CLIENT");

            Assert.DoesNotContain("last resort", repair.Purpose, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("first", repair.UseWhen, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_display_order_names_every_tool_exactly_once()
        {
            var ids = ToolCatalog.All.Select(t => t.Id).OrderBy(i => i, StringComparer.Ordinal).ToList();
            var order = ToolCatalog.DisplayOrder.OrderBy(i => i, StringComparer.Ordinal).ToList();

            Assert.Equal(ids, order);
        }
    }
}
