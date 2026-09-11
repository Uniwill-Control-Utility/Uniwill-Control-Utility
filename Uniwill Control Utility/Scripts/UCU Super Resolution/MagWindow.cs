using System;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using Uniwill_Control_Utility.Properties;

namespace Uniwill_Control_Utility.Scripts.UCU_Super_Resolution
{
    //
    // This is a customised version of Magpie from https://github.com/Blinue/Magpie
    // I do not take credit for the full functionality of the code.
    //

    internal sealed class MagWindow : IDisposable
    {
        // Closed event
        public event Action? Closed;

        public IntPtr SrcWindow { get; private set; } = IntPtr.Zero;

        private readonly Thread magThread;

        // Used to signal magThread
        private readonly AutoResetEvent runEvent = new(false);

        private enum MagWindowCmd
        {
            None,
            Run,
            Exit,
            SetLogLevel
        }

        // Passed to magThread
        private sealed class MagWindowParams
        {
            public volatile IntPtr hwndSrc;
            public volatile string effectsJson = "";
            public volatile int logLevel;
            public volatile MagWindowCmd cmd = MagWindowCmd.None;
        }

        private enum FlagMasks : uint
        {
            NoCursor = 0x1,
            AdjustCursorSpeed = 0x2,
            SaveEffectSources = 0x4,
            SimulateExclusiveFullscreen = 0x8,
            DisableLowLatency = 0x10,
            BreakpointMode = 0x20,
            DisableWindowResizing = 0x40,
            DisableDirectFlip = 0x80,
            Is3DMode = 0x100,
            CropTitleBarOfUWP = 0x200,
            DisableEffectCache = 0x400,
            DisableVSync = 0x800,
            WarningsAreErrors = 0x1000,
            ShowFPS = 0x2000
        }

        private readonly MagWindowParams magWindowParams = new();

        // Internal close event
        private event Action<string?> CloseEvent;

        public bool Running { get; private set; }

        private const int PollIntervalMs = 1000;
        private const int DisposePollIntervalMs = 1;
        private const int LogArchiveAboveSize = 100000;
        private const int LogMaxArchiveFiles = 1;

        public MagWindow(Window parent)
        {
            // Use WinRT Capturer API which requires MTA
            // C# defaults to STA, so we need a separate thread
            magThread = new Thread(() =>
            {
                Environment.CurrentDirectory = AppDomain.CurrentDomain.BaseDirectory;

                string logFileName = App.LOGS_FOLDER + "Runtime.log";

                static uint ResolveLogLevel(uint logLevel)
                {
                    return logLevel switch
                    {
                        1 => 2,
                        2 => 3,
                        3 => 4,
                        _ => 6,
                    };
                }

                bool initSuccess = false;
                try
                {
                    initSuccess = NativeMethods.Initialize(
                        ResolveLogLevel(Settings.Default.LoggingLevel),
                        logFileName,
                        LogArchiveAboveSize,
                        LogMaxArchiveFiles
                    );
                }
                catch (DllNotFoundException)
                {
                    // Native library not found; initSuccess remains false
                }
                catch (Exception)
                {
                    // Initialization failed for an unexpected reason; initSuccess remains false
                }

                if (!initSuccess)
                {
                    CloseEvent?.Invoke("Msg_Error_Init");
                    return;
                }

                while (magWindowParams.cmd != MagWindowCmd.Exit)
                {
                    _ = runEvent.WaitOne(PollIntervalMs);

                    MagWindowCmd cmd = magWindowParams.cmd;
                    magWindowParams.cmd = MagWindowCmd.None;

                    if (cmd == MagWindowCmd.Exit)
                    {
                        break;
                    }

                    if (cmd == MagWindowCmd.None)
                    {
                        continue;
                    }

                    if (cmd == MagWindowCmd.SetLogLevel)
                    {
                        NativeMethods.SetLogLevel(ResolveLogLevel((uint)magWindowParams.logLevel));
                    }
                    else
                    {
                        uint flags = BuildFlags();

                        bool customCropping = Settings.Default.CustomCropping;

                        string? msg = NativeMethods.Run(
                            magWindowParams.hwndSrc,
                            magWindowParams.effectsJson,
                            flags,
                            Settings.Default.CaptureMode,
                            Settings.Default.CursorZoomFactor,
                            Settings.Default.CursorInterpolationMode,
                            Settings.Default.AdapterIdx,
                            Settings.Default.MultiMonitorUsage,
                            customCropping ? Settings.Default.CropLeft : 0,
                            customCropping ? Settings.Default.CropTop : 0,
                            customCropping ? Settings.Default.CropRight : 0,
                            customCropping ? Settings.Default.CropBottom : 0
                        );

                        CloseEvent?.Invoke(msg);
                    }
                }
            });

            magThread.SetApartmentState(ApartmentState.MTA);
            magThread.IsBackground = true;
            magThread.Start();

            CloseEvent += (string? errorMsgId) =>
            {
                bool noError = string.IsNullOrEmpty(errorMsgId);

                if (noError && Closed != null)
                {
                    Closed.Invoke();
                }
                SrcWindow = IntPtr.Zero;
                Running = false;

                if (!noError)
                {
                    parent.Dispatcher.Invoke(() =>
                    {
                        _ = NativeMethods.SetForegroundWindow(new WindowInteropHelper(parent).Handle);
                    });
                }
            };
        }

