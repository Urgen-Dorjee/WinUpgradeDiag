using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Discovery;
using WinUpgradeDiag.Core.Orchestration;
using WinUpgradeDiag.Core.Rules;
using WinUpgradeDiag.Tests.Support;
using Xunit;

namespace WinUpgradeDiag.Tests.Rules
{
    /// <summary>
    /// Written from a real session on a machine whose Configuration Manager applet would not open.
    /// Working it out took a service check, a WMI provider query, a registry read and four logs,
    /// one PowerShell command at a time, across an afternoon. Every fact that mattered is
    /// collectable, so none of it should have been typed by hand.
    /// </summary>
    public class CcmClientAnalyzerTests
    {
        private static DiagnosticContext ContextWith(
            CcmClientHealthInfo health, params LogManifestEntry[] logs)
        {
            return new DiagnosticContext
            {
                ToolVersion = "0.1.0-test",
                StartedAtUtc = new DateTime(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc),
                PrivilegedReadEnabled = true,
                Manifest = logs,
                SystemState = new SystemState
                {
                    MachineName = "CM-TEST-0001",
                    IsElevated = true,
                    CcmClient = health
                }
            };
        }

        private static LogManifestEntry Entry(string path, string display)
        {
            var source = new LogSource(
                "ccm-" + display, LogSourceCategory.ClientOther, display, "", path);
            return new LogManifestBuilder().Build(new[] { source }).Single();
        }

        private static IReadOnlyList<Finding> Run(DiagnosticContext context)
        {
            return new CcmClientAnalyzer().Analyze(context, null, CancellationToken.None);
        }

        /// <summary>The state that cost the afternoon: installed, running, provider dead.</summary>
        private static CcmClientHealthInfo BrokenProvider()
        {
            var health = new CcmClientHealthInfo
            {
                ClientFolderExists = true,
                CcmExecInstalled = true,
                CcmExecState = "Stopped",
                CcmNamespaceResponds = false,
                CcmNamespaceHResult = "0x80041013",
                CcmNamespaceError = "Provider load failure",
                WmiRepositoryConsistent = true
            };
            health.Namespaces["root\\ccm"] = false;
            return health;
        }

        [Fact]
        public void A_dead_client_provider_is_reported_as_the_cause()
        {
            var finding = Run(ContextWith(BrokenProvider())).Single(f => f.Id == "CC-100");

            Assert.Equal(Severity.Critical, finding.Severity);
            Assert.Equal(Confidence.High, finding.Confidence);
            Assert.Contains("0x80041013", finding.Meaning, StringComparison.Ordinal);
            Assert.Contains("provider load failure", finding.Meaning, StringComparison.OrdinalIgnoreCase);

            // The advice has to say that the obvious fixes do not work, because they do not.
            Assert.Contains("will not fix this", finding.Action, StringComparison.Ordinal);
            Assert.Contains("Rebuild-CcmClient.ps1", finding.Action, StringComparison.Ordinal);
        }

        /// <summary>
        /// A stopped service under a dead provider is the same fault counted twice, and two
        /// Critical findings for one cause is how a report stops being readable.
        /// </summary>
        [Fact]
        public void A_stopped_service_is_not_reported_separately_from_the_dead_provider()
        {
            var findings = Run(ContextWith(BrokenProvider()));

            Assert.Contains(findings, f => f.Id == "CC-100");
            Assert.DoesNotContain(findings, f => f.Id == "CC-102");
        }

        [Fact]
        public void A_stopped_service_with_a_healthy_provider_is_reported_on_its_own()
        {
            var health = new CcmClientHealthInfo
            {
                ClientFolderExists = true,
                CcmExecInstalled = true,
                CcmExecState = "Stopped",
                CcmNamespaceResponds = true,
                SiteCode = "ABC"
            };

            var finding = Run(ContextWith(health)).Single(f => f.Id == "CC-102");
            Assert.Contains("CcmExec.log", finding.Action, StringComparison.Ordinal);
        }

        [Fact]
        public void A_missing_client_is_told_apart_from_a_broken_one()
        {
            var health = new CcmClientHealthInfo
            {
                ClientFolderExists = false,
                CcmNamespaceResponds = false,
                // Invalid namespace, not provider load failure: nothing is installed.
                CcmNamespaceHResult = "0x8004100E"
            };

            var findings = Run(ContextWith(health));

            Assert.Contains(findings, f => f.Id == "CC-105");
            Assert.DoesNotContain(findings, f => f.Id == "CC-100");
        }

