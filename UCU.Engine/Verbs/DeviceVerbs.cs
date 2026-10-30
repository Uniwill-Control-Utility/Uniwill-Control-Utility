using UCU.Contracts;
using UCU.Hardware.Devices;

namespace UCU.Engine
{
    /// <summary>
    /// Device rows: the webcam and dGPU PnP enable/disable. The engine answers
    /// these as LocalSystem with no consent prompt - which is the point: the GPU
    /// power-saving apply reaches this mutation from automatic paths (startup,
    /// charger transitions, tray, Automations), and an automatic apply that needs
    /// UAC is a prompt the user cannot anticipate. The filters carry the device
    /// identity (Camera class / NVIDIA display), so the verbs stay semantic and
    /// the CM API stays engine-side.
    /// </summary>
    internal static class DeviceVerbs
    {
        private static readonly PnpDeviceFilter Webcam = new()
        {
            DeviceClass = "Camera",
            FriendlyNamePatterns = new[] { "*Webcam*", "*Camera*" },
        };

        private static readonly PnpDeviceFilter Dgpu = new()
        {
            DeviceClass = "Display",
            FriendlyNamePatterns = new[] { "*NVIDIA*" },
        };

        public static void Register(Action<string, VerbHandler> add, IPnpDeviceService devices)
        {
            add(EngineVerbs.Device.GetWebcamStatus, (p, ct) =>
                Task.FromResult<object?>(ToResult(devices.GetStateAsync(Webcam, ct))));

            add(EngineVerbs.Device.SetWebcamEnabled, (p, ct) =>
            {
                DeviceToggleDto dto = EngineSerializer.BindParams<DeviceToggleDto>(p);
                return Task.FromResult<object?>(devices.SetEnabledAsync(Webcam, dto.Enabled, ct).GetAwaiter().GetResult());
            });

            add(EngineVerbs.Device.GetDgpuStatus, (p, ct) =>
                Task.FromResult<object?>(ToResult(devices.GetStateAsync(Dgpu, ct))));

            add(EngineVerbs.Device.SetDgpuEnabled, (p, ct) =>
            {
                DeviceToggleDto dto = EngineSerializer.BindParams<DeviceToggleDto>(p);
                return Task.FromResult<object?>(devices.SetEnabledAsync(Dgpu, dto.Enabled, ct).GetAwaiter().GetResult());
            });
        }

        /// <summary>The dispatcher runs handlers synchronously on the domain queue;
        /// the awaited value is the verb's answer (a null state means the engine
        /// found no matching device node, which the client renders as null). The
        /// hardware record maps to the contract DTO here: the contract assembly
        /// references no hardware, so the mapping lives on the engine side.</summary>
        private static PnpDeviceStateDto? ToResult(Task<PnpDeviceState?> task)
        {
            PnpDeviceState? state = task.GetAwaiter().GetResult();
            if (state == null)
                return null;

            return new PnpDeviceStateDto
            {
                InstanceId = state.InstanceId,
                Status = state.Status,
                Present = state.Present,
            };
        }
    }
}
