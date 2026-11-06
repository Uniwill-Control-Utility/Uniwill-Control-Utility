using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Serilog;
using Windows.Devices.Radios;

namespace Uniwill_Control_Utility.Scripts.Misc
{
    /// <summary>
    /// Hardware Management — enable/disable Wi-Fi, Bluetooth, webcam, and touchpad.
    /// Mirrors XMG CC's Device Manager page (GCService5.DeviceSwitchItem):
    /// - Wi-Fi/Bluetooth: Windows.Devices.Radios radio API — Radio.EnableAsync/DisableAsync
    ///   (XMG CC's WIFIModel/BTModel + WIFISwitch/BTSwitch and StateChanged listeners)
    /// - Webcam: PowerShell PnP enable/disable on the Camera device class — XMG CC's embedded
    ///   EnableCamd/DisableCamd/SearchStatus constants and Command\disableWebcam.ps1 scripts
    /// - Touchpad: SystemSettingsAdminFlows.exe EnableTouchpad 0/1 — the Windows Settings
    ///   admin flow used by open-revo on this platform (XMG CC's own EC write is unknown
    ///   from its Fody-stripped binary; the Settings flow is the Windows-level equivalent)
    /// </summary>
    internal static class HardwareManagement
    {
        /// <summary>
        /// The engine's device capability (webcam PnP). Set from DI at startup
        /// (App phase 1) and by SettingsPage, the same static-singleton idiom as
        /// RyzenAdj_To_UCU.EcService. The engine performs the PnP mutation as
        /// LocalSystem with no consent prompt and verifies it by re-reading the
        /// node; the elevated PowerShell path stays as the fallback for a machine
        /// whose engine is not running. Wi-Fi/Bluetooth stay here as radio-API
        /// calls: they need no privilege, so they need no engine.
        /// </summary>
        internal static UCU.Contracts.Capabilities.IDeviceCapability? Devices { get; set; }

        #region Windows Radios (Wi-Fi / Bluetooth)

        private static Windows.Devices.Radios.Radio? _wifiRadio;
        private static Windows.Devices.Radios.Radio? _btRadio;

        /// <summary>
        /// Gets (cached) the Wi-Fi radio object, or null when the system has no Wi-Fi radio.
        /// </summary>
        public static async Task<Windows.Devices.Radios.Radio?> GetWifiRadioAsync()
        {
            if (_wifiRadio != null)
                return _wifiRadio;

            _wifiRadio = await GetRadioByKindAsync(RadioKind.WiFi);
            return _wifiRadio;
        }

        /// <summary>
        /// Gets (cached) the Bluetooth radio object, or null when the system has no Bluetooth radio.
        /// </summary>
        public static async Task<Windows.Devices.Radios.Radio?> GetBluetoothRadioAsync()
        {
            if (_btRadio != null)
                return _btRadio;

            _btRadio = await GetRadioByKindAsync(RadioKind.Bluetooth);
            return _btRadio;
        }

        private static async Task<Windows.Devices.Radios.Radio?> GetRadioByKindAsync(RadioKind kind)
        {
            try
            {
                var radios = await Radio.GetRadiosAsync();
                foreach (var radio in radios)
                {
                    if (radio.Kind == kind)
                        return radio;
                }
            }
            catch (Exception ex)
            {
                Log.Information("[HardwareManagement] Failed to enumerate radios: {Message}", ex.Message);
            }

            return null;
        }

        public static async Task<bool> SetWifiAsync(bool enable)
        {
            var radio = await GetWifiRadioAsync();
            if (radio == null)
            {
                Log.Information("[HardwareManagement] No Wi-Fi radio available on this system");
                return false;
            }

            return await SetRadioAsync(radio, enable, "Wi-Fi");
        }

        public static async Task<bool> SetBluetoothAsync(bool enable)
        {
            var radio = await GetBluetoothRadioAsync();
            if (radio == null)
            {
                Log.Information("[HardwareManagement] No Bluetooth radio available on this system");
                return false;
            }

            return await SetRadioAsync(radio, enable, "Bluetooth");
        }

        private static async Task<bool> SetRadioAsync(Windows.Devices.Radios.Radio radio, bool enable, string label)
        {
            try
            {
                var access = await radio.SetStateAsync(enable ? RadioState.On : RadioState.Off);
                Log.Information("[HardwareManagement] {Label} radio set-state access result: {Access}", label, access);
                return access == RadioAccessStatus.Allowed;
            }
            catch (Exception ex)
            {
                Log.Information("[HardwareManagement] Failed to set {Label} radio: {Message}", label, ex.Message);
                return false;
            }
        }

        private static bool RadioIsOn(Windows.Devices.Radios.Radio radio)
        {
            return radio.State == RadioState.On;
        }

        public static async Task<bool?> GetWifiEnabledAsync()
        {
            var radio = await GetWifiRadioAsync();
            return radio == null ? null : RadioIsOn(radio);
        }

        public static async Task<bool?> GetBluetoothEnabledAsync()
        {
            var radio = await GetBluetoothRadioAsync();
            return radio == null ? null : RadioIsOn(radio);
        }

        #endregion

        #region Webcam (PnP Camera class)

