using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>
    /// Turns the numeric enumerations the storage and service WMI/registry providers return into
    /// the words they stand for.
    /// <para>
    /// This is not cosmetic. <c>MSFT_PhysicalDisk.HealthStatus</c> comes back as a
    /// <c>UInt16</c> and <c>OperationalStatus</c> as a <c>UInt16[]</c>, so passing them straight to
    /// <c>ToString()</c> puts "0" and the literal text "System.UInt16[]" in front of a technician —
    /// and rule HW-001 in docs/RULES.md, the highest-precedence rule in the catalogue, turns on
    /// telling Healthy from Warning from Unhealthy.
    /// </para>
    /// Unrecognised values are reported as-is rather than guessed at, so a provider that returns
    /// something new degrades to a number instead of a wrong word.
    /// </summary>
    public static class StorageEnums
    {
        // MSFT_PhysicalDisk.HealthStatus
        private static readonly Dictionary<int, string> HealthStatusNames = new Dictionary<int, string>
        {
            { 0, "Healthy" },
            { 1, "Warning" },
            { 2, "Unhealthy" },
            { 5, "Unknown" }
        };

        // MSFT_PhysicalDisk.OperationalStatus
        private static readonly Dictionary<int, string> OperationalStatusNames = new Dictionary<int, string>
        {
            { 0, "Unknown" },
            { 1, "Other" },
            { 2, "OK" },
            { 3, "Degraded" },
            { 4, "Stressed" },
            { 5, "Predictive Failure" },
            { 6, "Error" },
            { 7, "Non-Recoverable Error" },
            { 8, "Starting" },
            { 9, "Stopping" },
            { 10, "Stopped" },
            { 11, "In Service" },
            { 12, "No Contact" },
            { 13, "Lost Communication" },
            { 14, "Aborted" },
            { 15, "Dormant" },
            { 16, "Supporting Entity in Error" },
            { 17, "Completed" },
            { 18, "Power Mode" },
            { 0xD010, "Unrecognised Metadata" },
            { 0xD011, "Hardware Error" },
            { 0xD012, "Not Initialized" }
        };

        // HKLM\SYSTEM\CurrentControlSet\Services\<name>\Start
        private static readonly Dictionary<int, string> StartModeNames = new Dictionary<int, string>
        {
            { 0, "Boot" },
            { 1, "System" },
            { 2, "Automatic" },
            { 3, "Manual" },
            { 4, "Disabled" }
        };

        public static string HealthStatus(object value)
        {
            return Describe(value, HealthStatusNames);
        }

        /// <summary>
        /// Describes an operational status, which the provider returns as an array — a disk can be
        /// several things at once (for example "OK" plus "Predictive Failure").
        /// </summary>
        public static string OperationalStatus(object value)
        {
            if (value == null)
            {
                return null;
            }

            var array = value as System.Collections.IEnumerable;
            if (array != null && !(value is string))
            {
                var parts = array.Cast<object>()
                    .Select(v => Describe(v, OperationalStatusNames))
                    .Where(v => !string.IsNullOrEmpty(v))
                    .ToList();

                return parts.Count == 0 ? null : string.Join(", ", parts);
            }

            return Describe(value, OperationalStatusNames);
        }

        public static string StartMode(int? value)
        {
            return value.HasValue ? Describe(value.Value, StartModeNames) : null;
        }

        /// <summary>
        /// Maps a numeric value to its name, falling back to the raw number when the value is not
        /// one this table knows. Never throws: a surprising value is worth showing, not hiding.
        /// </summary>
        private static string Describe(object value, Dictionary<int, string> names)
        {
            if (value == null)
            {
                return null;
            }

            int number;
            try
            {
                number = Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                // Not numeric after all — show whatever the provider gave us.
                return value.ToString();
            }

            string name;
            return names.TryGetValue(number, out name)
                ? name
                : number.ToString(CultureInfo.InvariantCulture);
        }
    }
}
