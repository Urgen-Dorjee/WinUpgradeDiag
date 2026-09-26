using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
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
        /// <summary>
        /// Raw <c>ProductName</c> from the registry. On Windows 11 this still reads
        /// "Windows 10 ..." — see <see cref="DisplayName"/>. Kept verbatim so the report can show
        /// what the machine actually claims about itself.
        /// </summary>
        public string ProductName { get; set; }

        public string EditionId { get; set; }
        public string DisplayVersion { get; set; }
        public string CurrentBuildNumber { get; set; }
        public string Ubr { get; set; }
        public string Error { get; set; }

        /// <summary>Build number as an integer, or null if it could not be read.</summary>
        public int? Build
        {
            get
            {
                int value;
                return int.TryParse(CurrentBuildNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                    ? value
                    : (int?)null;
            }
        }

        /// <summary>
        /// First build number of Windows 11. Microsoft never updated <c>ProductName</c> in the
        /// registry for Windows 11 — a Windows 11 25H2 machine still reports "Windows 10 Pro" —
        /// so the build number is the only reliable discriminator.
        /// </summary>
        public const int FirstWindows11Build = 22000;

        public bool IsWindows11 => Build.HasValue && Build.Value >= FirstWindows11Build && !IsServer;

        private bool IsServer =>
            !string.IsNullOrEmpty(ProductName) &&
            ProductName.IndexOf("Server", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// The product name a technician should be shown: <see cref="ProductName"/> corrected to
        /// "Windows 11" when the build says so. Getting this wrong matters here beyond cosmetics —
        /// the whole tool is about a Windows 10 to 11 upgrade, so mislabelling the running OS
        /// misrepresents whether the upgrade succeeded.
        /// </summary>
        public string DisplayName
        {
            get
            {
                if (string.IsNullOrWhiteSpace(ProductName))
                {
                    return Build.HasValue ? (IsWindows11 ? "Windows 11" : "Windows 10") : null;
                }

                return IsWindows11
                    ? Regex.Replace(ProductName, @"Windows\s*10", "Windows 11", RegexOptions.IgnoreCase)
                    : ProductName;
            }
        }

        /// <summary>One line: name, release, and full build. e.g. "Windows 11 Pro 25H2 (build 26200.9457)".</summary>
        public string FullDescription
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(DisplayName)) parts.Add(DisplayName);
                if (!string.IsNullOrWhiteSpace(DisplayVersion)) parts.Add(DisplayVersion);

                var build = CurrentBuildNumber;
                if (!string.IsNullOrWhiteSpace(build))
                {
                    var full = string.IsNullOrWhiteSpace(Ubr) ? build : build + "." + Ubr;
                    parts.Add("(build " + full + ")");
                }

                return parts.Count == 0 ? null : string.Join(" ", parts);
            }
        }
    }

    public sealed class PendingRebootState
    {
        public bool ComponentBasedServicing { get; set; }
        public bool WindowsUpdate { get; set; }
        public bool PendingFileRenameOperations { get; set; }

        public bool Any => ComponentBasedServicing || WindowsUpdate || PendingFileRenameOperations;
    }
}
