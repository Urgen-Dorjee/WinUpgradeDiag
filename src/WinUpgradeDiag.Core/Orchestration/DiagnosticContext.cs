using System;
using System.Collections.Generic;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Discovery;
using WinUpgradeDiag.Core.Rules;

namespace WinUpgradeDiag.Core.Orchestration
{
    /// <summary>
    /// Everything one run produced. Each pipeline stage (DESIGN.md §4) writes into this;
    /// phase 1 fills discovery and collection only — no events, findings, or verdict yet.
    /// </summary>
    public sealed class DiagnosticContext
    {
        public string ToolVersion { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public DateTime FinishedAtUtc { get; set; }

        public bool PrivilegedReadEnabled { get; set; }
        public string PrivilegedReadError { get; set; }

        public IReadOnlyList<LogManifestEntry> Manifest { get; set; } = new List<LogManifestEntry>();
        public SystemState SystemState { get; set; }

        /// <summary>
        /// What the run concluded. Null only if the rules stage did not run (for example the run
        /// was cancelled during collection).
        /// </summary>
        public Verdict Verdict { get; set; }

        public bool Cancelled { get; set; }
    }
}
