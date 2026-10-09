using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using Serilog;
using Uniwill_Control_Utility.Scripts.Misc;

namespace Uniwill_Control_Utility.Scripts.GPUs.NVIDIA
{
    /// <summary>
    /// iGPU Power Saving Mode - controls dGPU usage at runtime without requiring a reboot.
    /// Maps to XMG CC's GPUPORWERSAVING enum (ENABLE=0, DISABLE=1, AUTO=2).
    /// Uses PnP hotswap to physically disable/enable the dGPU driver for maximum savings.
    /// </summary>
    public enum GpuPowerSavingMode
    {
        /// <summary>
        /// ENABLE - Disable the dGPU via PnP hotswap for maximum power savings.
        /// Windows routes all rendering to the iGPU.
        /// </summary>
        Enable = 0,

        /// <summary>
        /// DISABLE - Re-enable the dGPU via PnP hotswap (maximum performance).
        /// </summary>
        Disable = 1,

        /// <summary>
        /// AUTO - Let NVIDIA Optimus / Windows decide per-application.
        /// Re-enables the dGPU and clears explicit GPU preferences.
        /// </summary>
        Auto = 2
    }

    /// <summary>
    /// Runtime GPU power saving control. Disables/enables the dGPU via PnP hotswap
    /// (like XMG CC) and sets Windows GPU preference registry for new processes.
    /// Does NOT touch EC registers or the MUX switch — works live in Windows.
    /// </summary>
    internal static class GpuPowerSaving
    {
        #region Windows GPU Preference Registry

        private const string WindowsGpuPrefKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

        /// <summary>
        /// Sets the Windows GPU preference for all applications.
        /// GpuPreference=1 -> iGPU (power saving), GpuPreference=2 -> dGPU (performance).
        /// Writing an empty value clears the preference (Auto mode).
        /// </summary>
        private static void SetWindowsGpuPreference(GpuPowerSavingMode mode)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(WindowsGpuPrefKey);
                if (key == null)
                {
                    Log.Information("[GpuPowerSaving] Cannot open Windows GPU preference registry key");
                    return;
                }

                switch (mode)
                {
                    case GpuPowerSavingMode.Enable:
                        // Prefer integrated GPU for power savings
                        key.SetValue("GpuPreference", 1);
                        Log.Information("[GpuPowerSaving] Windows GPU preference set to iGPU (power saving)");
                        break;

                    case GpuPowerSavingMode.Disable:
                        // Prefer discrete GPU for performance
                        key.SetValue("GpuPreference", 2);
                        Log.Information("[GpuPowerSaving] Windows GPU preference set to dGPU (performance)");
                        break;

                    case GpuPowerSavingMode.Auto:
                        // Clear preference - let Windows/NVIDIA decide
                        key.DeleteValue("GpuPreference", false);
                        Log.Information("[GpuPowerSaving] Windows GPU preference cleared (auto)");
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Information("[GpuPowerSaving] Failed to set Windows GPU preference: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Reads the current Windows GPU preference.
        /// Returns null if no preference is set (Auto mode).
        /// 1 = iGPU preference, 2 = dGPU preference.
        /// </summary>
        public static int? GetWindowsGpuPreference()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(WindowsGpuPrefKey);
                if (key == null) return null;

                var value = key.GetValue("GpuPreference");
                if (value == null) return null;

                return Convert.ToInt32(value);
            }
            catch (Exception ex)
            {
                Log.Information("[GpuPowerSaving] Failed to read Windows GPU preference: {Message}", ex.Message);
                return null;
            }
        }

        #endregion

        #region NVIDIA DRS Global GPU Preference

