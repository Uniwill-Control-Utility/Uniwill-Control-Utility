using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UCU.Hardware.Gpu;

namespace UCU.Engine
{
    /// <summary>
    /// Re-applies the live half of a completed GPU unlock once, at engine start, before the
    /// pipe server answers its first <c>gpu.get_rom_override_state</c>. A reboot keeps the
    /// staged records and the originals file but clears what the EC and the driver hold —
    /// the cTGP offset, the rails, the laptop budget and the driver's own request — so
    /// without this the owner would see "Partly applied" on every boot for a feature they
    /// enabled days ago. The store does nothing (and returns null) when no unlock was ever
    /// completed, and never retries: a failed re-apply is the Uncertain state it reports.
    /// </summary>
    public sealed class GpuRomOverrideStartup : IHostedService
    {
        private readonly IGpuRomOverrideService _overrides;
        private readonly ILogger<GpuRomOverrideStartup> _log;

        public GpuRomOverrideStartup(IGpuRomOverrideService overrides, ILogger<GpuRomOverrideStartup> log)
        {
            _overrides = overrides;
            _log = log;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                _overrides.ReapplyAtStartup();
            }
            catch (Exception ex)
            {
                // The engine must come up even when the GPU side refuses: the state verb
                // then reports the failure, while a host that cannot start takes the EC,
                // the fans and every preset down with it.
                _log.LogWarning(ex, "GPU ROM override start-up re-apply failed");
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
