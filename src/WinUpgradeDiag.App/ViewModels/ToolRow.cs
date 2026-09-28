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
    }
}
