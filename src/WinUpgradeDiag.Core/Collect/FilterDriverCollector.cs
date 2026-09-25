using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>
    /// Lists registered minifilter drivers by reading each service's "Group" value under
    /// HKLM\SYSTEM\CurrentControlSet\Services. Any group starting with "FSFilter" is one of the
    /// standard Filter Manager altitude groups (Anti-Virus, Encryption, Content Screener —
    /// removable-media/DLP products register here — Activity Monitor, Compression, and so on).
    /// This is the same classification Windows itself uses, so it needs no vendor keyword list.
    /// </summary>
    public sealed class FilterDriverCollector
    {
        private const string ServicesKeyPath = @"SYSTEM\CurrentControlSet\Services";

        public IReadOnlyList<FilterDriverInfo> Collect()
        {
            var results = new List<FilterDriverInfo>();

            try
            {
                using (var services = Registry.LocalMachine.OpenSubKey(ServicesKeyPath))
                {
                    if (services == null)
                    {
                        return results;
                    }

                    foreach (var serviceName in services.GetSubKeyNames())
                    {
                        try
                        {
                            using (var service = services.OpenSubKey(serviceName))
                            {
                                var group = service?.GetValue("Group") as string;
                                if (group == null || !group.StartsWith("FSFilter", StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                results.Add(new FilterDriverInfo
                                {
                                    ServiceName = serviceName,
                                    DisplayName = service.GetValue("DisplayName") as string,
                                    ImagePath = service.GetValue("ImagePath") as string,
                                    AltitudeGroup = group,
                                    StartMode = service.GetValue("Start") as int?
                                });
                            }
                        }
                        catch (Exception)
                        {
                            // One unreadable service key must not stop the rest of the scan.
                        }
                    }
                }
            }
            catch (Exception)
            {
                // No access to the Services key at all — return whatever was found (nothing).
            }

            return results;
        }
    }
}
