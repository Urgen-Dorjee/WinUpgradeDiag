using System.Globalization;
using WinUpgradeDiag.Core.Discovery;
using WinUpgradeDiag.Core.Report;

namespace WinUpgradeDiag.App.ViewModels
{
    /// <summary>Display wrapper for one manifest entry in the Logs tab.</summary>
    public sealed class ManifestRow
    {
        public ManifestRow(LogManifestEntry entry)
        {
            Entry = entry;
        }

        public LogManifestEntry Entry { get; }

        public string Category => Entry.Source.Category.ToString();
        public string Name => Entry.Source.DisplayName;
        public string Path => Entry.ResolvedPath;
        public bool HighValue => Entry.Source.HighValue;
        public string Exists => Entry.Exists ? "Yes" : "No";
        public string Size => Entry.SizeKnown ? HtmlReportWriter.Size(Entry.SizeBytes) : "";

        public string LastWrite => Entry.LastWriteTimeUtc.HasValue && Entry.Exists
            ? Entry.LastWriteTimeUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)
            : "";

        public string Status
        {
            get
            {
                // Order matters: a log confirmed present but unreadable must never read as
                // "Not present". Reporting unreadable evidence as absent is the DESIGN.md §8 trap.
                if (Entry.Readable) return "Readable";
                if (Entry.RequiresPrivilegedRead) return "Protected (needs elevation)";
                if (!Entry.Exists) return "Not present";
                return "Unreadable: " + Entry.AccessError;
            }
        }
    }
}
