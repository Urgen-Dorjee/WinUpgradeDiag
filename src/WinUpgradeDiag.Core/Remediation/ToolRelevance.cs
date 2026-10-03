using System;
using System.Collections.Generic;
using System.Linq;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Orchestration;

namespace WinUpgradeDiag.Core.Remediation
{
    /// <summary>How a tool stands in relation to the machine that was just diagnosed.</summary>
    public enum ToolStanding
    {
        /// <summary>The diagnosis points at this one. There is normally exactly one.</summary>
        Recommended,

        /// <summary>Nothing says it applies and nothing rules it out. Safe to consider.</summary>
        Available,

        /// <summary>This machine fails a condition the tool needs. Running it would do nothing, or harm.</summary>
        NotApplicable
    }

    /// <summary>Where a tool stands, and the sentence explaining why.</summary>
    public sealed class ToolVerdict
    {
        public ToolVerdict(ToolStanding standing, string reason)
        {
            Standing = standing;
            Reason = reason;
        }

        public ToolStanding Standing { get; }

        /// <summary>Why, in one sentence a technician can act on. Never null for anything but Available.</summary>
        public string Reason { get; }

        public bool IsRecommended => Standing == ToolStanding.Recommended;
        public bool IsNotApplicable => Standing == ToolStanding.NotApplicable;
    }

    /// <summary>
    /// Decides which tools this machine actually needs.
    /// <para>
    /// The Tools tab presented twelve options as a flat list, six of them beginning "Fix:" and
    /// differing only in a parenthetical — "interrupted during Setup (keeps the download)" against
    /// "interrupted while downloading (discards the download)". Choosing between those is the
    /// diagnosis, and the diagnosis is the one thing the technician came here without. The run has
    /// already done it: it knows whether there is a download to discard, whether Windows.old is
    /// there to reclaim, and whether the client is broken rather than merely unregistered.
    /// </para>
    /// <para>
    /// Nothing is hidden. A tool ruled out stays visible with the reason, because "there is no
    /// Windows.old on this machine" is itself a useful thing to learn, and because a technician who
    /// disagrees with the reasoning must still be able to act.
    /// </para>
    /// </summary>
    public static class ToolRelevance
    {
        /// <summary>
        /// Standing for one tool. With no diagnostic run everything is Available: the honest answer
        /// before any evidence exists, not a guess dressed up as one.
        /// </summary>
        public static ToolVerdict For(ToolDefinition tool, DiagnosticContext context)
        {
            if (tool == null)
            {
                return new ToolVerdict(ToolStanding.Available, null);
            }

            if (context?.SystemState == null)
            {
                return new ToolVerdict(ToolStanding.Available, null);
            }

            var recommended = Recommended(tool, context);
            if (recommended != null)
            {
                return recommended;
            }

            var ruledOut = RuledOut(tool, context.SystemState);
            if (ruledOut != null)
            {
                return ruledOut;
            }

            return new ToolVerdict(ToolStanding.Available, null);
        }

        /// <summary>
        /// A tool is recommended when a finding prescribes it. The rules engine already attaches a
        /// remediation to each finding; this surfaces that on the Tools tab, where previously it
        /// was only visible by scrolling the Summary.
        /// </summary>
        private static ToolVerdict Recommended(ToolDefinition tool, DiagnosticContext context)
        {
            var verdict = context.Verdict;
            if (verdict == null || !verdict.HasFindings)
            {
                return null;
            }

            foreach (var finding in verdict.Findings)
            {
                var prescribed = finding.Remediation;
                if (prescribed == null)
                {
                    continue;
                }

                if (!string.Equals(prescribed.ScriptName, tool.ScriptName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return new ToolVerdict(
                    ToolStanding.Recommended,
                    "The diagnosis points here: " + finding.Title + ".");
            }

            return null;
        }

        /// <summary>
        /// Conditions that make a tool pointless or harmful on this particular machine, each stated
        /// as the fact that was observed rather than as a rule number.
        /// </summary>
        private static ToolVerdict RuledOut(ToolDefinition tool, SystemState state)
        {
            switch (tool.Id)
            {
                case "REMOVE-LEFTOVERS":
                    return FolderMissing(state, "Windows.old")
                        ? Not("There is no C:\\Windows.old on this machine, so there is no rollback data to reclaim.")
                        : null;

                case "FIX-D":
                    return FolderMissing(state, "$WINDOWS.~BT")
                        ? Not("There is no C:\\$WINDOWS.~BT folder, so there is no half-built Setup folder to remove.")
                        : null;

                case "FIX-A":
                {
                    var cache = state.CcmCache;
                    if (cache == null || !cache.Available)
                    {
                        return null;
                    }

                    return cache.LargestElement == null
                        ? Not("Nothing in the client cache is large enough to be the OS image, so there is no " +
                              "partial download to discard.")
                        : null;
                }

                case "RESET-TS":
                    return state.TaskSequenceExecutionRequest != null &&
                           string.IsNullOrWhiteSpace(state.TaskSequenceExecutionRequest.PackageId)
                        ? Not("No task sequence package id was found on this machine, so there is no run history " +
                              "to clear.")
                        : null;

                case "REBUILD-CLIENT":
                case "REPAIR-CLIENT":
                {
                    var client = state.CcmClient;
                    if (client == null)
                    {
                        return null;
                    }

                    if (!client.ClientFolderExists)
                    {
                        return Not("The ConfigMgr client is not installed on this machine, so there is nothing " +
                                   "to repair or rebuild. Install it instead.");
                    }

                    // Repairing a client that answers normally achieves nothing and restarts it.
                    if (tool.Id == "REPAIR-CLIENT" && client.CcmNamespaceResponds &&
                        !string.IsNullOrWhiteSpace(client.SiteCode))
                    {
                        return Not("The client answers queries and is assigned to site " + client.SiteCode +
                                   ", so it is healthy. Repairing it would restart it for no reason.");
                    }

                    return null;
                }

                default:
                    return null;
            }
        }

        /// <summary>
        /// True only when the folder was actually looked at and found absent. An unexamined folder
        /// is not evidence of anything, and must not rule a tool out.
        /// </summary>
        private static bool FolderMissing(SystemState state, string folderName)
        {
            var folders = state.UpgradeFolders;
            if (folders == null || folders.Count == 0)
            {
                return false;
            }

            var match = folders.FirstOrDefault(f =>
                (f.Path ?? "").IndexOf(folderName, StringComparison.OrdinalIgnoreCase) >= 0);

            return match != null && !match.Exists;
        }

        private static ToolVerdict Not(string reason)
        {
            return new ToolVerdict(ToolStanding.NotApplicable, reason);
        }

        /// <summary>
        /// Orders the catalogue so what the machine needs is at the top and what it cannot use is
        /// at the bottom, keeping the catalogue's own order within each group.
        /// </summary>
        public static IReadOnlyList<ToolDefinition> Order(
            IEnumerable<ToolDefinition> tools, DiagnosticContext context)
        {
            return tools
                .Select((tool, index) => new { tool, index, verdict = For(tool, context) })
                .OrderBy(x => x.verdict.Standing == ToolStanding.Recommended ? 0
                            : x.verdict.Standing == ToolStanding.Available ? 1 : 2)
                .ThenBy(x => x.index)
                .Select(x => x.tool)
                .ToList();
        }
    }
}
