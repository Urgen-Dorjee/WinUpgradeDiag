using System.Collections.Generic;
using System.Globalization;

namespace WinUpgradeDiag.Core.Rules
{
    /// <summary>
    /// Stop codes and their names, for the ones an in-place upgrade actually produces.
    /// <para>
    /// Deliberately short. Each entry carries a sentence on what the crash means for an upgrade,
    /// and a wrong sentence would be worse than a bare code, so anything not listed is reported by
    /// number with a pointer to Microsoft's bug check reference rather than guessed at.
    /// </para>
    /// </summary>
    public static class BugCheckNames
    {
        private sealed class Entry
        {
            public Entry(string name, string meaning)
            {
                Name = name;
                Meaning = meaning;
            }

            public string Name { get; }
            public string Meaning { get; }
        }

        private static readonly Dictionary<uint, Entry> Known = new Dictionary<uint, Entry>
        {
            { 0x1D5, new Entry("DRIVER_PNP_WATCHDOG",
                "A driver did not finish a plug and play operation in time. During an upgrade Setup asks the " +
                "driver for every device to start on the new build, and one of them stalled.") },
            { 0x9F, new Entry("DRIVER_POWER_STATE_FAILURE",
                "A driver did not complete a power state change. Common on the restarts an upgrade performs.") },
            { 0x133, new Entry("DPC_WATCHDOG_VIOLATION",
                "A driver held a processor at high priority for too long. Usually a storage or network driver.") },
            { 0x101, new Entry("CLOCK_WATCHDOG_TIMEOUT",
                "A processor stopped responding. Usually firmware or a very low-level driver.") },
            { 0x7B, new Entry("INACCESSIBLE_BOOT_DEVICE",
                "The new build could not reach its boot disk. Usually the storage controller's driver does not " +
                "work on the new version.") },
            { 0x7E, new Entry("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED",
                "A driver crashed while running a system thread.") },
            { 0x3B, new Entry("SYSTEM_SERVICE_EXCEPTION",
                "A driver or system component crashed while handling a call from the system.") },
            { 0xD1, new Entry("DRIVER_IRQL_NOT_LESS_OR_EQUAL",
                "A driver touched memory it was not allowed to at the time.") },
            { 0x0A, new Entry("IRQL_NOT_LESS_OR_EQUAL",
                "A driver or system component touched memory it was not allowed to at the time.") },
            { 0x50, new Entry("PAGE_FAULT_IN_NONPAGED_AREA",
                "Memory that must always be present was not. A faulty driver or faulty RAM.") },
            { 0x1E, new Entry("KMODE_EXCEPTION_NOT_HANDLED",
                "A kernel-mode program crashed and nothing handled it.") },
            { 0xEF, new Entry("CRITICAL_PROCESS_DIED",
                "A process Windows cannot run without stopped.") },
            { 0x116, new Entry("VIDEO_TDR_FAILURE",
                "The display driver stopped responding and could not be recovered.") },
            { 0x124, new Entry("WHEA_UNCORRECTABLE_ERROR",
                "The hardware reported an error it could not correct. Suspect the hardware before any driver.") },
            { 0x139, new Entry("KERNEL_SECURITY_CHECK_FAILURE",
                "The kernel found its own data corrupted, usually by a driver.") },
            { 0x154, new Entry("UNEXPECTED_STORE_EXCEPTION",
                "The memory store hit an error it did not expect. Often the disk.") },
            { 0x7A, new Entry("KERNEL_DATA_INPAGE_ERROR",
                "Windows could not read data back from disk into memory. Suspect the disk.") },
            { 0x19, new Entry("BAD_POOL_HEADER",
                "Kernel memory was corrupted, usually by a driver.") },
            { 0xC2, new Entry("BAD_POOL_CALLER",
                "A driver made an invalid kernel memory request.") },
            { 0xC5, new Entry("DRIVER_CORRUPTED_EXPOOL",
                "A driver corrupted kernel memory.") }
        };

        /// <summary>"0x000001D5", the form the event log and the crash screen use.</summary>
        public static string Code(uint code)
        {
            return "0x" + code.ToString("X8", CultureInfo.InvariantCulture);
        }

        /// <summary>The stop code's name, or null when it is not one this table knows.</summary>
        public static string Name(uint code)
        {
            Entry entry;
            return Known.TryGetValue(code, out entry) ? entry.Name : null;
        }

        public static string Meaning(uint code)
        {
            Entry entry;
            return Known.TryGetValue(code, out entry)
                ? entry.Meaning
                : "Look the code up in Microsoft's bug check code reference; the dump holds the driver responsible.";
        }

        /// <summary>"DRIVER_PNP_WATCHDOG (0x000001D5)", or the bare code when the name is unknown.</summary>
        public static string Describe(uint code)
        {
            var name = Name(code);
            return name == null ? "stop code " + Code(code) : name + " (" + Code(code) + ")";
        }
    }
}
