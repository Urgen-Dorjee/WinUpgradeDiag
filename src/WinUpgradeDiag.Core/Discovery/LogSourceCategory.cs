namespace WinUpgradeDiag.Core.Discovery
{
    /// <summary>
    /// Groups a <see cref="LogSource"/> by the phase of the upgrade it documents.
    /// Mirrors the table in docs/DESIGN.md §4.1.
    /// </summary>
    public enum LogSourceCategory
    {
        TaskSequence,
        SetupCurrent,
        SetupRollback,
        SetupCompleted,
        PreviousOs,
        DownlevelServicing,
        Servicing,
        ClientInstall,
        ClientOther,
        CrashDump
    }
}
