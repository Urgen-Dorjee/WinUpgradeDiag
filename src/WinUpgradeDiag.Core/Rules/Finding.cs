using System.Collections.Generic;

namespace WinUpgradeDiag.Core.Rules
{
    /// <summary>How much a finding matters to the diagnosis.</summary>
    public enum Severity
    {
        /// <summary>Background context; true but not a problem.</summary>
        Info = 0,

        /// <summary>Contributing factor, or something that will bite on the next attempt.</summary>
        Warning = 1,

        /// <summary>This is, or is very likely, the cause.</summary>
        Critical = 2
    }

    /// <summary>
    /// How far the evidence goes. docs/RULES.md: High means the evidence names the cause, Medium
    /// is a strong inference, Low is suggestive and needs a human.
    /// </summary>
    public enum Confidence
    {
        Low = 0,
        Medium = 1,
        High = 2
    }

    /// <summary>
    /// One quotable piece of proof. AGENTS.md is blunt about this: "a verdict with no quotable
    /// evidence is a bug", so every finding carries the lines that produced it.
    /// </summary>
    public sealed class Evidence
    {
        public Evidence(string source, int? lineNumber, string text)
        {
            Source = source;
            LineNumber = lineNumber;
            Text = text;
        }

        /// <summary>File path, or a description of the state that was read (e.g. "WMI: CCM_TSExecutionRequest").</summary>
        public string Source { get; }

        /// <summary>Line number in the source file, when the evidence came from a log.</summary>
        public int? LineNumber { get; }

        public string Text { get; }
    }

    /// <summary>
    /// A single conclusion the engine reached, with what it means, what to do, and why it fired.
    /// </summary>
    public sealed class Finding
    {
        public Finding(
            string id,
            string title,
            Severity severity,
            Confidence confidence,
            string meaning,
            string action,
            IReadOnlyList<Evidence> evidence,
            string command = null)
        {
            Id = id;
            Title = title;
            Severity = severity;
            Confidence = confidence;
            Meaning = meaning;
            Action = action;
            Evidence = evidence ?? new List<Evidence>();
            Command = command;
        }

        /// <summary>Rule id from docs/RULES.md, e.g. "TS-001".</summary>
        public string Id { get; }

        /// <summary>One line a technician reads first.</summary>
        public string Title { get; }

        public Severity Severity { get; }
        public Confidence Confidence { get; }

        /// <summary>Plain-language explanation of what the signal actually means.</summary>
        public string Meaning { get; }

        /// <summary>The concrete next step.</summary>
        public string Action { get; }

        /// <summary>A command the technician can copy, when the action has one. Never auto-run.</summary>
        public string Command { get; }

        public IReadOnlyList<Evidence> Evidence { get; }

        /// <summary>
        /// The recovery script that addresses this finding, with its parameters already resolved,
        /// or null if no scripted fix applies. Set by the rules engine after evaluation.
        /// </summary>
        public Remediation.RemediationAction Remediation { get; internal set; }

        public bool HasRemediation => Remediation != null;

        public string SeverityText => Severity.ToString();
        public string ConfidenceText => Confidence + " confidence";
        public bool HasCommand => !string.IsNullOrWhiteSpace(Command);
        public bool HasEvidence => Evidence.Count > 0;
        public string EvidenceSummary => Evidence.Count == 1 ? "1 piece of evidence" : Evidence.Count + " pieces of evidence";
    }
}
