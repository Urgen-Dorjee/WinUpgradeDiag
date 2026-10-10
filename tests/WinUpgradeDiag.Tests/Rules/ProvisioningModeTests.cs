using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Orchestration;
using WinUpgradeDiag.Core.Rules;
using Xunit;

namespace WinUpgradeDiag.Tests.Rules
{
    /// <summary>
    /// The field case: an overnight upgrade crashed, Windows rolled back, and on roughly one PC in
    /// ten Software Center no longer offered the upgrade. The client was installed, running and
    /// registered, so every existing check passed. It had been left in provisioning mode, which
    /// the team was clearing by wiping WMI and reinstalling the client.
    /// </summary>
    public class ProvisioningModeTests
    {
        private static CcmClientHealthInfo HealthyClient(bool? provisioning)
        {
            return new CcmClientHealthInfo
            {
                ClientFolderExists = true,
                CcmExecInstalled = true,
                CcmExecState = "Running",
                CcmNamespaceResponds = true,
                SiteCode = "ABC",
                ProvisioningMode = provisioning,
                SystemTaskExcludes = provisioning == true ? "SMSClientPolicy;SMSSoftwareDistribution" : null
            };
        }

        private static DiagnosticContext Context(CcmClientHealthInfo health, bool upgradeRunning = false)
        {
            return new DiagnosticContext
            {
                StartedAtUtc = new DateTime(2026, 10, 9, 9, 0, 0, DateTimeKind.Utc),
                Manifest = new List<WinUpgradeDiag.Core.Discovery.LogManifestEntry>(),
                SystemState = new SystemState
                {
                    CcmClient = health,
                    Processes = new ProcessSnapshot(new List<ProcessInfo>
                    {
                        new ProcessInfo("TSManager", upgradeRunning, null, null)
                    })
                }
            };
        }

        private static Finding Provisioning(DiagnosticContext context)
        {
            return new CcmClientAnalyzer().Analyze(context, null, CancellationToken.None)
                .SingleOrDefault(f => f.Id == "CC-106");
        }

        [Fact]
        public void A_client_left_in_provisioning_mode_is_named_as_the_cause()
        {
            var finding = Provisioning(Context(HealthyClient(true)));

            Assert.NotNull(finding);
            Assert.Equal(Severity.Critical, finding.Severity);
            Assert.Contains("provisioning mode", finding.Title);
            Assert.Contains(finding.Evidence, e => e.Text == "ProvisioningMode = true");
            Assert.Contains(finding.Evidence, e => e.Text.Contains("SMSClientPolicy"));
        }

        /// <summary>During a live upgrade provisioning mode is supposed to be on.</summary>
        [Fact]
        public void Provisioning_mode_during_a_live_upgrade_is_not_a_fault()
        {
            Assert.Null(Provisioning(Context(HealthyClient(true), upgradeRunning: true)));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(null)]
        public void A_client_not_in_provisioning_mode_reports_nothing(bool? provisioning)
        {
            Assert.Null(Provisioning(Context(HealthyClient(provisioning))));
        }

        [Fact]
        public void The_finding_gives_the_one_command_not_a_reinstall()
        {
            var finding = Provisioning(Context(HealthyClient(true)));

            Assert.Contains("SetClientProvisioningMode", finding.Command);
            Assert.Contains("No WMI rebuild", finding.Action);
        }
    }
}
