namespace WinUpgradeDiag.Core.Discovery
{
    /// <summary>
    /// Whether a <see cref="LogSource"/> names one file directly, or a directory that may hold
    /// several matching files (e.g. multiple smsts.log runs, or a pile of minidumps).
    /// </summary>
    public enum LogSourceKind
    {
        File,
        DirectoryGlob
    }
}
