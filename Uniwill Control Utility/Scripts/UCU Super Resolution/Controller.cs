using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using Gma.System.MouseKeyHook;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Scripts.UCU_Super_Resolution;
using Uniwill_Control_Utility.Views.Windows;

namespace Uniwill_Control_Utility.Scripts.UCU_Super_Resolution
{
    //
    // This is a customised version of Magpie from https://github.com/Blinue/Magpie
    // I do not take credit for the full functionality of the code.
    //

    internal static class Controller
    {
        internal static MagWindow? magWindow;

        private static readonly ScaleModelManager scaleModelManager = new();

        private static string AppName = null!;
        private static string currentAppName = null!;
        private static bool canReapply;

        private static IntPtr Handle;
        private static IntPtr prevSrcWindow;

        private static void ToggleOverlay()
        {
            _ = NativeMethods.BroadcastMessage(NativeMethods.MAGPIE_WM_TOGGLE_OVERLAY);
        }

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        internal static extern bool SystemParametersInfo(int uAction, int uParam, ref RECT lpvParam, int fuWinIni);

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private const uint SWP_SHOWWINDOW = 0x0040;
        private const int SPI_GETWORKAREA = 48;

        private const int SM_CYBORDER = 6;

        private const int GW_OWNER = 4;
        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_CHILD = 0x40000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_BORDER = 0x00800000;
        private const int Win32HeightAdjustment = 32;
        private const int NonWin32HeightAdjustment = 40;
        private const int WindowTitleBufferSize = 256;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        public static void ToggleMagWindow()
        {
            if (!(Settings.Default.isMagpie || magWindow!.Running))
            {
                return;
            }

            if (!scaleModelManager.IsValid() || magWindow == null)
            {
                return;
            }

            if (magWindow.Running)
            {
                magWindow.Destory();
                canReapply = false;
                using (Process p = Process.GetCurrentProcess()) p.PriorityClass = ProcessPriorityClass.Normal;
                return;
            }

            using (Process p = Process.GetCurrentProcess()) p.PriorityClass = ProcessPriorityClass.AboveNormal;
            canReapply = true;

            string sharpness = Convert.ToString(Settings.Default.Sharpness);

            double scaleFactor = Settings.Default.ResMode switch
            {
                1 => 0.77,
                2 => 0.67,
                3 => 0.59,
                4 => 0.50,
                5 => 0.33,
                _ => 0.59,
            };

            IntPtr foregroundWindow = GetForegroundWindow();

            if (foregroundWindow != IntPtr.Zero && Settings.Default.ResMode > 0)
            {
                if (GetWindowRect(foregroundWindow, out _))
                {
                    Screen primaryScreen = Screen.PrimaryScreen!;
                    int screenWidth = primaryScreen.Bounds.Width;
                    int screenHeight = primaryScreen.Bounds.Height;

                    int newWidth = (int)Math.Floor(screenWidth * scaleFactor);
                    int newHeight = (int)Math.Floor(screenHeight * scaleFactor);

                    int windowStyle = GetWindowLong(foregroundWindow, GWL_STYLE);

                    StringBuilder windowText = new StringBuilder(WindowTitleBufferSize);
                    _ = GetWindowText(foregroundWindow, windowText, windowText.Capacity);

                    AppName = windowText.ToString();

                    // Check if the window has the WS_BORDER style bit set
                    bool isBorderless = (windowStyle & WS_BORDER) == 0;

                    if (!isBorderless)
                    {
                        int extendedWindowStyle = GetWindowLong(foregroundWindow, GWL_EXSTYLE);

                        // Check if it's a Win32 application by looking for certain window styles
                        bool isWin32Application = (windowStyle & WS_CHILD) == 0 && (extendedWindowStyle & WS_EX_TOOLWINDOW) == 0;

                        newHeight += isWin32Application ? Win32HeightAdjustment : NonWin32HeightAdjustment;
                    }

                    int newX = (screenWidth - newWidth) / 2;
                    int newY = (screenHeight - newHeight) / 2;

                    SetWindowPos(foregroundWindow, IntPtr.Zero, newX, newY, newWidth, newHeight, SWP_SHOWWINDOW);
                }
            }

            string effectsJson = scaleModelManager.GetScaleModels()![Settings.Default.ScaleMode].Effects;

            int index = effectsJson.LastIndexOf(':');
            if (index >= 0)
                effectsJson = effectsJson.Substring(0, index);

            effectsJson = $"{effectsJson}:{sharpness}}}]";

            magWindow.Create(effectsJson);

            prevSrcWindow = magWindow.SrcWindow;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        public static void AutoRestore_Tick(object? _ = null, EventArgs? __ = null)
        {
            if (Settings.Default.AutoRestore && Settings.Default.isMagpie && canReapply)
            {
                IntPtr foregroundWindow = GetForegroundWindow();

                if (foregroundWindow != IntPtr.Zero)
                {
                    StringBuilder windowText = new StringBuilder(WindowTitleBufferSize);
                    _ = GetWindowText(foregroundWindow, windowText, windowText.Capacity);
                    currentAppName = windowText.ToString();

                    if (currentAppName == AppName && !magWindow!.Running)
                    {
                        ToggleMagWindow();
                    }
                }
            }
        }
        private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.MAGPIE_WM_SHOWME)
            {
                _ = NativeMethods.SetForegroundWindow(Handle);
                handled = true;
            }

            _ = (hwnd, wParam, lParam); // unused
            return IntPtr.Zero;
        }

        public static void SetUpMagWindow(MainWindow main)
        {
            Handle = new WindowInteropHelper(main).Handle;

            magWindow = new MagWindow(main);
            magWindow.Closed += MagWindow_Closed;

            OnHotkeyChanged();
        }

        private static void MagWindow_Closed()
        {
            if (!Settings.Default.AutoRestore || prevSrcWindow == Handle)
            {
                return;
            }
        }

        private static IKeyboardMouseEvents? keyboardEvents;

        public static bool OnHotkeyChanged()
        {
            keyboardEvents?.Dispose();
            keyboardEvents = Hook.GlobalEvents();

            try
            {
                keyboardEvents.OnCombination(new Dictionary<Combination, Action> {
                    {Combination.FromString(Settings.Default.MagpieHotkey), () => ToggleMagWindow()},
                });
            }
            catch (ArgumentException)
            {
                return false;
            }

            return true;
        }
    }
}
