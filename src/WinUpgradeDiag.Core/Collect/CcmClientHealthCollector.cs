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

        /// <summary>
        /// The site code and management point this machine was last assigned to, as
        /// "SITE/mp.fqdn", or null if neither can be established.
        /// <para>
        /// Exists so the rebuild tool does not have to ask. Being asked to type
        /// "SITE/mp.fqdn" is a bad question: it is two facts the operator has to go and find,
        /// joined by a separator that only exists because the tool runner passes one argument.
        /// The machine knows both, and keeps knowing them in ccmsetup.log even after the
        /// registry has been cleaned out - which is exactly the state this tool runs in.
        /// </para>
        /// </summary>
        public static string SuggestTarget()
        {
            var site = ReadString(@"SOFTWARE\Microsoft\SMS\Mobile Client", "AssignedSiteCode")
                       ?? ReadString(@"SOFTWARE\Microsoft\CCM", "AssignedSiteCode");
            var mp = ReadString(@"SOFTWARE\Microsoft\SMS\Mobile Client", "AssignedMP")
                     ?? ReadString(@"SOFTWARE\Microsoft\CCM", "SMSSLP");

            if (site == null || mp == null)
            {
                var fromLog = FromCcmSetupLog();
                site = site ?? fromLog.Item1;
                mp = mp ?? fromLog.Item2;
            }

            if (string.IsNullOrWhiteSpace(site) || string.IsNullOrWhiteSpace(mp))
            {
                return null;
            }

            return site.Trim() + "/" + mp.Trim();
        }

        /// <summary>
        /// Recovers the site code and management point from the last install attempt recorded in
        /// ccmsetup.log. The folder survives the cleanup this tool performs, so this is what
        /// answers the question on a machine whose client registry keys are already gone.
        /// </summary>
        public static Tuple<string, string> FromCcmSetupLog(string logPath = null)
        {
            string site = null;
            string mp = null;

            try
            {
                var path = logPath ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "ccmsetup", "Logs", "ccmsetup.log");

                if (!File.Exists(path))
                {
                    return Tuple.Create(site, mp);
                }

                // Read-share: ccmsetup may still hold the file open.
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        // Later entries win: the most recent attempt is the relevant one.
                        var siteMatch = SiteCodeInLog.Match(line);
                        if (siteMatch.Success)
                        {
                            site = siteMatch.Groups["site"].Value;
                        }

                        var mpMatch = ManagementPointInLog.Match(line);
                        if (mpMatch.Success)
                        {
                            mp = mpMatch.Groups["mp"].Value;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // A missing or unreadable log just means no suggestion.
            }

            return Tuple.Create(site, mp);
        }

        private static readonly System.Text.RegularExpressions.Regex SiteCodeInLog =
            new System.Text.RegularExpressions.Regex(
                @"(?:SMSSITECODE=|Assigned site code[:\s]+|site code[:\s]+)(?<site>[A-Za-z0-9]{3})\b",
                System.Text.RegularExpressions.RegexOptions.Compiled |
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static readonly System.Text.RegularExpressions.Regex ManagementPointInLog =
            new System.Text.RegularExpressions.Regex(
                @"(?:SMSMP=|/mp:|MP '|management point[:\s]+)(?<mp>[A-Za-z0-9][A-Za-z0-9.\-]{3,})",
                System.Text.RegularExpressions.RegexOptions.Compiled |
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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
