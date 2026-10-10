using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;

namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>One task sequence deployment the client has received in policy.</summary>
    public sealed class TaskSequencePolicy
    {
        public string PackageId { get; set; }
        public string Name { get; set; }
        public string DeploymentId { get; set; }

        public string Describe()
        {
            return (string.IsNullOrWhiteSpace(Name) ? "(unnamed)" : Name) + " (" + PackageId +
                   (string.IsNullOrWhiteSpace(DeploymentId) ? "" : ", deployment " + DeploymentId) + ")";
        }
    }

    /// <summary>
    /// Everything that decides whether Software Center offers a deployment, read from this PC.
    /// <para>
    /// A deployment reaches Software Center through a chain: the server targets a device record,
    /// the client downloads policy for that record, evaluates it, and lists the result. When an
    /// upgrade "is never offered", the break is somewhere in that chain, and each link leaves a
    /// trace here: which identity the client registers as, whether it is asking for policy, what
    /// policy it holds, and what Software Center can show. Collected once so nobody has to type
    /// the queries by hand on each PC.
    /// </para>
    /// </summary>
    public sealed class DeploymentState
    {
        /// <summary>The ConfigMgr identity this PC registers as (the SMS Unique Identifier).</summary>
        public string ClientId { get; set; }

        /// <summary>The identity it had before, when the client has re-registered as a new device.</summary>
        public string PreviousClientId { get; set; }

        public DateTime? ClientIdChangeDate { get; set; }

        public bool IdentityChanged => !string.IsNullOrWhiteSpace(PreviousClientId) &&
                                       !string.Equals(PreviousClientId, ClientId, StringComparison.OrdinalIgnoreCase);

        /// <summary>Task sequence deployments this PC holds in policy. Empty when none, or unreadable.</summary>
        public IList<TaskSequencePolicy> TaskSequencePolicies { get; } = new List<TaskSequencePolicy>();
        public bool PolicyReadable { get; set; }

        /// <summary>Task sequences Software Center can currently list.</summary>
        public IList<string> SoftwareCenterTaskSequences { get; } = new List<string>();
        public bool SoftwareCenterReadable { get; set; }

        /// <summary>When the client last asked its management point for machine policy.</summary>
        public DateTime? LastMachinePolicyRequest { get; set; }

        /// <summary>When the client last ran hardware inventory, which feeds query-based collections.</summary>
        public DateTime? LastHardwareInventory { get; set; }

        public IList<string> Errors { get; } = new List<string>();
    }

    public sealed class DeploymentStateCollector
    {
        private const string MachinePolicySchedule = "{00000000-0000-0000-0000-000000000021}";
        private const string HardwareInventoryAction = "{00000000-0000-0000-0000-000000000001}";

        public DeploymentState Collect()
        {
            var state = new DeploymentState();

            Query(state, @"root\ccm", "SELECT ClientId, PreviousClientId, ClientIdChangeDate FROM CCM_Client", row =>
            {
                state.ClientId = row["ClientId"] as string;
                state.PreviousClientId = row["PreviousClientId"] as string;
                state.ClientIdChangeDate = ParseClientDate(row["ClientIdChangeDate"] as string);
            });

            state.PolicyReadable = Query(state, @"root\ccm\policy\machine\actualconfig",
                "SELECT PKG_PackageID, PKG_Name, ADV_AdvertisementID FROM CCM_TaskSequence", row =>
                {
                    var id = row["PKG_PackageID"] as string;
                    if (!string.IsNullOrWhiteSpace(id) &&
                        !state.TaskSequencePolicies.Any(p => p.PackageId == id && p.DeploymentId == row["ADV_AdvertisementID"] as string))
                    {
                        state.TaskSequencePolicies.Add(new TaskSequencePolicy
                        {
                            PackageId = id,
                            Name = row["PKG_Name"] as string,
                            DeploymentId = row["ADV_AdvertisementID"] as string
                        });
                    }
                });

            // Task sequences are listed as programs with program id "*".
            state.SoftwareCenterReadable = Query(state, @"root\ccm\ClientSDK",
                "SELECT Name, PackageID, ProgramID FROM CCM_Program", row =>
                {
                    if ((row["ProgramID"] as string) == "*")
                    {
                        state.SoftwareCenterTaskSequences.Add((row["Name"] as string) + " (" + (row["PackageID"] as string) + ")");
                    }
                });

            Query(state, @"root\ccm\scheduler",
                "SELECT LastTriggerTime FROM CCM_Scheduler_History WHERE ScheduleID='" + MachinePolicySchedule +
                "' AND UserSID='Machine'", row =>
                {
                    state.LastMachinePolicyRequest = Dmtf(row["LastTriggerTime"]);
                });

            Query(state, @"root\ccm\invagt",
                "SELECT LastCycleStartedDate FROM InventoryActionStatus WHERE InventoryActionID='" +
                HardwareInventoryAction + "'", row =>
                {
                    state.LastHardwareInventory = Dmtf(row["LastCycleStartedDate"]);
                });

            return state;
        }

        /// <summary>Runs one query; false when the namespace or class could not be read.</summary>
        private static bool Query(DeploymentState state, string ns, string wql, Action<ManagementBaseObject> each)
        {
            try
            {
                var scope = new ManagementScope(ns);
                scope.Options.Timeout = TimeSpan.FromSeconds(20);
                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql)))
                {
                    searcher.Options.Timeout = TimeSpan.FromSeconds(20);
                    foreach (ManagementBaseObject row in searcher.Get())
                    {
                        using (row)
                        {
                            each(row);
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                state.Errors.Add(ns + ": " + ex.Message.Trim());
                return false;
            }
        }

        private static DateTime? Dmtf(object value)
        {
            var text = value as string;
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            try
            {
                return ManagementDateTimeConverter.ToDateTime(text);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>CCM_Client writes the change date as "MM/dd/yyyy HH:mm:ss".</summary>
        public static DateTime? ParseClientDate(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            DateTime parsed;
            if (DateTime.TryParseExact(text.Trim(), new[] { "MM/dd/yyyy HH:mm:ss", "M/d/yyyy H:mm:ss", "yyyy-MM-dd HH:mm:ss" },
                    CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed) ||
                DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
            {
                return parsed;
            }
            return null;
        }
    }

    /// <summary>
    /// The deployment facts as label/value pairs, so the app, the HTML report and the console all
    /// show the same list in the same words.
    /// </summary>
    public static class DeploymentFacts
    {
        public static IReadOnlyList<KeyValuePair<string, string>> For(SystemState state)
        {
            var facts = new List<KeyValuePair<string, string>>();
            if (state == null)
            {
                return facts;
            }

            var client = state.CcmClient;
            var d = state.Deployment;

            if (client != null && !client.ClientFolderExists)
            {
                Add(facts, "ConfigMgr client", "Not installed");
                return facts;
            }

            Add(facts, "Site / management point",
                client == null ? null : (client.SiteCode ?? "none") + " / " + (client.ManagementPoint ?? "none"));
            Add(facts, "ConfigMgr client ID", d?.ClientId);
            if (d != null)
            {
                Add(facts, "Previous client ID", d.IdentityChanged
                    ? d.PreviousClientId + (d.ClientIdChangeDate.HasValue ? " (changed " + When(d.ClientIdChangeDate) + ")" : "")
                    : "none - the PC has kept the same identity");
            }

            Add(facts, "Provisioning mode", client?.ProvisioningMode == null ? null
                : client.ProvisioningMode.Value
                    ? "ON - the client is ignoring new policy" +
                      (string.IsNullOrWhiteSpace(client.SystemTaskExcludes) ? "" : " (" + client.SystemTaskExcludes + ")")
                    : "Off");

            var request = state.TaskSequenceExecutionRequest;
            Add(facts, "'Task sequence running' record", request == null ? null
                : request.ExecutionRequestExists
                    ? "Present" + (request.PackageId != null ? " (package " + request.PackageId + ")" : "")
                    : "None");

            if (d != null)
            {
                Add(facts, "Task sequences deployed to this PC", !d.PolicyReadable ? "could not be read"
                    : d.TaskSequencePolicies.Count == 0 ? "NONE in the client's policy"
                    : string.Join("; ", d.TaskSequencePolicies.Select(p => p.Describe())));
                Add(facts, "Shown in Software Center", !d.SoftwareCenterReadable ? "could not be read"
                    : d.SoftwareCenterTaskSequences.Count == 0 ? "No task sequence"
                    : string.Join("; ", d.SoftwareCenterTaskSequences));
                Add(facts, "Last machine policy request", When(d.LastMachinePolicyRequest));
                Add(facts, "Last hardware inventory", When(d.LastHardwareInventory));
            }

            var history = state.TaskSequenceHistory;
            if (history != null)
            {
                Add(facts, "Task sequence run history", history.Runs.Count == 0 ? "None recorded"
                    : string.Join("; ", history.Runs.OrderByDescending(r => r.Started ?? DateTime.MinValue).Select(r => r.Describe())));
            }

            return facts;
        }

        private static string When(DateTime? value)
        {
            return value.HasValue ? value.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "never recorded";
        }

        private static void Add(List<KeyValuePair<string, string>> facts, string label, string value)
        {
            facts.Add(new KeyValuePair<string, string>(label, value ?? "unknown"));
        }
    }
}
