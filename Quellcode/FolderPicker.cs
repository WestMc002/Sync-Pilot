// Sync Pilot – Ordnerauswahl im aktuellen Windows-Dialog (statt des alten Ordner-Baums)
// Copyright (C) 2026 Günther Schoder · https://www.westmc002.de
// Lizenz: GNU General Public License v3.0 oder später (siehe LICENSE)
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SyncPilot
{
    /// <summary>
    /// Ersatz für FolderBrowserDialog mit gleicher Bedienung (Description, SelectedPath, ShowDialog).
    /// .NET Framework zeigt dort noch den alten Baum ohne Adressleiste und ohne Dunkelmodus; hier kommt der
    /// Windows-Dialog „Ordner auswählen“ wie im Explorer (IFileOpenDialog mit FOS_PICKFOLDERS).
    /// Klappt das nicht (sehr alte Systeme), wird der alte Dialog verwendet.
    /// </summary>
    public sealed class FolderPicker : IDisposable
    {
        public string Description = "";
        public string SelectedPath = "";
        /// <summary>Nur für gleiche Bedienung wie FolderBrowserDialog; der neue Dialog kann immer Ordner anlegen.</summary>
        public bool ShowNewFolderButton = true;

        /// <summary>Ordner wählen (Globale Richtlinien, „Weitere Fenster“); null bei Abbruch.</summary>
        public static string Pick(IWin32Window owner, string title, string initial)
        {
            using (FolderPicker p = new FolderPicker())
            {
                p.Description = title ?? "";
                p.SelectedPath = initial ?? "";
                return p.ShowDialog(owner) == DialogResult.OK ? p.SelectedPath : null;
            }
        }

        public DialogResult ShowDialog(IWin32Window owner)
        {
            try { return ShowModern(owner); }
            catch (COMException) { }
            catch (InvalidCastException) { }
            catch (NotImplementedException) { }
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = Description;
                d.ShowNewFolderButton = ShowNewFolderButton;
                if (Directory.Exists(SelectedPath)) d.SelectedPath = SelectedPath;
                DialogResult r = d.ShowDialog(owner);
                if (r == DialogResult.OK) SelectedPath = d.SelectedPath;
                return r;
            }
        }

        DialogResult ShowModern(IWin32Window owner)
        {
            IFileOpenDialog dlg = (IFileOpenDialog)new FileOpenDialogRCW();
            try
            {
                uint opts;
                dlg.GetOptions(out opts);
                dlg.SetOptions(opts | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
                if (!string.IsNullOrEmpty(Description)) dlg.SetTitle(Description);
                if (!string.IsNullOrEmpty(SelectedPath) && Directory.Exists(SelectedPath))
                {
                    IShellItem item;
                    Guid iid = typeof(IShellItem).GUID;
                    if (SHCreateItemFromParsingName(SelectedPath, IntPtr.Zero, ref iid, out item) == 0 && item != null)
                        dlg.SetFolder(item);
                }
                // Besitzer ist oft eine Ansicht im Hauptfenster: deren Hauptfenster verwenden
                IntPtr hwnd = owner != null ? GetAncestor(owner.Handle, 2) : IntPtr.Zero;
                int hr = dlg.Show(hwnd);
                if (hr == unchecked((int)0x800704C7)) return DialogResult.Cancel; // abgebrochen
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                IShellItem result;
                dlg.GetResult(out result);
                string path;
                result.GetDisplayName(SIGDN_FILESYSPATH, out path);
                SelectedPath = path;
                return DialogResult.OK;
            }
            finally { Marshal.ReleaseComObject(dlg); }
        }

        public void Dispose() { }

        const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800;
        const uint SIGDN_FILESYSPATH = 0x80058000;

        [DllImport("user32.dll")]
        static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem item);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogRCW { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
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
            void GetResults(out IntPtr ppenum);
            void GetSelectedItems(out IntPtr ppsai);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }
    }
}
