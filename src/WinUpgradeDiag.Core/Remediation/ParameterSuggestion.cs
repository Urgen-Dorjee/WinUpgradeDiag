using System;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Orchestration;

namespace WinUpgradeDiag.Core.Remediation
{
    /// <summary>
    /// Works out what a tool's parameter should be, from what the run already collected.
    /// <para>
    /// Every one of these values is sitting in the diagnostic context by the time a technician is
    /// asked to type it. Asking anyway turns a tool into a quiz: the content id of a cache item,
    /// the package id of a task sequence, the site code and management point of a client that is
    /// too broken to open its own control panel. The answer is known; the box should arrive with
    /// it in, and the operator should be correcting rather than researching.
    /// </para>
    /// </summary>
    public static class ParameterSuggestion
    {
        /// <summary>
        /// A suggested value for the tool's parameter, or empty when nothing can be established.
        /// Empty is a legitimate answer and must never be filled with a plausible-looking guess:
        /// a wrong package id passed to a destructive script is worse than an empty box.
        /// </summary>
        public static string For(ToolDefinition tool, DiagnosticContext context)
        {
            if (tool == null || !tool.RequiresParameter)
            {
                return string.Empty;
            }

            switch ((tool.ParameterName ?? "").ToLowerInvariant())
            {
                case "contentid":
                    return ContentId(context);

                case "target":
                    return CcmClientHealthCollector.SuggestTarget() ?? string.Empty;

                case "computername":
                    // Deliberately not this machine. Both tools that take a ComputerName query a
                    // DIFFERENT machine over the network - their own help says "not this machine"
                    // - so prefilling the local name offers the one answer that is certainly
                    // wrong, and offers it as the default.
                    return string.Empty;

                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// The OS package's content id: the largest item in the client cache. On a machine part way
        /// through an in-place upgrade nothing else is that size.
        /// </summary>
        private static string ContentId(DiagnosticContext context)
        {
            var cache = context?.SystemState?.CcmCache;
            if (cache == null || !cache.Available)
            {
                return string.Empty;
            }

            var largest = cache.LargestElement;
            return largest?.ContentId ?? string.Empty;
        }

        /// <summary>
        /// What to say under a box that could not be filled in, naming the step that would produce
        /// the value. Without this an empty field is just a dead end.
        /// </summary>
        public static string WhereToFind(ToolDefinition tool, DiagnosticContext context)
        {
            if (tool == null || !tool.RequiresParameter)
            {
                return null;
            }

            switch ((tool.ParameterName ?? "").ToLowerInvariant())
            {
                case "contentid":
                    return context?.SystemState?.CcmCache == null
                        ? "Run a diagnostic first and this fills in by itself. Otherwise run \"Show downloaded content\" " +
                          "from this tab: it is the item several gigabytes in size."
                        : "No cached item was large enough to be the OS image, so there may be no partial " +
                          "download to discard. Check with \"Show downloaded content\" before running this.";

                case "target":
                    return "Run a diagnostic first and this fills in by itself. Otherwise both values are in " +
                           "ccmsetup.log, and whoever owns ConfigMgr will know them.";

                case "computername":
                    return "The name of the machine you want to look at - not this one. This tool reaches " +
                           "another machine over the network.";

                default:
                    return null;
            }
        }
    }
}
