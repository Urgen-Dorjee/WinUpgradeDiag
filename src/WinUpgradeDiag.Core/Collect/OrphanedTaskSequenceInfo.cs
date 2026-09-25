namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>
    /// Whether a <c>CCM_TSExecutionRequest</c> lock survives in WMI. On its own this only means
    /// "Software Center still thinks a task sequence is running" — AGENTS.md is explicit that
    /// this must never be read as "orphaned" without also checking that TSManager is not alive
    /// (see <see cref="ProcessSnapshot"/>). Correlating the two is a rule, not a collector's job.
    /// </summary>
    public sealed class OrphanedTaskSequenceInfo
    {
        public bool ExecutionRequestExists { get; }
        public string PackageId { get; }
        public string AdvertisementId { get; }
        public string Error { get; }

        private OrphanedTaskSequenceInfo(bool exists, string packageId, string advertisementId, string error)
        {
            ExecutionRequestExists = exists;
            PackageId = packageId;
            AdvertisementId = advertisementId;
            Error = error;
        }

        public static OrphanedTaskSequenceInfo NotFound()
        {
            return new OrphanedTaskSequenceInfo(false, null, null, null);
        }

        public static OrphanedTaskSequenceInfo Found(string packageId, string advertisementId)
        {
            return new OrphanedTaskSequenceInfo(true, packageId, advertisementId, null);
        }

        public static OrphanedTaskSequenceInfo Failed(string error)
        {
            return new OrphanedTaskSequenceInfo(false, null, null, error);
        }
    }
}
