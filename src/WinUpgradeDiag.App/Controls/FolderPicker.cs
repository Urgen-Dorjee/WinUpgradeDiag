using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WinUpgradeDiag.App.Controls
{
    /// <summary>
    /// The standard Windows folder picker.
    /// <para>
    /// WPF ships no folder dialog at all, which is why the output path started life as a textbox a
    /// technician had to type a path into. That is the one place a typo is both easy and silent —
    /// the report lands somewhere nobody looks. This wraps the shell's <c>IFileOpenDialog</c> in
    /// pick-folders mode, the same dialog every other Windows application shows, rather than the
    /// WinForms tree control, which has looked out of place since Vista.
    /// </para>
    /// </summary>
    internal static class FolderPicker
    {
        /// <summary>
        /// Shows the picker and returns the chosen folder, or null if the operator cancelled.
        /// </summary>
        /// <param name="owner">Window to centre on and block.</param>
        /// <param name="title">Dialog caption, saying what the folder will be used for.</param>
        /// <param name="initialFolder">Where to open. Ignored if it does not exist.</param>
        internal static string Pick(Window owner, string title, string initialFolder)
        {
            IFileOpenDialog dialog = null;
            try
            {
                dialog = (IFileOpenDialog)new FileOpenDialogRcw();

                // FORCEFILESYSTEM keeps the result to a real path: without it the operator can
                // choose a virtual place such as "This PC" or a library, which has no directory
                // behind it and cannot be written to.
                dialog.SetOptions(FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR);

                if (!string.IsNullOrWhiteSpace(title))
                {
                    dialog.SetTitle(title);
                }

                // "Open" is the shell default and reads wrongly for a destination folder.
                dialog.SetOkButtonLabel("Save here");

                SetStartingFolder(dialog, initialFolder);

                var hwnd = OwnerHandle(owner);
                var hr = dialog.Show(hwnd);
                if (hr == HRESULT_CANCELLED)
                {
                    return null;
                }
                if (hr != 0)
                {
                    return null;
                }

                IShellItem item;
                dialog.GetResult(out item);
                if (item == null)
                {
                    return null;
                }

                try
                {
                    IntPtr buffer;
                    item.GetDisplayName(SIGDN_FILESYSPATH, out buffer);
                    if (buffer == IntPtr.Zero)
                    {
                        return null;
                    }

                    try
                    {
                        return Marshal.PtrToStringUni(buffer);
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(buffer);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
            catch (COMException)
            {
                // A shell failure must not take the application down: the operator can still type
                // the path, which is what they were doing before this existed.
                return null;
            }
            catch (InvalidCastException)
            {
                return null;
            }
            catch (NotImplementedException)
            {
                return null;
            }
            finally
            {
                if (dialog != null)
                {
                    Marshal.ReleaseComObject(dialog);
                }
            }
        }

        /// <summary>
        /// Opens the dialog on the nearest existing ancestor of the suggested path. A path typed
        /// but not yet created is common here, and pointing the dialog at its parent is more use
        /// than dropping the operator at "This PC".
        /// </summary>
        private static void SetStartingFolder(IFileOpenDialog dialog, string initialFolder)
        {
            var candidate = initialFolder;
            while (!string.IsNullOrWhiteSpace(candidate))
            {
                bool exists;
                try
                {
                    exists = Directory.Exists(candidate);
                }
                catch (Exception)
                {
                    return;
                }

                if (exists)
                {
                    break;
                }

                try
                {
                    candidate = Path.GetDirectoryName(candidate);
                }
                catch (Exception)
                {
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            try
            {
                var riid = IID_IShellItem;
                IShellItem item;
                var hr = SHCreateItemFromParsingName(candidate, IntPtr.Zero, ref riid, out item);
                if (hr != 0 || item == null)
                {
                    return;
                }

                try
                {
                    dialog.SetFolder(item);
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
            catch (COMException)
            {
            }
        }

        private static IntPtr OwnerHandle(Window owner)
        {
            var window = owner ?? Application.Current?.MainWindow;
            if (window == null)
            {
                return IntPtr.Zero;
            }

            try
            {
                return new WindowInteropHelper(window).Handle;
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }

        // ------------------------------------------------------------------ shell interop

        private const uint FOS_NOCHANGEDIR = 0x00000008;
        private const uint FOS_PICKFOLDERS = 0x00000020;
        private const uint FOS_FORCEFILESYSTEM = 0x00000040;
        private const uint FOS_PATHMUSTEXIST = 0x00000800;

        private const uint SIGDN_FILESYSPATH = 0x80058000;

        /// <summary>HRESULT_FROM_WIN32(ERROR_CANCELLED) — the operator closed the dialog.</summary>
        private const int HRESULT_CANCELLED = unchecked((int)0x800704C7);

        private static Guid IID_IShellItem = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        [ComImport]
        [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        [ClassInterface(ClassInterfaceType.None)]
        private class FileOpenDialogRcw
        {
        }

        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            // Declared but unused members still have to appear, in order: the vtable is positional.
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport]
        [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            // IModalWindow
            [PreserveSig]
            int Show(IntPtr parent);

            // IFileDialog
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);
        }
    }
}
