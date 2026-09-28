using System;
using System.Collections.Generic;
using System.Globalization;
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

        /// <summary>
        /// Enumerates the client cache. Implements the <c>CacheInfoEx</c> half of DESIGN.md §4.2,
        /// which the rules need for CT-002 (a record pointing at a folder somebody deleted by hand)
        /// and CT-005 (cache too small for the image).
        /// </summary>
        public CcmCacheSnapshot GetCacheSnapshot()
        {
            var snapshot = new CcmCacheSnapshot();
            var elements = new List<CcmCacheElement>();

            try
            {
                var scope = new ManagementScope(Namespace);
                scope.Connect();

                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM CacheInfoEx")))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject item in results)
                    {
                        using (item)
                        {
                            var location = SafeGetString(item, "Location");
                            elements.Add(new CcmCacheElement
                            {
                                ContentId = SafeGetString(item, "ContentId"),
                                ContentVersion = SafeGetString(item, "ContentVer"),
                                Location = location,
                                // ContentSize is reported in kilobytes by this provider.
                                SizeKilobytes = SafeGetLong(item, "ContentSize") ?? 0,
                                PersistInCache = SafeGetBool(item, "PersistInCache") ?? false,
                                LastReferencedUtc = SafeGetDate(item, "LastReferenced"),
                                FolderExists = SafeFolderExists(location)
                            });
                        }
                    }
                }

                snapshot.Elements = elements;
                return snapshot;
            }
            catch (ManagementException ex)
            {
                if (ex.ErrorCode == ManagementStatus.InvalidNamespace ||
                    ex.ErrorCode == ManagementStatus.InvalidClass ||
                    ex.ErrorCode == ManagementStatus.NotFound)
                {
                    snapshot.Error = "No ConfigMgr client cache on this machine.";
                    return snapshot;
                }

                snapshot.Error = "Could not read the client cache: " + ex.Message;
                return snapshot;
            }
            catch (Exception ex)
            {
                snapshot.Error = "Could not read the client cache: " + ex.Message;
                return snapshot;
            }
        }

        private static bool SafeFolderExists(string location)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(location) && System.IO.Directory.Exists(location);
            }
            catch (Exception)
            {
                // Unreadable is not the same as absent; assume present rather than advise a delete.
                return true;
            }
        }

        private static long? SafeGetLong(ManagementObject o, string property)
        {
            try
            {
                var value = o[property];
                return value == null ? (long?)null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool? SafeGetBool(ManagementObject o, string property)
        {
            try
            {
                var value = o[property];
                return value == null ? (bool?)null : Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static DateTime? SafeGetDate(ManagementObject o, string property)
        {
            try
            {
                var value = o[property] as string;
                return string.IsNullOrEmpty(value)
                    ? (DateTime?)null
                    : ManagementDateTimeConverter.ToDateTime(value).ToUniversalTime();
            }
            catch (Exception)
            {
                return null;
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
