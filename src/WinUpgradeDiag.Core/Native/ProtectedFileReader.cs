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

        private const int ERROR_FILE_NOT_FOUND = 2;
        private const int ERROR_PATH_NOT_FOUND = 3;
        private const int ERROR_ACCESS_DENIED = 5;

        /// <summary>Why a probe could not confirm a file is present and readable.</summary>
        public enum ProbeResult
        {
            /// <summary>The file exists and can be read (with backup privilege if it was needed).</summary>
            Readable,

            /// <summary>The file, or a directory on the way to it, genuinely is not there.</summary>
            NotFound,

            /// <summary>The file is there but cannot be read even with backup privilege.</summary>
            AccessDenied,

            /// <summary>Something else went wrong; see the reported message.</summary>
            OtherError
        }

        /// <summary>
        /// Answers "is this file there, and can we read it" for a path whose parent directory may
        /// deny us listing.
        /// <para>
        /// <see cref="System.IO.File.Exists"/> cannot be used for this: it returns <c>false</c>
        /// both when a file is absent and when the caller may not traverse to it, which would make
        /// an unreadable rollback log indistinguishable from a machine that never rolled back.
        /// DESIGN.md §8 calls that out specifically — unreadable evidence must be reported as
        /// unreadable, never as "nothing found". CreateFile reports the two cases distinctly.
        /// </para>
        /// </summary>
        public static ProbeResult Probe(string path, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                error = "No path supplied.";
                return ProbeResult.OtherError;
            }

            // Try an ordinary open first so the common case costs nothing extra and no privilege
            // is touched. Only a denial is worth escalating.
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    return ProbeResult.Readable;
                }
            }
            catch (FileNotFoundException)
            {
                return ProbeResult.NotFound;
            }
            catch (DirectoryNotFoundException)
            {
                return ProbeResult.NotFound;
            }
            catch (UnauthorizedAccessException)
            {
                // Fall through to the privileged probe below.
            }
            catch (IOException ex)
            {
                error = "In use or locked: " + ex.Message;
                return ProbeResult.OtherError;
            }

            string privilegeError;
            if (!SeBackupPrivilege.TryEnable(out privilegeError))
            {
                error = "Access denied, and backup privilege is unavailable: " + privilegeError;
                return ProbeResult.AccessDenied;
            }

            using (var handle = CreateBackupHandle(path))
            {
                if (!handle.IsInvalid)
                {
                    return ProbeResult.Readable;
                }

                var win32Error = Marshal.GetLastWin32Error();
                switch (win32Error)
                {
                    case ERROR_FILE_NOT_FOUND:
                    case ERROR_PATH_NOT_FOUND:
                        return ProbeResult.NotFound;
                    case ERROR_ACCESS_DENIED:
                        error = "Access denied even with backup privilege enabled.";
                        return ProbeResult.AccessDenied;
                    default:
                        error = "Could not open the file: Win32 error " + win32Error;
                        return ProbeResult.OtherError;
                }
            }
        }

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

            var handle = CreateBackupHandle(path);

            if (handle.IsInvalid)
            {
                var win32Error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException(
                    "CreateFile failed for '" + path + "' even with SeBackupPrivilege enabled: Win32 error " + win32Error);
            }

            return new FileStream(handle, FileAccess.Read);
        }

        private static SafeFileHandle CreateBackupHandle(string path)
        {
            return CreateFile(
                path,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS,
                IntPtr.Zero);
        }
    }
}
