using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>
    /// One item in the ConfigMgr client cache, as recorded in
    /// <c>root\ccm\SoftMgmtAgent:CacheInfoEx</c>.
    /// </summary>
    public sealed class CcmCacheElement
    {
        public string ContentId { get; set; }
        public string ContentVersion { get; set; }
        public string Location { get; set; }

        /// <summary>
        /// Size as the provider reports it, in kilobytes. Kept in the provider's own unit so the
        /// conversion happens in exactly one place — <see cref="SizeBytes"/>.
        /// </summary>
        public long SizeKilobytes { get; set; }

        public long SizeBytes => SizeKilobytes * 1024L;

        public bool PersistInCache { get; set; }
        public DateTime? LastReferencedUtc { get; set; }

        /// <summary>
        /// False when the record points at a folder that is no longer on disk — the signature of a
        /// cache deleted by hand in Explorer rather than through the client. The client still
        /// believes the content is present, so it will not download it again.
        /// </summary>
        public bool FolderExists { get; set; }

        public string SizeText
        {
            get
            {
                double value = SizeBytes;
                string[] units = { "B", "KB", "MB", "GB", "TB" };
                var unit = 0;
                while (value >= 1024 && unit < units.Length - 1)
                {
                    value /= 1024;
                    unit++;
                }
                return value.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
            }
        }
    }

    /// <summary>
    /// What is in the client cache right now. Pure data — whether any of it is a problem is for the
    /// rules to decide.
    /// </summary>
    public sealed class CcmCacheSnapshot
    {
        public IReadOnlyList<CcmCacheElement> Elements { get; set; } = new List<CcmCacheElement>();

        /// <summary>Why the cache could not be read, or null. A machine with no client is normal.</summary>
        public string Error { get; set; }

        /// <summary>True when the client was present and the cache could be enumerated.</summary>
        public bool Available => Error == null;

        public long TotalBytes => Elements.Sum(e => e.SizeBytes);

        /// <summary>Records whose folder is gone. These block a re-download until removed.</summary>
        public IReadOnlyList<CcmCacheElement> StaleElements =>
            Elements.Where(e => !e.FolderExists).ToList();

        /// <summary>
        /// The largest cached item, which on a machine running an in-place upgrade is the OS
        /// package. This is the value a technician currently reads off a table by eye to pass to
        /// the download-recovery script; having it here is what lets the tool fill that in.
        /// </summary>
        public CcmCacheElement LargestElement =>
            Elements.OrderByDescending(e => e.SizeBytes).FirstOrDefault();

        /// <summary>
        /// The largest item, but only if it is big enough to plausibly be an OS image rather than
        /// an application. Guessing wrong here would put the wrong id into a delete command.
        /// </summary>
        public CcmCacheElement LikelyOsUpgradePackage
        {
            get
            {
                var largest = LargestElement;
                return largest != null && largest.SizeBytes >= MinimumOsPackageBytes ? largest : null;
            }
        }

        /// <summary>An in-place upgrade package is multiple gigabytes; nothing smaller qualifies.</summary>
        public const long MinimumOsPackageBytes = 2L * 1024 * 1024 * 1024;
    }
}
