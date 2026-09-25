using System;
using System.Linq;
using System.Threading;
using WinUpgradeDiag.Core.Orchestration;
using WinUpgradeDiag.Core.Report;

namespace WinUpgradeDiag.Cli
{
    /// <summary>
    /// Console head over the same Core as the WPF app, for ConfigMgr Run Script and fleet use.
    /// Exit codes: 0 = ran and exported, 1 = ran but export failed, 2 = bad arguments,
    /// 3 = cancelled.
    /// </summary>
    internal static class Program
    {
        private const string Usage =
@"WinUpgradeDiag.Cli — offline, read-only upgrade diagnostic (phase 1: collect + export, no verdict)

Usage:
  WinUpgradeDiag.Cli [--output <folder>] [--no-redact] [--no-html] [--no-json] [--zip] [--quiet]

Options:
  --output <folder>  Root folder for results (default: %ProgramData%\WinUpgradeDiag).
                     A subfolder UpgradeDiag_<PC>_<timestamp> is created inside it.
  --no-redact        Do not redact usernames/profile paths/machine name in HTML and JSON.
  --no-html          Skip the HTML report.
  --no-json          Skip the JSON report.
  --zip              Also write the evidence zip (never redacted; internal escalation only).
  --quiet            Print only the output folder path.
  --help             Show this help.";

        private static int Main(string[] args)
        {
            string output = null;
            var redact = true;
            var artifacts = ExportArtifacts.Html | ExportArtifacts.Json;
            var quiet = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--output":
                    case "-o":
                        if (i + 1 >= args.Length)
                        {
                            Console.Error.WriteLine("--output needs a folder path.");
                            return 2;
                        }
                        output = args[++i];
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
                        return 0;
                    default:
                        Console.Error.WriteLine("Unknown argument: " + args[i]);
                        Console.Error.WriteLine(Usage);
                        return 2;
                }
            }

            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (s, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };

                var progress = new Progress<string>(step =>
                {
                    if (!quiet) Console.Error.WriteLine("  " + step);
                });

                if (!quiet) Console.Error.WriteLine("WinUpgradeDiag " + DiagnosticRunner.ToolVersion + " — collecting (read-only)…");
                var context = new DiagnosticRunner().Run(progress, cts.Token);

                if (context.Cancelled)
                {
                    Console.Error.WriteLine("Cancelled.");
                    return 3;
                }

                if (!quiet) PrintSummary(context);

                if (artifacts == ExportArtifacts.None)
                {
                    return 0;
                }

                try
                {
                    var result = ReportExporter.Export(context, output, artifacts, redact);
                    Console.WriteLine(result.OutputDirectory);
                    if (!quiet && result.EvidenceZipPath != null)
                    {
                        Console.Error.WriteLine("Evidence zip is NOT redacted — keep it on internal systems only.");
                    }
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Export failed: " + ex.Message);
                    return 1;
                }
            }
        }

        private static void PrintSummary(DiagnosticContext context)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Log manifest:");
            foreach (var m in context.Manifest)
            {
                var status = !m.Exists ? "absent"
                    : m.Readable ? "ok"
                    : m.RequiresPrivilegedRead ? "protected"
                    : "unreadable";
                var size = m.Exists ? HtmlReportWriter.Size(m.SizeBytes) : "";
                Console.Error.WriteLine("  {0,-10} {1,10}  {2}{3}", status, size, m.ResolvedPath, m.Source.HighValue && m.Exists ? "  [rollback]" : "");
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
