using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Threading;
using WinUpgradeDiag.Core.Discovery;
using WinUpgradeDiag.Core.IO;
using WinUpgradeDiag.Core.Orchestration;
using WinUpgradeDiag.Core.Report;

namespace WinUpgradeDiag.Cli
{
    /// <summary>
    /// Console head over the same Core as the WPF app, for ConfigMgr Run Script and fleet use.
    /// Runs at whatever privilege the caller has (see app.manifest) and reports what it could not
    /// read rather than refusing to start.
    /// </summary>
    internal static class Program
    {
        private static class ExitCode
        {
            public const int Ok = 0;
            public const int ExportFailed = 1;
            public const int BadArguments = 2;
            public const int Cancelled = 3;

            /// <summary>Ran, but not elevated, so protected logs could not be read.</summary>
            public const int NotElevated = 4;
        }

        private const string Usage =
@"WinUpgradeDiag.Cli — offline, read-only diagnostic for failed Windows 10 to 11 in-place upgrades

Usage:
  WinUpgradeDiag.Cli [--output <folder>] [--no-redact] [--no-html] [--no-json] [--zip] [--quiet]
  WinUpgradeDiag.Cli --find <text> [--in <path>] [--context <n>] [--max-matches <n>] [--quiet]

Collect, diagnose and export:
  --output <folder>  Root folder for results (default: %ProgramData%\WinUpgradeDiag).
                     A subfolder UpgradeDiag_<PC>_<timestamp> is created inside it.
  --no-redact        Do not redact usernames/profile paths/machine name in HTML and JSON.
  --no-html          Skip the HTML report.
  --no-json          Skip the JSON report.
  --zip              Also write the evidence zip (never redacted; internal escalation only).

Search:
  --find <text>      Search every discovered log for <text> and print each hit with its line
                     number. Streams the files, so a 700 MB setupact.log that Notepad cannot
                     open at all is searched end to end in seconds using a few MB of memory.
  --in <path>        Search only this file instead of every discovered log.
  --context <n>      Lines of context either side of each hit (default 2).
  --max-matches <n>  Stop after this many hits per file (default 5000).

Common:
  --quiet            Print only results; suppress progress and the manifest summary.
  --help             Show this help.

Exit codes: 0 ok, 1 export failed, 2 bad arguments, 3 cancelled,
            4 ran but not elevated (protected logs unreadable).";

        /// <summary>
        /// Deliberately thin, and deliberately free of any Core type.
        /// <para>
        /// The JIT resolves a method's assembly references the first time that method is compiled.
        /// If Main touched a Core type directly, the runtime would try to load Core before the
        /// resolver below had been installed, and the single-file build would fail at startup.
        /// All the real work lives in <see cref="Run"/>, which is not inlined for the same reason.
        /// </para>
        /// </summary>
        private static int Main(string[] args)
        {
            EmbeddedAssemblyLoader.Install();
            return Run(args);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string[] args)
        {
            string output = null;
            string findText = null;
            string findIn = null;
            var contextLines = LogSearcher.DefaultContextLines;
            var maxMatches = LogSearcher.DefaultMaxMatches;
            var redact = true;
            var artifacts = ExportArtifacts.Html | ExportArtifacts.Json;
            var quiet = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--output":
                    case "-o":
                        if (!TryTakeValue(args, ref i, "--output", out output))
                        {
                            return ExitCode.BadArguments;
                        }
                        break;
                    case "--find":
                    case "-f":
                        if (!TryTakeValue(args, ref i, "--find", out findText))
                        {
                            return ExitCode.BadArguments;
                        }
                        break;
                    case "--in":
                        if (!TryTakeValue(args, ref i, "--in", out findIn))
                        {
                            return ExitCode.BadArguments;
                        }
                        break;
                    case "--context":
                        if (!TryTakeInt(args, ref i, "--context", 0, 1000, out contextLines))
                        {
                            return ExitCode.BadArguments;
                        }
                        break;
                    case "--max-matches":
                        if (!TryTakeInt(args, ref i, "--max-matches", 1, int.MaxValue, out maxMatches))
                        {
                            return ExitCode.BadArguments;
                        }
                        break;
                    case "--no-redact": redact = false; break;
                    case "--no-html": artifacts &= ~ExportArtifacts.Html; break;
                    case "--no-json": artifacts &= ~ExportArtifacts.Json; break;
                    case "--zip": artifacts |= ExportArtifacts.EvidenceZip; break;
                    case "--quiet":
                    case "-q": quiet = true; break;
                    case "--help":
                    case "-h":
                    case "/?":
                        Console.WriteLine(Usage);
                        return ExitCode.Ok;
                    default:
                        Console.Error.WriteLine("Unknown argument: " + args[i]);
                        Console.Error.WriteLine(Usage);
                        return ExitCode.BadArguments;
                }
            }

