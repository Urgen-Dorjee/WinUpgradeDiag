using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;

namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>
    /// Collects the machine's identity and its third-party driver packages.
    /// <para>
    /// Built for the question the tool could not answer: the same model upgrades fine elsewhere, so
    /// what is different about this one. Model alone never answers it — two machines off the same
    /// order diverge in BIOS level and in driver versions within months. Reporting the versions
    /// makes the comparison a diff instead of an argument.
    /// </para>
    /// <para>
    /// Everything here is best effort. A machine with a broken WMI repository, a VM with no SMBIOS
    /// data, or a denied query all degrade to nulls plus a recorded error, never a crash.
    /// </para>
    /// </summary>
    public sealed class MachineIdentityCollector
    {
        public MachineIdentityInfo CollectIdentity(IList<string> errors)
        {
            var info = new MachineIdentityInfo();

            Try(errors, "Win32_ComputerSystem", () =>
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Manufacturer, Model, TotalPhysicalMemory, SystemSKUNumber FROM Win32_ComputerSystem"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject row in results)
                    {
                        using (row)
                        {
                            info.Manufacturer = Text(row, "Manufacturer");
                            info.Model = Text(row, "Model");
                            info.SystemSku = Text(row, "SystemSKUNumber");
                            info.TotalPhysicalMemoryBytes = Bytes(row, "TotalPhysicalMemory");
                            break;
                        }
                    }
                }
            });

            Try(errors, "Win32_BIOS", () =>
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT SMBIOSBIOSVersion, Version, ReleaseDate FROM Win32_BIOS"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject row in results)
                    {
                        using (row)
                        {
                            info.BiosVersion = Text(row, "SMBIOSBIOSVersion") ?? Text(row, "Version");
                            info.BiosReleaseDate = Date(row, "ReleaseDate");
                            break;
                        }
                    }
                }
            });

            Try(errors, "firmware type", () =>
            {
                // PEFirmwareType: 1 legacy BIOS, 2 UEFI. Only written when Windows was installed
                // from WinPE, so it is missing on plenty of machines — including the one this was
                // first tested on, which reported "unknown" while sitting on Secure Boot.
                var value = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control", "PEFirmwareType", null);
                if (value != null)
                {
                    info.FirmwareType = Convert.ToInt32(value, CultureInfo.InvariantCulture) == 2 ? "UEFI" : "Legacy BIOS";
                    return;
                }

                // firmware_type is set by the boot environment on every modern build.
                var env = Environment.GetEnvironmentVariable("firmware_type");
                if (!string.IsNullOrWhiteSpace(env))
                {
                    info.FirmwareType = env.Trim();
                }
            });

            return info;
        }

        /// <summary>
        /// Third-party driver packages only. The in-box set is identical on every machine running
        /// the same build, so listing it buries the handful of rows that can actually differ.
        /// </summary>
        public IReadOnlyList<DriverPackageInfo> CollectDriverPackages(IList<string> errors)
        {
            var packages = new List<DriverPackageInfo>();

            Try(errors, "Win32_PnPSignedDriver", () =>
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT DeviceName, DriverProviderName, DriverVersion, DriverDate, InfName, DeviceClass " +
                    "FROM Win32_PnPSignedDriver"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject row in results)
                    {
                        using (row)
                        {
                            var inf = Text(row, "InfName");

                            // An oem*.inf published name is exactly what marks a package as
                            // third-party: Windows renames every driver it did not ship.
                            if (inf == null || !inf.StartsWith("oem", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            packages.Add(new DriverPackageInfo
                            {
                                PublishedName = inf,
                                Provider = Text(row, "DriverProviderName"),
                                Version = Text(row, "DriverVersion"),
                                DriverDate = Date(row, "DriverDate"),
                                DeviceClass = Text(row, "DeviceClass"),
                                DeviceName = Text(row, "DeviceName")
                            });
                        }
                    }
                }
            });

            // One row per package: a package bound to eight devices is one thing to update.
            return packages
                .GroupBy(p => p.PublishedName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(p => p.Provider ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.PublishedName ?? "", StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void Try(IList<string> errors, string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                errors?.Add("Could not read " + what + ": " + ex.Message);
            }
        }

        private static string Text(ManagementObject row, string property)
        {
            try
            {
                var value = row[property] as string;
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static long? Bytes(ManagementObject row, string property)
        {
            try
            {
                var value = row[property];
                return value == null ? (long?)null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>WMI dates are CIM_DATETIME strings, e.g. 20240117000000.000000+000.</summary>
        private static DateTime? Date(ManagementObject row, string property)
        {
            try
            {
                var raw = row[property] as string;
                if (string.IsNullOrWhiteSpace(raw) || raw.Length < 8)
                {
                    return null;
                }

                return ManagementDateTimeConverter.ToDateTime(raw);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
