using System.Collections.Generic;
using System.Linq;
using WinUpgradeDiag.Core.Rules;

namespace WinUpgradeDiag.App.ViewModels
{
    /// <summary>Display wrapper for one finding in the Summary tab.</summary>
    public sealed class FindingRow : ObservableObject
    {
        private bool _isExpanded;

        public FindingRow(Finding finding, bool expanded)
        {
            Finding = finding;
            _isExpanded = expanded;
            EvidenceLines = finding.Evidence
                .Select(e => new EvidenceRow(e))
                .ToList();
        }

        public Finding Finding { get; }

        public string Id => Finding.Id;
        public string Title => Finding.Title;
        public string Meaning => Finding.Meaning;
        public string Action => Finding.Action;
        public string Command => Finding.Command;
        public bool HasCommand => Finding.HasCommand;
        public string Severity => Finding.Severity.ToString();
        public string Confidence => Finding.ConfidenceText;
        public IReadOnlyList<EvidenceRow> EvidenceLines { get; }
        public bool HasEvidence => EvidenceLines.Count > 0;

        /// <summary>Findings start expanded only when they are the thing to act on.</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set => Set(ref _isExpanded, value);
        }

        public string ExpanderGlyph => IsExpanded ? "Hide details" : "Show details";
    }

    /// <summary>One evidence line, with its origin formatted for display.</summary>
    public sealed class EvidenceRow
    {
        public EvidenceRow(Evidence evidence)
        {
            Evidence = evidence;
        }

        public Evidence Evidence { get; }

        public string Text => Evidence.Text;

        /// <summary>e.g. "setupact.log line 2,481,003" or "WMI: root\ccm\SoftMgmtAgent".</summary>
        public string Origin
        {
            get
            {
                var source = Evidence.Source ?? "";
                var name = source.Contains("\\") ? System.IO.Path.GetFileName(source) : source;
                return Evidence.LineNumber.HasValue
                    ? name + "  line " + Evidence.LineNumber.Value.ToString("N0")
                    : name;
            }
        }

        /// <summary>Full path, shown as a tooltip so the short origin stays readable.</summary>
        public string FullSource => Evidence.Source;
    }
}
