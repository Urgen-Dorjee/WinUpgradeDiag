using System;
using System.Runtime.InteropServices;

namespace WinUpgradeDiag.Core.Native
{
    /// <summary>
    /// Enables SeBackupPrivilege on the current process token. Per AGENTS.md, this is how the
    /// tool reads TrustedInstaller-owned logs (`$WINDOWS.~BT\Sources\Panther` and `\Rollback`)
    /// without taking ownership or touching ACLs. The process must already be elevated
    /// (Administrator) — the privilege exists on the admin token but is disabled by default.
    /// </summary>
    public static class SeBackupPrivilege
    {
        private const string PrivilegeName = "SeBackupPrivilege";
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID_AND_ATTRIBUTES
        {
            public LUID Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID_AND_ATTRIBUTES Privileges;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(
            IntPtr tokenHandle,
            [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
            ref TOKEN_PRIVILEGES newState,
            uint bufferLengthInBytes,
            IntPtr previousState,
            IntPtr returnLengthInBytes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        /// <summary>
        /// Attempts to enable SeBackupPrivilege for the running process. Returns false (never
        /// throws) if the privilege cannot be enabled — e.g. the process is not elevated — so
        /// callers can fall back to reporting the log as unreadable rather than crashing.
        /// </summary>
        public static bool TryEnable(out string error)
        {
            error = null;
            IntPtr tokenHandle = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out tokenHandle))
                {
                    error = "OpenProcessToken failed: Win32 error " + Marshal.GetLastWin32Error();
                    return false;
                }

                LUID luid;
                if (!LookupPrivilegeValue(null, PrivilegeName, out luid))
                {
                    error = "LookupPrivilegeValue failed: Win32 error " + Marshal.GetLastWin32Error();
                    return false;
                }

                var privileges = new TOKEN_PRIVILEGES
                {
                    PrivilegeCount = 1,
                    Privileges = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED }
                };

                if (!AdjustTokenPrivileges(tokenHandle, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
                {
                    error = "AdjustTokenPrivileges failed: Win32 error " + Marshal.GetLastWin32Error();
                    return false;
                }

                // AdjustTokenPrivileges can succeed but silently not grant the privilege if the
                // token doesn't hold it at all (ERROR_NOT_ALL_ASSIGNED = 1300).
                var lastError = Marshal.GetLastWin32Error();
                if (lastError == 1300)
                {
                    error = "Token does not hold SeBackupPrivilege — is the process running elevated?";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (tokenHandle != IntPtr.Zero)
                {
                    CloseHandle(tokenHandle);
                }
            }
        }
    }
}
