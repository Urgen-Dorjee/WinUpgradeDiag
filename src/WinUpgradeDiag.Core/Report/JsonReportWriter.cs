using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using WinUpgradeDiag.Core.Orchestration;
using WinUpgradeDiag.Core.Redaction;

namespace WinUpgradeDiag.Core.Report
{
    /// <summary>
    /// Machine-readable export for fleet-wide aggregation (DESIGN.md §4.6). Uses the in-box
    /// JavaScriptSerializer so no NuGet package is needed.
    /// </summary>
    public static class JsonReportWriter
    {
        public static string Serialize(DiagnosticContext context, Redactor redactor)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };
            return serializer.Serialize(ReportModelBuilder.Build(context, redactor));
        }

        public static void Write(DiagnosticContext context, Redactor redactor, string path)
        {
            File.WriteAllText(path, Serialize(context, redactor), new UTF8Encoding(false));
        }
    }
}
