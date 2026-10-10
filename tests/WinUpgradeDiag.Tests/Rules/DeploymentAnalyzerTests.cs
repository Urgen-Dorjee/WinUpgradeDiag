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
    /// The field case: deployment A failed overnight and rolled back; deployment B of the same
    /// image was added for the PC and never appeared; removing the PC from A's collection said it
    /// was not a member. These tests pin down which link of the delivery chain each state names.
    /// </summary>
    public class DeploymentAnalyzerTests
    {
        private static readonly DateTime Run = new DateTime(2026, 10, 9, 9, 0, 0, DateTimeKind.Utc);

        private static CcmClientHealthInfo Client(bool? provisioning = false)
        {
            return new CcmClientHealthInfo
            {
                ClientFolderExists = true,
                CcmExecInstalled = true,
                CcmExecState = "Running",
                CcmNamespaceResponds = true,
                SiteCode = "ABC",
                ProvisioningMode = provisioning
            };
        }

        private static DeploymentState Deployment(params string[] taskSequences)
        {
            var d = new DeploymentState
            {
                ClientId = "GUID:NEW-0000-1111",
                PolicyReadable = true,
                SoftwareCenterReadable = true,
                LastMachinePolicyRequest = Run.AddMinutes(-40).ToLocalTime()
            };
            foreach (var ts in taskSequences)
            {
                d.TaskSequencePolicies.Add(new TaskSequencePolicy { PackageId = ts, Name = "Windows 11 " + ts });
            }
            return d;
        }

        private static IReadOnlyList<Finding> Analyze(CcmClientHealthInfo client, DeploymentState d)
        {
            var context = new DiagnosticContext
            {
                StartedAtUtc = Run,
                SystemState = new SystemState { CcmClient = client, Deployment = d }
            };
            return new DeploymentAnalyzer().Analyze(context, null, CancellationToken.None);
        }

        /// <summary>
        /// A re-registered client is why B never arrives and the PC is "not in" A's collection:
        /// both were attached to the old record.
        /// </summary>
        [Fact]
        public void A_new_identity_since_the_upgrade_is_named_with_both_ids()
        {
            var d = Deployment();
            d.PreviousClientId = "GUID:OLD-9999-8888";
            d.ClientIdChangeDate = new DateTime(2026, 10, 7, 2, 14, 0);

            var finding = Analyze(Client(), d).Single(f => f.Id == "CC-107");

            Assert.Equal(Severity.Critical, finding.Severity);
            Assert.Contains("2026-10-07", finding.Title);
            Assert.Contains("GUID:NEW-0000-1111", finding.Action);
            Assert.Contains(finding.Evidence, e => e.Text.Contains("GUID:OLD-9999-8888"));
        }

        [Fact]
        public void An_identity_change_long_ago_is_not_the_current_problem()
        {
            var d = Deployment("ABC00456");
            d.PreviousClientId = "GUID:OLD-9999-8888";
            d.ClientIdChangeDate = new DateTime(2025, 1, 5, 10, 0, 0);

            Assert.DoesNotContain(Analyze(Client(), d), f => f.Id == "CC-107");
        }

        /// <summary>Healthy, listening, and holding no task sequence: the server is not sending one.</summary>
        [Fact]
        public void A_healthy_client_with_nothing_deployed_points_at_the_server()
        {
            var finding = Analyze(Client(), Deployment()).Single(f => f.Id == "CC-108");

            Assert.Equal(Severity.Critical, finding.Severity);
            Assert.Contains("collection", finding.Meaning);
            Assert.Contains("GUID:NEW-0000-1111", finding.Action);
        }

        [Fact]
        public void A_deployed_task_sequence_is_not_reported_as_missing()
        {
            Assert.Empty(Analyze(Client(), Deployment("ABC00456")));
        }

        /// <summary>A paused client explains everything after it; reporting "nothing deployed" would mislead.</summary>
        [Fact]
        public void A_paused_client_is_left_to_the_provisioning_mode_finding()
        {
            var findings = Analyze(Client(provisioning: true), Deployment());

            Assert.DoesNotContain(findings, f => f.Id == "CC-108" || f.Id == "CC-109");
        }

        [Fact]
        public void A_client_that_stopped_asking_for_policy_is_named()
        {
            var d = Deployment("ABC00456");
            d.LastMachinePolicyRequest = Run.AddDays(-3).ToLocalTime();

            var finding = Analyze(Client(), d).Single(f => f.Id == "CC-109");

            Assert.Contains("3 day", finding.Title);
            Assert.NotNull(finding.Command);
        }

        [Fact]
        public void Unreadable_policy_is_not_mistaken_for_no_deployment()
        {
            var d = Deployment();
            d.PolicyReadable = false;

            Assert.DoesNotContain(Analyze(Client(), d), f => f.Id == "CC-108");
        }

        [Fact]
        public void The_facts_show_every_link_of_the_chain()
        {
            var d = Deployment("ABC00456");
            d.PreviousClientId = "GUID:OLD-9999-8888";
            d.SoftwareCenterTaskSequences.Add("Windows 11 ABC00456 (ABC00456)");
            var state = new SystemState
            {
                CcmClient = Client(provisioning: true),
                Deployment = d,
                TaskSequenceExecutionRequest = OrphanedTaskSequenceInfo.Found("ABC00456", "ABC20001")
            };

            var facts = DeploymentFacts.For(state).ToDictionary(f => f.Key, f => f.Value);

            Assert.StartsWith("ON", facts["Provisioning mode"]);
            Assert.Equal("GUID:NEW-0000-1111", facts["ConfigMgr client ID"]);
            Assert.StartsWith("GUID:OLD-9999-8888", facts["Previous client ID"]);
            Assert.StartsWith("Present", facts["'Task sequence running' record"]);
            Assert.Contains("ABC00456", facts["Task sequences deployed to this PC"]);
            Assert.Contains("ABC00456", facts["Shown in Software Center"]);
        }

        [Fact]
        public void No_client_is_one_plain_fact()
        {
            var facts = DeploymentFacts.For(new SystemState { CcmClient = new CcmClientHealthInfo() });

            Assert.Equal("Not installed", facts.Single().Value);
        }

        [Theory]
        [InlineData("10/07/2026 02:14:33", 2026, 10, 7)]
        [InlineData("2026-10-07 02:14:33", 2026, 10, 7)]
        public void The_client_id_change_date_is_read(string text, int y, int m, int day)
        {
            var parsed = DeploymentStateCollector.ParseClientDate(text);

            Assert.Equal(new DateTime(y, m, day), parsed.Value.Date);
        }
    }
}
