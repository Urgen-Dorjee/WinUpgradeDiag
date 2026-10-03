using System;
using System.IO;
using System.Linq;
using WinUpgradeDiag.Core.Remediation;
using WinUpgradeDiag.Tests.Support;
using Xunit;

namespace WinUpgradeDiag.Tests.Remediation
{
    /// <summary>
    /// The gates in front of anything that changes the machine. These matter more than the
    /// execution itself: a fix that runs when it should not have is how a recoverable machine
    /// becomes a broken one.
    /// </summary>
    public class RemediationRunnerTests
    {
        private static ToolDefinition Tool(string id) => ToolCatalog.ById(id);

        /// <summary>
        /// A tool whose script the assembly does not carry, so the on-disk fallback path is used.
        /// Every catalogue script is embedded now, which is the point — substituting one on disk
        /// no longer changes what runs.
        /// </summary>
        private static ToolDefinition DiskOnlyTool(
            string scriptName = "Zz-Test-Only.ps1",
            RemediationRisk risk = RemediationRisk.ReadOnly)
        {
            return new ToolDefinition(
                "TEST-ONLY", scriptName, "Test tool", "Exists only for tests.",
                risk, "Diagnose",
                new[] { "Run a stub." }, new string[0]);
        }

        private static string FolderWithScripts(TempDirectory tmp, params string[] scriptNames)
        {
            foreach (var name in scriptNames)
            {
                tmp.File(name, "# stub\n");
            }
            return tmp.Path;
        }

        // ---------------------------------------------------------------- preflight refusals

