namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>
    /// Storage reliability data for one physical disk. AGENTS.md acceptance test #6: a failing
    /// SSD must not be misdiagnosed as a software problem, so this has to be checked alongside
    /// everything else, not treated as an afterthought.
    /// </summary>
    public sealed class StorageHealthInfo
    {
        public string DeviceId { get; set; }
        public string FriendlyName { get; set; }
        public string HealthStatus { get; set; }
        public string OperationalStatus { get; set; }
        public ulong? Wear { get; set; }
        public ulong? ReadErrorsUncorrected { get; set; }
        public ulong? WriteErrorsUncorrected { get; set; }
        public ulong? PowerOnHours { get; set; }
        public string Error { get; set; }
    }
}
