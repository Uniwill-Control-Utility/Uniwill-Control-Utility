using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Scripts;
using Uniwill_Control_Utility.Scripts.Misc;
using Uniwill_Control_Utility.Services;
using Uniwill_Control_Utility.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Abstractions.Controls;

namespace Uniwill_Control_Utility.Views.Pages
{
    public partial class DashboardPage :
        Page,
        INavigableView<DashboardViewModel>
    {
        private readonly PresetsNavigationRequest _presetsNavigationRequest;

        public DashboardViewModel ViewModel { get; }

        public DashboardPage(
            DashboardViewModel viewModel,
            PresetsNavigationRequest presetsNavigationRequest)
        {
            ViewModel = viewModel
                ?? throw new ArgumentNullException(nameof(viewModel));
            _presetsNavigationRequest = presetsNavigationRequest
                ?? throw new ArgumentNullException(nameof(presetsNavigationRequest));

            DataContext = this;

            InitializeComponent();

            ViewModel.MetricsUpdated += OnMetricsUpdated;
            ViewModel.DeviceMetricsUpdated += OnDeviceMetricsUpdated;

            InitializeDeviceInfo();
            InitializeDeviceControls();
        }

        private void DashboardCard_MouseLeftButtonUp(
            object sender,
            MouseButtonEventArgs e)
        {
            NavigateFromDashboardCard(sender);
            e.Handled = true;
        }

        private void DashboardCard_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is not (Key.Enter or Key.Space))
                return;

