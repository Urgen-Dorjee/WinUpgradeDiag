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
            IReadOnlyList<string> gaps)
        {
            Kind = kind;
            Headline = headline;
            Detail = detail;
            Findings = findings ?? new List<Finding>();
            Gaps = gaps ?? new List<string>();
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

        public IReadOnlyList<Finding> ContributingFindings =>
            Findings.Count > 1 ? Findings.Skip(1).ToList() : new List<Finding>();

        public bool HasFindings => Findings.Count > 0;
        public bool HasGaps => Gaps.Count > 0;

        public int CriticalCount => Findings.Count(f => f.Severity == Severity.Critical);
        public int WarningCount => Findings.Count(f => f.Severity == Severity.Warning);

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
}
