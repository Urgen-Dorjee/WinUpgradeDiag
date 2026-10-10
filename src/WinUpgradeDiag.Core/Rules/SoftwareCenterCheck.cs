using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WinUpgradeDiag.Core.Collect;

namespace WinUpgradeDiag.Core.Rules
{
    public enum LinkStatus
    {
        Ok,
        Problem,
        Unknown
    }

    /// <summary>One link in the chain that brings a deployment to Software Center.</summary>
    public sealed class ChainLink
    {
        public ChainLink(string label, string value, LinkStatus status)
        {
            Label = label;
            Value = value ?? "unknown";
            Status = status;
        }

        public string Label { get; }
        public string Value { get; }
        public LinkStatus Status { get; }

        /// <summary>For the colour converters: good, critical or info.</summary>
        public string StatusKey => Status == LinkStatus.Ok ? "good" : Status == LinkStatus.Problem ? "critical" : "info";

        public string StatusText => Status == LinkStatus.Ok ? "OK" : Status == LinkStatus.Problem ? "PROBLEM" : "-";
    }

    /// <summary>
    /// "Why isn't the upgrade offered in Software Center?" answered link by link.
    /// <para>
    /// A deployment reaches Software Center through a fixed chain: a working client, registered
    /// under the identity the server targets, accepting policy, not blocked by a task sequence it
    /// thinks is running, asking for policy, receiving the deployment, and listing it. The first
    /// broken link is the answer. Laid out in that order, a technician sees where the chain
    /// breaks without reading a list of thirty unrelated machine facts.
    /// </para>
    /// </summary>
    public sealed class SoftwareCenterCheck
    {
        private static readonly TimeSpan PolicyOverdue = TimeSpan.FromHours(24);

        private SoftwareCenterCheck(IReadOnlyList<ChainLink> links, string conclusion, string action,
            string command, LinkStatus overall)
        {
            Links = links;
            Conclusion = conclusion;
            Action = action;
            Command = command;
            Overall = overall;
        }

        public IReadOnlyList<ChainLink> Links { get; }
        public string Conclusion { get; }
        public string Action { get; }
        public string Command { get; }
        public LinkStatus Overall { get; }

        public string OverallKey => Overall == LinkStatus.Ok ? "good" : Overall == LinkStatus.Problem ? "critical" : "info";
        public bool HasAction => !string.IsNullOrWhiteSpace(Action);
        public bool HasCommand => !string.IsNullOrWhiteSpace(Command);

