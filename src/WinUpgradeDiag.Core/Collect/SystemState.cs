using System;
using System.Collections.Generic;

namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>
    /// Live-state snapshot of the machine at collection time (DESIGN.md §4.2). Pure data:
    /// nothing here decides what any of it means.
    /// </summary>
    public sealed class SystemState
    {
        public string MachineName { get; set; }
        public DateTime CollectedAtUtc { get; set; }
        public bool IsElevated { get; set; }

        public OsIdentity Os { get; set; }
        public int? SetupProgressPercent { get; set; }
        public PendingRebootState PendingReboot { get; set; }
        public bool? SecureBootEnabled { get; set; }
        public bool? MemoryIntegrityEnabled { get; set; }

        public long? SystemDriveFreeBytes { get; set; }
        public long? SystemDriveTotalBytes { get; set; }
        public IReadOnlyList<UpgradeFolderInfo> UpgradeFolders { get; set; }

        public ProcessSnapshot Processes { get; set; }
        public OrphanedTaskSequenceInfo TaskSequenceExecutionRequest { get; set; }

        /// <summary>ConfigMgr client cache contents, or the reason they could not be read.</summary>
        public CcmCacheSnapshot CcmCache { get; set; }
        public IReadOnlyList<StorageHealthInfo> StorageHealth { get; set; }
        public IReadOnlyList<FilterDriverInfo> FilterDrivers { get; set; }
        public IReadOnlyList<EventRecordInfo> Events { get; set; }

        /// <summary>Anything a collector could not read. Surfaced, never swallowed.</summary>
        public List<string> CollectionErrors { get; } = new List<string>();
    }

    public sealed class UpgradeFolderInfo
    {
        public string Path { get; set; }
        public bool Exists { get; set; }
        public DateTime? LastWriteTimeUtc { get; set; }
    }
}
