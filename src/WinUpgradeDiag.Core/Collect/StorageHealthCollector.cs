using System;
using System.Collections.Generic;
using System.Management;

namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>
    /// Reads physical disk health and reliability counters via storage management WMI
    /// (root\Microsoft\Windows\Storage), per DESIGN.md §4.2. Runs best-effort: a machine with
    /// an older stack, a VM with no storage management provider, or denied WMI access all
    /// degrade to an empty list plus a recorded error rather than a crash.
    /// </summary>
    public sealed class StorageHealthCollector
    {
        private const string Namespace = @"root\Microsoft\Windows\Storage";

        public IReadOnlyList<StorageHealthInfo> Collect()
        {
            var results = new List<StorageHealthInfo>();

            try
            {
                var scope = new ManagementScope(Namespace);
                scope.Connect();

                var reliabilityByDeviceId = ReadReliabilityCounters(scope);

                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk")))
                using (var disks = searcher.Get())
                {
                    foreach (ManagementObject disk in disks)
                    {
                        using (disk)
                        {
                            var deviceId = SafeGet(disk, "DeviceId");
                            var info = new StorageHealthInfo
                            {
                                DeviceId = deviceId,
                                FriendlyName = SafeGet(disk, "FriendlyName"),
                                HealthStatus = SafeGetEnumName(disk, "HealthStatus"),
                                OperationalStatus = SafeGet(disk, "OperationalStatus")
                            };

                            StorageHealthInfo reliability;
                            if (deviceId != null && reliabilityByDeviceId.TryGetValue(deviceId, out reliability))
                            {
                                info.Wear = reliability.Wear;
                                info.ReadErrorsUncorrected = reliability.ReadErrorsUncorrected;
                                info.WriteErrorsUncorrected = reliability.WriteErrorsUncorrected;
                                info.PowerOnHours = reliability.PowerOnHours;
                            }

                            results.Add(info);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                results.Add(new StorageHealthInfo { Error = "Storage health collection failed: " + ex.Message });
            }

            return results;
        }

        private static Dictionary<string, StorageHealthInfo> ReadReliabilityCounters(ManagementScope scope)
        {
            var byDeviceId = new Dictionary<string, StorageHealthInfo>();

            try
            {
                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_StorageReliabilityCounter")))
                using (var counters = searcher.Get())
                {
                    foreach (ManagementObject counter in counters)
                    {
                        using (counter)
                        {
                            var deviceId = SafeGet(counter, "DeviceId");
                            if (deviceId == null)
                            {
                                continue;
                            }

                            byDeviceId[deviceId] = new StorageHealthInfo
                            {
                                Wear = SafeGetULong(counter, "Wear"),
                                ReadErrorsUncorrected = SafeGetULong(counter, "ReadErrorsUncorrected"),
                                WriteErrorsUncorrected = SafeGetULong(counter, "WriteErrorsUncorrected"),
                                PowerOnHours = SafeGetULong(counter, "PowerOnHours")
                            };
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Reliability counters are not available on every storage stack (e.g. some VMs).
                // Physical disk entries without reliability data are still worth reporting.
            }

            return byDeviceId;
        }

        private static string SafeGet(ManagementObject o, string property)
        {
            try
            {
                return o[property]?.ToString();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string SafeGetEnumName(ManagementObject o, string property)
        {
            // HealthStatus etc. come back as numeric enums; keep the raw value since mapping the
            // full enum table is not needed for phase 1 (no verdicts yet).
            return SafeGet(o, property);
        }

        private static ulong? SafeGetULong(ManagementObject o, string property)
        {
            try
            {
                var value = o[property];
                if (value == null)
                {
                    return null;
                }
                return Convert.ToUInt64(value);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
