using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Scripts;
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
        public DashboardViewModel ViewModel { get; }

        public DashboardPage(DashboardViewModel viewModel)
        {
            ViewModel = viewModel
                ?? throw new ArgumentNullException(nameof(viewModel));

            DataContext = this;

            InitializeComponent();

            ViewModel.MetricsUpdated += OnMetricsUpdated;
            ViewModel.DeviceMetricsUpdated += OnDeviceMetricsUpdated;

            InitializeDeviceInfo();
            InitializeDeviceControls();
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
            // Hardware detection may still be running on a background thread.
            // Subscribe to the completion event so we populate the header as soon
            // as the data is ready, without blocking page construction.
            App.HardwareDetectionCompleted += OnHardwareDetectionCompleted;

            // If detection already finished (e.g. app was already running), populate now.
            if (!string.IsNullOrEmpty(Family.LaptopModel) || !string.IsNullOrEmpty(Family.CPUName))
            {
                OnHardwareDetectionCompleted();
            }
        }

        private void OnHardwareDetectionCompleted()
        {
            string model = Family.LaptopModel;
            string cpuName = Family.CPUName;
            string gpuName = Family.GPUName;

            // Determine laptop image path
            string? imagePath = GetLaptopImagePath(model, cpuName);

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

            // Set laptop display name
            string displayName = !string.IsNullOrEmpty(model)
                ? GetCleanModelName(model).ToUpperInvariant()
                : "DESKTOP SYSTEM";

            _header.LaptopName = displayName;
            _header.LaptopCPUName = CleanCpuName(cpuName);

            if (!string.IsNullOrEmpty(gpuName))
                _header.LaptopGPUName = FilterGpuName(gpuName);

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
        /// Returns the pack URI for the laptop image based on the detected model and CPU.
        /// </summary>
        private static string? GetLaptopImagePath(string model, string cpuName)
        {
            string modelUpper = model.ToUpperInvariant();
            bool isAmd = !cpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase);

            // XMG Neo E25/A25 (both use the same image)
            if (modelUpper.Contains("XMG") && modelUpper.Contains("NEO"))
            {
                return "pack://application:,,,/Assets/Laptops/XMG/Neo-25.png";
            }

            // PC Specialist Recoil 3 16
            if (modelUpper.Contains("RECOIL"))
            {
                if (isAmd)
                {
                    return "pack://application:,,,/Assets/Laptops/PCSpecialist/Recoil-3-16-AMD.png";
                }
                else
                {
                    return "pack://application:,,,/Assets/Laptops/PCSpecialist/Recoil-3-16-Intel.png";
                }
            }

            return null;
        }

        /// <summary>
        /// Returns a clean model string for display, stripping manufacturer prefixes.
        /// </summary>
        private static string GetCleanModelName(string model)
        {
            // XMG Neo: strip "SchenkerTechnologiesGmbH " prefix
            if (model.Contains("XMG", StringComparison.OrdinalIgnoreCase))
            {
                int xmgIndex = model.IndexOf("XMG", StringComparison.OrdinalIgnoreCase);
                if (xmgIndex > 0)
                {
                    string cleaned = model.Substring(xmgIndex).Trim();
                    // Insert "16" before the variant suffix (e.g., "XMG NEO (A25)" -> "XMG NEO 16 (A25)")
                    int parenIndex = cleaned.IndexOf('(');
                    if (parenIndex > 0)
                        cleaned = cleaned.Insert(parenIndex, "16 ");
                    return cleaned;
                }
            }

            return model;
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
                // Only Hydro UI — center it.
                _deviceRow.Visibility = Visibility.Visible;
                _deviceRow.ColumnDefinitions[1].Width = GridLength.Auto;
                _deviceRow.ColumnDefinitions[2].Width = GridLength.Auto;
                _hydroUi.HorizontalAlignment = HorizontalAlignment.Center;
                _hydroUi.MinWidth = 536;
                _hydroUi.MaxWidth = 536;
            }
            else if (!hydroVisible && flydigiVisible)
            {
                // Only Flydigi — center it.
                _deviceRow.Visibility = Visibility.Visible;
                _deviceRow.ColumnDefinitions[0].Width = GridLength.Auto;
                _deviceRow.ColumnDefinitions[1].Width = GridLength.Auto;
                _flydigi.HorizontalAlignment = HorizontalAlignment.Center;
                _flydigi.MinWidth = 536;
                _flydigi.MaxWidth = 536;
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
