using System;
using System.Globalization;
using System.IO;
using System.Management;
using Microsoft.Win32;

namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>
    /// Reads the ConfigMgr client's own health, in the order a technician would check it by hand.
    /// <para>
    /// Every probe is best effort and every failure is recorded rather than thrown: this runs on a
    /// machine where the client is, by assumption, broken, so WMI queries returning provider load
    /// failures is the expected case and not an error condition.
    /// </para>
    /// </summary>
    public sealed class CcmClientHealthCollector
    {
        /// <summary>Namespaces a working client owns. Their absence is what a failed repair leaves.</summary>
        private static readonly string[] ClientNamespaces =
        {
            @"root\ccm",
            @"root\ccm\SoftMgmtAgent",
            @"root\ccm\Policy\Machine",
            @"root\cimv2\sms"
        };

        public CcmClientHealthInfo Collect()
        {
            var info = new CcmClientHealthInfo();

            CollectFolderAndVersion(info);
            CollectService(info);
            CollectNamespaces(info);
            CollectAssignment(info);
            CollectWmiRepository(info);

            return info;
        }

        private static void CollectFolderAndVersion(CcmClientHealthInfo info)
        {
            try
            {
                var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                info.ClientFolderExists = Directory.Exists(Path.Combine(windir, "CCM"));
            }
            catch (Exception ex)
            {
                info.Errors.Add("Could not check for the CCM folder: " + ex.Message);
            }

            try
            {
                // The registry version survives a client whose WMI provider will not load, which
                // is exactly the machine this runs on.
                using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(@"SOFTWARE\Microsoft\CCM"))
                {
                    info.ClientVersion = key?.GetValue("ProductVersion") as string;
                }
            }
            catch (Exception ex)
            {
                info.Errors.Add("Could not read the client version: " + ex.Message);
            }
        }

        private static void CollectService(CcmClientHealthInfo info)
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT State, StartMode FROM Win32_Service WHERE Name = 'CcmExec'"))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject row in results)
                    {
                        using (row)
                        {
                            info.CcmExecInstalled = true;
                            info.CcmExecState = row["State"] as string;
                            info.CcmExecStartMode = row["StartMode"] as string;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                info.Errors.Add("Could not read the CcmExec service: " + ex.Message);
            }
        }

        /// <summary>
        /// Queries root\ccm the way the control panel applet does. A provider load failure here is
        /// the single most useful fact about a client that "starts and then stops".
        /// </summary>
        private static void CollectNamespaces(CcmClientHealthInfo info)
        {
            foreach (var ns in ClientNamespaces)
            {
                bool present;
                try
                {
                    var scope = new ManagementScope(ns);
                    scope.Connect();
                    present = scope.IsConnected;
                }
                catch (Exception)
                {
                    present = false;
                }

                info.Namespaces[ns] = present;
            }

            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    new ManagementScope(@"root\ccm"), new ObjectQuery("SELECT * FROM SMS_Client")))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject row in results)
                    {
                        using (row)
                        {
                            info.CcmNamespaceResponds = true;
                            info.ClientVersion = info.ClientVersion ?? row["ClientVersion"] as string;
                            break;
                        }
                    }

                    // An empty result set still means the provider loaded and answered.
                    info.CcmNamespaceResponds = true;
                }
            }
            catch (ManagementException ex)
            {
                info.CcmNamespaceResponds = false;
                info.CcmNamespaceError = ex.Message;
                info.CcmNamespaceHResult = Hex(ex.ErrorCode == ManagementStatus.Failed
                    ? System.Runtime.InteropServices.Marshal.GetHRForException(ex)
                    : (int)ex.ErrorCode);
            }
            catch (Exception ex)
            {
                info.CcmNamespaceResponds = false;
                info.CcmNamespaceError = ex.Message;
                info.CcmNamespaceHResult = Hex(System.Runtime.InteropServices.Marshal.GetHRForException(ex));
            }
        }

        private static void CollectAssignment(CcmClientHealthInfo info)
        {
            // The registry copy is readable even when the provider is not, which is the whole
            // reason to look here rather than asking SMS_Client for its site code.
            info.SiteCode = ReadString(@"SOFTWARE\Microsoft\SMS\Mobile Client", "AssignedSiteCode")
                            ?? ReadString(@"SOFTWARE\Microsoft\CCM", "AssignedSiteCode");

            info.ManagementPoint = ReadString(@"SOFTWARE\Microsoft\SMS\Mobile Client", "AssignedMP")
                                   ?? ReadString(@"SOFTWARE\Microsoft\CCM", "SMSSLP");

            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    new ManagementScope(@"root\ccm"), new ObjectQuery("SELECT * FROM SMS_Authority")))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject row in results)
                    {
                        using (row)
                        {
                            var name = row["Name"] as string;
                            if (!string.IsNullOrWhiteSpace(name) && name.StartsWith("SMS:", StringComparison.OrdinalIgnoreCase))
                            {
                                info.SiteCode = info.SiteCode ?? name.Substring(4);
                            }
                            info.ManagementPoint = info.ManagementPoint ?? row["CurrentManagementPoint"] as string;
                            break;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Expected when the provider is down; the registry values above still stand.
            }
        }

        private static void CollectWmiRepository(CcmClientHealthInfo info)
        {
            try
            {
                // Connecting to root and reading a class is the same consistency question
                // winmgmt /verifyrepository answers, without shelling out to it.
                var scope = new ManagementScope(@"root\cimv2");
                scope.Connect();
                using (var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Name FROM Win32_ComputerSystem")))
                using (var results = searcher.Get())
                {
                    foreach (ManagementObject row in results)
                    {
                        row.Dispose();
                        break;
                    }
                }
                info.WmiRepositoryConsistent = true;
            }
            catch (Exception ex)
            {
                info.WmiRepositoryConsistent = false;
                info.Errors.Add("WMI core namespace did not answer: " + ex.Message);
            }
        }

        private static string ReadString(string subKey, string valueName)
        {
            try
            {
                using (var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                    .OpenSubKey(subKey))
                {
                    var value = key?.GetValue(valueName) as string;
                    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Hex(int value)
        {
            return "0x" + value.ToString("X8", CultureInfo.InvariantCulture);
        }
    }
}