        // XMG CC's embedded PowerShell commands (DeviceSwitchItem EnableCamd/DisableCamd/SearchStatus
        // plus the *Camera* friendly-name variant from Command\disableWebcam.ps1 / enableWebcam.ps1),
        // restructured: the friendly-name patterns are queried in one pass without the -Status CIM
        // filter (a no-match CIM query throws CimJobException when the device is already in the
        // target state), and Enable/Disable only runs on devices whose state actually differs.
        private const string DisableWebcamCommand =
            "Get-PnpDevice -FriendlyName *Webcam*,*Camera* -Class Camera | Where-Object { $_.Present -eq $true -and $_.Status -eq 'OK' } | ForEach-Object { Disable-PnpDevice -InstanceId $_.InstanceId -Confirm:$false }";
        private const string EnableWebcamCommand =
            "Get-PnpDevice -FriendlyName *Webcam*,*Camera* -Class Camera | Where-Object { $_.Present -eq $true -and $_.Status -ne 'OK' } | ForEach-Object { Enable-PnpDevice -InstanceId $_.InstanceId -Confirm:$false }";
        private const string SearchWebcamStatus =
            "Get-PnpDevice -FriendlyName *Webcam*,*Camera* -Class Camera | Where-Object { $_.Present -eq $true } | ForEach-Object { $_.Status }";

        /// <summary>
        /// Runs a PowerShell status query unelevated and captures its output.
        /// Get-PnpDevice queries need no elevation; the mutations (SetWebcamAsync)
        /// go through ElevatedProcess instead.
        /// </summary>
        private static async Task<(int exitCode, string output, string error)> RunPowershellAsync(string command)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"{command}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
                return (-1, string.Empty, "process start failed");

            string output = await proc.StandardOutput.ReadToEndAsync();
            string error = await proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();
            return (proc.ExitCode, output.Trim(), error.Trim());
        }

        /// <summary>
        /// Enables/disables the webcam via PnP device hotswap on the Camera device class.
        /// The mutation is the engine's device verb: LocalSystem answers it with no
        /// consent prompt (the per-operation UAC this row used to raise is gone), the
        /// CM API targets the present *Webcam*/*Camera* node, and the engine's own
        /// verification re-read answers the verb. The elevated PowerShell route
        /// remains as the fallback for a machine whose engine is not running.
        /// </summary>
        public static async Task<bool> SetWebcamAsync(bool enable)
        {
            if (Devices != null)
            {
                try
                {
                    bool verified = await Devices.SetWebcamEnabledAsync(enable).ConfigureAwait(false);

                    Log.Information("[HardwareManagement] Webcam PnP {Action} via engine: verified={Verified}",
                        enable ? "enable" : "disable", verified);

                    return verified;
                }
                catch (Exception ex)
                {
                    Log.Information("[HardwareManagement] Engine webcam toggle unavailable ({Message}) - using the elevated fallback", ex.Message);
                }
            }

            int code = await ElevatedProcess.RunPowershellAsync(
                enable ? EnableWebcamCommand : DisableWebcamCommand, "Webcam PnP toggle").ConfigureAwait(false);

            Log.Information("[HardwareManagement] Webcam PnP {Action} elevated exit={Code}",
                enable ? "enable" : "disable", code);

            return code == 0;
        }

        /// <summary>
        /// Queries the current webcam PnP status. True when a present Camera-class device
        /// reports OK, false when it reports any other status, null when no camera is present.
        /// </summary>
        public static async Task<bool?> GetWebcamEnabledAsync()
        {
            var (code, output, _) = await RunPowershellAsync(SearchWebcamStatus);
            if (code != 0 || output.Length == 0)
                return null;

            // Enabled when at least one present Camera-class device reports OK
            foreach (var line in output.Split('\n', '\r'))
            {
                if (line.Trim().ToUpper() == "OK")
                    return true;
            }

            return false;
        }

        #endregion

        #region Touchpad (Windows Settings admin flow)

        /// <summary>
        /// Enables/disables the touchpad via the Windows Settings admin flow
        /// (SystemSettingsAdminFlows.exe EnableTouchpad 0/1) — the mechanism open-revo
        /// uses on this platform. The flow owns the same toggle the Settings app shows.
        /// The flow needs a full administrator token AND the interactive session, so
        /// it cannot run in the LocalSystem engine (session 0). It runs as a one-shot
        /// "highest privileges" scheduled task for the logged-on user - the same
        /// route the Windows Settings app itself uses for this flow - which the
        /// unelevated app can create, run and delete with NO consent prompt. The
        /// direct elevated spawn remains as the fallback for a machine where the
        /// task cannot be created.
        /// </summary>
        public static async Task<bool> SetTouchpadAsync(bool enable)
        {
            try
            {
                int? taskResult = Services.ScheduledTaskService.RunOneShotHighestPrivileges(
                    "UCU Touchpad Flow",
                    "One-shot run of the Windows touchpad admin flow (UCU).",
                    "SystemSettingsAdminFlows.exe",
                    enable ? "EnableTouchpad 1" : "EnableTouchpad 0");

                if (taskResult != null)
                {
                    Log.Information("[HardwareManagement] Touchpad admin flow via one-shot task (enable={Enable}) exit code: {ExitCode}",
                        enable, taskResult.Value);
                    return taskResult.Value == 0;
                }

                Log.Information("[HardwareManagement] Touchpad task bridge unavailable - using the elevated fallback");

                // Fallback route: elevated per-operation consent, since the app
                // process is unelevated.
                int exitCode = await ElevatedProcess.RunAsync(
                    "SystemSettingsAdminFlows.exe",
                    enable ? "EnableTouchpad 1" : "EnableTouchpad 0",
                    "Touchpad admin flow").ConfigureAwait(false);

                Log.Information("[HardwareManagement] Touchpad admin flow (enable={Enable}) exit code: {ExitCode}",
                    enable, exitCode);
                return exitCode == 0;
            }
            catch (Exception ex)
            {
                Log.Information("[HardwareManagement] Failed to set touchpad: {Message}", ex.Message);
                return false;
            }
        }

        #endregion

    }
}
