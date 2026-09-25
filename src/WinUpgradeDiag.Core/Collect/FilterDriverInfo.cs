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
        public int? StartMode { get; set; }
    }
}