        public static SoftwareCenterCheck For(SystemState state, IEnumerable<Finding> findings, DateTime runUtc)
        {
            var links = new List<ChainLink>();
            var found = (findings ?? Enumerable.Empty<Finding>()).ToList();
            Func<string, Finding> finding = id => found.FirstOrDefault(f => f.Id == id);

            var client = state?.CcmClient;
            var d = state?.Deployment;

            // 1. A working, registered client.
            if (client == null)
            {
                links.Add(new ChainLink("ConfigMgr client", "could not be checked", LinkStatus.Unknown));
                return new SoftwareCenterCheck(links, "The ConfigMgr client could not be checked.", null, null, LinkStatus.Unknown);
            }
            if (!client.ClientFolderExists)
            {
                links.Add(new ChainLink("ConfigMgr client", "Not installed", LinkStatus.Problem));
                return new SoftwareCenterCheck(links,
                    "There is no ConfigMgr client on this PC, so Software Center has nothing to offer.",
                    "Install the client from the console (Install Client) or with ccmsetup.exe and the site's " +
                    "SMSSITECODE and SMSMP.", null, LinkStatus.Problem);
            }

            var running = string.Equals(client.CcmExecState, "Running", StringComparison.OrdinalIgnoreCase);
            var registered = !string.IsNullOrWhiteSpace(client.SiteCode);
            var clientOk = running && registered && (client.CcmNamespaceResponds || !client.CcmExecInstalled);
            links.Add(new ChainLink("ConfigMgr client",
                (running ? "Running" : "NOT running (" + (client.CcmExecState ?? "absent") + ")") +
                (registered ? ", site " + client.SiteCode : ", NOT registered with a site") +
                (client.CcmNamespaceResponds ? "" : ", its WMI does not answer"),
                clientOk ? LinkStatus.Ok : LinkStatus.Problem));

            // 2. The identity the server targets.
            if (d == null || string.IsNullOrWhiteSpace(d.ClientId))
            {
                links.Add(new ChainLink("Device identity", "could not be read", LinkStatus.Unknown));
            }
            else if (finding("CC-107") != null)
            {
                links.Add(new ChainLink("Device identity",
                    "CHANGED" + (d.ClientIdChangeDate.HasValue ? " on " + Day(d.ClientIdChangeDate.Value) : "") +
                    ": now " + d.ClientId + ", was " + d.PreviousClientId, LinkStatus.Problem));
            }
            else
            {
                links.Add(new ChainLink("Device identity", d.ClientId +
                    (d.IdentityChanged ? " (changed long ago, from " + d.PreviousClientId + ")" : " (unchanged)"), LinkStatus.Ok));
            }

            // 3. Accepting policy at all.
            if (client.ProvisioningMode == true)
            {
                links.Add(new ChainLink("Accepting new policy",
                    finding("CC-106") != null
                        ? "NO - left in provisioning mode"
                        : "No - provisioning mode is on, which is expected while an upgrade runs",
                    finding("CC-106") != null ? LinkStatus.Problem : LinkStatus.Unknown));
            }
            else
            {
                links.Add(new ChainLink("Accepting new policy",
                    client.ProvisioningMode == false ? "Yes (provisioning mode off)" : "could not be read",
                    client.ProvisioningMode == false ? LinkStatus.Ok : LinkStatus.Unknown));
            }

            // 4. Not waiting on a task sequence that is no longer running.
            var request = state.TaskSequenceExecutionRequest;
            links.Add(request == null
                ? new ChainLink("Leftover 'running' task sequence", "could not be read", LinkStatus.Unknown)
                : request.ExecutionRequestExists
                    ? new ChainLink("Leftover 'running' task sequence",
                        finding("TS-001") != null ? "YES - the client thinks one is still running" : "Present, and an upgrade is running",
                        finding("TS-001") != null ? LinkStatus.Problem : LinkStatus.Unknown)
                    : new ChainLink("Leftover 'running' task sequence", "None", LinkStatus.Ok));

            // 5. Asking for policy.
            if (d?.LastMachinePolicyRequest == null)
            {
                links.Add(new ChainLink("Asking for policy", "no record of a request", LinkStatus.Unknown));
            }
            else
            {
                var age = runUtc - d.LastMachinePolicyRequest.Value.ToUniversalTime();
                links.Add(new ChainLink("Asking for policy",
                    "Last asked " + Stamp(d.LastMachinePolicyRequest.Value) +
                    (age > PolicyOverdue ? " - " + (int)age.TotalDays + " day(s) ago" : ""),
                    age > PolicyOverdue ? LinkStatus.Problem : LinkStatus.Ok));
            }

            // 6. Receiving the deployment.
            if (d == null || !d.PolicyReadable)
            {
                links.Add(new ChainLink("Upgrade deployed to this PC", "could not be read", LinkStatus.Unknown));
            }
            else if (d.TaskSequencePolicies.Count == 0)
            {
                links.Add(new ChainLink("Upgrade deployed to this PC", "NONE - the server is not sending one", LinkStatus.Problem));
            }
            else
            {
                links.Add(new ChainLink("Upgrade deployed to this PC",
                    string.Join("; ", d.TaskSequencePolicies.Select(p => p.Describe())), LinkStatus.Ok));
            }

            // 7. Listing it.
            if (d == null || !d.SoftwareCenterReadable)
            {
                links.Add(new ChainLink("Listed in Software Center", "could not be read", LinkStatus.Unknown));
            }
            else if (d.SoftwareCenterTaskSequences.Count == 0)
            {
                links.Add(new ChainLink("Listed in Software Center", "No task sequence listed",
                    d.TaskSequencePolicies.Count > 0 ? LinkStatus.Problem : LinkStatus.Unknown));
            }
            else
            {
                links.Add(new ChainLink("Listed in Software Center", string.Join("; ", d.SoftwareCenterTaskSequences), LinkStatus.Ok));
            }

            // Context, not a link: the last run and how it ended.
            var last = state.TaskSequenceHistory?.Runs.OrderByDescending(r => r.Started ?? DateTime.MinValue).FirstOrDefault();
            links.Add(new ChainLink("Last task sequence run", last?.Describe() ?? "none recorded", LinkStatus.Unknown));

            return Conclude(links, found, client, d);
        }

        private static SoftwareCenterCheck Conclude(
            List<ChainLink> links, List<Finding> found, CcmClientHealthInfo client, DeploymentState d)
        {
            // The finding behind each link, in chain order: the first broken one is the answer.
            var byLink = new[]
            {
                Tuple.Create("ConfigMgr client", new[] { "CC-100", "CC-101", "CC-102", "CC-103" }),
                Tuple.Create("Device identity", new[] { "CC-107" }),
                Tuple.Create("Accepting new policy", new[] { "CC-106" }),
                Tuple.Create("Leftover 'running' task sequence", new[] { "TS-001" }),
                Tuple.Create("Asking for policy", new[] { "CC-109" }),
                Tuple.Create("Upgrade deployed to this PC", new[] { "CC-108" }),
            };

            foreach (var link in links.Where(l => l.Status == LinkStatus.Problem))
            {
                var ids = byLink.FirstOrDefault(b => b.Item1 == link.Label)?.Item2 ?? new string[0];
                var f = found.FirstOrDefault(x => ids.Contains(x.Id));
                if (f != null)
                {
                    return new SoftwareCenterCheck(links, f.Title, f.Action, f.Command, LinkStatus.Problem);
                }
                if (link.Label == "Listed in Software Center")
                {
                    return new SoftwareCenterCheck(links,
                        "The upgrade is deployed to this PC but Software Center does not list it",
                        "Check the deployment's available time, any maintenance window and its rerun setting on the " +
                        "server, then run Machine Policy Retrieval & Evaluation Cycle on this PC.",
                        null, LinkStatus.Problem);
                }
                return new SoftwareCenterCheck(links, link.Label + ": " + link.Value, null, null, LinkStatus.Problem);
            }

            if (links.All(l => l.Status != LinkStatus.Problem) &&
                d != null && d.TaskSequencePolicies.Count > 0 && d.SoftwareCenterTaskSequences.Count > 0)
            {
                return new SoftwareCenterCheck(links,
                    "Nothing on this PC is blocking the upgrade: it is deployed and Software Center lists it",
                    null, null, LinkStatus.Ok);
            }

            return new SoftwareCenterCheck(links,
                "No break found in the parts that could be read - see the links marked '-' below",
                null, null, LinkStatus.Unknown);
        }

        private static string Day(DateTime value)
        {
            return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private static string Stamp(DateTime value)
        {
            return value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }
    }
}
