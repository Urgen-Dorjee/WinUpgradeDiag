using System;
using System.IO;
using System.Text;
using WinUpgradeDiag.Core.IO;

namespace WinUpgradeDiag.Core.Collect
{
    /// <summary>What sort of dump a file turned out to be.</summary>
    public enum DumpKind
    {
        /// <summary>Not a dump format this reader recognises, or too short to tell.</summary>
        Unknown,

        /// <summary>A 64-bit kernel dump: written when Windows bugchecked. Carries the stop code.</summary>
        Kernel64,

        /// <summary>A 32-bit kernel dump.</summary>
        Kernel32,

        /// <summary>A user-mode minidump of a single process. Not a bugcheck.</summary>
        UserMode
    }

    /// <summary>The stop code and parameters read from a dump's header.</summary>
    public sealed class CrashDumpInfo
    {
        public string Path { get; set; }
        public DumpKind Kind { get; set; }

        /// <summary>The bug check (stop) code, e.g. 0x1D5 for DRIVER_PNP_WATCHDOG.</summary>
        public uint BugCheckCode { get; set; }

        /// <summary>The four bug check parameters, as the dump records them.</summary>
        public ulong[] Parameters { get; set; } = new ulong[4];

        /// <summary>When the dump file was written — at the boot after the crash.</summary>
        public DateTime? WrittenUtc { get; set; }

        public string Error { get; set; }

        public bool IsBugCheck => Kind == DumpKind.Kernel64 || Kind == DumpKind.Kernel32;
    }

    /// <summary>
    /// Reads the stop code from a crash dump's header, without a debugger.
    /// <para>
    /// The name on the crash screen — DRIVER_PNP_WATCHDOG — is not written to any Setup log. The
    /// logs record what Setup saw afterwards, typically 0xC1900101 and an extend code, which is why
    /// searching them for the name found nothing on a machine that had shown it on screen. The code
    /// itself is in the dump, at a fixed offset in a header that has had the same layout for every
    /// kernel dump Windows has written in twenty years: "PAGE" then "DU64", and the stop code at
    /// 0x38 with its four parameters after it.
    /// </para>
    /// <para>
    /// Which driver caused it is not in the header. That needs the stack, which needs a debugger
    /// and symbols, and this reader does not pretend otherwise. The driver is usually recoverable a
    /// different way — the device install that was cut off by the crash, in setupapi.dev.log.
    /// </para>
    /// </summary>
    public static class CrashDumpReader
    {
        private const int HeaderBytes = 0x60;

        public static CrashDumpInfo ReadFile(string path)
        {
            var info = new CrashDumpInfo { Path = path };

            try
            {
                // Through the privileged opener: setupmem.dmp under $WINDOWS.~BT is owned by
                // TrustedInstaller and a plain open is refused even to an administrator.
                using (var stream = TailReader.OpenForRead(path))
                {
                    var read = Read(stream);
                    read.Path = path;
                    info = read;
                }
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
                return info;
            }

            try
            {
                info.WrittenUtc = File.GetLastWriteTimeUtc(path);
            }
            catch (Exception)
            {
                // The stop code is the point; a missing timestamp is not worth failing over.
            }

            return info;
        }

        /// <summary>Parses a dump header from a stream positioned at its start.</summary>
        public static CrashDumpInfo Read(Stream stream)
        {
            var info = new CrashDumpInfo();
            var header = new byte[HeaderBytes];

            var total = 0;
            while (total < header.Length)
            {
                var n = stream.Read(header, total, header.Length - total);
                if (n <= 0)
                {
                    break;
                }
                total += n;
            }

            if (total < 8)
            {
                info.Error = "The file is too short to be a dump.";
                return info;
            }

            var signature = Encoding.ASCII.GetString(header, 0, 4);
            var valid = Encoding.ASCII.GetString(header, 4, 4);

            if (signature == "MDMP")
            {
                info.Kind = DumpKind.UserMode;
                return info;
            }

            if (signature != "PAGE")
            {
                info.Error = "Not a recognised dump format.";
                return info;
            }

            if (valid == "DU64" && total >= 0x60)
            {
                // DUMP_HEADER64: BugCheckCode at 0x38 (with 4 bytes padding after), then four
                // 64-bit parameters from 0x40.
                info.Kind = DumpKind.Kernel64;
                info.BugCheckCode = BitConverter.ToUInt32(header, 0x38);
                for (var i = 0; i < 4; i++)
                {
                    info.Parameters[i] = BitConverter.ToUInt64(header, 0x40 + i * 8);
                }
                return info;
            }

            if (valid == "DUMP" && total >= 0x3C)
            {
                // DUMP_HEADER32: BugCheckCode at 0x28, then four 32-bit parameters from 0x2C.
                info.Kind = DumpKind.Kernel32;
                info.BugCheckCode = BitConverter.ToUInt32(header, 0x28);
                for (var i = 0; i < 4; i++)
                {
                    info.Parameters[i] = BitConverter.ToUInt32(header, 0x2C + i * 4);
                }
                return info;
            }

            info.Error = "A kernel dump signature, but a header too short or of an unknown version.";
            return info;
        }
    }
}
