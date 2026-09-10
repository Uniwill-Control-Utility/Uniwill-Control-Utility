using System;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Models
{
    /// <summary>
    /// Thin wrapper that delegates temperature reads to the shared
    /// IHardwareMonitoringService so the Flydigi page and Dashboard
    /// use the same sensor data.
    /// </summary>
    public sealed class FlydigiTemperatureProvider : IDisposable
    {
        private IHardwareMonitoringService? _monitoring;
        private IDisposable? _lease;

        private IHardwareMonitoringService Monitoring
        {
            get
            {
                if (_monitoring == null)
                {
                    _monitoring = App.GetService<IHardwareMonitoringService>();
                    _lease = _monitoring?.Acquire(HardwareMonitoringCategory.Cpu | HardwareMonitoringCategory.Gpu);
                }
                return _monitoring!;
            }
        }

        public double? GetCpuTemperature()
        {
            try
            {
                var snapshot = Monitoring.ReadSnapshot();
                return snapshot.CpuTemperature > 0 ? (double?)snapshot.CpuTemperature : null;
            }
            catch
            {
                return null;
            }
        }

        public double? GetGpuTemperature()
        {
            try
            {
                var snapshot = Monitoring.ReadSnapshot();
                return snapshot.GpuTemperature > 0 ? (double?)snapshot.GpuTemperature : null;
            }
            catch
            {
                return null;
            }
        }

        public double? GetMaxTemperature()
        {
            try
            {
                var cpuTemp = GetCpuTemperature();
                var gpuTemp = GetGpuTemperature();

                if (cpuTemp == null && gpuTemp == null)
                    return null;
                if (cpuTemp == null)
                    return gpuTemp;
                if (gpuTemp == null)
                    return cpuTemp;

                return Math.Max(cpuTemp.Value, gpuTemp.Value);
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            _lease?.Dispose();
            _lease = null;
            _monitoring = null;
            GC.SuppressFinalize(this);
        }
    }
}
