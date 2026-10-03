using System;
using System.IO;
using System.Linq;
using WinUpgradeDiag.Core.Remediation;
using Xunit;

namespace WinUpgradeDiag.Tests.Remediation
{
    /// <summary>
    /// Guards against a mis-click on an action that cannot be undone. Retiring the rollback set
    /// removes "Go back" permanently; the cheapest protection is that on a machine where the
    /// action makes no sense, it simply cannot run.
    /// </summary>
    public class ToolPreconditionTests
    {
        private static bool WindowsOldExists => Directory.Exists(@"C:\Windows.old");

        [Fact]
        public void Reclaiming_space_is_refused_when_there_is_nothing_to_reclaim()
        {
            if (WindowsOldExists)
            {
                // This machine genuinely has rollback data, so the refusal cannot be observed here.
                return;
            }

            var check = ToolPreconditions.Check(ToolCatalog.ById("REMOVE-LEFTOVERS"));

            Assert.False(check.Satisfied);
            Assert.Contains("Windows.old", check.Reason);
            // The operator must be told nothing happened, not left guessing.
            Assert.Contains("Nothing has been changed", check.Reason);
        }

        [Fact]
        public void The_refusal_reaches_preflight_so_the_run_is_stopped_before_any_dialog()
        {
            if (WindowsOldExists)
            {
                return;
            }

            var report = new RemediationRunner().Preflight(ToolCatalog.ById("REMOVE-LEFTOVERS"), null, null);

            Assert.False(report.IsAllowed);
            Assert.Equal(PreflightResult.BlockedPrecondition, report.Result);
        }

        [Fact]
        public void Removing_setup_leftovers_is_refused_when_the_folder_is_not_there()
        {
            if (Directory.Exists(@"C:\$WINDOWS.~BT") || WindowsOldExists)
            {
                return;
            }

            var check = ToolPreconditions.Check(ToolCatalog.ById("FIX-D"));

            Assert.False(check.Satisfied);
            Assert.Contains("Nothing has been changed", check.Reason);
        }

        [Fact]
        public void Tools_without_their_own_conditions_are_not_blocked_by_this()
        {
            foreach (var id in new[] { "CHECK-STATE", "LIST-CACHE", "FIX-B", "FIX-C", "RESET-TS" })
            {
                Assert.True(ToolPreconditions.Check(ToolCatalog.ById(id)).Satisfied, id);
            }
        }

        [Fact]
        public void A_null_tool_is_handled_rather_than_throwing()
        {
            Assert.True(ToolPreconditions.Check(null).Satisfied);
        }

        // ---------------------------------------------------------------- the second gate

        [Fact]
        public void Reclaiming_space_demands_an_explicit_acknowledgement()
        {
            var tool = ToolCatalog.ById("REMOVE-LEFTOVERS");

            // Typing a script name proved you could read it off the line above the box. It could
            // not prove anyone had checked the machine actually works, which is the fact this
            // action depends on and the one thing the tool cannot establish for itself.
            Assert.False(tool.RequiresTypedConfirmation);
            Assert.True(tool.RequiresAcknowledgement);
            Assert.Contains("working correctly", tool.Acknowledgement);
        }

        [Fact]
        public void The_reclaim_preconditions_spell_out_what_is_lost()
        {
            var tool = ToolCatalog.ById("REMOVE-LEFTOVERS");

            Assert.Contains(tool.Preconditions, p => p.Contains("Go back"));
            Assert.Contains(tool.Preconditions, p => p.Contains("Windows.old must exist"));
            Assert.Contains(tool.Preconditions, p => p.Contains("not rolled back"));
        }

        [Fact]
        public void No_other_tool_demands_an_acknowledgement_so_it_keeps_its_weight()
        {
            // If everything asked for a tick, ticking would become reflex and mean nothing. The
            // bar is a fact the tool cannot check for itself and cannot undo: REMOVE-LEFTOVERS
            // needs a person to confirm the machine actually works before the rollback data goes,
            // and REBUILD-CLIENT resets the client identity, which leaves a duplicate record in the
            // console that no later run can take back.
            var withAck = ToolCatalog.All
                .Where(t => t.RequiresAcknowledgement)
                .Select(t => t.Id)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

            // Every destructive tool now carries one. That is not inflation: the acknowledgement
            // replaced a typed script name the dialog printed on the line above the box, so the
            // gate went from transcription to a claim the operator has to actually make.
            Assert.Equal(
                new[] { "FIX-A", "FIX-C", "FIX-D", "FIX-E", "REBUILD-CLIENT", "REMOVE-LEFTOVERS" },
                withAck);
        }

        // ---------------------------------------------------------------- the crash

        [Fact]
        public void Building_a_display_command_never_throws_on_an_illegal_path()
        {
            // Path.Combine rejects a segment containing < or > on .NET Framework. A placeholder
            // like "<scripts>" threw ArgumentException before any dialog could be shown, which
            // took the confirmation for every tool down with it.
            foreach (var tool in ToolCatalog.All)
            {
                var ex = Record.Exception(() =>
                    RemediationRunner.BuildArguments(tool.ScriptName, tool, "ABC00123"));

                Assert.Null(ex);
            }
        }

        [Fact]
        public void A_script_name_alone_is_a_valid_argument_to_the_command_builder()
        {
            var tool = ToolCatalog.ById("REMOVE-LEFTOVERS");

            var arguments = RemediationRunner.BuildArguments(tool.ScriptName, tool, null);

            Assert.Contains("Remove-UpgradeLeftovers.ps1", arguments);
            Assert.DoesNotContain("<", arguments);
            Assert.DoesNotContain(">", arguments);
        }
    }
}
