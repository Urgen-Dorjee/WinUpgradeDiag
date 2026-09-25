using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinUpgradeDiag.Core.Native
{
    /// <summary>
    /// Opens a file the normal way fails on with access denied — TrustedInstaller-owned Setup
    /// logs under `$WINDOWS.~BT` — by enabling SeBackupPrivilege and opening with
    /// FILE_FLAG_BACKUP_SEMANTICS, which lets an elevated backup-privileged process read past the
    /// ACL instead of taking ownership of the file (AGENTS.md, non-negotiable constraint #4 and
    /// the "conventions" section).
    /// </summary>
    public static class ProtectedFileReader
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint FILE_SHARE_DELETE = 0x00000004;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        /// <summary>
        /// Opens <paramref name="path"/> for read using SeBackupPrivilege. Throws
        /// <see cref="IOException"/> with a plain-language message on failure — callers should
        /// catch it and record the log as unreadable rather than let it propagate, per
        /// AGENTS.md's "degrade, never crash".
        /// </summary>
        public static FileStream OpenForPrivilegedRead(string path)
        {
            string privilegeError;
            if (!SeBackupPrivilege.TryEnable(out privilegeError))
            {
                throw new IOException(
                    "Cannot read '" + path + "' as a protected file: " + privilegeError);
            }

            var handle = CreateFile(
                path,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                var win32Error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException(
                    "CreateFile failed for '" + path + "' even with SeBackupPrivilege enabled: Win32 error " + win32Error);
            }

            return new FileStream(handle, FileAccess.Read);
        }
    }
}