        [Fact]
        public void A_missing_script_folder_no_longer_blocks_an_embedded_tool()
        {
            // The scripts travel inside the assembly, so a wrong or absent folder on disk is
            // irrelevant. Only the elevation gate should stop a fix here.
            var report = new RemediationRunner().Preflight(Tool("FIX-B"), @"C:\nope\does\not\exist", null);

            Assert.NotEqual(PreflightResult.BlockedScriptMissing, report.Result);
            Assert.Contains(report.Observations, o => o.IndexOf("embedded", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void A_script_that_is_neither_embedded_nor_on_disk_is_refused()
        {
            using (var tmp = new TempDirectory())
            {
                var report = new RemediationRunner().Preflight(DiskOnlyTool(), tmp.Path, null);

                Assert.Equal(PreflightResult.BlockedScriptMissing, report.Result);
                Assert.Contains("Zz-Test-Only.ps1", report.Message);
            }
        }

        [Fact]
        public void The_embedded_copy_is_used_even_when_a_different_one_sits_on_disk()
        {
            using (var tmp = new TempDirectory())
            {
                // Drop a tampered file with the right name next to the app.
                File.WriteAllText(Path.Combine(tmp.Path, "Check-UpgradeState.ps1"), "Write-Output 'TAMPERED'\r\n");

                var runner = new RemediationRunner();
                var report = runner.Preflight(Tool("CHECK-STATE"), tmp.Path, null);
                var result = runner.Run(Tool("CHECK-STATE"), report, null, Path.Combine(tmp.Path, "audit"));

                // The substituted script must not have run.
                Assert.DoesNotContain("TAMPERED", result.Output ?? string.Empty);
                Assert.Contains(report.Observations, o => o.IndexOf("differs from the embedded", StringComparison.OrdinalIgnoreCase) >= 0);
            }
        }

        [Fact]
        public void A_tool_needing_a_parameter_is_refused_without_one()
        {
            using (var tmp = new TempDirectory())
            {
                var folder = FolderWithScripts(tmp, "Fix-A-DownloadInterrupted.ps1");

                var report = new RemediationRunner().Preflight(Tool("FIX-A"), folder, parameterValue: null);

                Assert.Equal(PreflightResult.BlockedMissingParameter, report.Result);
                Assert.Contains("ContentId", report.Message);
            }
        }

        [Fact]
        public void A_read_only_tool_needs_neither_elevation_nor_a_quiet_machine()
        {
            using (var tmp = new TempDirectory())
            {
                var folder = FolderWithScripts(tmp, "Check-UpgradeState.ps1");

                var report = new RemediationRunner().Preflight(Tool("CHECK-STATE"), folder, null);

                // Diagnosis must stay available no matter what state the machine is in.
                Assert.True(report.IsAllowed, report.Message);
            }
        }

        [Fact]
        public void A_refused_preflight_still_produces_an_audit_record()
        {
            using (var tmp = new TempDirectory())
            {
                var audit = Path.Combine(tmp.Path, "audit");
                var tool = DiskOnlyTool();
                var refused = new RemediationRunner().Preflight(tool, @"C:\nope", null);

                var result = new RemediationRunner().Run(tool, refused, null, audit);

                Assert.False(result.Started);
                Assert.NotNull(result.AuditLogPath);
                var text = File.ReadAllText(result.AuditLogPath);
                // A refusal is as much a fact worth recording as a run.
                Assert.Contains("REFUSED", text);
                Assert.Contains("BlockedScriptMissing", text);
                Assert.Contains(Environment.UserName, text);
            }
        }

        // ---------------------------------------------------------------- argument safety

        [Fact]
        public void A_parameter_cannot_break_out_and_append_its_own_arguments()
        {
            var tool = Tool("FIX-A");
            var hostile = "ABC00123\" -Command \"Remove-Item C:\\ -Recurse";

            var arguments = RemediationRunner.BuildArguments(@"C:\Script\Fix-A.ps1", tool, hostile);

            // The quotes that would terminate the argument are stripped, so the whole hostile
            // string stays a single value of -ContentId.
            Assert.DoesNotContain("\" -Command", arguments);
            Assert.Contains("-ContentId \"ABC00123 -Command Remove-Item C:\\ -Recurse\"", arguments);
        }

        [Fact]
        public void Arguments_are_non_interactive_so_a_prompt_can_never_hang_the_app()
        {
            var arguments = RemediationRunner.BuildArguments(@"C:\Script\Fix-B.ps1", Tool("FIX-B"), null);

            Assert.Contains("-NonInteractive", arguments);
            Assert.Contains("-NoProfile", arguments);
            Assert.Contains("-ExecutionPolicy Bypass", arguments);
            Assert.Contains("-File \"C:\\Script\\Fix-B.ps1\"", arguments);
        }

        // ---------------------------------------------------------------- execution

        [Fact]
        public void A_read_only_tool_actually_runs_and_its_output_is_captured_and_audited()
        {
            using (var tmp = new TempDirectory())
            {
                // A real script, really executed, so this covers the process plumbing end to end.
                tmp.File("Zz-State-Stub.ps1", "Write-Output 'STATE-CHECK-OK'\r\nexit 0\r\n");
                var audit = Path.Combine(tmp.Path, "audit");

                var runner = new RemediationRunner();
                var tool = DiskOnlyTool("Zz-State-Stub.ps1");
                var report = runner.Preflight(tool, tmp.Path, null);
                Assert.True(report.IsAllowed, report.Message);

                var result = runner.Run(tool, report, null, audit);

                Assert.True(result.Started, result.Error);
                Assert.Equal(0, result.ExitCode);
                Assert.Contains("STATE-CHECK-OK", result.Output);

                var text = File.ReadAllText(result.AuditLogPath);
                Assert.Contains("STATE-CHECK-OK", text);
                Assert.Contains("Exit code     : 0", text);
                Assert.Contains("TEST-ONLY", text);
            }
        }

        [Fact]
        public void A_non_zero_exit_is_reported_rather_than_treated_as_success()
        {
            using (var tmp = new TempDirectory())
            {
                tmp.File("Zz-Fail-Stub.ps1", "Write-Output 'failing'\r\nexit 3\r\n");
                var audit = Path.Combine(tmp.Path, "audit");

                var runner = new RemediationRunner();
                var tool = DiskOnlyTool("Zz-Fail-Stub.ps1");
                var report = runner.Preflight(tool, tmp.Path, null);
                var result = runner.Run(tool, report, null, audit);

                Assert.True(result.Started);
                Assert.Equal(3, result.ExitCode);
                Assert.False(result.Succeeded);
            }
        }

        [Fact]
        public void Standard_error_is_captured_too_not_silently_dropped()
        {
            using (var tmp = new TempDirectory())
            {
                tmp.File("Zz-Err-Stub.ps1", "[Console]::Error.WriteLine('SOMETHING-WENT-WRONG')\r\nexit 0\r\n");

                var runner = new RemediationRunner();
                var tool = DiskOnlyTool("Zz-Err-Stub.ps1");
                var report = runner.Preflight(tool, tmp.Path, null);
                var result = runner.Run(tool, report, null, Path.Combine(tmp.Path, "audit"));

                Assert.Contains("SOMETHING-WENT-WRONG", result.Output);
            }
        }

        // ---------------------------------------------------------------- catalogue contract

        [Fact]
        public void Every_tool_that_changes_the_machine_is_blocked_by_a_live_upgrade()
        {
            var changing = ToolCatalog.All.Where(t => t.Risk != RemediationRisk.ReadOnly).ToList();

            Assert.NotEmpty(changing);
            Assert.All(changing, t => Assert.True(t.BlockedByLiveUpgrade, t.Id + " is not gated"));
        }

        [Fact]
        public void Every_destructive_tool_demands_an_acknowledgement()
        {
            var destructive = ToolCatalog.All.Where(t => t.Risk == RemediationRisk.Destructive).ToList();

            Assert.NotEmpty(destructive);

            // The gate is a claim, not a transcription. Typing the script name proved only that
            // the operator could copy it from the line above the box; the acknowledgement states
            // the specific irreversible consequence and makes them assert something the tool
            // cannot check for itself.
            Assert.All(destructive, t =>
            {
                Assert.False(t.RequiresTypedConfirmation, t.Id + " still asks for a typed name.");
                Assert.True(t.RequiresAcknowledgement,
                    t.Id + " is destructive but states no consequence to acknowledge.");

                // And it has to say what is lost, not merely "are you sure".
                Assert.True(t.Acknowledgement.Length > 40,
                    t.Id + " acknowledgement is too vague: " + t.Acknowledgement);
            });
        }

        [Fact]
        public void Read_only_tools_are_never_gated_so_diagnosis_always_works()
        {
            var readOnly = ToolCatalog.All.Where(t => t.Risk == RemediationRisk.ReadOnly).ToList();

            Assert.NotEmpty(readOnly);
            Assert.All(readOnly, t =>
            {
                Assert.False(t.BlockedByLiveUpgrade, t.Id);
                Assert.False(t.RequiresTypedConfirmation, t.Id);
            });
        }

        [Fact]
        public void Every_tool_describes_what_it_does_before_it_is_run()
        {
            Assert.All(ToolCatalog.All, t =>
            {
                Assert.False(string.IsNullOrWhiteSpace(t.Title), t.Id);
                Assert.False(string.IsNullOrWhiteSpace(t.Purpose), t.Id);
                Assert.False(string.IsNullOrWhiteSpace(t.ScriptName), t.Id);
                Assert.NotEmpty(t.Steps);
                if (t.RequiresParameter)
                {
                    Assert.False(string.IsNullOrWhiteSpace(t.ParameterPrompt), t.Id);
                }
            });
        }

        [Fact]
        public void The_dangerous_pair_is_described_so_they_cannot_be_confused()
        {
            var fixD = Tool("FIX-D");
            var remove = Tool("REMOVE-LEFTOVERS");

            // Deleting $WINDOWS.~BT on a machine that upgraded successfully strips half the
            // rollback set. Each must point at the other.
            Assert.Contains(fixD.Preconditions, p => p.Contains("Windows.old must NOT exist"));
            Assert.Contains(remove.Preconditions, p => p.Contains("Go back"));
        }

        [Fact]
        public void The_script_folder_is_found_from_the_repository_layout()
        {
            // Proves the locator copes with the real nesting, not just an ideal one.
            using (var tmp = new TempDirectory())
            {
                var nested = Path.Combine(tmp.Path, "Script", "sccm-inplace-upgrade-scripts", "inner");
                Directory.CreateDirectory(nested);
                File.WriteAllText(Path.Combine(nested, "Check-UpgradeState.ps1"), "# stub");

                var found = ToolCatalog.LocateScriptFolder(tmp.Path);

                Assert.Equal(nested, found);
            }
        }

        [Fact]
        public void A_layout_with_no_scripts_returns_null_rather_than_guessing()
        {
            using (var tmp = new TempDirectory())
            {
                Assert.Null(ToolCatalog.LocateScriptFolder(tmp.Path));
            }
        }
    }
}
