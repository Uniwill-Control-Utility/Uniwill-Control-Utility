using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Uniwill_Control_Utility.Services;
using Wpf.Ui.Abstractions.Controls;

namespace Uniwill_Control_Utility.Views.Pages
{
    public partial class Display : Page, INavigationAware
    {
        private const int ApplyDelaySeconds = 2;

        // After a user change, skip syncing the combo box from EC for this many
        // milliseconds to avoid reading back the stale value before the EC commits.
        private const int ApplyGraceMs = 10000; // 10 seconds

        // NVRAM GUID for OemMagicVariable
        private const string NvramGuidStr = "{9F33F85C-13CA-4FD1-9C4A-96217722C593}";
        private const int OffsetOemDisplayMode = 98; // OpenRevo probe confirmed offset 98 (not 99)

        private readonly UniwillECService? _ecService;
        private bool _isEcAvailable;
        private bool _isLoaded;
        private bool _isAdministrator;
        private bool _isNvramAvailable;

        // Delayed refresh timer created after each user change.
        private DispatcherTimer? _delayTimer;

        // Shared across page instances (AddScoped creates a new page on each navigation).
        private static DateTime _lastUserApplyTime;

        // NVRAM P/Invoke
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFirmwareEnvironmentVariable(
            string lpName, string lpGuid, [Out] byte[] lpBuffer, uint nSize);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFirmwareEnvironmentVariable(
            string lpName, string lpGuid, [In] byte[] lpValue, uint nSize);

