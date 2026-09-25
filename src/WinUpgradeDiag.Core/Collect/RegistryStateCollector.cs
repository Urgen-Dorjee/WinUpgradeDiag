using System;
using Microsoft.Win32;

namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>Best-effort registry reads for OS identity and machine state, per DESIGN.md §4.2.</summary>
    public sealed class RegistryStateCollector
    {
        public OsIdentity GetOsIdentity()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key == null)
                    {
                        return new OsIdentity();
                    }

                    return new OsIdentity
                    {
                        ProductName = key.GetValue("ProductName") as string,
                        EditionId = key.GetValue("EditionID") as string,
                        DisplayVersion = (key.GetValue("DisplayVersion") ?? key.GetValue("ReleaseId")) as string,
                        CurrentBuildNumber = key.GetValue("CurrentBuildNumber") as string,
                        Ubr = key.GetValue("UBR")?.ToString()
                    };
                }
            }
            catch (Exception ex)
            {
                return new OsIdentity { Error = ex.Message };
            }
        }

        public int? GetSetupProgressPercent()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\Setup\MoSetup\Volatile"))
                {
                    var value = key?.GetValue("SetupProgress");
                    return value == null ? (int?)null : Convert.ToInt32(value);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public PendingRebootState GetPendingReboot()
        {
            var state = new PendingRebootState();

            state.ComponentBasedServicing = KeyExists(
                Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");

            state.WindowsUpdate = KeyExists(
                Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");

            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
                {
                    var value = key?.GetValue("PendingFileRenameOperations") as string[];
                    state.PendingFileRenameOperations = value != null && value.Length > 0;
                }
            }
            catch (Exception)
            {
                state.PendingFileRenameOperations = false;
            }

            return state;
        }

        public bool? GetSecureBootEnabled()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State"))
                {
                    var value = key?.GetValue("UEFISecureBootEnabled");
                    return value == null ? (bool?)null : Convert.ToInt32(value) != 0;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public bool? GetHvciEnabled()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity"))
                {
                    var value = key?.GetValue("Enabled");
                    return value == null ? (bool?)null : Convert.ToInt32(value) != 0;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool KeyExists(RegistryKey hive, string path)
        {
            try
            {
                using (var key = hive.OpenSubKey(path))
                {
                    return key != null;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public sealed class OsIdentity
    {
        public string ProductName { get; set; }
        public string EditionId { get; set; }
        public string DisplayVersion { get; set; }
        public string CurrentBuildNumber { get; set; }
        public string Ubr { get; set; }
        public string Error { get; set; }
    }

    public sealed class PendingRebootState
    {
        public bool ComponentBasedServicing { get; set; }
        public bool WindowsUpdate { get; set; }
        public bool PendingFileRenameOperations { get; set; }

        public bool Any => ComponentBasedServicing || WindowsUpdate || PendingFileRenameOperations;
    }
}
