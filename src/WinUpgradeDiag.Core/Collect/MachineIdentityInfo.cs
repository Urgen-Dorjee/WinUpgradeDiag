using System;
using System.Collections.Generic;

namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>
    /// What distinguishes this machine from another of the same model.
    /// <para>
    /// "The same model upgrades fine" is the most common thing a technician says about a failure,
    /// and it is almost always true and almost always beside the point. Two machines off the same
    /// order differ in BIOS level, in the driver versions Windows Update has handed each of them
    /// over three years, and in what has been installed on top. Those are the axes a failure
    /// actually falls on, and the tool collected none of them, so it could not help anyone compare
    /// a machine that failed with one that did not.
    /// </para>
    /// </summary>
    public sealed class MachineIdentityInfo
    {
        public string Manufacturer { get; set; }
        public string Model { get; set; }

        /// <summary>SKU or product number — same model name often covers several of these.</summary>
        public string SystemSku { get; set; }

        public string BiosVersion { get; set; }
        public DateTime? BiosReleaseDate { get; set; }

        /// <summary>Firmware mode, because an upgrade behaves differently under legacy BIOS.</summary>
        public string FirmwareType { get; set; }

        public long? TotalPhysicalMemoryBytes { get; set; }

        public string Describe()
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Manufacturer)) parts.Add(Manufacturer.Trim());
            if (!string.IsNullOrWhiteSpace(Model)) parts.Add(Model.Trim());
            if (!string.IsNullOrWhiteSpace(SystemSku)) parts.Add("(" + SystemSku.Trim() + ")");
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }
    }

    /// <summary>
    /// A third-party driver package in the driver store, with the version that is actually
    /// installed. This is the field that differs between two machines of the same model.
    /// </summary>
    public sealed class DriverPackageInfo
    {
        /// <summary>The published name, e.g. oem47.inf — what pnputil takes to remove it.</summary>
        public string PublishedName { get; set; }

        public string OriginalName { get; set; }
        public string Provider { get; set; }
        public string DeviceClass { get; set; }
        public string Version { get; set; }
        public DateTime? DriverDate { get; set; }

        /// <summary>One device this package is bound to, for recognising what it is.</summary>
        public string DeviceName { get; set; }

        public string Describe()
        {
            var name = Provider ?? "unknown vendor";
            if (!string.IsNullOrWhiteSpace(DeviceClass))
            {
                name += " " + DeviceClass;
            }
            if (!string.IsNullOrWhiteSpace(Version))
            {
                name += " " + Version;
            }
            return name;
        }
    }
}
