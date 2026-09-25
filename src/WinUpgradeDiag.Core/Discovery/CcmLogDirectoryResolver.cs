using System;
using Microsoft.Win32;

namespace WinUpgradeDiag.Core.Discovery
{
    /// <summary>
    /// Resolves the ConfigMgr client log directory at run time instead of hardcoding it —
    /// DESIGN.md §4.1 is explicit that this moves by client configuration.
    /// </summary>
    public static class CcmLogDirectoryResolver
    {
        private const string KeyPath = @"SOFTWARE\Microsoft\CCM\Logging\@GLOBAL";
        private const string ValueName = "LogDirectory";

        public static string Resolve()
        {
            var fromRegistry = TryReadFromRegistry(RegistryView.Registry64)
                ?? TryReadFromRegistry(RegistryView.Registry32);

            if (!string.IsNullOrWhiteSpace(fromRegistry))
            {
                return fromRegistry.TrimEnd('\\');
            }

            var windir = Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows";
            return System.IO.Path.Combine(windir, "CCM", "Logs");
        }

        private static string TryReadFromRegistry(RegistryView view)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var key = baseKey.OpenSubKey(KeyPath))
                {
                    return key?.GetValue(ValueName) as string;
                }
            }
            catch (Exception)
            {
                // Missing key, no CCM client installed, or no access — all normal; the caller
                // falls back to the default client path.
                return null;
            }
        }
    }
}
