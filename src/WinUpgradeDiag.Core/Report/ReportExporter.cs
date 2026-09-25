using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using WinUpgradeDiag.Core.Orchestration;
using WinUpgradeDiag.Core.Redaction;

namespace WinUpgradeDiag.Core.Report
{
    [Flags]
    public enum ExportArtifacts
    {
        None = 0,
        Html = 1,
        Json = 2,
        EvidenceZip = 4,
        All = Html | Json | EvidenceZip
    }

    public sealed class ExportResult
    {
        public string OutputDirectory { get; set; }
        public string HtmlPath { get; set; }
        public string JsonPath { get; set; }
        public string EvidenceZipPath { get; set; }
        public IReadOnlyList<string> SkippedEvidence { get; set; } = new List<string>();
    }

    /// <summary>
    /// Writes a run's artefacts into one folder: <c>&lt;root&gt;\UpgradeDiag_&lt;PC&gt;_&lt;timestamp&gt;\</c>.
    /// </summary>
    public static class ReportExporter
    {
        public static string DefaultOutputRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinUpgradeDiag");

        public static ExportResult Export(DiagnosticContext context, string outputRoot, ExportArtifacts artifacts, bool redact)
        {
            var root = string.IsNullOrWhiteSpace(outputRoot) ? DefaultOutputRoot : outputRoot;
            var folderName = "UpgradeDiag_" + SafeName(context.SystemState?.MachineName ?? Environment.MachineName) + "_" +
                             context.StartedAtUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var dir = Path.Combine(root, folderName);
            Directory.CreateDirectory(dir);

            var redactor = redact ? Redactor.ForCurrentMachine() : null;
            var result = new ExportResult { OutputDirectory = dir };

            if (artifacts.HasFlag(ExportArtifacts.Html))
            {
                result.HtmlPath = Path.Combine(dir, redact ? "report.html" : "report.unredacted.html");
                HtmlReportWriter.Write(context, redactor, result.HtmlPath);
            }

            if (artifacts.HasFlag(ExportArtifacts.Json))
            {
                result.JsonPath = Path.Combine(dir, redact ? "report.json" : "report.unredacted.json");
                JsonReportWriter.Write(context, redactor, result.JsonPath);
            }

            if (artifacts.HasFlag(ExportArtifacts.EvidenceZip))
            {
                result.EvidenceZipPath = Path.Combine(dir, "evidence.zip");
                if (File.Exists(result.EvidenceZipPath))
                {
                    result.EvidenceZipPath = Path.Combine(dir, "evidence_" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture) + ".zip");
                }
                result.SkippedEvidence = EvidenceBundleWriter.Write(context, result.EvidenceZipPath);
            }

            return result;
        }

        private static string SafeName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }
    }
}
