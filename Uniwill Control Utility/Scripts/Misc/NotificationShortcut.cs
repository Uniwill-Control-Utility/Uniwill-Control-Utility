using System;
using System.IO;
using System.Runtime.InteropServices;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Scripts.Misc
{
    /// <summary>
    /// Gives UCU a single stable notification identity in Windows.
    ///
    /// For unpackaged Win32 apps, Windows only attributes toasts to the process
    /// AppUserModelID when a Start Menu shortcut carrying that same AUMID exists.
    /// Without such a shortcut, Windows falls back to keying notifications by the
    /// executable path, so every build location (bin\Debug, bin\Release, C:\UCU,
    /// TFM folder changes) appears as its own entry under Settings > Notifications.
    /// Creating/refreshing the shortcut at startup consolidates all toasts under
    /// one "Uniwill Control Utility" umbrella regardless of where the exe runs from.
    /// </summary>
    internal static class NotificationShortcut
    {
        internal const string AppUserModelId = "UniwillControlUtility.UCU";

        private const string ShortcutFolderName = "Uniwill Control Utility";
        private const string ShortcutFileName = "Uniwill Control Utility.lnk";

        /// <summary>
        /// Creates or refreshes the Start Menu shortcut tagged with our AUMID.
        /// Must run before the first toast is shown. Safe to call on every startup —
        /// it also heals the shortcut if it was deleted or re-pointed by an update.
        /// </summary>
        public static void Ensure()
        {
            try
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                    return;

                var shortcutDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                    "Programs", ShortcutFolderName);
                Directory.CreateDirectory(shortcutDir);
                var shortcutPath = Path.Combine(shortcutDir, ShortcutFileName);

                var link = (IShellLinkW)new ShellLink();
                link.SetPath(exePath);
                link.SetWorkingDirectory(Path.GetDirectoryName(exePath)!);
                link.SetIconLocation(exePath, 0);
                link.SetDescription("Uniwill Control Utility");

                // Tag the shortcut with PKEY_AppUserModel_ID
                // ({9F4C236E-00DF-4F8F-9C6D-24C8C085B5E0}, pid 5). This is what makes
                // Windows resolve our toast attribution (set via SetCurrentProcessExplicitAppUserModelID)
                // to this app entry instead of keying it by exe path.
                var store = (IPropertyStore)link;
                var propertyKey = new PropertyKey
                {
                    fmtid = new Guid("9F4C236E-00DF-4F8F-9C6D-24C8C085B5E0"),
                    pid = 5
                };
                var value = new PropVariant { vt = 31 }; // VT_LPWSTR
                value.StringValue = Marshal.StringToHGlobalUni(AppUserModelId);
                try
                {
                    store.SetValue(ref propertyKey, ref value);
                }
                finally
                {
                    Marshal.FreeHGlobal(value.StringValue);
                }
                store.Save();

                ((IPersistFile)link).Save(shortcutPath, true);
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to create notification shortcut");
            }
        }

        /* -------------------------------------------------------------------- */
        /*  Shell COM interop (ShellLink object, IPropertyStore, IPersistFile)   */
        /* -------------------------------------------------------------------- */

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLink
        {
        }

        // IShellLinkW — slot order must match the native vtable exactly;
        // only the methods we actually call carry marshalling signatures.
        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath();
            void GetIDList();
            void SetIDList();
            void GetDescription();
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory();
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments();
            void SetArguments();
            void GetHotkey();
            void SetHotkey();
            void GetShowCmd();
            void SetShowCmd();
            void GetIconLocation();
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath();
            void Resolve();
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF91"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            void GetCount(out uint count);
            void GetAt(uint iProp, out PropertyKey pKey);
            void GetValue(ref PropertyKey key, out PropVariant pv);
            void SetValue(ref PropertyKey key, ref PropVariant pv);
            void Save();
        }

        [ComImport, Guid("0000010C-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFile
        {
            void GetClassInfo(out IntPtr pclass);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName,
                      [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PropertyKey
        {
            public Guid fmtid;
            public int pid;
        }

        // Minimal PROPVARIANT for VT_LPWSTR (x64: 8-byte header + 8-byte union).
        [StructLayout(LayoutKind.Sequential)]
        private struct PropVariant
        {
            public ushort vt;
            private readonly ushort wReserved1;
            private readonly ushort wReserved2;
            private readonly ushort wReserved3;
            public IntPtr StringValue; // pwszVal for VT_LPWSTR
        }
    }
}
