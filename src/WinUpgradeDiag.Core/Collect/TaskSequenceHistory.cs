using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;
using Microsoft.Win32;

namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>The most recent recorded run of one task sequence on this machine.</summary>
    public sealed class TaskSequenceRun
    {
        public string PackageId { get; set; }

        /// <summary>The name from the client's policy, or null when policy could not be read.</summary>
        public string Name { get; set; }

        /// <summary>"Success", "Failure", or whatever the client recorded.</summary>
        public string State { get; set; }

        public DateTime? Started { get; set; }
        public string ExitCode { get; set; }

        public bool Failed => !string.Equals(State, "Success", StringComparison.OrdinalIgnoreCase);

        public string Describe()
        {
            return (string.IsNullOrWhiteSpace(Name) ? "Task sequence" : "\"" + Name + "\"") +
                   " (" + PackageId + ")" +
                   (Started.HasValue ? ", last run " + Started.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "") +
                   ", " + (string.IsNullOrWhiteSpace(State) ? "result not recorded" : State.ToLowerInvariant()) +
                   (string.IsNullOrWhiteSpace(ExitCode) || ExitCode == "0" ? "" : " with code " + ExitCode);
        }
    }

    public sealed class TaskSequenceHistory
    {
        public IList<TaskSequenceRun> Runs { get; } = new List<TaskSequenceRun>();
        public string Error { get; set; }

        /// <summary>The run "Let a deployment run again" would reset, or null when none failed.</summary>
        public TaskSequenceRun ToReset => TaskSequenceHistoryReader.Choose(Runs);
    }

    /// <summary>
    /// Finds which task sequence the client is refusing to run again, so nobody has to look up a
    /// package id.
    /// <para>
    /// The client records each run under Execution History\System\&lt;package id&gt;, with its
    /// result. A task sequence whose last run failed is the one Software Center will not offer
    /// again; the client's policy gives its name. Only failed runs are candidates: clearing the
    /// history of one that succeeded would let a required deployment run a second time.
    /// </para>
    /// </summary>
    public static class TaskSequenceHistoryReader
    {
        public const string HistoryPath =
            @"SOFTWARE\Microsoft\SMS\Mobile Client\Software Distribution\Execution History\System";

        public static TaskSequenceHistory Read()
        {
            var history = new TaskSequenceHistory();
            var names = PolicyNames();

            try
            {
                using (var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var root = hive.OpenSubKey(HistoryPath))
                {
                    if (root == null)
                    {
                        return history;
                    }

                    foreach (var packageId in root.GetSubKeyNames())
                    {
                        using (var package = root.OpenSubKey(packageId))
                        {
                            var latest = package == null ? null : LatestRun(package);
                            if (latest == null)
                            {
                                continue;
                            }

                            // Task sequences run program "*"; policy confirms it when readable.
                            string name;
                            var isTaskSequence = names.TryGetValue(packageId, out name) || latest.Item1 == "*";
                            if (!isTaskSequence)
                            {
                                continue;
                            }

                            latest.Item2.PackageId = packageId;
                            latest.Item2.Name = name;
                            history.Runs.Add(latest.Item2);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                history.Error = ex.Message;
            }

            return history;
        }

        /// <summary>
        /// The most recently started failed run. Separate from the reading so it can be tested
        /// without a ConfigMgr client.
        /// </summary>
        public static TaskSequenceRun Choose(IEnumerable<TaskSequenceRun> runs)
        {
            return (runs ?? Enumerable.Empty<TaskSequenceRun>())
                .Where(r => r != null && r.Failed && !string.IsNullOrWhiteSpace(r.PackageId))
                .OrderByDescending(r => r.Started ?? DateTime.MinValue)
                .FirstOrDefault();
        }

        public static DateTime? ParseTime(string text)
        {
            DateTime parsed;
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed) ||
                DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out parsed))
            {
                return parsed;
            }
            return null;
        }

        /// <summary>(program id, run) for the newest run recorded under one package.</summary>
        private static Tuple<string, TaskSequenceRun> LatestRun(RegistryKey package)
        {
            Tuple<string, TaskSequenceRun> latest = null;
            foreach (var runId in package.GetSubKeyNames())
            {
                using (var run = package.OpenSubKey(runId))
                {
                    if (run == null)
                    {
                        continue;
                    }

                    var entry = new TaskSequenceRun
                    {
                        State = run.GetValue("_State") as string,
                        Started = ParseTime(run.GetValue("_RunStartTime") as string),
                        ExitCode = run.GetValue("SuccessOrFailureCode")?.ToString()
                    };

                    if (latest == null || (entry.Started ?? DateTime.MinValue) > (latest.Item2.Started ?? DateTime.MinValue))
                    {
                        latest = Tuple.Create(run.GetValue("_ProgramID") as string, entry);
                    }
                }
            }
            return latest;
        }

        /// <summary>Task sequence names by package id, from machine policy. Empty when unreadable.</summary>
        private static Dictionary<string, string> PolicyNames()
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var scope = new ManagementScope(@"root\ccm\policy\machine\actualconfig");
                scope.Options.Timeout = TimeSpan.FromSeconds(15);
                using (var searcher = new ManagementObjectSearcher(scope,
                           new ObjectQuery("SELECT PKG_PackageID, PKG_Name FROM CCM_TaskSequence")))
                {
                    searcher.Options.Timeout = TimeSpan.FromSeconds(15);
                    foreach (ManagementBaseObject o in searcher.Get())
                    {
                        using (o)
                        {
                            var id = o["PKG_PackageID"] as string;
                            if (!string.IsNullOrWhiteSpace(id))
                            {
                                names[id] = o["PKG_Name"] as string;
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // No client, or a client too broken to answer: the history alone still works.
            }
            return names;
        }
    }
}
