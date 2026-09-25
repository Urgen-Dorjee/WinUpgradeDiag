namespace WinUpgradeDiag.Core.Discovery
{
    /// <summary>
    /// A candidate log location the discovery stage knows how to look for. A source describes
    /// where to look, not whether anything is actually there — that is what
    /// <see cref="LogManifestBuilder"/> finds out.
    /// </summary>
    public sealed class LogSource
    {
        public string Id { get; }
        public LogSourceCategory Category { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public LogSourceKind Kind { get; }

        /// <summary>File path (Kind == File) or directory path (Kind == DirectoryGlob).</summary>
        public string Path { get; }

        /// <summary>Search pattern used when Kind == DirectoryGlob, e.g. "smsts*.log".</summary>
        public string SearchPattern { get; }

        /// <summary>
        /// Flags the highest-value, most-often-missed sources (the Rollback set) so the UI and
        /// CLI can call them out even before anything is parsed.
        /// </summary>
        public bool HighValue { get; }

        public LogSource(
            string id,
            LogSourceCategory category,
            string displayName,
            string description,
            string path,
            LogSourceKind kind = LogSourceKind.File,
            string searchPattern = null,
            bool highValue = false)
        {
            Id = id;
            Category = category;
            DisplayName = displayName;
            Description = description;
            Path = path;
            Kind = kind;
            SearchPattern = searchPattern;
            HighValue = highValue;
        }
    }
}
