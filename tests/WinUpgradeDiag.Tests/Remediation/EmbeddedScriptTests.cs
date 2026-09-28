using System;
using System.IO;
using System.Linq;
using WinUpgradeDiag.Core.Remediation;
using WinUpgradeDiag.Tests.Support;
using Xunit;

namespace WinUpgradeDiag.Tests.Remediation
{
    /// <summary>
    /// The scripts ship inside the assembly rather than beside it. A folder of loose .ps1 files
    /// that an elevated process executes is a writable execution path: whoever can drop a file
    /// there chooses what runs as administrator.
    /// </summary>
    public class EmbeddedScriptTests
    {
        [Fact]
        public void Every_tool_in_the_catalogue_has_its_script_embedded()
        {
            var missing = ToolCatalog.All
                .Where(t => !EmbeddedScriptProvider.Contains(t.ScriptName))
                .Select(t => t.Id + " -> " + t.ScriptName)
                .ToList();

            // A tool whose script is not carried would silently fall back to whatever is on disk.
            Assert.True(missing.Count == 0, "not embedded: " + string.Join(", ", missing));
        }

        [Fact]
        public void An_embedded_script_has_real_content_and_a_stable_hash()
        {
            var bytes = EmbeddedScriptProvider.Read("Fix-B-SetupInterrupted.ps1");

            Assert.NotNull(bytes);
            Assert.True(bytes.Length > 200, "the embedded script looks truncated");

            var first = EmbeddedScriptProvider.HashOf("Fix-B-SetupInterrupted.ps1");
            var second = EmbeddedScriptProvider.HashOf("Fix-B-SetupInterrupted.ps1");

            Assert.Equal(64, first.Length);          // SHA-256, hex
            Assert.Equal(first, second);
        }

        [Fact]
        public void Each_script_hashes_differently_so_the_audit_identifies_which_one_ran()
        {
            var hashes = ToolCatalog.All
                .Select(t => EmbeddedScriptProvider.HashOf(t.ScriptName))
                .ToList();

            Assert.Equal(hashes.Count, hashes.Distinct().Count());
        }

        [Fact]
        public void An_unknown_script_is_reported_as_absent_rather_than_throwing()
        {
            Assert.False(EmbeddedScriptProvider.Contains("Definitely-Not-Real.ps1"));
            Assert.Null(EmbeddedScriptProvider.Read("Definitely-Not-Real.ps1"));
            Assert.Null(EmbeddedScriptProvider.HashOf("Definitely-Not-Real.ps1"));
            Assert.Null(EmbeddedScriptProvider.Extract("Definitely-Not-Real.ps1"));
            Assert.Null(EmbeddedScriptProvider.Contains(null) ? "x" : null);
        }

        [Fact]
        public void Extraction_writes_the_exact_embedded_bytes_and_cleans_up_after_itself()
        {
            string extractedPath;
            string folder;

            using (var script = EmbeddedScriptProvider.Extract("Check-UpgradeState.ps1"))
            {
                Assert.NotNull(script);
                Assert.Equal(ScriptOrigin.Embedded, script.Origin);
                Assert.True(File.Exists(script.Path));

                extractedPath = script.Path;
                folder = Path.GetDirectoryName(script.Path);

                // The bytes on disk must be the bytes we shipped, not a re-encoded copy.
                Assert.Equal(EmbeddedScriptProvider.Read("Check-UpgradeState.ps1"), File.ReadAllBytes(script.Path));
                Assert.Equal(EmbeddedScriptProvider.HashOf("Check-UpgradeState.ps1"), script.Sha256);
                Assert.Contains("signed application", script.OriginText);
            }

            // Disposal removes the scratch copy, so nothing executable is left lying about.
            Assert.False(File.Exists(extractedPath));
            Assert.False(Directory.Exists(folder));
        }

        [Fact]
        public void Each_extraction_uses_its_own_folder_so_two_runs_cannot_collide()
        {
            using (var a = EmbeddedScriptProvider.Extract("Check-UpgradeState.ps1"))
            using (var b = EmbeddedScriptProvider.Extract("Check-UpgradeState.ps1"))
            {
                Assert.NotEqual(Path.GetDirectoryName(a.Path), Path.GetDirectoryName(b.Path));
            }
        }

        [Fact]
        public void A_tampered_copy_on_disk_is_detected_as_different()
        {
            using (var tmp = new TempDirectory())
            {
                var good = Path.Combine(tmp.Path, "Check-UpgradeState.ps1");
                File.WriteAllBytes(good, EmbeddedScriptProvider.Read("Check-UpgradeState.ps1"));
                Assert.True(EmbeddedScriptProvider.MatchesEmbedded("Check-UpgradeState.ps1", good));

                // One appended line is the whole attack: the file still looks right by name.
                File.AppendAllText(good, "\r\nWrite-Output 'tampered'\r\n");
                Assert.False(EmbeddedScriptProvider.MatchesEmbedded("Check-UpgradeState.ps1", good));
            }
        }

        [Fact]
        public void A_missing_disk_copy_is_simply_not_a_match()
        {
            Assert.False(EmbeddedScriptProvider.MatchesEmbedded("Check-UpgradeState.ps1", @"C:\nope\nothing.ps1"));
        }

        [Fact]
        public void A_tool_can_run_with_no_script_folder_configured_at_all()
        {
            // The point of embedding: the app no longer depends on a folder existing anywhere.
            var report = new RemediationRunner().Preflight(ToolCatalog.ById("CHECK-STATE"), null, null);

            Assert.True(report.IsAllowed, report.Message);
            Assert.Contains(report.Observations, o => o.IndexOf("embedded", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [Fact]
        public void The_audit_records_which_bytes_ran()
        {
            using (var tmp = new TempDirectory())
            {
                var audit = Path.Combine(tmp.Path, "audit");
                var tool = ToolCatalog.ById("CHECK-STATE");
                var runner = new RemediationRunner();

                var report = runner.Preflight(tool, null, null);
                var result = runner.Run(tool, report, null, audit);

                var log = File.ReadAllText(result.AuditLogPath);
                Assert.Contains("Script source : embedded", log);
                Assert.Contains(EmbeddedScriptProvider.HashOf(tool.ScriptName), log);
            }
        }
    }
}
