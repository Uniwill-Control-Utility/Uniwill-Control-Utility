using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Serilog;
using System.Windows.Threading;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Scripts.GPUs.NVIDIA;
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
        private bool _localDimmingToggling;
        private bool _refreshRateBusy;

        // Delayed refresh timer created after each user change.

        // --- MiniLED Local Dimming ---
        private const string WmiClassName = "AcpiTest_MULong";
        private const string DefaultInstanceName = @"ACPI\PNP0C14\1_1";
        private const ulong CmdEnableStep1 = 0x50000000002;
        private const ulong CmdEnableStep2 = 0x5000000000E;
        private const ulong CmdDisableStep1 = 0x50000000001;
        private const ulong CmdDisableStep2 = 0x5000000000D;
        private const byte ExpectedEnableStep1 = 0xBB;
        private const byte ExpectedEnableStep2 = 0x0E;
        private const byte ExpectedDisableStep1 = 0xAA;
        private const byte ExpectedDisableStep2 = 0x0D;
        private const ushort EcLocalDimmingRegister = 0x072B;
        private const byte EcLocalDimmingBit = 0x02;
        private DispatcherTimer? _delayTimer;
        private DispatcherTimer? _cooldownTimer;
        private DispatcherTimer? _statusTimer;

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

            // Show MiniLED card only on MiniLED panels
            CardLocalDimming.Visibility = App.HasMiniLEDPanel ? Visibility.Visible : Visibility.Collapsed;

            // Show the refresh-rate card only when the panel offers more than one rate
            CardRefreshRate.Visibility = AvailableRefreshRates().Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <inheritdoc />
        public async Task OnNavigatedToAsync()
        {
            SyncFromEcAndRefresh();

            // Keep the bottom-bar monitor status live: follows display switches
            // (internal panel <-> external monitor) and resolution changes.
            _statusTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Normal,
                (s, e) => tbRefreshRateTarget.Text = RefreshRateLabel(), Dispatcher);
            _statusTimer.Start();
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
            _cooldownTimer?.Stop();
            borderPowerSavingCooldown.Visibility = Visibility.Collapsed;
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

            // Show/hide GPU Power Saving card — only available in Hybrid mode
            bool isHybrid = mode == 0;
            CardGpuPowerSaving.Visibility = isHybrid ? Visibility.Visible : Visibility.Collapsed;

            // Load GPU Power Saving mode from settings
            int powerSavingMode = Settings.Default.GpuPowerSavingMode;
            UpdatePowerSavingButtons(powerSavingMode);

            // Load Local Dimming state from settings
            if (App.HasMiniLEDPanel)
            {
                tsLocalDimming.IsChecked = Settings.Default.DisplayLocalDimming;
            }

            // Sync the refresh-rate combo from the live Windows display mode
            UpdateRefreshRateCombo(GetRefreshRateSafe());

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

            // Ignore click if this mode is already active
            Style? activeStyle = (Style?)Resources["GpuModeButtonActiveStyle"];
            if (btn.Style == activeStyle)
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

        /// <summary>
        /// Updates the visual state of the three power saving buttons.
        /// </summary>
        private void UpdatePowerSavingButtons(int mode)
        {
            Style? inactiveStyle = (Style)Resources["GpuModeButtonStyle"];
            Style? activeStyle = (Style)Resources["GpuModeButtonActiveStyle"];

            // Reset all to inactive
            btnPowerSavingOn.Style = inactiveStyle;
            btnPowerSavingOff.Style = inactiveStyle;
            btnPowerSavingAuto.Style = inactiveStyle;

            // Activate the matching button
            switch (mode)
            {
                case 0: // On (Power Saving)
                    btnPowerSavingOn.Style = activeStyle;
                    break;
                case 1: // Off (Performance)
                    btnPowerSavingOff.Style = activeStyle;
                    break;
                case 2: // Auto
                default:
                    btnPowerSavingAuto.Style = activeStyle;
                    break;
            }
        }

        private async void btnPowerSaving_Click(object sender, RoutedEventArgs e)
        {
            if (!_isLoaded)
                return;

            if (sender is not Button btn || btn.Tag is not string tag)
                return;

            if (!int.TryParse(tag, out int mode))
                return;

            // Ignore click if this mode is already active
            Style? activeStyle = (Style?)Resources["GpuModeButtonActiveStyle"];
            if (btn.Style == activeStyle)
                return;

            // Block clicks during GPU driver transition cooldown
            if (GpuInteropGuard.IsBlocked)
                return;

            try
            {
                // Update button visual state immediately
                UpdatePowerSavingButtons(mode);

                // Persist to settings
                Settings.Default.GpuPowerSavingMode = mode;
                Settings.Default.Save();

                // Apply the power saving mode (always uses PnP hotswap)
                var powerSavingMode = (GpuPowerSavingMode)mode;
                string pnpStatusBefore = GpuPowerSaving.GetDgpuPnpStatus();
                await GpuPowerSaving.ApplyAsync(powerSavingMode);
                string pnpStatusAfter = GpuPowerSaving.GetDgpuPnpStatus();

                // Reset LHM Computer so it re-probes with fresh driver handles.
                // After a PnP hotswap the GPU availability changes and LHM needs
                // to re-enumerate to pick up the new active GPU (iGPU fallback).
                try
                {
                    var monitoring = App.GetService<IHardwareMonitoringService>();
                    monitoring?.ResetComputer();
                    Log.Information("[Display] LHM Computer reset after GPU power saving mode change");
                }
                catch (Exception resetEx)
                {
                    Log.Information("[Display] LHM reset after power saving change failed: {Message}", resetEx.Message);
                }

                // After a PnP hotswap the GPU driver reinitializes, which can break
                // the taskbar icon association. Wait for the driver to settle then
                // re-assert the AppUserModelID to restore the taskbar icon.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000);
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        App.ReassertAppUserModelID();
                    });
                });

                // Start cooldown overlay to block user clicks during the guard window
                StartCooldownOverlay();

                Log.Information("[Display] GPU Power Saving set to {Mode}, dGPU PnP: {Before} -> {After}, GuardBlocked: {Blocked}, IsAdmin: {IsAdmin}",
                    GpuPowerSaving.GetModeDisplayName(powerSavingMode), pnpStatusBefore, pnpStatusAfter, GpuInteropGuard.IsBlocked, _isAdministrator);
            }
            catch (Exception ex)
            {
                Log.Information("[Display] [ERROR] Failed to apply GPU power saving mode: {Message}", ex.Message);
            }
        }

        private void StartCooldownOverlay()
        {
            _cooldownTimer?.Stop();

            int remainingSeconds = 15;
            txtCooldownCountdown.Text = remainingSeconds.ToString();
            borderPowerSavingCooldown.Visibility = Visibility.Visible;

            _cooldownTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _cooldownTimer.Tick += (s, e) =>
            {
                remainingSeconds--;
                if (remainingSeconds <= 0)
                {
                    _cooldownTimer?.Stop();
                    borderPowerSavingCooldown.Visibility = Visibility.Collapsed;
                    txtCooldownCountdown.Text = string.Empty;
                }
                else
                {
                    txtCooldownCountdown.Text = remainingSeconds.ToString();
                }
            };
            _cooldownTimer.Start();
        }

        private void btnRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshAll();
        }

        #region Display Refresh Rate

        /// <summary>
        /// The display in use — the primary (taskbar) display. Follows the user to an
        /// external monitor when that monitor is primary, mirroring OpenRevo's
        /// is_primary/is_internal monitor model.
        /// </summary>
        private string? RefreshRateTarget()
        {
            try
            {
                return Uniwill_Control_Utility.Scripts.Misc.Display.CurrentDisplayName();
            }
            catch
            {
                return null;
            }
        }

        private int GetRefreshRateSafe()
        {
            try
            {
                return Uniwill_Control_Utility.Scripts.Misc.Display.GetCurrentRefreshRate(RefreshRateTarget());
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>True while the runtime mux mode is dGPU-only (unlocks the 300 Hz internal-panel mode).</summary>
        private bool IsDgpuOnlyMode()
        {
            try
            {
                return _ecService is not null
                    && _ecService.GetCurrentGpuMuxMode() == UniwillECService.GpuMuxMode.DgpuOnly;
            }
            catch
            {
                return false;
            }
        }

        private List<int> AvailableRefreshRates()
        {
            try
            {
                return Uniwill_Control_Utility.Scripts.Misc.Display.GetOfferedRefreshRates(
                    RefreshRateTarget(), IsDgpuOnlyMode());
            }
            catch
            {
                return new List<int>();
            }
        }

        /// <summary>
        /// "Internal panel — 2560×1600" / "External monitor — 1920×1080" for the display in use.
        /// </summary>
        private string RefreshRateLabel()
        {
            var target = RefreshRateTarget();
            if (target is null)
                return "--";

            bool isInternalPanel = Uniwill_Control_Utility.Scripts.Misc.Display.IsInternalDisplay(target);
            var resolution = Uniwill_Control_Utility.Scripts.Misc.Display.GetDisplayResolution(target);
            var size = resolution.Length > 0 ? $" — {resolution}" : string.Empty;
            return (isInternalPanel ? "Internal panel" : "External monitor") + size;
        }

        /// <summary>
        /// Rebuilds the combo box with exactly the rates the display in use offers,
        /// selected at the current one. The programmatic selection detaches the
        /// handler so the sync does not re-apply the setting.
        /// </summary>
        private void UpdateRefreshRateCombo(int currentHz)
        {
            var rates = AvailableRefreshRates();

            cbRefreshRate.Items.Clear();
            int selectedIndex = -1;
            for (int i = 0; i < rates.Count; i++)
            {
                cbRefreshRate.Items.Add(new ComboBoxItem { Content = $"{rates[i]} Hz", Tag = rates[i] });
                if (rates[i] == currentHz)
                    selectedIndex = i;
            }

            // Monitor status first — it must refresh even if the selection sync below fails.
            tbRefreshRateTarget.Text = RefreshRateLabel();

            if (selectedIndex >= 0)
            {
                cbRefreshRate.SelectionChanged -= cbRefreshRate_SelectionChanged;
                try
                {
                    cbRefreshRate.SelectedIndex = selectedIndex;
                }
                catch (Exception ex)
                {
                    Log.Warning("[Display] Refresh rate selection sync failed: {Message}", ex.Message);
                }
                finally
                {
                    cbRefreshRate.SelectionChanged += cbRefreshRate_SelectionChanged;
                }
            }
        }

        private void cbRefreshRate_SelectionChanged(object sender, EventArgs e)
        {
            if (!_isLoaded || _refreshRateBusy)
                return;

            if (cbRefreshRate.SelectedItem is not ComboBoxItem item || item.Tag is not int hz)
                return;

            // Ignore selection of the rate that is already active
            if (hz == GetRefreshRateSafe())
                return;

            _refreshRateBusy = true;

            // The GDI mode switch can take ~1s — keep it off the UI thread.
            _ = Task.Run(() =>
            {
                var target = RefreshRateTarget();
                try
                {
                    Uniwill_Control_Utility.Scripts.Misc.Display.ChangeDisplaySettings(target, hz);
                }
                catch (Exception ex)
                {
                    Log.Information("[Display] [ERROR] Refresh rate apply failed: {Message}", ex.Message);
                }

                // Verify via readback — ApplySettings reports failures only to the console.
                int applied = GetRefreshRateSafe();
                bool success = applied == hz;
                Log.Information("[Display] Refresh rate set to {Applied} Hz (requested {Hz} Hz, success: {Success})",
                    applied, hz, success);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    UpdateRefreshRateCombo(applied);
                    _refreshRateBusy = false;
                });
            });
        }

        #endregion

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

            // The refresh-rate combo tracks the live Windows display mode
            UpdateRefreshRateCombo(GetRefreshRateSafe());

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

            // The NVRAM byte uses the same platform-specific encoding as the EC
            // register; return the semantic mode so callers can compare it directly
            // against the button tags / GpuMuxMode values.
            return (byte)UniwillECService.RawToMode(nvram[OffsetOemDisplayMode]);
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

            // The caller passes the semantic mode; the firmware expects the
            // platform-specific raw byte (swapped on Intel platforms).
            byte raw = UniwillECService.ModeToRaw((UniwillECService.GpuMuxMode)mode);
            byte[] modified = (byte[])nvram.Clone();
            modified[OffsetOemDisplayMode] = raw;

            bool ok = SetFirmwareEnvironmentVariable("OemMagicVariable", NvramGuidStr, modified, (uint)modified.Length);
            if (!ok)
            {
                int error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"NVRAM OemMagicVariable write failed with error {error}");
            }

            Debug.WriteLine($"[Display] NVRAM OemDisplayMode set to {raw} (mode {mode}) at offset {OffsetOemDisplayMode}");
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

        #region MiniLED Local Dimming

        /// <summary>
        /// Calls GetSetULong via WMI using the correct GetMethodParameters pattern.
        /// </summary>
        private static byte WmiCall(ManagementObject mo, ulong data)
        {
            using var input = mo.GetMethodParameters("GetSetULong");
            input["Data"] = data;

            using var output = (ManagementBaseObject)mo.InvokeMethod("GetSetULong", input, null);
            return (byte)(uint)output["Return"];
        }

        private void TsLocalDimming_Toggled(object sender, RoutedEventArgs e)
        {
            if (_localDimmingToggling)
                return;

            bool isOn = tsLocalDimming.IsChecked == true;

            _localDimmingToggling = true;
            tsLocalDimming.IsEnabled = false;

            // WMI ManagementObject has apartment threading affinity — must be called on a dedicated thread.
            _ = Task.Run(() =>
            {
                try
                {
                    bool success = SetLocalDimming(isOn);
                    Debug.WriteLine($"[Display] Local Dimming {(isOn ? "enabled" : "disabled")}: {(success ? "success" : "failed")}");

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (!success)
                        {
                            tsLocalDimming.IsChecked = !isOn;
                        }
                        tsLocalDimming.IsEnabled = true;
                        _localDimmingToggling = false;

                        Settings.Default.DisplayLocalDimming = tsLocalDimming.IsChecked == true;
                        Settings.Default.Save();
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Display] Local Dimming toggle failed: {ex.Message}");

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        tsLocalDimming.IsChecked = !isOn;
                        tsLocalDimming.IsEnabled = true;
                        _localDimmingToggling = false;

                        Settings.Default.DisplayLocalDimming = tsLocalDimming.IsChecked == true;
                        Settings.Default.Save();
                    });
                }
            });
        }

        private bool SetLocalDimming(bool enable)
        {
            try
            {
                var instancePath = $@"{WmiClassName}.InstanceName='{DefaultInstanceName}'";
                using var mo = new ManagementObject(@"root\WMI", instancePath, null);

                if (enable)
                {
                    byte result1 = WmiCall(mo, CmdEnableStep1);
                    if (result1 != ExpectedEnableStep1)
                    {
                        Debug.WriteLine($"[Display] Enable step 1 returned 0x{result1:X2} (expected 0x{ExpectedEnableStep1:X2})");
                        return false;
                    }

                    byte result2 = WmiCall(mo, CmdEnableStep2);
                    if (result2 != ExpectedEnableStep2)
                    {
                        Debug.WriteLine($"[Display] Enable step 2 returned 0x{result2:X2} (expected 0x{ExpectedEnableStep2:X2})");
                        return false;
                    }
                }
                else
                {
                    byte result1 = WmiCall(mo, CmdDisableStep1);
                    if (result1 != ExpectedDisableStep1)
                    {
                        Debug.WriteLine($"[Display] Disable step 1 returned 0x{result1:X2} (expected 0x{ExpectedDisableStep1:X2})");
                        return false;
                    }

                    byte result2 = WmiCall(mo, CmdDisableStep2);
                    if (result2 != ExpectedDisableStep2)
                    {
                        Debug.WriteLine($"[Display] Disable step 2 returned 0x{result2:X2} (expected 0x{ExpectedDisableStep2:X2})");
                        return false;
                    }
                }

                Thread.Sleep(100);

                // Verify via EC readback.
                if (_ecService != null && _isEcAvailable)
                {
                    byte ecValue = _ecService.ReadECRegister(EcLocalDimmingRegister);
                    bool actualState = (ecValue & EcLocalDimmingBit) != 0;

                    if (actualState != enable)
                    {
                        Debug.WriteLine($"[Display] EC 0x72B readback 0x{ecValue:X2} shows {(actualState ? "ON" : "OFF")}, expected {(enable ? "ON" : "OFF")} — mismatch");
                        return false;
                    }

                    Debug.WriteLine($"[Display] EC 0x72B readback 0x{ecValue:X2} confirms Local Dimming {(enable ? "ON" : "OFF")}");
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Display] WMI/ACPI call failed: {ex.Message}");
                return false;
            }
        }

        #endregion
    }
}
