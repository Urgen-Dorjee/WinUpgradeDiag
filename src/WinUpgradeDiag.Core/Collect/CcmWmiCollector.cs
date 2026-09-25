using System;
using System.Management;

namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>
    /// Queries ConfigMgr client WMI state. Every method degrades to a "not found"/"error" result
    /// instead of throwing — a machine with no ConfigMgr client, or one where WMI access is
    /// denied, is a normal thing to encounter here.
    /// </summary>
    public sealed class CcmWmiCollector
    {
        private const string Namespace = @"root\ccm\SoftMgmtAgent";

        /// <summary>
        /// Looks for a surviving <c>CCM_TSExecutionRequest</c> instance — the WMI-side half of
        /// AGENTS.md acceptance test #1 (orphaned task sequence).
        /// </summary>
        public OrphanedTaskSequenceInfo GetOrphanedTaskSequenceInfo()
        {
            try
            {
                var scope = new ManagementScope(Namespace);
                scope.Connect();

                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM CCM_TSExecutionRequest")))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject instance in results)
                    {
                        using (instance)
                        {
                            var packageId = SafeGetString(instance, "PackageID");
                            var advertisementId = SafeGetString(instance, "AdvertisementID");
                            return OrphanedTaskSequenceInfo.Found(packageId, advertisementId);
                        }
                    }
                }

                return OrphanedTaskSequenceInfo.NotFound();
            }
            catch (ManagementException ex)
            {
                // Invalid namespace/class almost always means "no ConfigMgr client here", which
                // is not an error worth surfacing as one.
                if (ex.ErrorCode == ManagementStatus.InvalidNamespace ||
                    ex.ErrorCode == ManagementStatus.InvalidClass ||
                    ex.ErrorCode == ManagementStatus.NotFound)
                {
                    return OrphanedTaskSequenceInfo.NotFound();
                }

                return OrphanedTaskSequenceInfo.Failed(ex.Message);
            }
            catch (Exception ex)
            {
                return OrphanedTaskSequenceInfo.Failed(ex.Message);
            }
        }

        private static string SafeGetString(ManagementObject instance, string property)
        {
            try
            {
                var value = instance[property];
                return value?.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
