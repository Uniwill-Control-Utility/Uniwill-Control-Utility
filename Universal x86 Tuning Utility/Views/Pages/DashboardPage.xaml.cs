using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Universal_x86_Tuning_Utility.Scripts;
using Universal_x86_Tuning_Utility.Services;
using Universal_x86_Tuning_Utility.ViewModels;
using Wpf.Ui.Abstractions.Controls;

namespace Universal_x86_Tuning_Utility.Views.Pages
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
        }

        private void InitializeDeviceInfo()
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
                    LaptopImage.Source = bitmap;
                }
                catch
                {
                    LaptopImage.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                LaptopImage.Visibility = Visibility.Collapsed;
            }

            // Set laptop display name (use clean laptop model, no Windows PC name)
            if (!string.IsNullOrEmpty(model))
            {
                LaptopNameText.Text = GetCleanModelName(model).ToUpperInvariant();
                LaptopInfoContent.Visibility = Visibility.Visible;
            }
            else
            {
                LaptopNameText.Text = "DESKTOP SYSTEM";
                LaptopInfoContent.Visibility = Visibility.Visible;
            }

            // Set CPU name spec textblock - clean up core count suffix
            LaptopCPUNameText.Text = CleanCpuName(cpuName);

            // Set GPU name spec textblock - filter and clean
            if (!string.IsNullOrEmpty(gpuName))
            {
                LaptopGPUNameText.Text = FilterGpuName(gpuName);
            }

            // Set display, RAM, and storage spec textblocks
            LaptopDisplaySpecText.Text = SystemSpecs.GetDisplayString();
            LaptopRamText.Text = SystemSpecs.GetRamString();
            LaptopStorageText.Text = SystemSpecs.GetStorageString();
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
            if (model.ToUpperInvariant().Contains("XMG"))
            {
                int xmgIndex = model.IndexOf("XMG", StringComparison.OrdinalIgnoreCase);
                if (xmgIndex > 0)
                    return model.Substring(xmgIndex).Trim();
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
            var parts = gpuName.Split(new[] { " / " }, StringSplitOptions.RemoveEmptyEntries);
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
            _hydroUi.UpdateMetrics(snapshot);
            _flydigi.UpdateMetrics(snapshot);
        }
    }
}
