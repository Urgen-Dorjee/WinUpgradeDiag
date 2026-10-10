using System;
using System.Collections.Generic;
using System.Linq;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Orchestration;
using WinUpgradeDiag.Core.Rules;
using Xunit;

namespace WinUpgradeDiag.Tests.Rules
{
    /// <summary>
    /// The Summary panel that answers "why isn't the upgrade offered in Software Center?". It walks
    /// the delivery chain in order and names the first broken link.
    /// </summary>
    public class SoftwareCenterCheckTests
    {
        private static readonly DateTime Run = new DateTime(2026, 10, 9, 9, 0, 0, DateTimeKind.Utc);

        private static SystemState Healthy()
        {
            var d = new DeploymentState
            {
                ClientId = "GUID:NEW-1111",
                PolicyReadable = true,
                SoftwareCenterReadable = true,
                LastMachinePolicyRequest = Run.AddMinutes(-30).ToLocalTime()
            };
            d.TaskSequencePolicies.Add(new TaskSequencePolicy { PackageId = "ABC00789", Name = "Windows 11 upgrade B" });
            d.SoftwareCenterTaskSequences.Add("Windows 11 upgrade B (ABC00789)");

            return new SystemState
            {
                CcmClient = new CcmClientHealthInfo
                {
                    ClientFolderExists = true, CcmExecInstalled = true, CcmExecState = "Running",
                    CcmNamespaceResponds = true, SiteCode = "ABC", ProvisioningMode = false
                },
                Deployment = d,
                TaskSequenceExecutionRequest = OrphanedTaskSequenceInfo.NotFound(),
                Processes = new ProcessSnapshot(new List<ProcessInfo>())
            };
        }

        private static SoftwareCenterCheck Check(SystemState state)
        {
            var context = new DiagnosticContext
            {
                StartedAtUtc = Run,
                Manifest = new List<WinUpgradeDiag.Core.Discovery.LogManifestEntry>(),
                SystemState = state
            };
            var findings = new CcmClientAnalyzer().Analyze(context, null, System.Threading.CancellationToken.None)
                .Concat(new DeploymentAnalyzer().Analyze(context, null, System.Threading.CancellationToken.None));
            return SoftwareCenterCheck.For(state, findings, Run);
        }

        [Fact]
        public void A_healthy_chain_says_nothing_is_blocking_it()
        {
            var check = Check(Healthy());

            Assert.Equal(LinkStatus.Ok, check.Overall);
            Assert.Contains("Nothing on this PC is blocking", check.Conclusion);
            Assert.DoesNotContain(check.Links, l => l.Status == LinkStatus.Problem);
        }

        /// <summary>The field case: B added but never offered, after A crashed and rolled back.</summary>
        [Fact]
        public void The_A_and_B_case_names_the_first_broken_link_and_shows_the_others()
        {
            var state = Healthy();
            state.Deployment.PreviousClientId = "GUID:OLD-9999";
            state.Deployment.ClientIdChangeDate = new DateTime(2026, 10, 7, 2, 14, 0);
            state.Deployment.SoftwareCenterTaskSequences.Clear();
            state.CcmClient.ProvisioningMode = true;

            var check = Check(state);

            Assert.Equal(LinkStatus.Problem, check.Overall);
            Assert.Contains("re-registered", check.Conclusion);
            Assert.Equal(LinkStatus.Problem, check.Links.Single(l => l.Label == "Device identity").Status);
            Assert.Equal(LinkStatus.Problem, check.Links.Single(l => l.Label == "Accepting new policy").Status);
        }

        [Fact]
        public void A_paused_client_gives_the_one_command()
        {
            var state = Healthy();
            state.CcmClient.ProvisioningMode = true;

            var check = Check(state);

            Assert.Contains("provisioning mode", check.Conclusion);
            Assert.Contains("SetClientProvisioningMode", check.Command);
        }

        [Fact]
        public void Nothing_deployed_points_at_the_server()
        {
            var state = Healthy();
            state.Deployment.TaskSequencePolicies.Clear();
            state.Deployment.SoftwareCenterTaskSequences.Clear();

            var check = Check(state);

            Assert.Contains("No upgrade is deployed", check.Conclusion);
            Assert.Equal(LinkStatus.Problem, check.Links.Single(l => l.Label == "Upgrade deployed to this PC").Status);
        }

        [Fact]
        public void Deployed_but_not_listed_is_its_own_answer()
        {
            var state = Healthy();
            state.Deployment.SoftwareCenterTaskSequences.Clear();

            var check = Check(state);

            Assert.Contains("does not list it", check.Conclusion);
        }

        [Fact]
        public void A_pc_without_a_client_says_so_plainly()
        {
            var check = Check(new SystemState { CcmClient = new CcmClientHealthInfo() });

            Assert.Contains("no ConfigMgr client", check.Conclusion);
            Assert.Single(check.Links);
        }
    }
}