        /// <summary>
        /// Sets the NVIDIA DRS (Driver Reasonable Settings) global GPU preference.
        /// Uses NVAPI to set the system-wide default GPU preference.
        /// </summary>
        private static void SetNvidiaDrsGpuPreference(GpuPowerSavingMode mode)
        {
            if (GpuInteropGuard.IsBlocked) return;

            try
            {
                if (!NvApiPrivate.TryGetFirstGpuHandle(out IntPtr gpu))
                {
                    Log.Information("[GpuPowerSaving] Cannot get GPU handle for DRS preference");
                    return;
                }

                // NvAPI_GPU_SetPreferredGPU - sets the preferred GPU for applications
                // Interface ID: 0x8B34C9A2 (NvAPI_GPU_SetPreferredGPU)
                IntPtr setPreferredPtr = NvApiPrivate.QueryInterface(0x8B34C9A2);
                if (setPreferredPtr == IntPtr.Zero)
                {
                    Log.Information("[GpuPowerSaving] NvAPI_GPU_SetPreferredGPU not available, skipping DRS preference");
                    return;
                }

                var setPreferred = Marshal.GetDelegateForFunctionPointer<NvApiPrivate.GpuBufferDelegate>(setPreferredPtr);

                // Buffer: GPU type (4 bytes) + padding
                // GPU type: 0 = Auto, 1 = Integrated, 2 = Discrete
                byte[] buffer = new byte[64];
                int gpuType = mode switch
                {
                    GpuPowerSavingMode.Enable => 1, // Integrated
                    GpuPowerSavingMode.Disable => 2, // Discrete
                    GpuPowerSavingMode.Auto => 0,    // Auto
                    _ => 0
                };

                BitConverter.GetBytes(gpuType).CopyTo(buffer, 0);

                int status = setPreferred(gpu, buffer);
                if (status == 0)
                {
                    string gpuName = mode == GpuPowerSavingMode.Enable ? "iGPU" : mode == GpuPowerSavingMode.Disable ? "dGPU" : "Auto";
                    Log.Information("[GpuPowerSaving] NVIDIA DRS GPU preference set to {GpuName}", gpuName);
                }
                else
                {
                    Log.Information("[GpuPowerSaving] NvAPI_GPU_SetPreferredGPU returned 0x{Status:X}", status);
                }
            }
            catch (Exception ex)
            {
                Log.Information("[GpuPowerSaving] Failed to set NVIDIA DRS preference: {Message}", ex.Message);
            }
        }

        #endregion

        #region PnP Device Hotswap

