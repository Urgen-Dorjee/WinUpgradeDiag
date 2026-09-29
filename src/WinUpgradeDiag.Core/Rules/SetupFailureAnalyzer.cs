using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using WinUpgradeDiag.Core.Discovery;
using WinUpgradeDiag.Core.IO;
using WinUpgradeDiag.Core.Orchestration;

namespace WinUpgradeDiag.Core.Rules
{
    /// <summary>
    /// Pulls the actual failure out of Setup's own logs.
    /// <para>
    /// The signature catalogue matches twelve fixed strings, mostly hex codes. On a real rollback
    /// those literals are frequently absent: a machine that reverted from 22H2 had 373 MB of
    /// Panther setupact.log, 104 MB of Rollback setupact.log and a 42 KB setuperr.log all read
    /// successfully, and not one finding quoted a line from any of them. Every finding came from
    /// the registry, WMI, folder existence or smsts.log, and the recommended action was "read the
    /// rollback setupact.log and setupapi.dev.log for the last operation before the revert" — the
    /// tool telling the technician to do the job it had just been given the files for.
    /// </para>
    /// <para>
    /// This does not need a pattern for every possible failure. setuperr.log contains nothing but
    /// errors, so quoting it is always right; and Setup announces its own failures in a small,
    /// stable vocabulary, so the last few of those lines name the operation that lost.
    /// </para>
    /// </summary>
    public sealed class SetupFailureAnalyzer
    {
        /// <summary>setuperr.log is small and is entirely errors, so the whole file is in scope.</summary>
        private const int ErrorLogWindowBytes = 4 * 1024 * 1024;

        /// <summary>
        /// The end of setupact.log is where the failure is. Setup writes the diagnosis and the
        /// revert in its last moments, so a window off the end beats scanning hundreds of MB.
        /// </summary>
        private const int ActLogWindowBytes = 8 * 1024 * 1024;

        private const int MaxQuotedErrors = 12;
        private const int MaxQuotedOperations = 6;

        /// <summary>
        /// Lines Setup writes when something has actually gone wrong. Ordered most specific first:
        /// the first group that matches anything is the one reported, so a concrete failed operation
        /// wins over a generic fatal marker.
        /// </summary>
        private static readonly string[][] FailureVocabulary =
        {
            new[] { "Abandoning apply due to error", "Failed to apply" },
            new[] { "Operation failed", "operation failed:" },
            new[] { "SP Fatal", "Fatal error", "FATAL:" },
            new[] { "CSetupHost::Execute result", "CSetupPlatform::" },
            new[] { "MIG_ROLLBACK", "Rollback started", "Setup has failed" },
            new[] { "returned error", "failed with error", "hr = 0x", "result = 0x" }
        };

