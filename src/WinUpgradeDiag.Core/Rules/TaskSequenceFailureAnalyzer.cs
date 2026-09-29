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
    /// Names the task sequence step that failed, and the code it failed with.
    /// <para>
    /// This is the fact on the error dialog the user actually sees — "failed with the error code
    /// 0x80070002 in the task sequence step 'Run Hardware Inventory'" — and the engine had no
    /// concept of it. It carried signatures for bare hex codes, so it could say "0x80070002 appears
    /// in a log" but never "the step called Run Hardware Inventory is the one that returned it".
    /// Without the step name the code is close to useless: the same code means different things
    /// depending on which step produced it.
    /// </para>
    /// </summary>
    public sealed class TaskSequenceFailureAnalyzer
    {
        /// <summary>smsts.log rolls at 5 MB by default, so this covers a whole one.</summary>
        private const int WindowBytes = 8 * 1024 * 1024;

        /// <summary>
        /// The line the engine writes when a step returns non-zero. The step name is everything up
        /// to the sentence-ending period, which is why the capture is non-greedy.
        /// </summary>
        private static readonly Regex FailedAction = new Regex(
            @"Failed to run the action:\s*(?<step>.+?)\.\s*(?<rest>.*)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The group abort that follows a failed step, which gives the phase.</summary>
        private static readonly Regex FailedGroup = new Regex(
            @"execution of the group \((?<group>[^)]+)\) has failed",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>ConfigMgr writes codes bare, as "Error: 80070002", not as 0x80070002.</summary>
        private static readonly Regex ErrorCode = new Regex(
            @"(?:Error:\s*|Code\s*0x|code\s+0x)(?<code>[0-9A-Fa-f]{8})",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Plain meanings for the codes that actually turn up on an in-place upgrade. Deliberately
        /// short: a wrong expansion is worse than none, so anything not listed is reported bare.
        /// </summary>
        private static readonly Dictionary<string, string> CodeMeanings =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "80070002", "the system cannot find the file specified" },
                { "80070003", "the system cannot find the path specified" },
                { "80070005", "access denied" },
                { "80004005", "unspecified error — the step failed but did not say why" },
                { "80070032", "the request is not supported" },
                { "8007000E", "out of memory" },
                { "80070070", "there is not enough space on the disk" },
                { "C1900101", "Setup rolled back — a driver failed during the upgrade" },
                { "80091007", "the hash of the downloaded content does not match" },
                { "87D00267", "the client could not reach the management point" }
            };

        private readonly TailReader _reader = new TailReader();

        public IReadOnlyList<Finding> Analyze(
            DiagnosticContext context, IProgress<string> progress, CancellationToken cancellationToken)
        {
            var findings = new List<Finding>();
            if (context?.Manifest == null)
            {
                return findings;
            }

            var logs = context.Manifest
                .Where(m => m.Exists && m.Readable &&
                            m.Source.Category == LogSourceCategory.TaskSequence &&
                            Path.GetFileName(m.ResolvedPath).StartsWith("smsts", StringComparison.OrdinalIgnoreCase))
                .GroupBy(m => m.ResolvedPath, StringComparer.OrdinalIgnoreCase)
                // Newest log first: an upgrade that ran twice leaves the old attempt behind.
                .Select(g => g.First())
                .OrderByDescending(m => m.LastWriteTimeUtc ?? DateTime.MinValue)
                .ToList();

            foreach (var log in logs)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                progress?.Report("Reading " + Path.GetFileName(log.ResolvedPath) + " for the failing step");

                var finding = FromLog(log);
                if (finding != null)
                {
                    findings.Add(finding);
                    // One named step is the answer. Older logs describe earlier attempts.
                    break;
                }
            }

            return findings;
        }

        private Finding FromLog(LogManifestEntry log)
        {
            IReadOnlyList<string> lines;
            try
            {
                lines = _reader.ReadTail(log.ResolvedPath, WindowBytes).Lines;
            }
            catch (Exception)
            {
                // Degrade, never crash: an unreadable log is already recorded as a gap.
                return null;
            }

            string step = null;
            string code = null;
            string group = null;
            var evidence = new List<Evidence>();

            // Walk forwards and keep the last match: the final failure is the one that stopped it.
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];

                var action = FailedAction.Match(line);
                if (action.Success)
                {
                    step = Clean(action.Groups["step"].Value);
                    // The code is on the same line or the next one; ConfigMgr wraps these.
                    code = FirstCode(line) ?? (i + 1 < lines.Count ? FirstCode(lines[i + 1]) : null);
                    evidence.Clear();
                    evidence.Add(new Evidence(log.ResolvedPath, null, Trim(line)));
                    if (code == null && i + 1 < lines.Count)
                    {
                        evidence.Add(new Evidence(log.ResolvedPath, null, Trim(lines[i + 1])));
                    }
                    continue;
                }

                var grouped = FailedGroup.Match(line);
                if (grouped.Success && step != null)
                {
                    group = Clean(grouped.Groups["group"].Value);
                    evidence.Add(new Evidence(log.ResolvedPath, null, Trim(line)));
                }
            }

            if (step == null)
            {
                return null;
            }

            var title = "Task sequence step “" + step + "” failed" +
                        (code != null ? " with 0x" + code.ToUpperInvariant() : "");

            var meaning = "The task sequence engine stopped here. " +
                          (group != null ? "It was running the “" + group + "” group. " : "") +
                          Explain(code) +
                          " Every other error in this log happened before or after this step; this is the one that " +
                          "ended the deployment.";

            return new Finding(
                "TS-100",
                title,
                Severity.Critical,
                Confidence.High,
                meaning,
                "Go to that step in the task sequence and check what it runs. " + Advice(step, code),
                evidence);
        }

        /// <summary>
        /// Advice that depends on the step, because the same code means different things in
        /// different places. A missing file in an inventory step is not a missing OS image.
        /// </summary>
        private static string Advice(string step, string code)
        {
            var isPostUpgrade =
                step.IndexOf("inventory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                step.IndexOf("client", StringComparison.OrdinalIgnoreCase) >= 0 ||
                step.IndexOf("cleanup", StringComparison.OrdinalIgnoreCase) >= 0 ||
                step.IndexOf("post", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isPostUpgrade)
            {
                return "This step runs after the operating system has already been installed, so Windows itself is " +
                       "in place and the machine is usable — what failed is the tail end of the deployment, and " +
                       "ConfigMgr will still report the whole task sequence as failed. Check whether the step's " +
                       "package content survived the upgrade, and whether the ConfigMgr client had finished " +
                       "re-registering by the time the step ran.";
            }

            if (string.Equals(code, "C1900101", StringComparison.OrdinalIgnoreCase))
            {
                return "This is the Windows Setup phase, so the failure is inside Setup rather than in the task " +
                       "sequence. The Setup findings in this report name the driver or operation involved.";
            }

            return "Its own log will say more than the task sequence log does.";
        }

        private static string Explain(string code)
        {
            if (code == null)
            {
                return "The log does not carry a code on that line.";
            }

            string meaning;
            return CodeMeanings.TryGetValue(code, out meaning)
                ? "0x" + code.ToUpperInvariant() + " is " + meaning + "."
                : "0x" + code.ToUpperInvariant() + " is the code it returned.";
        }

        private static string FirstCode(string line)
        {
            var match = ErrorCode.Match(line ?? "");
            return match.Success ? match.Groups["code"].Value : null;
        }

        /// <summary>Strips the ConfigMgr log wrapper so a step name reads as a step name.</summary>
        private static string Clean(string value)
        {
            var text = (value ?? "").Trim();
            var log = text.IndexOf("]LOG]", StringComparison.Ordinal);
            if (log >= 0)
            {
                text = text.Substring(0, log);
            }
            return text.Trim().TrimEnd('.').Trim();
        }

        private static string Trim(string line)
        {
            var trimmed = (line ?? "").Trim();
            return trimmed.Length <= 400 ? trimmed : trimmed.Substring(0, 400) + "…";
        }
    }
}
