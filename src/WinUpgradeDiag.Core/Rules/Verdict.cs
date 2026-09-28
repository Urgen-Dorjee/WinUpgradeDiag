using System.Collections.Generic;
using System.Linq;

namespace WinUpgradeDiag.Core.Rules
{
    /// <summary>What the run concluded, in the shape the Summary tab and the report both show.</summary>
    public enum VerdictKind
    {
        /// <summary>An upgrade or task sequence is running right now. Advise patience, never cleanup.</summary>
        InProgress,

        /// <summary>A cause was identified with usable confidence.</summary>
        CauseIdentified,

        /// <summary>Something is wrong but the evidence does not name a single cause.</summary>
        Inconclusive,

        /// <summary>No sign of a failed upgrade on this machine.</summary>
        NoFailureFound,

        /// <summary>Too little could be read to say anything — usually not elevated.</summary>
        InsufficientEvidence
    }

    /// <summary>
    /// The one sentence a technician under pressure reads, plus the ranked findings behind it.
    /// <para>
    /// DESIGN.md §5: "If nothing conclusive is found, say so plainly and show the manifest — a
    /// confident wrong answer is worse than 'unknown'." That is why
    /// <see cref="VerdictKind.NoFailureFound"/> and <see cref="VerdictKind.InsufficientEvidence"/>
    /// are first-class outcomes rather than an empty findings list.
    /// </para>
    /// </summary>
    public sealed class Verdict
    {
        public Verdict(
            VerdictKind kind,
            string headline,
            string detail,
            IReadOnlyList<Finding> findings,
            IReadOnlyList<string> gaps,
            Finding cause = null)
        {
            Kind = kind;
            Headline = headline;
            Detail = detail;
            Findings = findings ?? new List<Finding>();
            Gaps = gaps ?? new List<string>();
            Cause = cause;
        }

        public VerdictKind Kind { get; }

        /// <summary>One sentence, no jargon. The answer.</summary>
        public string Headline { get; }

        /// <summary>A sentence or two of supporting explanation.</summary>
        public string Detail { get; }

        /// <summary>Every finding, most important first.</summary>
        public IReadOnlyList<Finding> Findings { get; }

        /// <summary>
        /// What could not be examined. Shown next to the verdict so a clean result from a partial
        /// collection is never mistaken for a clean machine.
        /// </summary>
        public IReadOnlyList<string> Gaps { get; }

        public Finding TopFinding => Findings.Count > 0 ? Findings[0] : null;

        /// <summary>
        /// The finding the headline was written from, when there is one.
        /// <para>
        /// This is not always <see cref="TopFinding"/>. Ranking puts a live upgrade first so its
        /// "change nothing" advice leads the list, but the headline may come from the Setup-progress
        /// rule underneath it. Reading the action off the first finding regardless is how a report
        /// ends up printing "leave it alone" under a headline that says Setup is blocked.
        /// </para>
        /// </summary>
        public Finding Cause { get; }

        public IReadOnlyList<Finding> ContributingFindings =>
            Findings.Count > 1 ? Findings.Skip(1).ToList() : new List<Finding>();

        public bool HasFindings => Findings.Count > 0;
        public bool HasGaps => Gaps.Count > 0;

        public int CriticalCount => Findings.Count(f => f.Severity == Severity.Critical);
        public int WarningCount => Findings.Count(f => f.Severity == Severity.Warning);

