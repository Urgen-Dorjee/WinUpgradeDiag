using System.Collections.Generic;

namespace WinUpgradeDiag.Core.Remediation
{
    /// <summary>How much damage an action could do if it were run at the wrong moment.</summary>
    public enum RemediationRisk
    {
        /// <summary>Reads only. Safe to run at any time, including on a healthy machine.</summary>
        ReadOnly,

        /// <summary>
        /// Restarts services or clears client state. Interrupts work in progress but destroys
        /// nothing that cannot be rebuilt.
        /// </summary>
        Disruptive,

        /// <summary>
        /// Deletes content, folders or rollback data. Cannot be undone, and in the wrong state
        /// would break a machine that was about to recover on its own.
        /// </summary>
        Destructive
    }

    /// <summary>
    /// A specific recovery script, with its parameters already resolved from what the tool
    /// collected, plus the preconditions that must still hold when it runs.
    /// <para>
    /// This is the bridge between diagnosis and the existing PowerShell recovery set. The tool
    /// decides <em>which</em> script and supplies the arguments a technician would otherwise have
    /// to go and find by hand — the cache ContentId, the task sequence package id. It stops there
    /// deliberately: <c>AGENTS.md</c> constraint #2 makes diagnosis read-only, and the value of
    /// that guarantee is what gets this tool approved to run on a clinical endpoint at all.
    /// </para>
    /// </summary>
    public sealed class RemediationAction
    {
        public RemediationAction(
            string id,
            string scriptName,
            string title,
            string summary,
            RemediationRisk risk,
            IReadOnlyList<string> steps,
            IReadOnlyList<string> preconditions,
            string commandLine,
            string blockedReason = null)
        {
            Id = id;
            ScriptName = scriptName;
            Title = title;
            Summary = summary;
            Risk = risk;
            Steps = steps ?? new List<string>();
            Preconditions = preconditions ?? new List<string>();
            CommandLine = commandLine;
            BlockedReason = blockedReason;
        }

        /// <summary>Short identifier, e.g. "FIX-B".</summary>
        public string Id { get; }

        /// <summary>The script that performs it, e.g. "Fix-B-SetupInterrupted.ps1".</summary>
        public string ScriptName { get; }

        public string Title { get; }

        /// <summary>One or two sentences on what it is for.</summary>
        public string Summary { get; }

        public RemediationRisk Risk { get; }

        /// <summary>What the script will actually do, in plain language, in order.</summary>
        public IReadOnlyList<string> Steps { get; }

        /// <summary>Conditions that must hold. Re-checked immediately before any execution.</summary>
        public IReadOnlyList<string> Preconditions { get; }

        /// <summary>
        /// A complete, copy-and-paste command with every parameter filled in, or null when a
        /// required parameter could not be determined — see <see cref="BlockedReason"/>.
        /// </summary>
        public string CommandLine { get; }

        /// <summary>
        /// Why this action cannot be offered ready-to-run, or null if it can. Stated rather than
        /// silently omitted, so the technician knows a fix exists and what is missing.
        /// </summary>
        public string BlockedReason { get; }

        public bool IsReady => !string.IsNullOrWhiteSpace(CommandLine) && BlockedReason == null;

        public string RiskText
        {
            get
            {
                switch (Risk)
                {
                    case RemediationRisk.ReadOnly: return "Read-only";
                    case RemediationRisk.Disruptive: return "Disruptive";
                    case RemediationRisk.Destructive: return "Destructive";
                    default: return Risk.ToString();
                }
            }
        }

        /// <summary>Maps risk onto the same severity vocabulary the rest of the UI colours by.</summary>
        public string RiskSeverity
        {
            get
            {
                switch (Risk)
                {
                    case RemediationRisk.Destructive: return "Critical";
                    case RemediationRisk.Disruptive: return "Warning";
                    default: return "Info";
                }
            }
        }
    }
}
