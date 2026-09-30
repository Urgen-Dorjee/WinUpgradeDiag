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

        /// <summary>
        /// Model, BIOS level and firmware mode. Collected because "the same model upgrades fine" is
        /// the first thing said about every failure, and the differences that matter between two
        /// machines off the same order are never the model.
        /// </summary>
        public MachineIdentityInfo Machine { get; set; }

        /// <summary>
        /// Health of the ConfigMgr client itself. A broken client and a broken upgrade look almost
        /// identical from Software Center, and only one of them was previously diagnosable.
        /// </summary>
        public CcmClientHealthInfo CcmClient { get; set; }

        /// <summary>Third-party driver packages with their installed versions.</summary>
        public IReadOnlyList<DriverPackageInfo> DriverPackages { get; set; }
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
