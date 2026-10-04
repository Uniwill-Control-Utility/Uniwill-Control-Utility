using System;
using System.Windows;
using Wpf.Ui.Abstractions;

namespace Uniwill_Control_Utility.Services
{
    public class PageService : INavigationViewPageProvider
    {
        private readonly IServiceProvider _serviceProvider;

        // Cached scoped instances (one per page type)
        private Views.Pages.Presets? _presetsInstance;
        private Views.Pages.Adaptive? _adaptiveInstance;

        public PageService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        /// <summary>
        /// Call when Power Boost mode is toggled to invalidate cached page instances.
        /// The next navigation to Presets or Adaptive will create a fresh instance.
        /// </summary>
        public void InvalidateBoostDependentPages()
        {
            _presetsInstance = null;
            _adaptiveInstance = null;
        }

        public object? GetPage(Type pageType)
        {
            if (!typeof(FrameworkElement).IsAssignableFrom(pageType))
                throw new InvalidOperationException("The page should be a WPF control.");

            // Presets — reuse cached instance unless invalidated
            if (pageType == typeof(Views.Pages.Presets))
            {
                if (_presetsInstance == null)
                {
                    var logger = _serviceProvider.GetService(typeof(Microsoft.Extensions.Logging.ILogger<Views.Pages.Presets>)) as Microsoft.Extensions.Logging.ILogger<Views.Pages.Presets>;
                    var navRequest = _serviceProvider.GetService(typeof(Services.PresetsNavigationRequest)) as Services.PresetsNavigationRequest;
                    var gpuInventory = _serviceProvider.GetService(typeof(Services.GpuInventoryService)) as Services.GpuInventoryService;
                    var waterCooler = _serviceProvider.GetService(typeof(Services.WaterCoolerService)) as Services.WaterCoolerService;
                    var deviceApplier = _serviceProvider.GetService(typeof(Services.DeviceApplier)) as Services.DeviceApplier;
                    _presetsInstance = new Views.Pages.Presets(logger!, navRequest!, gpuInventory, waterCooler, deviceApplier);
                }
                return _presetsInstance;
            }

            // Adaptive — reuse cached instance unless invalidated
            if (pageType == typeof(Views.Pages.Adaptive))
            {
                if (_adaptiveInstance == null)
                {
                    var gpuInventory = _serviceProvider.GetService(typeof(Services.GpuInventoryService)) as Services.GpuInventoryService;
                    _adaptiveInstance = new Views.Pages.Adaptive(gpuInventory!);
                }
                return _adaptiveInstance;
            }

            return _serviceProvider.GetService(pageType);
        }
    }
}
