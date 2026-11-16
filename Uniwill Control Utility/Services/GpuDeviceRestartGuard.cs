using System;
using System.Threading.Tasks;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Runs a GPU device-restarting verb (the unlock's <c>SetRomOverrideAsync</c>,
    /// which ends in the engine's own <c>pnputil /restart-device</c>) with hardware
    /// monitoring parked. A poll on a handle from before the restart is an access
    /// violation in native NVML/driver code that no managed catch can stop, so the
    /// park is the only protection: no poll in the window, handles closed before it,
    /// reopened only after the driver's cooldown.
    /// </summary>
    internal static class GpuDeviceRestartGuard
    {
        public static async Task<T> RunAsync<T>(
            IHardwareMonitoringService? monitoring,
            Func<Task<T>> restart)
        {
            using (monitoring?.Suspend())
            {
                return await restart().ConfigureAwait(true);
            }
        }
    }
}