        private static uint BuildFlags()
        {
            return (Settings.Default.NoCursor ? (uint)FlagMasks.NoCursor : 0) |
                (Settings.Default.AdjustCursorSpeed ? (uint)FlagMasks.AdjustCursorSpeed : 0) |
                (Settings.Default.DebugSaveEffectSources ? (uint)FlagMasks.SaveEffectSources : 0) |
                (Settings.Default.DisableLowLatency ? (uint)FlagMasks.DisableLowLatency : 0) |
                (Settings.Default.DebugBreakpointMode ? (uint)FlagMasks.BreakpointMode : 0) |
                (Settings.Default.DisableWindowResizing ? (uint)FlagMasks.DisableWindowResizing : 0) |
                (Settings.Default.DisableDirectFlip ? (uint)FlagMasks.DisableDirectFlip : 0) |
                (Settings.Default.Is3DMode ? (uint)FlagMasks.Is3DMode : 0) |
                (Settings.Default.CropTitleBarOfUWP ? (uint)FlagMasks.CropTitleBarOfUWP : 0) |
                (Settings.Default.DebugDisableEffectCache ? (uint)FlagMasks.DisableEffectCache : 0) |
                (Settings.Default.SimulateExclusiveFullscreen ? (uint)FlagMasks.SimulateExclusiveFullscreen : 0) |
                (Settings.Default.DebugWarningsAreErrors ? (uint)FlagMasks.WarningsAreErrors : 0) |
                (Settings.Default.VSync ? 0 : (uint)FlagMasks.DisableVSync) |
                (Settings.Default.ShowFPS ? (uint)FlagMasks.ShowFPS : 0);
        }

        public void Create(string effectsJson)
        {
            if (Running)
            {
                return;
            }

            IntPtr hwndSrc = NativeMethods.GetForegroundWindow();
            if (!NativeMethods.IsWindow(hwndSrc)
                || !NativeMethods.IsWindowVisible(hwndSrc)
                || NativeMethods.GetWindowShowCmd(hwndSrc) != NativeMethods.SW_NORMAL)
            {
                return;
            }

            SrcWindow = hwndSrc;

            magWindowParams.cmd = MagWindowCmd.Run;
            magWindowParams.hwndSrc = hwndSrc;
            magWindowParams.effectsJson = effectsJson;

            _ = runEvent.Set();
            Running = true;
        }

        public void SetLogLevel(uint logLevel)
        {
            magWindowParams.cmd = MagWindowCmd.SetLogLevel;
            magWindowParams.logLevel = (int)logLevel;

            _ = runEvent.Set();
        }

        public void Destory()
        {
            if (!Running)
            {
                return;
            }

            _ = NativeMethods.BroadcastMessage(NativeMethods.MAGPIE_WM_DESTORYHOST);
        }

        private bool disposed = false;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;

            magWindowParams.cmd = MagWindowCmd.Exit;

            if (Running)
            {
                Destory();

                while (Running)
                {
                    Thread.Sleep(DisposePollIntervalMs);
                }
            }
            else
            {
                _ = runEvent.Set();
                Thread.Sleep(DisposePollIntervalMs);
            }

            runEvent.Dispose();
        }
    }
}
