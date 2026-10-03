using System.Collections.Generic;
using WinUpgradeDiag.Core.Remediation;

namespace WinUpgradeDiag.App.ViewModels
{
    /// <summary>Display wrapper for one entry in the Tools catalogue.</summary>
    public sealed class ToolRow : ObservableObject
    {
        private bool _isExpanded;

        public ToolRow(ToolDefinition tool)
        {
            Tool = tool;
        }

        public ToolDefinition Tool { get; }

        public string Id => Tool.Id;
        public string Title => Tool.Title;
        public string Purpose => Tool.Purpose;
        public string ScriptName => Tool.ScriptName;
        public string Risk => Tool.RiskText;
        public string RiskSeverity => Tool.RiskSeverity;
        public string Category => Tool.Category;
        public IReadOnlyList<string> Steps => Tool.Steps;
        public IReadOnlyList<string> Preconditions => Tool.Preconditions;
        public bool HasPreconditions => Tool.Preconditions.Count > 0;
        public bool RequiresParameter => Tool.RequiresParameter;
        public string ParameterPrompt => Tool.ParameterPrompt;
        public string ParameterExample => Tool.ParameterExample;
        public string ParameterHelp => Tool.ParameterHelp;
        public bool HasParameterHelp => !string.IsNullOrWhiteSpace(Tool.ParameterHelp);
        public bool RunsUntilStopped => Tool.RunsUntilStopped;

        /// <summary>Operator-supplied argument, for the tools that take one.</summary>
        private string _parameterValue = "";
        public string ParameterValue
        {
            get => _parameterValue;
            set => Set(ref _parameterValue, value);
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set => Set(ref _isExpanded, value);
        }

        public string ExpanderGlyph => IsExpanded ? "Hide details" : "What will this do?";

        /// <summary>
        /// Where this tool stands against the machine that was just diagnosed. Set after a run.
        /// <para>
        /// Six of the twelve tools are called "Fix: ..." and differ only in a parenthetical.
        /// Choosing between them is the diagnosis, which is the one thing a technician arrives
        /// without — and the run has already worked it out.
        /// </para>
        /// </summary>
        private ToolVerdict _standing = new ToolVerdict(ToolStanding.Available, null);
        public ToolVerdict Standing
        {
            get => _standing;
            set
            {
                if (Set(ref _standing, value))
                {
                    OnPropertyChanged(nameof(IsRecommended));
                    OnPropertyChanged(nameof(IsNotApplicable));
                    OnPropertyChanged(nameof(StandingReason));
                    OnPropertyChanged(nameof(HasStandingReason));
                    OnPropertyChanged(nameof(StandingLabel));
                }
            }
        }

        public bool IsRecommended => Standing.IsRecommended;
        public bool IsNotApplicable => Standing.IsNotApplicable;
        public string StandingReason => Standing.Reason;
        public bool HasStandingReason => !string.IsNullOrWhiteSpace(Standing.Reason);

        public string StandingLabel =>
            Standing.IsRecommended ? "START HERE"
            : Standing.IsNotApplicable ? "DOES NOT APPLY TO THIS MACHINE"
            : "";
    }
}
