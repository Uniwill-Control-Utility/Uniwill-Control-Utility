using System;
using System.ComponentModel;
using System.IO;
using System.ServiceProcess;
using System.Threading.Tasks;
using Serilog;
using Uniwill_Control_Utility.Scripts.Misc;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Keeps the engine service installed and running, the way the XMG Control
    /// Center shell keeps GCUBridge alive: the app owns the engine's lifecycle,
    /// the engine owns the hardware.
    ///
    /// The service is registered <c>start= auto</c>, so at every boot it is
    /// already up; this is the self-heal pass. A missing service (fresh install,
    /// portable zip) triggers one elevated self-launch of
    /// "<c>Uniwill Control Utility Engine.exe --install-engine</c>" — the single
    /// UAC prompt of the product. A stopped service is started unelevated: the
    /// install grants Authenticated Users SERVICE_START, which is what lets the
    /// de-elevated app restart the engine without consent. A missing engine exe
    /// (dev checkout, no Service\ staging) is normal and silent: dev runs use
    /// the dev-inprocess bridge instead.
    /// </summary>
    internal static class EngineLifecycle
    {
        public const string ServiceName = "UCUEngine";
        public const string EngineExeFileName = "Uniwill Control Utility Engine.exe";

        /// <summary>
        /// The engine executable in the install layout's Service\ subfolder
        /// (staged by the csproj targets), or null in a dev checkout.
        /// </summary>
        public static string? EngineExePath
        {
            get
            {
                string path = Path.Combine(AppContext.BaseDirectory, "Service", EngineExeFileName);
                return File.Exists(path) ? path : null;
            }
        }

        public static async Task EnsureAsync()
        {
            try
            {
                ServiceControllerStatus? status = QueryStatus();

                if (status is null)
                {
                    string? exe = EngineExePath;
                    if (exe is null)
                    {
                        Log.Information("[Engine] No engine service and no staged engine exe — dev layout, nothing to ensure.");
                        return;
                    }

                    Log.Information("[Engine] Engine service missing — running the one-time elevated install.");
                    int rc = await ElevatedProcess
                        .RunAsync(exe, "--install-engine", "Engine service install")
                        .ConfigureAwait(false);
                    if (rc != 0)
                        Log.Warning("[Engine] Elevated engine install exited with {Code}", rc);
                    return;
                }

                if (status == ServiceControllerStatus.Stopped || status == ServiceControllerStatus.Paused)
                {
                    using var controller = new ServiceController(ServiceName);
                    controller.Start();
                    controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                    Log.Information("[Engine] Engine service started.");
                }
            }
            catch (Win32Exception ex)
            {
                // Access denied: a service installed by an older build (no
                // SERVICE_START grant) or a tightened machine policy. The RPC
                // client keeps answering calls with HardwareUnavailable; the
                // user can start the service from services.msc.
                Log.Warning(ex, "[Engine] Could not start the engine service (native code {Code})", ex.NativeErrorCode);
            }
            catch (InvalidOperationException ex)
            {
                Log.Warning(ex, "[Engine] The engine service vanished mid-check");
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                // ServiceController.WaitForStatus signals the start timeout.
                Log.Warning("[Engine] The engine service did not reach Running within 30 s");
            }
        }

        /// <summary>The service's current status, or null when it is not installed.</summary>
        private static ServiceControllerStatus? QueryStatus()
        {
            try
            {
                using var controller = new ServiceController(ServiceName);
                controller.Refresh();
                return controller.Status;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}