        /// <summary>
        /// What to tell the technician to do, derived from the conclusion rather than from whichever
        /// finding happened to sort first.
        /// <para>
        /// The findings list is a list of observations; only some of them are the answer. On a
        /// machine already running Windows 11 the highest-ranked finding was a pending reboot, so
        /// the report printed "Restart the machine before attempting the upgrade again" directly
        /// underneath "No failed upgrade found - this machine is already running Windows 11". The
        /// advice has to follow the verdict, and where the verdict is "nothing is wrong" the honest
        /// answer is that there is nothing to do.
        /// </para>
        /// </summary>
        public VerdictAction Action
        {
            get
            {
                var source = Cause ?? TopFinding;

                switch (Kind)
                {
                    case VerdictKind.InProgress:
                        // Never take this from a finding. An in-progress upgrade is the one state
                        // where the destructive advice attached to other findings would do harm.
                        return new VerdictAction(
                            "DO NOTHING YET",
                            "Leave the machine alone and let Setup finish. Do not clear the task sequence, " +
                            "restart the machine, or delete the $WINDOWS.~BT folder while it is running. " +
                            "Re-run this diagnostic in 30 minutes if nothing has changed.",
                            null);

                    case VerdictKind.CauseIdentified:
                        return source == null
                            ? null
                            : new VerdictAction("RECOMMENDED ACTION", source.Action, source.Command);

                    case VerdictKind.Inconclusive:
                        return source == null
                            ? null
                            : new VerdictAction(
                                "WHERE TO START",
                                "No single cause is confirmed, so treat this as the first thing to rule out " +
                                "rather than the answer: " + Lower(source.Action),
                                source.Command);

                    case VerdictKind.InsufficientEvidence:
                        return new VerdictAction(
                            "RECOMMENDED ACTION",
                            "Re-run this tool as an administrator on the affected machine. Until then nothing " +
                            "below rules anything out.",
                            null);

                    default:
                        // NoFailureFound. Any findings present are conditions worth knowing about;
                        // none of them is a cause, because there is no failure for them to cause.
                        return new VerdictAction(
                            "NOTHING TO DO",
                            HasFindings
                                ? "No failed upgrade was found, so there is nothing to fix here. The observations " +
                                  "below are worth noting before the next upgrade attempt, but none of them has " +
                                  "broken anything on this machine."
                                : "No failed upgrade was found and nothing needs attention on this machine.",
                            null);
                }
            }
        }

        /// <summary>Lower-cases the first letter so a sentence can be quoted inside another one.</summary>
        private static string Lower(string sentence)
        {
            if (string.IsNullOrEmpty(sentence) || !char.IsUpper(sentence[0]))
            {
                return sentence;
            }

            return char.ToLowerInvariant(sentence[0]) + sentence.Substring(1);
        }

        /// <summary>
        /// How the findings section should be introduced. When nothing failed, calling these
        /// "causes ranked by likelihood" states something untrue about every row in the list.
        /// </summary>
        public string FindingsHeading =>
            Kind == VerdictKind.NoFailureFound || Kind == VerdictKind.InProgress ? "Observations" : "Findings";

        public string FindingsPreamble
        {
            get
            {
                switch (Kind)
                {
                    case VerdictKind.NoFailureFound:
                        return "Conditions found on this machine. None of them caused a failure - there is no " +
                               "failed upgrade here. They are listed because they are worth knowing before the " +
                               "next attempt.";
                    case VerdictKind.InProgress:
                        return "What the machine looks like right now, while the upgrade is still running. Advice " +
                               "attached to individual items does not apply until Setup has finished.";
                    case VerdictKind.Inconclusive:
                        return "Ranked by how likely each is to be the cause. None reached the confidence needed " +
                               "to name it the answer. Every item quotes the evidence that produced it.";
                    case VerdictKind.InsufficientEvidence:
                        return "The only things visible without the logs that could not be read. This is not a " +
                               "diagnosis, and an empty or short list here says nothing about the machine.";
                    default:
                        return "Ranked by how likely each is to be the cause. Every item quotes the evidence that " +
                               "produced it.";
                }
            }
        }

        /// <summary>Severity used to colour the verdict card.</summary>
        public Severity DisplaySeverity
        {
            get
            {
                switch (Kind)
                {
                    case VerdictKind.InProgress:
                        return Severity.Info;
                    case VerdictKind.NoFailureFound:
                        return Severity.Info;
                    case VerdictKind.CauseIdentified:
                        return Severity.Critical;
                    default:
                        return Findings.Count > 0 ? Findings[0].Severity : Severity.Warning;
                }
            }
        }
    }

    /// <summary>The single instruction that goes with a verdict, and the label it is shown under.</summary>
    public sealed class VerdictAction
    {
        public VerdictAction(string label, string text, string command)
        {
            Label = label;
            Text = text;
            Command = command;
        }

        /// <summary>Heading, e.g. "RECOMMENDED ACTION" or "NOTHING TO DO".</summary>
        public string Label { get; }

        public string Text { get; }

        /// <summary>A command to copy, when the action has one. Never run by this tool.</summary>
        public string Command { get; }

        public bool HasCommand => !string.IsNullOrWhiteSpace(Command);
        public bool HasText => !string.IsNullOrWhiteSpace(Text);
    }
}