        /// <summary>
        /// The second failure from that session: the client installed and returned 0, then looped
        /// on "Failed to refresh site code" forever because it had no management point to ask.
        /// </summary>
        [Fact]
        public void A_client_that_cannot_find_a_management_point_is_named_with_the_missing_argument()
        {
            using (var tmp = new TempDirectory())
            {
                var location = tmp.File("LocationServices.log",
                    "<![LOG[LSGetSiteInformationFromManagementPoint('ABC'): Assignment Site Code []]LOG]!>\r\n");
                var clientId = tmp.File("ClientIDManagerStartup.log",
                    "<![LOG[RegTask: Failed to refresh site code. Error: 0x8000ffff]LOG]!>\r\n");

                var health = new CcmClientHealthInfo
                {
                    ClientFolderExists = true,
                    CcmExecInstalled = true,
                    CcmExecState = "Running",
                    CcmNamespaceResponds = true,
                    SiteCode = null
                };

                var finding = Run(ContextWith(health,
                    Entry(location, "LocationServices.log"),
                    Entry(clientId, "ClientIDManagerStartup.log"))).Single(f => f.Id == "CC-103");

                // SMSMP= is the whole answer, and the reason /mp: alone is not.
                Assert.Contains("SMSMP=", finding.Action, StringComparison.Ordinal);
                Assert.Contains("downloads from", finding.Meaning, StringComparison.Ordinal);

                var quoted = string.Join("\n", finding.Evidence.Select(e => e.Text));
                Assert.Contains("Assignment Site Code []", quoted, StringComparison.Ordinal);
                Assert.Contains("Failed to refresh site code", quoted, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void A_healthy_registered_client_produces_nothing()
        {
            var health = new CcmClientHealthInfo
            {
                ClientFolderExists = true,
                CcmExecInstalled = true,
                CcmExecState = "Running",
                CcmNamespaceResponds = true,
                SiteCode = "ABC",
                ManagementPoint = "mp01.contoso.com",
                WmiRepositoryConsistent = true
            };

            Assert.Empty(Run(ContextWith(health)));
        }

        [Fact]
        public void No_client_state_collected_means_no_findings_rather_than_a_crash()
        {
            var context = ContextWith(null);
            Assert.Empty(Run(context));
        }

        /// <summary>
        /// The rebuild script must not run ccmsetup.exe /uninstall by default.
        /// <para>
        /// That is the step that fails: once ccmsetup has cleaned up the folder a previous install
        /// ran from, the MSI uninstall returns 1612 - installation source not available - and the
        /// attempt ends with 0x8007064c. The same applies to /forceinstall, which uninstalls
        /// first. The manual cleanup removes the same things without involving MSI at all, which
        /// is why it is the route that works. Asserted here because it is knowledge from a real
        /// session that a later edit could quietly undo.
        /// </para>
        /// </summary>
        [Fact]
        public void The_rebuild_tool_does_not_rely_on_the_msi_uninstaller()
        {
            var script = WinUpgradeDiag.Core.Remediation.EmbeddedScriptProvider
                .Read("Rebuild-CcmClient.ps1");

            Assert.NotNull(script);
            var text = System.Text.Encoding.UTF8.GetString(script);

            // /forceinstall must appear only in comments explaining why it is not used.
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("#", StringComparison.Ordinal)) { continue; }
                Assert.DoesNotContain("/forceinstall", trimmed, StringComparison.OrdinalIgnoreCase);
            }

            // The uninstaller is reachable, but only behind an explicit switch.
            Assert.Contains("$TryMsiUninstall", text, StringComparison.Ordinal);
            Assert.Contains("1612", text, StringComparison.Ordinal);

            // And the catalogue entry says so too, so the confirmation dialog does not promise
            // a step the script deliberately skips.
            var tool = WinUpgradeDiag.Core.Remediation.ToolCatalog.All
                .Single(t => t.Id == "REBUILD-CLIENT");
            Assert.Contains(tool.Steps, step =>
                step.IndexOf("1612", StringComparison.Ordinal) >= 0);
        }

        /// <summary>
        /// A healthy WMI repository alongside a dead client provider is worth stating: it rules
        /// out the repair everyone reaches for first.
        /// </summary>
        [Fact]
        public void A_healthy_wmi_repository_is_recorded_so_it_is_not_rebuilt_needlessly()
        {
            var finding = Run(ContextWith(BrokenProvider())).Single(f => f.Id == "CC-100");

            var quoted = string.Join("\n", finding.Evidence.Select(e => e.Text));
            Assert.Contains("core WMI repository answers normally", quoted, StringComparison.Ordinal);
        }
    }
}