            if (findIn != null && findText == null)
            {
                Console.Error.WriteLine("--in only makes sense together with --find.");
                return ExitCode.BadArguments;
            }

            var elevated = IsElevated();
            if (!quiet && !elevated)
            {
                // Say it once, up front, rather than letting every protected log fail mysteriously.
                Console.Error.WriteLine(
                    "Not running elevated: TrustedInstaller-owned logs ($WINDOWS.~BT\\Sources\\Panther " +
                    "and \\Rollback) cannot be read. Everything else still works.");
            }

            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };

                return findText != null
                    ? RunSearch(findText, findIn, contextLines, maxMatches, quiet, elevated, cts.Token)
                    : RunCollect(output, artifacts, redact, quiet, elevated, cts.Token);
            }
        }

        // ---------------- collect and export ----------------

        private static int RunCollect(
            string output, ExportArtifacts artifacts, bool redact, bool quiet, bool elevated, CancellationToken token)
        {
            var progress = new Progress<string>(step =>
            {
                if (!quiet) Console.Error.WriteLine("  " + step);
            });

            if (!quiet)
            {
                Console.Error.WriteLine("WinUpgradeDiag " + DiagnosticRunner.ToolVersion + " — collecting (read-only)…");
            }

            var context = new DiagnosticRunner().Run(progress, token);

            if (context.Cancelled)
            {
                Console.Error.WriteLine("Cancelled.");
                return ExitCode.Cancelled;
            }

            if (!quiet) PrintSummary(context);

            if (artifacts != ExportArtifacts.None)
            {
                try
                {
                    var result = ReportExporter.Export(context, output, artifacts, redact);
                    Console.WriteLine(result.OutputDirectory);
                    if (!quiet && result.EvidenceZipPath != null)
                    {
                        Console.Error.WriteLine("Evidence zip is NOT redacted — keep it on internal systems only.");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Export failed: " + ex.Message);
                    return ExitCode.ExportFailed;
                }
            }

            return elevated ? ExitCode.Ok : ExitCode.NotElevated;
        }

        // ---------------- search ----------------

        private static int RunSearch(
            string query, string singlePath, int contextLines, int maxMatches, bool quiet, bool elevated, CancellationToken token)
        {
            var targets = singlePath != null
                ? new List<string> { singlePath }
                : DiscoverSearchableLogs(quiet);

            if (targets.Count == 0)
            {
                Console.Error.WriteLine("No searchable logs found on this machine.");
                return elevated ? ExitCode.Ok : ExitCode.NotElevated;
            }

            var searcher = new LogSearcher();
            var totalMatches = 0;
            var unreadable = 0;

            foreach (var path in targets)
            {
                if (token.IsCancellationRequested)
                {
                    Console.Error.WriteLine("Cancelled.");
                    return ExitCode.Cancelled;
                }

                string refusal;
                if (!LogViewerPolicy.CanRender(path, out refusal))
                {
                    if (!quiet) Console.Error.WriteLine("  skipped  " + path + " — " + refusal);
                    continue;
                }

                // Whole lines only, on both streams: a partial "scanning… " line interleaves
                // badly with the results going to stdout when the two are piped together.
                if (!quiet) Console.Error.WriteLine("  scanning " + path);

                LogSearchResult result;
                try
                {
                    result = searcher.Search(path, query, maxMatches, contextLines, null, token);
                }
                catch (Exception ex)
                {
                    unreadable++;
                    if (!quiet) Console.Error.WriteLine("    unreadable: " + ex.Message);
                    continue;
                }

                if (!quiet)
                {
                    Console.Error.WriteLine(string.Format(
                        CultureInfo.CurrentCulture, "    {0:N0} hit(s) in {1:N0} lines of {2}",
                        result.Matches.Count, result.LinesScanned, LogSearchView.FormatSize(result.FileSizeBytes)));
                }

                if (result.Matches.Count == 0)
                {
                    continue;
                }

                totalMatches += result.Matches.Count;
                Console.WriteLine();
                Console.WriteLine("=== " + path);
                Console.WriteLine("    " + LogSearchView.DescribeSearch(result, query, maxMatches));
                Console.WriteLine();

                foreach (var line in LogSearchView.FromSearch(result))
                {
                    if (line.IsGap)
                    {
                        Console.WriteLine("           " + line.Text);
                        continue;
                    }

                    Console.WriteLine(string.Format(
                        CultureInfo.CurrentCulture, "{0,10:N0} {1} {2}",
                        line.LineNumber, line.IsMatch ? ">" : " ", line.Text));
                }
            }

            if (!quiet)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine(string.Format(
                    CultureInfo.CurrentCulture, "{0:N0} total hit(s) for \"{1}\" across {2:N0} log(s){3}.",
                    totalMatches, query, targets.Count,
                    unreadable > 0 ? ", " + unreadable + " unreadable" : string.Empty));
            }

            return elevated ? ExitCode.Ok : ExitCode.NotElevated;
        }

        /// <summary>Every discovered log that exists and is worth searching as text.</summary>
        private static List<string> DiscoverSearchableLogs(bool quiet)
        {
            if (!quiet) Console.Error.WriteLine("Discovering logs…");

            var manifest = new LogManifestBuilder().Build(LogSourceCatalog.GetDefaultSources());

            return manifest
                .Where(e => e.Exists)
                .Select(e => e.ResolvedPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // ---------------- helpers ----------------

        private static bool TryTakeValue(string[] args, ref int i, string name, out string value)
        {
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine(name + " needs a value.");
                value = null;
                return false;
            }

            value = args[++i];
            return true;
        }

        private static bool TryTakeInt(string[] args, ref int i, string name, int min, int max, out int value)
        {
            value = 0;
            string raw;
            if (!TryTakeValue(args, ref i, name, out raw))
            {
                return false;
            }

            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ||
                value < min || value > max)
            {
                Console.Error.WriteLine(string.Format(
                    CultureInfo.CurrentCulture, "{0} needs a whole number between {1} and {2}.", name, min, max));
                return false;
            }

            return true;
        }

        private static bool IsElevated()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void PrintSummary(DiagnosticContext context)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Log manifest:");
            foreach (var m in context.Manifest)
            {
                // "absent" is checked last on purpose: a log we know is there but cannot read
                // must not be reported as missing (DESIGN.md §8).
                var status = m.Readable ? "ok"
                    : m.RequiresPrivilegedRead ? "protected"
                    : !m.Exists ? "absent"
                    : "unreadable";
                var size = m.SizeKnown ? LogSearchView.FormatSize(m.SizeBytes) : "";
                Console.Error.WriteLine("  {0,-10} {1,10}  {2}{3}", status, size, m.ResolvedPath,
                    m.Source.HighValue && m.Exists ? "  [rollback]" : "");
            }

            var s = context.SystemState;
            if (s != null)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("Processes running: " +
                    string.Join(", ", s.Processes?.Processes.Where(p => p.IsRunning).Select(p => p.Name) ?? Enumerable.Empty<string>()));
                if (s.TaskSequenceExecutionRequest != null)
                {
                    Console.Error.WriteLine("CCM_TSExecutionRequest present: " + (s.TaskSequenceExecutionRequest.ExecutionRequestExists ? "yes" : "no"));
                }
                if (s.CollectionErrors.Count > 0)
                {
                    Console.Error.WriteLine("Collection gaps: " + s.CollectionErrors.Count + " (see report)");
                }
            }
            Console.Error.WriteLine();
        }
    }
}
