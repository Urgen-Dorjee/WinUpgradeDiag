using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Orchestration;

namespace WinUpgradeDiag.Core.Rules
{
    /// <summary>
    /// Works out why Software Center is not offering a deployment, from the chain that delivers it.
    /// <para>
    /// The field case: an overnight upgrade (deployment A) failed and rolled back; a second
    /// deployment of the same image (B) was added for the PC and never appeared; removing the PC
    /// from A's collection reported it was not a member. A new deployment that never arrives is
    /// not caused by A's history - that is filed under A's package. It means the delivery chain is
    /// broken: the client is paused (provisioning mode, reported by the client analyzer), the
    /// client registers under an identity the deployments do not target, or no deployment reaches
    /// this PC's current record at all.
    /// </para>
    /// </summary>
    public sealed class DeploymentAnalyzer
    {
        /// <summary>An identity change older than this is unlikely to be the current problem.</summary>
        private static readonly TimeSpan RecentIdentityChange = TimeSpan.FromDays(60);

        /// <summary>A healthy client asks for policy every hour by default.</summary>
        private static readonly TimeSpan PolicyOverdue = TimeSpan.FromHours(24);

        public IReadOnlyList<Finding> Analyze(
            DiagnosticContext context, IProgress<string> progress, CancellationToken cancellationToken)
        {
            var findings = new List<Finding>();
            var state = context?.SystemState;
            var d = state?.Deployment;
            var client = state?.CcmClient;
            if (d == null || client == null || !client.ClientFolderExists || cancellationToken.IsCancellationRequested)
            {
                return findings;
            }

            progress?.Report("Checking how deployments reach this PC");

            var now = context.StartedAtUtc == default(DateTime) ? DateTime.UtcNow : context.StartedAtUtc;
            var paused = client.ProvisioningMode == true;

            var identity = FromIdentity(d, now);
            if (identity != null)
            {
                findings.Add(identity);
            }

            // A paused client is already the answer; anything below would be its consequence.
            if (paused)
            {
                return findings;
            }

            var polling = FromPolicyPolling(d, client, now);
            if (polling != null)
            {
                findings.Add(polling);
            }

            var nothing = FromNothingDeployed(d, client, identity != null);
            if (nothing != null)
            {
                findings.Add(nothing);
            }

            return findings;
        }

        /// <summary>
        /// The client re-registered as a new device. Deployments and collection memberships made
        /// against its old record no longer reach it.
        /// </summary>
        private static Finding FromIdentity(DeploymentState d, DateTime nowUtc)
        {
            if (!d.IdentityChanged)
            {
                return null;
            }

            var recent = !d.ClientIdChangeDate.HasValue ||
                         nowUtc - d.ClientIdChangeDate.Value.ToUniversalTime() <= RecentIdentityChange;
            if (!recent)
            {
                return null;
            }

            var evidence = new List<Evidence>
            {
                new Evidence(@"WMI: root\ccm CCM_Client", null, "ClientId = " + d.ClientId),
                new Evidence(@"WMI: root\ccm CCM_Client", null, "PreviousClientId = " + d.PreviousClientId),
            };
            if (d.ClientIdChangeDate.HasValue)
            {
                evidence.Add(new Evidence(@"WMI: root\ccm CCM_Client", null,
                    "ClientIdChangeDate = " + d.ClientIdChangeDate.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            }

            return new Finding(
                "CC-107",
                "This PC re-registered with ConfigMgr as a new device" +
                (d.ClientIdChangeDate.HasValue
                    ? " on " + d.ClientIdChangeDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : ""),
                Severity.Critical,
                Confidence.Medium,
                "The client changed its ConfigMgr identity, which happens when an upgrade or a repair makes " +
                "it believe it is on different hardware. The server now has two device records for this PC. " +
                "Deployments and collection memberships attached to the old record do not reach it - which is " +
                "how a PC can fail to receive a new deployment and also not appear in a collection it was added to.",
                "In the ConfigMgr console, search Devices for this PC's name. Keep the record whose SMS Unique " +
                "Identifier matches " + d.ClientId + " and delete the other. Add that record to the deployment's " +
                "collection, update the collection's membership, then run Machine Policy Retrieval on this PC.",
                evidence);
        }

        /// <summary>The client is not asking for policy, so nothing new can reach it.</summary>
        private static Finding FromPolicyPolling(DeploymentState d, CcmClientHealthInfo client, DateTime nowUtc)
        {
            if (!d.LastMachinePolicyRequest.HasValue ||
                !string.Equals(client.CcmExecState, "Running", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var age = nowUtc - d.LastMachinePolicyRequest.Value.ToUniversalTime();
            if (age <= PolicyOverdue)
            {
                return null;
            }

            return new Finding(
                "CC-109",
                "The client has not asked for policy in " + (int)age.TotalDays + " day(s)",
                Severity.Warning,
                Confidence.Medium,
                "A working client asks its management point for machine policy every hour. This one last asked on " +
                d.LastMachinePolicyRequest.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) +
                ", so a deployment added since then cannot have reached it, whatever the console says.",
                "Run Machine Policy Retrieval & Evaluation Cycle from the Configuration Manager applet and read " +
                @"C:\Windows\CCM\Logs\PolicyAgent.log for the reply.",
                new[]
                {
                    new Evidence(@"WMI: root\ccm\scheduler CCM_Scheduler_History", null,
                        "Machine policy last requested " +
                        d.LastMachinePolicyRequest.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                },
                command: "Invoke-CimMethod -Namespace root\\ccm -ClassName SMS_Client -MethodName TriggerSchedule " +
                         "-Arguments @{ sScheduleID = '{00000000-0000-0000-0000-000000000021}' }");
        }

        /// <summary>
        /// The client is healthy and listening, and holds no task sequence at all: no deployment
        /// reaches this PC's current record. That is decided on the server.
        /// </summary>
        private static Finding FromNothingDeployed(DeploymentState d, CcmClientHealthInfo client, bool identityReported)
        {
            if (!d.PolicyReadable || d.TaskSequencePolicies.Count > 0 || !client.CcmNamespaceResponds ||
                string.IsNullOrWhiteSpace(client.SiteCode))
            {
                return null;
            }

            return new Finding(
                "CC-108",
                "No upgrade is deployed to this PC's current ConfigMgr record",
                identityReported ? Severity.Warning : Severity.Critical,
                Confidence.High,
                "The client is installed, registered, not paused and holds policy - but no task sequence is in it. " +
                "Nothing on this PC is hiding the upgrade; the server is not sending it. That happens when the PC is " +
                "not a member of the deployment's collection, when a query or exclusion rule keeps it out (for " +
                "example one based on the OS version or on a failed status), or when the deployment targets an old " +
                "record of this PC.",
                "In the console, find the device whose SMS Unique Identifier is " + (d.ClientId ?? "this PC's client ID") +
                ", check which collections it is in, and check the deployment's collection rules and exclusions. " +
                "Wiping WMI and reinstalling the client appears to fix this only because it registers the PC as a " +
                "brand-new device that a collection query then picks up.",
                new[]
                {
                    new Evidence(@"WMI: root\ccm\policy\machine\actualconfig", null, "CCM_TaskSequence: no instances"),
                    new Evidence(@"WMI: root\ccm CCM_Client", null, "ClientId = " + (d.ClientId ?? "unknown")),
                    new Evidence("Registry: SMS\\Mobile Client", null, "Assigned site code = " + client.SiteCode)
                });
        }
    }
}