        /// <summary>Hex result codes, so a non-zero one can be surfaced as the code to chase.</summary>
        private static readonly Regex ResultCode = new Regex(
            @"0x[0-9A-Fa-f]{8}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly TailReader _reader = new TailReader();

        public IReadOnlyList<Finding> Analyze(
            DiagnosticContext context, IProgress<string> progress, CancellationToken cancellationToken)
        {
            var findings = new List<Finding>();
            if (context?.Manifest == null)
            {
                return findings;
            }

            var setupLogs = context.Manifest
                .Where(m => m.Exists && m.Readable && IsSetupCategory(m.Source.Category))
                .GroupBy(m => m.ResolvedPath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            var errorFinding = FromErrorLogs(setupLogs, progress, cancellationToken);
            if (errorFinding != null)
            {
                findings.Add(errorFinding);
            }

            var operationFinding = FromActivityLogs(setupLogs, progress, cancellationToken);
            if (operationFinding != null)
            {
                findings.Add(operationFinding);
            }

            return findings;
        }

        private static bool IsSetupCategory(LogSourceCategory category)
        {
            return category == LogSourceCategory.SetupRollback ||
                   category == LogSourceCategory.SetupCurrent ||
                   category == LogSourceCategory.SetupCompleted ||
                   category == LogSourceCategory.PreviousOs;
        }

        // ------------------------------------------------------------------ setuperr.log

        /// <summary>
        /// Quotes what Setup itself recorded as an error. No pattern is involved: setuperr.log
        /// exists only to hold errors, so every line in it is by definition a finding.
        /// </summary>
        private Finding FromErrorLogs(
            IReadOnlyList<LogManifestEntry> logs, IProgress<string> progress, CancellationToken cancellationToken)
        {
            var errorLogs = logs
                .Where(m => Path.GetFileName(m.ResolvedPath)
                    .StartsWith("setuperr", StringComparison.OrdinalIgnoreCase))
                // The live attempt and its rollback describe this failure; a completed upgrade's
                // setuperr.log is from a different, older run.
                .OrderBy(m => CategoryRank(m.Source.Category))
                .ToList();

            var evidence = new List<Evidence>();
            var codes = new List<string>();

            foreach (var log in errorLogs)
            {
                if (cancellationToken.IsCancellationRequested || evidence.Count >= MaxQuotedErrors)
                {
                    break;
                }

                progress?.Report("Reading " + Path.GetFileName(log.ResolvedPath) + " for the recorded errors");

                var lines = ReadTailLines(log.ResolvedPath, ErrorLogWindowBytes);
                if (lines == null)
                {
                    continue;
                }

                // Newest last in the file; newest first is what a reader wants.
                foreach (var line in Distinct(lines).Reverse().Take(MaxQuotedErrors - evidence.Count))
                {
                    evidence.Add(new Evidence(log.ResolvedPath, null, Trim(line)));
                    codes.AddRange(NonZeroCodes(line));
                }
            }

            if (evidence.Count == 0)
            {
                return null;
            }

            var distinctCodes = codes
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(4)
                .ToList();

            var meaning =
                "These are the errors Setup itself recorded, newest first. setuperr.log contains " +
                "nothing but failures, so every line here is something Setup could not do." +
                (distinctCodes.Count > 0
                    ? " The result code" + (distinctCodes.Count == 1 ? " is " : "s are ") +
                      string.Join(", ", distinctCodes) + "."
                    : "");

            return new Finding(
                "SU-100",
                "Setup recorded " + evidence.Count.ToString(CultureInfo.CurrentCulture) +
                (evidence.Count == 1 ? " error" : " errors") + " of its own",
                Severity.Critical,
                Confidence.High,
                meaning,
                distinctCodes.Count > 0
                    ? "Search the first code above — " + distinctCodes[0] + " — together with the component " +
                      "named on its line. That pair identifies the failure; the lines below are the machine's " +
                      "own words for it, not an inference."
                    : "Work from the newest line down. Each names a component and an operation Setup could not complete.",
                evidence);
        }

        // ------------------------------------------------------------------ setupact.log

        /// <summary>
        /// Finds the operation Setup was performing when it gave up, from the end of setupact.log.
        /// </summary>
        private Finding FromActivityLogs(
            IReadOnlyList<LogManifestEntry> logs, IProgress<string> progress, CancellationToken cancellationToken)
        {
            var actLogs = logs
                .Where(m => Path.GetFileName(m.ResolvedPath)
                    .StartsWith("setupact", StringComparison.OrdinalIgnoreCase))
                .OrderBy(m => CategoryRank(m.Source.Category))
                .ToList();

            foreach (var log in actLogs)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return null;
                }

                progress?.Report("Reading the end of " + Path.GetFileName(log.ResolvedPath) + " for the failing operation");

                var lines = ReadTailLines(log.ResolvedPath, ActLogWindowBytes);
                if (lines == null)
                {
                    continue;
                }

                foreach (var group in FailureVocabulary)
                {
                    var hits = lines
                        .Where(l => group.Any(marker => l.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0))
                        .Where(l => !IsSuccessLine(l))
                        .ToList();

                    if (hits.Count == 0)
                    {
                        continue;
                    }

                    var evidence = Distinct(hits)
                        .Reverse()
                        .Take(MaxQuotedOperations)
                        .Select(l => new Evidence(log.ResolvedPath, null, Trim(l)))
                        .ToList();

                    return new Finding(
                        "SU-101",
                        "The last thing Setup did before it stopped",
                        Severity.Critical,
                        Confidence.Medium,
                        "Taken from the end of " + Path.GetFileName(log.ResolvedPath) + ", newest first. Setup writes " +
                        "its diagnosis in its final moments, so the operation named here is the one that lost — or the " +
                        "one immediately after it.",
                        "Read the newest line first. Where it names a device, driver or migration unit, that is the " +
                        "thing to remove, update or exclude before the next attempt.",
                        evidence);
                }
            }

            return null;
        }

        /// <summary>
        /// A result of zero is Setup reporting success, and the vocabulary that reports failures is
        /// the same vocabulary that reports them succeeding.
        /// </summary>
        private static bool IsSuccessLine(string line)
        {
            return line.IndexOf("result = 0x0]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("result = 0x00000000", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("hr = 0x0]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("hr = 0x00000000", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   line.IndexOf("error 0x0]", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static IEnumerable<string> NonZeroCodes(string line)
        {
            foreach (Match match in ResultCode.Matches(line))
            {
                if (!string.Equals(match.Value, "0x00000000", StringComparison.OrdinalIgnoreCase))
                {
                    yield return match.Value.ToUpperInvariant().Replace("0X", "0x");
                }
            }
        }

        /// <summary>
        /// Rollback and the current attempt describe the failure being investigated. A completed
        /// upgrade's logs are from an earlier, successful run and must never outrank them.
        /// </summary>
        private static int CategoryRank(LogSourceCategory category)
        {
            switch (category)
            {
                case LogSourceCategory.SetupRollback: return 0;
                case LogSourceCategory.SetupCurrent: return 1;
                case LogSourceCategory.PreviousOs: return 2;
                default: return 3;
            }
        }

        private IReadOnlyList<string> ReadTailLines(string path, int windowBytes)
        {
            try
            {
                var tail = _reader.ReadTail(path, windowBytes);
                return tail.Lines
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();
            }
            catch (Exception)
            {
                // Degrade, never crash: an unreadable log is already recorded as a gap.
                return null;
            }
        }

        /// <summary>
        /// Collapses repeats while keeping order. Setup logs the same failure once per retry, and
        /// six copies of one line is one fact presented six times.
        /// </summary>
        private static IEnumerable<string> Distinct(IEnumerable<string> lines)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();

            foreach (var line in lines)
            {
                if (seen.Add(Key(line)))
                {
                    result.Add(line);
                }
            }

            return result;
        }

        /// <summary>
        /// Strips the leading timestamp so two logs of the same failure collapse together rather
        /// than looking distinct because they happened a second apart.
        /// </summary>
        private static string Key(string line)
        {
            var trimmed = Trim(line);
            var comma = trimmed.IndexOf(',');
            return comma > 0 && comma < 40 ? trimmed.Substring(comma + 1).Trim() : trimmed;
        }

        private static string Trim(string line)
        {
            var trimmed = (line ?? "").Trim();
            return trimmed.Length <= 400 ? trimmed : trimmed.Substring(0, 400) + "…";
        }
    }
}