        // Token privilege P/Invoke (required for NVRAM access)
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr h, uint d, out IntPtr t);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool LookupPrivilegeValue(string? s, string n, ref LUID l);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr t, bool d, ref TP n, uint b, IntPtr p, IntPtr r);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr h);

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID { public uint Low; public int High; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TP { public int Count; public LUID Luid; public uint Attr; }

        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        public Display()
        {
            InitializeComponent();

            _ecService = App.GetService<UniwillECService>();
            if (_ecService is not null)
            {
                try
                {
                    _isEcAvailable = _ecService.Initialize();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Display] Failed to initialize EC service: {ex.Message}");
                    _isEcAvailable = false;
                }
            }

            // Check if running as administrator (needed for NVRAM access)
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                _isAdministrator = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                _isAdministrator = false;
            }

            // Enable SeSystemEnvironmentPrivilege and probe NVRAM access
            if (_isAdministrator)
            {
                EnablePrivilege("SeSystemEnvironmentPrivilege");
                _isNvramAvailable = ReadNvram("OemMagicVariable") is not null;
                Debug.WriteLine($"[Display] NVRAM available: {_isNvramAvailable}");
            }
        }

        /// <inheritdoc />
        public async Task OnNavigatedToAsync()
        {
            SyncFromEcAndRefresh();
        }

        /// <inheritdoc />
        public async Task OnNavigatedFromAsync()
        {
            _delayTimer?.Stop();
            _delayTimer = null;
            _isLoaded = false;
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_isEcAvailable)
            {
                DisplayAvailable.Visibility = Visibility.Collapsed;
                DisplayUnavailable.Visibility = Visibility.Visible;
            }
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            _delayTimer?.Stop();
            _delayTimer = null;
            _isLoaded = false;
        }

        /// <summary>
        /// Syncs combo box from NVRAM (the authoritative source), refreshes GPU info panel.
        /// Does NOT auto-reconcile EC from NVRAM — if the EC differs (e.g., BIOS or
        /// another tool changed the mode), the user sees the actual persisted value and
        /// can decide whether to re-apply it.
        /// </summary>
        private void SyncFromEcAndRefresh()
        {
            if (!_isEcAvailable)
            {
                DisplayAvailable.Visibility = Visibility.Collapsed;
                DisplayUnavailable.Visibility = Visibility.Visible;
                return;
            }

            DisplayAvailable.Visibility = Visibility.Visible;
            DisplayUnavailable.Visibility = Visibility.Collapsed;

            // Read NVRAM mode (authoritative source for persisted GPU mode)
            int mode;
            if (_isNvramAvailable)
            {
                try
                {
                    byte? nvramMode = ReadNvramMode();
                    if (nvramMode.HasValue)
                    {
                        mode = nvramMode.Value;
                        // Note: EC register may differ if BIOS or another tool changed
                        // the mode. We show the NVRAM value and let the user decide.
                        try
                        {
                            int ecMode = (int)_ecService!.GetCurrentGpuMuxMode();
                            if (ecMode != mode)
                            {
                                Debug.WriteLine($"[Display] EC mode ({ecMode}) differs from NVRAM ({mode}) — showing NVRAM value");
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[Display] Failed to read EC mode: {ex.Message}");
                        }
                    }
                    else
                    {
                        mode = 0; // Default to Hybrid if NVRAM read fails
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Display] NVRAM read failed: {ex.Message}");
                    mode = 0;
                }
            }
            else
            {
                // Fallback: read from EC if NVRAM is unavailable
                try
                {
                    mode = (int)_ecService.GetCurrentGpuMuxMode();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Display] Failed to read MUX mode from EC: {ex.Message}");
                    mode = 0;
                }
            }

            UpdateGpuModeButtons(mode);

            RefreshAll();
            _isLoaded = true;
        }

        /// <summary>
        /// Updates the visual state of the three GPU mode buttons to reflect the current mode.
        /// </summary>
        private void UpdateGpuModeButtons(int mode)
        {
            Style? inactiveStyle = (Style)Resources["GpuModeButtonStyle"];
            Style? activeStyle = (Style)Resources["GpuModeButtonActiveStyle"];

            // Reset all to inactive
            btnIgpuOnly.Style = inactiveStyle;
            btnHybrid.Style = inactiveStyle;
            btnDgpuOnly.Style = inactiveStyle;

            // Activate the matching button
            switch (mode)
            {
                case 0: // Hybrid
                    btnHybrid.Style = activeStyle;
                    break;
                case 1: // dGPU Only
                    btnDgpuOnly.Style = activeStyle;
                    break;
                case 2: // iGPU Only
                    btnIgpuOnly.Style = activeStyle;
                    break;
            }
        }

        private void btnGpuMode_Click(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded)
                return;

            if (sender is not Button btn || btn.Tag is not string tag)
                return;

            if (!int.TryParse(tag, out int mode))
                return;

            try
            {
                // Write EC register
                _ecService!.SetGpuMuxMode((UniwillECService.GpuMuxMode)mode);
                _lastUserApplyTime = DateTime.UtcNow;
                Debug.WriteLine($"[Display] Applied GPU mode: {(UniwillECService.GpuMuxMode)mode}");

                // Update button visual state immediately
                UpdateGpuModeButtons(mode);

                // Write NVRAM for persistence
                try
                {
                    WriteNvramMode((byte)mode);
                }
                catch (Exception nvramEx)
                {
                    Debug.WriteLine($"[Display] NVRAM write failed: {nvramEx.Message}");
                }

                // Show reboot warning
                cardRebootWarning.Visibility = Visibility.Visible;

                // Delay refresh to let the EC process the change
                _delayTimer?.Stop();
                _delayTimer = new DispatcherTimer(TimeSpan.FromSeconds(ApplyDelaySeconds), DispatcherPriority.Normal, (s, ev) =>
                {
                    RefreshAll();
                    ((DispatcherTimer)s!).Stop();
                    _delayTimer = null;
                }, Dispatcher);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Display] Failed to apply mode: {ex.Message}");
            }
        }

        private void btnRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshAll();
        }

        private void btnRestartNow_Click(object sender, RoutedEventArgs e)
        {
            // Initiate system restart
            try
            {
                Debug.WriteLine("[Display] Restart requested by user");
                System.Diagnostics.Process.Start(new ProcessStartInfo
                {
                    FileName = "shutdown",
                    Arguments = "/r /t 0",
                    UseShellExecute = true,
                    Verb = "runas" // Request elevation
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Display] Restart failed: {ex.Message}");
            }
        }

        private void RefreshAll()
        {
            if (_ecService is null) return;

            // Skip syncing the buttons if the user recently changed the setting
            bool withinGrace = (DateTime.UtcNow - _lastUserApplyTime).TotalMilliseconds < ApplyGraceMs;

            if (!withinGrace)
            {
                try
                {
                    // Read from NVRAM (authoritative) or fall back to EC
                    int mode;
                    if (_isNvramAvailable)
                    {
                        byte? nvramMode = ReadNvramMode();
                        mode = nvramMode ?? 0;
                    }
                    else
                    {
                        mode = (int)_ecService.GetCurrentGpuMuxMode();
                    }
                    UpdateGpuModeButtons(mode);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Display] Failed to read MUX mode: {ex.Message}");
                }
            }

            RefreshGpuInfo();
        }

        private void RefreshGpuInfo()
        {
            if (_ecService is null) return;

            try
            {
                var mode = _ecService.GetCurrentGpuMuxMode();
                Application.Current.Dispatcher.Invoke(() =>
                {
                    tbCurrentMode.Text = GetModeDisplayName(mode);
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Display] Failed to read current mode: {ex.Message}");
            }

            try
            {
                var moduleId = _ecService.GetGpuModuleId();
                Application.Current.Dispatcher.Invoke(() =>
                {
                    tbGpuModuleId.Text = $"0x{moduleId:X2} ({moduleId})";
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Display] Failed to read GPU module ID: {ex.Message}");
            }

            try
            {
                var muxState = _ecService.GetMuxHardwareState();
                Application.Current.Dispatcher.Invoke(() =>
                {
                    tbMuxHardwareState.Text = $"0x{muxState:X4}";
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Display] Failed to read MUX hardware state: {ex.Message}");
            }

            // Read NVRAM mode from OemMagicVariable (offset 98)
            try
            {
                byte? nvramMode = ReadNvramMode();
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (nvramMode.HasValue)
                    {
                        tbNvramMode.Text = $"{nvramMode.Value} ({GetModeDisplayName((UniwillECService.GpuMuxMode)nvramMode.Value)})";
                    }
                    else if (!_isNvramAvailable)
                    {
                        tbNvramMode.Text = "NVRAM unavailable";
                    }
                    else
                    {
                        tbNvramMode.Text = "Unable to read";
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Display] NVRAM read failed: {ex.Message}");
                Application.Current.Dispatcher.Invoke(() =>
                {
                    tbNvramMode.Text = "Error";
                });
            }
        }

        private string GetModeDisplayName(UniwillECService.GpuMuxMode mode)
        {
            return mode switch
            {
                UniwillECService.GpuMuxMode.Hybrid => "Hybrid",
                UniwillECService.GpuMuxMode.DgpuOnly => "dGPU Only",
                UniwillECService.GpuMuxMode.IgpuOnly => "iGPU Only",
                _ => $"Unknown ({(int)mode})"
            };
        }

        #region NVRAM Access

        /// <summary>
        /// Enables a privilege on the current process token (e.g., SeSystemEnvironmentPrivilege for NVRAM access).
        /// </summary>
        private static void EnablePrivilege(string privilegeName)
        {
            IntPtr token;
            if (!OpenProcessToken(Process.GetCurrentProcess().Handle, TOKEN_ADJUST_PRIVILEGES, out token))
            {
                Debug.WriteLine($"[Display] OpenProcessToken failed: {Marshal.GetLastWin32Error()}");
                return;
            }

            try
            {
                var tp = new TP { Count = 1, Attr = SE_PRIVILEGE_ENABLED };
                if (!LookupPrivilegeValue(null, privilegeName, ref tp.Luid))
                {
                    Debug.WriteLine($"[Display] LookupPrivilegeValue failed: {Marshal.GetLastWin32Error()}");
                    return;
                }

                if (!AdjustTokenPrivileges(token, false, ref tp, (uint)Marshal.SizeOf(tp), IntPtr.Zero, IntPtr.Zero))
                {
                    Debug.WriteLine($"[Display] AdjustTokenPrivileges failed: {Marshal.GetLastWin32Error()}");
                }
                else
                {
                    Debug.WriteLine($"[Display] Enabled privilege: {privilegeName}");
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }

        /// <summary>
        /// Reads the OemDisplayMode byte from the OemMagicVariable NVRAM entry (offset 98).
        /// </summary>
        private byte? ReadNvramMode()
        {
            if (!_isNvramAvailable) return null;

            byte[]? nvram = ReadNvram("OemMagicVariable");
            if (nvram == null || nvram.Length <= OffsetOemDisplayMode)
                return null;

            return nvram[OffsetOemDisplayMode];
        }

        /// <summary>
        /// Writes the OemDisplayMode byte to the OemMagicVariable NVRAM entry (offset 98).
        /// This is the same mechanism OpenRevo uses for BIOS-visible GPU mode persistence.
        /// </summary>
        private void WriteNvramMode(byte mode)
        {
            if (!_isNvramAvailable)
            {
                throw new InvalidOperationException("NVRAM access is not available");
            }

            byte[]? nvram = ReadNvram("OemMagicVariable");
            if (nvram == null)
            {
                throw new InvalidOperationException("Cannot read OemMagicVariable for NVRAM write");
            }

            byte[] modified = (byte[])nvram.Clone();
            modified[OffsetOemDisplayMode] = mode;

            bool ok = SetFirmwareEnvironmentVariable("OemMagicVariable", NvramGuidStr, modified, (uint)modified.Length);
            if (!ok)
            {
                int error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"NVRAM OemMagicVariable write failed with error {error}");
            }

            Debug.WriteLine($"[Display] NVRAM OemDisplayMode set to {mode} at offset {OffsetOemDisplayMode}");
        }

        /// <summary>
        /// Reads an NVRAM variable by name using the string-GUID API.
        /// </summary>
        private byte[]? ReadNvram(string name)
        {
            byte[] buf = new byte[256];
            uint len = GetFirmwareEnvironmentVariable(name, NvramGuidStr, buf, (uint)buf.Length);
            if (len > 0 && len <= buf.Length)
            {
                byte[] result = new byte[len];
                Array.Copy(buf, result, len);
                return result;
            }
            return null;
        }

        #endregion
    }
}