            NavigateFromDashboardCard(sender);
            e.Handled = true;
        }

        private void NavigateFromDashboardCard(object sender)
        {
            if (sender is not FrameworkElement
                {
                    Tag: DashboardCard card
                } ||
                !DashboardCardNavigation.TryResolve(
                    card,
                    out DashboardCardDestination destination))
            {
                return;
            }

            if (destination.TuningSection is PresetsTuningSection section)
                _presetsNavigationRequest.Request(section);

            ViewModel.GetNavigationService().Navigate(destination.PageTag);
        }

        private void InitializeDeviceControls()
        {
            // Gate Hydro UI control by hardware support — unsupported hardware
            // never shows, even if connected (won't be on a watercooler laptop).
            if (!WaterCoolerHardwareDetector.IsSupportedHardware())
                _hydroUi.Visibility = Visibility.Collapsed;

            // Gate Flydigi control by user setting — collapsed immediately so it
            // doesn't flash visible before the first metrics update.
            if (!Settings.Default.EnableFlydigi)
                _flydigi.Visibility = Visibility.Collapsed;

            // Pass navigation service to device controls for "Connect" buttons
            var navService = ViewModel.GetNavigationService();
            _hydroUi.SetNavigationService(navService);
            _flydigi.SetNavigationService(navService);
        }

        private void InitializeDeviceInfo()
        {
            // Hardware detection runs before the UI is built, so Family.* statics
            // are already populated by the time the page constructor runs.
            // The event subscription is kept for safety (e.g. if detection is
            // re-run or called from a different path).
            App.HardwareDetectionCompleted += OnHardwareDetectionCompleted;

            if (!string.IsNullOrEmpty(Family.CPUName))
                OnHardwareDetectionCompleted();
        }

        private void OnHardwareDetectionCompleted()
        {
            string cpuName = Family.CPUName;
            string gpuName = Family.GPUName;
            bool isIntel = Family.TYPE == Scripts.Family.ProcessorType.Intel;

            // Determine laptop image path from the detector catalog
            string? imagePath = LaptopDetector.GetImagePath(isIntel);

            if (!string.IsNullOrEmpty(imagePath))
            {
                try
                {
                    var bitmap = new BitmapImage(new Uri(imagePath, UriKind.RelativeOrAbsolute));
                    _header.SetLaptopImage(bitmap);
                }
                catch
                {
                    _header.SetLaptopImageVisibility(Visibility.Collapsed);
                }
            }
            else
            {
                _header.SetLaptopImageVisibility(Visibility.Collapsed);
            }

            // Set laptop display name from the detector (or fallback to "DESKTOP SYSTEM")
            _header.LaptopName = LaptopDetector.GetDisplayName().ToUpperInvariant();
            _header.LaptopCPUName = CleanCpuName(cpuName);
            _cpu.HardwareName = CleanCpuName(cpuName);

            if (!string.IsNullOrEmpty(gpuName))
            {
                string displayGpuName = FilterGpuName(gpuName);
                _header.LaptopGPUName = displayGpuName;
                _gpu.HardwareName = displayGpuName;
            }

            _memory.UpdateModuleInfo(
                CachedSystemInfo.RamProducer,
                CachedSystemInfo.RamModel,
                CachedSystemInfo.RamSpeed);

            // Run the slower SystemSpecs WMI queries on a background thread so they
            // don't block the UI. These are cosmetic (display/RAM/storage specs).
            _ = Task.Run(() =>
            {
                var display = SystemSpecs.GetDisplayString();
                var ram = SystemSpecs.GetRamString();
                var storage = SystemSpecs.GetStorageString();

                Application.Current.Dispatcher.Invoke(() =>
                {
                    _header.LaptopDisplaySpec = display;
                    _header.LaptopRam = ram;
                    _header.LaptopStorage = storage;
                });
            });
        }

        /// <summary>
        /// Strips trailing core-count suffix from CPU names (e.g., "16-Core Processor", "8 core processor").
        /// </summary>
        private static string CleanCpuName(string cpuName)
        {
            if (string.IsNullOrEmpty(cpuName))
                return cpuName;

            // Match "Core Processor" (case-insensitive) - covers "16-Core Processor", "8 Core Processor"
            int index = cpuName.IndexOf("Core Processor", StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                // Walk back to find the start of the core count number
                int trimIndex = index;
                for (int i = index - 1; i >= 0; i--)
                {
                    if (char.IsDigit(cpuName[i]) || cpuName[i] == '-' || cpuName[i] == ' ')
                        trimIndex = i;
                    else
                        break;
                }
                return cpuName.Substring(0, trimIndex).Trim();
            }

            return cpuName;
        }

        /// <summary>
        /// Strips trailing " GPU" suffix from GPU names (keeps "Laptop").
        /// </summary>
        private static string CleanGpuName(string gpuName)
        {
            if (string.IsNullOrEmpty(gpuName))
                return gpuName;

            string result = gpuName;

            // Remove " GPU" at the end (e.g., "NVIDIA GeForce RTX 4070 Laptop GPU" -> "...Laptop")
            // Also handle " Laptop GPU" -> keep "Laptop"
            int gpuIndex = result.LastIndexOf(" GPU", StringComparison.OrdinalIgnoreCase);
            if (gpuIndex > 0)
                result = result.Substring(0, gpuIndex).Trim();

            return result;
        }

        /// <summary>
        /// Returns true if the GPU name looks like an integrated/iGPU (not a discrete card).
        /// </summary>
        private static bool IsIntegratedGpu(string gpuName)
        {
            string upper = gpuName.ToUpperInvariant();

            // AMD iGPU markers: Radeon(TM), Radeon Graphics, Radeon Vega, "Radeon" with no model number pattern
            if (upper.Contains("RADEON(TM)") ||
                upper.Contains("RADEON GRAPHICS") ||
                upper.Contains("RADEON VEGA GRAPHICS"))
                return true;

            // Intel iGPU markers: UHD Graphics, Iris Xe, Iris Plus, Intel HD
            if (upper.Contains("UHD GRAPHICS") ||
                upper.Contains("IRIS XE") ||
                upper.Contains("IRIS PLUS") ||
                upper.Contains("INTEL HD GRAPHICS") ||
                upper.Contains("INTEL ARC"))
                return true;

            return false;
        }

        /// <summary>
        /// Filters GPU names to only include discrete NVIDIA and AMD GPUs, and cleans up suffixes.
        /// The Family.GPUName may contain multiple GPUs separated by " / ".
        /// </summary>
        private static string FilterGpuName(string gpuName)
        {
            string[] separator = { " / " };
            var parts = gpuName.Split(separator, StringSplitOptions.RemoveEmptyEntries);
            var filtered = new System.Collections.Generic.List<string>();

            foreach (var part in parts)
            {
                string trimmed = part.Trim();
                if (string.IsNullOrEmpty(trimmed))
                    continue;

                // Skip integrated GPUs
                if (IsIntegratedGpu(trimmed))
                    continue;

                bool isNvidia = trimmed.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                                trimmed.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                                trimmed.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
                                trimmed.Contains("Quadro", StringComparison.OrdinalIgnoreCase);

                bool isAmd = trimmed.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
                             trimmed.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                             trimmed.Contains("RX ", StringComparison.OrdinalIgnoreCase);

                if (isNvidia || isAmd)
                    filtered.Add(CleanGpuName(trimmed));
            }

            return string.Join(" / ", filtered);
        }

        private void OnMetricsUpdated(object? sender, HardwareMetricsSnapshot snapshot)
        {
            _cpu.UpdateMetrics(snapshot);
            _gpu.UpdateMetrics(snapshot);
            _memory.UpdateMetrics(snapshot);
            _battery.UpdateMetrics(snapshot);
        }

        private void OnDeviceMetricsUpdated(object? sender, DeviceMetricsSnapshot snapshot)
        {
            // Update metrics first — each control sets its own visibility based on connection state.
            _hydroUi.UpdateMetrics(snapshot);

            // Only update Flydigi metrics if enabled in Settings
            if (Settings.Default.EnableFlydigi)
                _flydigi.UpdateMetrics(snapshot);

            // Adjust layout based on which cards are visible.
            bool hydroVisible = _hydroUi.Visibility == Visibility.Visible;
            bool flydigiVisible = _flydigi.Visibility == Visibility.Visible;

            if (!hydroVisible && !flydigiVisible)
            {
                // Neither card is visible — hide the entire row.
                _deviceRow.Visibility = Visibility.Collapsed;
            }
            else if (hydroVisible && !flydigiVisible)
            {
                // Only Hydro UI — let the modular card use the full row.
                _deviceRow.Visibility = Visibility.Visible;
                _deviceRow.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                _deviceRow.ColumnDefinitions[1].Width = new GridLength(0);
                _deviceRow.ColumnDefinitions[2].Width = new GridLength(0);
                _hydroUi.HorizontalAlignment = HorizontalAlignment.Stretch;
                _hydroUi.MinWidth = 0;
                _hydroUi.MaxWidth = double.MaxValue;
            }
            else if (!hydroVisible && flydigiVisible)
            {
                // Only Flydigi — let the modular card use the full row.
                _deviceRow.Visibility = Visibility.Visible;
                _deviceRow.ColumnDefinitions[0].Width = new GridLength(0);
                _deviceRow.ColumnDefinitions[1].Width = new GridLength(0);
                _deviceRow.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
                _flydigi.HorizontalAlignment = HorizontalAlignment.Stretch;
                _flydigi.MinWidth = 0;
                _flydigi.MaxWidth = double.MaxValue;
            }
            else
            {
                // Both visible — side by side.
                _deviceRow.Visibility = Visibility.Visible;
                _deviceRow.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                _deviceRow.ColumnDefinitions[1].Width = new GridLength(8);
                _deviceRow.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
                _hydroUi.HorizontalAlignment = HorizontalAlignment.Stretch;
                _hydroUi.MinWidth = 0;
                _hydroUi.MaxWidth = double.MaxValue;
                _flydigi.HorizontalAlignment = HorizontalAlignment.Stretch;
                _flydigi.MinWidth = 0;
                _flydigi.MaxWidth = double.MaxValue;
            }
        }
    }
}