        /// <summary>
        /// Disables the NVIDIA dGPU via PnP device hotswap to save maximum power.
        /// This is the mechanism XMG CC uses to "stop giving power to the dGPU".
        /// Uses InstanceId-based targeting with Present filtering to find the active device.
        /// Requires administrator privileges.
        /// </summary>
        public static async Task<bool> DisableDgpuAsync()
        {
            if (GpuInteropGuard.IsBlocked) return false;

            try
            {
                // Log pre-disable status
                var statusBefore = GetDgpuPnpStatus();
                Log.Information("[GpuPowerSaving] Disabling dGPU, current PnP status: {Status}", statusBefore);

                // Target the NVIDIA device that is physically present (Present=$true).
                // Blackwell GPUs may show as "Unknown" status after disable (not "Error" like older GPUs),
                // so we filter by Present=$true to always hit the active device.
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -Command \"Disable-PnpDevice -InstanceId (Get-PnpDevice -FriendlyName '*NVIDIA*' -Class Display | Where-Object { $_.Present -eq $true }).InstanceId -Confirm:$false\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                    // UCU runs elevated by default, no need for runas
                };

                using var proc = Process.Start(psi);
                if (proc == null) return false;

                string output = await proc.StandardOutput.ReadToEndAsync();
                string error = await proc.StandardError.ReadToEndAsync();
                proc.WaitForExit();

                bool success = proc.ExitCode == 0;
                if (success)
                {
                    var statusAfter = GetDgpuPnpStatus();
                    Log.Information("[GpuPowerSaving] dGPU disabled via PnP hotswap, new status: {Status}", statusAfter);
                    Log.Information("[GpuPowerSaving] PowerShell output: {Output}", output.Trim());
                    // Trigger the interop guard - the GPU driver is now in a transitional state
                    GpuInteropGuard.Reset();
                }
                else
                {
                    Log.Information("[GpuPowerSaving] PnP disable failed (exit code {ExitCode}): {Error}", proc.ExitCode, error);
                    Log.Information("[GpuPowerSaving] PowerShell output: {Output}", output.Trim());
                }
                return success;
            }
            catch (Exception ex)
            {
                Log.Information("[GpuPowerSaving] Failed to disable dGPU: {Message}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Re-enables the NVIDIA dGPU via PnP device hotswap after it was disabled.
        /// Uses InstanceId-based targeting with status filtering to find disabled devices.
        /// Requires administrator privileges.
        /// </summary>
        public static async Task<bool> EnableDgpuAsync()
        {
            if (GpuInteropGuard.IsBlocked) return false;

            try
            {
                // Log pre-enable status
                var statusBefore = GetDgpuPnpStatus();
                Log.Information("[GpuPowerSaving] Enabling dGPU, current PnP status: {Status}", statusBefore);

                // After PnP disable, Blackwell GPUs show status "Unknown" (not "Error" like older Turing/Ampere).
                // We target physically present devices that are NOT in OK state — this catches both
                // Error (older GPUs) and Unknown (Blackwell) disabled states, while ignoring ghost replicas.
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -Command \"Enable-PnpDevice -InstanceId (Get-PnpDevice -FriendlyName '*NVIDIA*' -Class Display | Where-Object { $_.Present -eq $true -and $_.Status -ne 'OK' }).InstanceId -Confirm:$false\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                    // UCU runs elevated by default, no need for runas
                };

                using var proc = Process.Start(psi);
                if (proc == null) return false;

                string output = await proc.StandardOutput.ReadToEndAsync();
                string error = await proc.StandardError.ReadToEndAsync();
                proc.WaitForExit();

                bool success = proc.ExitCode == 0;
                if (success)
                {
                    var statusAfter = GetDgpuPnpStatus();
                    Log.Information("[GpuPowerSaving] dGPU enabled via PnP hotswap, new status: {Status}", statusAfter);
                    Log.Information("[GpuPowerSaving] PowerShell output: {Output}", output.Trim());
                    // GPU driver just re-initialized, need a cooldown
                    GpuInteropGuard.Reset();
                }
                else
                {
                    Log.Information("[GpuPowerSaving] PnP enable failed (exit code {ExitCode}): {Error}", proc.ExitCode, error);
                    Log.Information("[GpuPowerSaving] PowerShell output: {Output}", output.Trim());
                }
                return success;
            }
            catch (Exception ex)
            {
                Log.Information("[GpuPowerSaving] Failed to enable dGPU: {Message}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Queries the current PnP status of the NVIDIA dGPU.
        /// Returns "OK" if enabled, other status strings if disabled, or "Unknown" on failure.
        /// </summary>
        public static string GetDgpuPnpStatus()
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -Command \"Get-PnpDevice -FriendlyName '*NVIDIA*' -Class Display | Where-Object { $_.Present -eq $true } | ForEach-Object { $_.Status } | Select-Object -First 1\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return "Unknown";

                string output = proc.StandardOutput.ReadToEnd().Trim();
                string error = proc.StandardError.ReadToEnd().Trim();
                proc.WaitForExit();

                Log.Information("[GpuPowerSaving] GetDgpuPnpStatus: exitCode={ExitCode}, output=[{Output}], error=[{Error}]", proc.ExitCode, output, error);

                return proc.ExitCode == 0 && !string.IsNullOrEmpty(output) ? output : $"Unknown(exit={proc.ExitCode},err=[{error}])";
            }
            catch (Exception ex)
            {
                Log.Information("[GpuPowerSaving] Failed to query dGPU PnP status: {Message}", ex.Message);
                return $"Unknown(ex={ex.Message})";
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Applies the specified GPU power saving mode.
        /// Always uses PnP hotswap to disable/enable the dGPU (like XMG CC).
        /// Also sets Windows GPU preference registry for new processes.
        /// </summary>
        /// <param name="mode">The power saving mode to apply.</param>
        public static async Task ApplyAsync(GpuPowerSavingMode mode)
        {
            Log.Information("[GpuPowerSaving] Applying mode: {Mode}", mode);

            // Step 1: Set Windows GPU preference registry for new processes
            SetWindowsGpuPreference(mode);

            // Step 2: Set NVIDIA DRS global GPU preference
            SetNvidiaDrsGpuPreference(mode);

            // Step 3: PnP hotswap — skip if dGPU is already in the desired state
            string currentStatus = GetDgpuPnpStatus();
            if (mode == GpuPowerSavingMode.Enable)
            {
                // Need to disable dGPU — skip if already disabled (not OK)
                if (currentStatus == "OK")
                {
                    await DisableDgpuAsync();
                }
                else
                {
                    Log.Information("[GpuPowerSaving] dGPU already disabled (status: {Status}), skipping PnP disable", currentStatus);
                }
            }
            else
            {
                // Need to enable dGPU — skip if already enabled (OK)
                if (currentStatus != "OK")
                {
                    await EnableDgpuAsync();
                }
                else
                {
                    Log.Information("[GpuPowerSaving] dGPU already enabled (status: OK), skipping PnP enable");
                }
            }

            Log.Information("[GpuPowerSaving] Mode {Mode} applied successfully", mode);
        }

        /// <summary>
        /// Gets a display name for the power saving mode.
        /// </summary>
        public static string GetModeDisplayName(GpuPowerSavingMode mode)
        {
            return mode switch
            {
                GpuPowerSavingMode.Enable => "On (Power Saving)",
                GpuPowerSavingMode.Disable => "Off (Performance)",
                GpuPowerSavingMode.Auto => "Auto",
                _ => $"Unknown ({(int)mode})"
            };
        }

        #endregion
    }
}
