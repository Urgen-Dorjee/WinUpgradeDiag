using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using WinUpgradeDiag.Core.Collect;
using WinUpgradeDiag.Core.Orchestration;

namespace WinUpgradeDiag.Core.Rules
{
    /// <summary>
    /// Reports what Microsoft's SetupDiag concluded, where Windows has already run it.
    /// <para>
    /// SetupDiag is Microsoft's own analyser for failed upgrades, and since Windows 10 2004 Setup runs
    /// it automatically when an upgrade fails. Its result is on the machine before anyone opens a
    /// log. This tool used to work everything out for itself and never looked at that answer.
    /// </para>
    /// </summary>
    public sealed class SetupDiagAnalyzer
    {
        /// <summary>Older than this before the run, and the result describes an earlier attempt.</summary>
        private static readonly TimeSpan Stale = TimeSpan.FromDays(45);

        public IReadOnlyList<Finding> Analyze(
            DiagnosticContext context, IProgress<string> progress, CancellationToken cancellationToken)
        {
            var findings = new List<Finding>();
            if (context?.Manifest == null || cancellationToken.IsCancellationRequested)
            {
                return findings;
            }

            progress?.Report("Reading what Windows' own upgrade diagnosis (SetupDiag) found");

            var results = context.Manifest
                .Where(m => m.Exists && Path.GetFileName(m.ResolvedPath)
                    .Equals("SetupDiagResults.xml", StringComparison.OrdinalIgnoreCase))
                .GroupBy(m => m.ResolvedPath, StringComparer.OrdinalIgnoreCase)
                .Select(g => SetupDiagResultsReader.ReadFile(g.Key))
                .Where(r => r != null && r.HasConclusion)
                .OrderByDescending(r => r.WrittenUtc ?? DateTime.MinValue)
                .ToList();

            // The registry copy stands in when the file has been tidied away.
            var chosen = results.FirstOrDefault();
            if (chosen == null)
            {
                var fromRegistry = context.SystemState?.SetupDiag;
                if (fromRegistry != null && fromRegistry.HasConclusion)
                {
                    chosen = fromRegistry;
                }
            }

            if (chosen != null)
            {
                findings.Add(FromResult(chosen, context.StartedAtUtc));
            }

            return findings;
        }

        private static Finding FromResult(SetupDiagResult r, DateTime runUtc)
        {
            var stale = r.WrittenUtc.HasValue && runUtc != default(DateTime) && runUtc - r.WrittenUtc.Value > Stale;

            var evidence = new List<Evidence>();
            if (!string.IsNullOrWhiteSpace(r.ProfileName))
            {
                evidence.Add(new Evidence(r.Source, null, "Rule matched: " + r.ProfileName));
            }
            if (!string.IsNullOrWhiteSpace(r.ErrorCode) || !string.IsNullOrWhiteSpace(r.ExtendedErrorCode))
            {
                evidence.Add(new Evidence(r.Source, null,
                    "Error " + (r.ErrorCode ?? "?") +
                    (string.IsNullOrWhiteSpace(r.ExtendedErrorCode) ? "" : ", extended " + r.ExtendedErrorCode)));
            }
            foreach (var m in r.Messages.Take(6))
            {
                evidence.Add(new Evidence(r.Source, null, m));
            }
            foreach (var d in r.DriverLines.Take(6))
            {
                evidence.Add(new Evidence(r.Source, null, d));
            }
            if (!string.IsNullOrWhiteSpace(r.FailureDetails))
            {
                evidence.Add(new Evidence(r.Source, null, r.FailureDetails));
            }

            var title = "Windows' own upgrade diagnosis: " +
                        (string.IsNullOrWhiteSpace(r.ProfileName) ? "see its messages" : Humanise(r.ProfileName)) +
                        (stale ? " (from an earlier attempt)" : "");

            var meaning =
                "SetupDiag is Microsoft's analyser for failed upgrades. Windows ran it when the upgrade failed " +
                "and recorded this result" +
                (r.WrittenUtc.HasValue
                    ? " on " + r.WrittenUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                    : "") +
                ". " +
                (r.Messages.Count > 0 ? r.Messages[0] : "") +
                (stale ? " This result is more than six weeks older than this run, so it may describe a previous attempt." : "");

            var action = r.Remediation.Count > 0
                ? "Microsoft's suggested remediation: " + string.Join(" ", r.Remediation.Take(3))
                : (r.DriverLines.Count > 0
                    ? "SetupDiag names the device or driver above. Update or remove it before retrying, and confirm " +
                      "by disabling that device alone and running the upgrade again."
                    : "Read SetupDiag's messages above alongside the other findings - where they name the same " +
                      "phase or driver, that is the cause.");

            return new Finding(
                "SD-100",
                title,
                stale ? Severity.Warning : Severity.Critical,
                stale ? Confidence.Low : Confidence.High,
                meaning.Trim(),
                action,
                evidence);
        }

        /// <summary>"DriverInstallFailure" reads as "Driver install failure".</summary>
        public static string Humanise(string profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName))
            {
                return profileName;
            }

            var spaced = Regex.Replace(profileName.Trim(), "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
            return spaced.Substring(0, 1).ToUpperInvariant() + spaced.Substring(1).ToLowerInvariant()
                .Replace(" os ", " OS ").Replace(" wim", " WIM");
        }
    }
}
