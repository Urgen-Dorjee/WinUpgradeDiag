namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>
    /// A third-party (or Windows) filter driver, classified by its registered altitude group —
    /// the same vendor-neutral grouping Windows itself uses (FSFilter Anti-Virus, FSFilter
    /// Encryption, FSFilter Content Screener for removable-media/DLP products, etc.). DESIGN.md
    /// §4.2 calls for flagging these classes as a Collect-stage fact, not a verdict.
    /// </summary>
    public sealed class FilterDriverInfo
    {
        public string ServiceName { get; set; }
        public string DisplayName { get; set; }
        public string ImagePath { get; set; }
        public string AltitudeGroup { get; set; }

        /// <summary>Raw Start value from the service key (0 Boot .. 4 Disabled).</summary>
        public int? StartMode { get; set; }

        /// <summary>
        /// <see cref="StartMode"/> as the word it stands for. A technician reading "Boot" learns
        /// that this filter loads before almost anything else; reading "0" learns nothing.
        /// </summary>
        public string StartModeName => StorageEnums.StartMode(StartMode);
    }
}
