using RyzenSmu;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using Uniwill_Control_Utility.Scripts.AMD_Backend;
using Uniwill_Control_Utility.Scripts.Intel_Backend;

namespace Uniwill_Control_Utility.Scripts
{
    public class Family
    {
        public enum RyzenFamily
        {
            Unknown = -1,
            FireRange,
        }

        // Zen 5 (FireRange) CPUID identifiers
        private const int Zen5Family = 26;
        private const int Zen5FireRangeModel = 68;

        private static RyzenFamily _fam = RyzenFamily.Unknown;
        internal static RyzenFamily FAM => _fam;

        public enum ProcessorType
        {
            Unknown = -1,
            Amd_Apu,
            Amd_Desktop_Cpu,
            Amd_Laptop_Cpu,
            Intel,
        }

        private static ProcessorType _type = ProcessorType.Unknown;
        internal static ProcessorType TYPE => _type;


        private static string _cpuName = string.Empty;
        internal static string CPUName
        {
            get => _cpuName;
            private set => _cpuName = value;
        }

        private static string _gpuName = string.Empty;
        internal static string GPUName
        {
            get => _gpuName;
            private set => _gpuName = value;
        }

        private static int CPUFamily;
        private static int CPUModel;

        /// <summary>
        /// Returns a recommended PL1 power limit (in watts) based on the detected CPU
        /// and whether a watercooler (LCT or Flydigi BS2 Pro) is actively connected.
        /// </summary>
        public static int GetRecommendedPowerLimit(bool watercoolerConnected)
        {
            string name = CPUName.ToUpperInvariant();

            // --- Intel ---
            if (TYPE == ProcessorType.Intel)
            {
                // Core Ultra 9 275HX (2025 XMG Neo) - 140W air / 160W liquid
                if (name.Contains("275HX"))
                    return watercoolerConnected ? 160 : 140;

                // Core i9-14900HX (2024 XMG Neo) - 125W air / 160W liquid
                if (name.Contains("14900HX"))
                    return watercoolerConnected ? 160 : 125;

                // Core i9-13900HX / 13905HX - 125W air / 150W liquid
                if (name.Contains("13900HX") || name.Contains("13905HX"))
                    return watercoolerConnected ? 150 : 125;

                // Core i7-14700HX - 115W air / 140W liquid
                if (name.Contains("14700HX"))
                    return watercoolerConnected ? 140 : 115;

                // Core i7-13700H / 13700P
                if (name.Contains("13700H") || name.Contains("13700P"))
                    return 45;

                // Core Ultra 9 185H / 185U
                if (name.Contains("185H") || name.Contains("185U"))
                    return 55;

                // Core Ultra 7 155H / 155U
                if (name.Contains("155H") || name.Contains("155U"))
                    return 55;

                // Core Ultra 5 125H / 125U
                if (name.Contains("125H") || name.Contains("125U"))
                    return 55;

                // Generic HX series (high-performance mobile)
                if (name.Contains("HX"))
                    return watercoolerConnected ? 130 : 100;

                // Generic H series (performance mobile)
                if (name.Contains("H ", StringComparison.Ordinal) || name.EndsWith('H'))
                    return 45;

                // Generic U series (ultralow)
                if (name.Contains("U ", StringComparison.Ordinal) || name.EndsWith('U'))
                    return 15;

                // Unknown Intel fallback
                return watercoolerConnected ? 80 : 65;
            }

            // AMD Ryzen 9 9955HX / 9955HX3D (2025 XMG Neo) - 80W air / 110W liquid
            if (name.Contains("9955HX"))
                return watercoolerConnected ? 110 : 80;

            // AMD Ryzen 9 7945HX / 7945HX3D (DragonRange)
            if (name.Contains("7945HX"))
                return watercoolerConnected ? 100 : 55;

            // AMD Ryzen 9 7845HX
            if (name.Contains("7845HX"))
                return watercoolerConnected ? 90 : 55;

            // AMD Ryzen 9 8945HS
            if (name.Contains("8945HS"))
                return 55;

            // AMD Ryzen 9 894HS
            if (name.Contains("894HS"))
                return 40;

            // AMD Ryzen 7 8845HS
            if (name.Contains("8845HS"))
                return 55;

            // AMD Ryzen 7 8840HS
            if (name.Contains("8840HS"))
                return 55;

            // AMD Ryzen 7 7840HS
            if (name.Contains("7840HS"))
                return 55;

            // Generic AMD HX series
            if (name.Contains("HX"))
                return watercoolerConnected ? 80 : 55;

            // Generic AMD HS series
            if (name.Contains("HS"))
                return 40;

            // Generic AMD U series
            if (name.Contains("U ", StringComparison.Ordinal) || name.EndsWith('U'))
                return 15;

            // Unknown AMD mobile fallback
            return 55;
        }

        public static void setCpuFamily()
        {
            try
            {
                string? processorIdentifier = System.Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");

                if (string.IsNullOrEmpty(processorIdentifier))
                {
                    return;
                }

                // Split the string into individual words
                string[] words = processorIdentifier.Split(' ');

                // Find the indices of the words "Family" and "Model"
                int familyIndex = Array.IndexOf(words, "Family") + 1;
                int modelIndex = Array.IndexOf(words, "Model") + 1;

                // Extract the family and model values from the corresponding words
                CPUFamily = int.Parse(words[familyIndex]);
                CPUModel = int.Parse(words[modelIndex]);

                ManagementObjectSearcher mos = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_Processor");
#pragma warning disable S3217 // Loops should be type-safe
                foreach (ManagementObject mo in mos.Get())
#pragma warning restore S3217
                {
                   CPUName = mo["Name"]?.ToString()?.Trim() ?? "";
                }

                // Detect all GPUs: separate dedicated GPUs from integrated ones,
                // then combine them as "dGPU / iGPU" for display.
                try
                {
                    List<string> dedicatedGpus = new List<string>();
                    List<string> integratedGpus = new List<string>();

                    ManagementObjectSearcher gpuSearcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_VideoController");
#pragma warning disable S3217 // Loops should be type-safe
                    foreach (ManagementObject gpu in gpuSearcher.Get())
#pragma warning restore S3217
                    {
                        string gpuName = gpu["Name"]?.ToString()?.Trim() ?? "";
                        if (string.IsNullOrEmpty(gpuName) || gpuName.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
                            continue;

                        bool isDedicated = false;

                        // NVIDIA GPUs are always dedicated
                        // NVIDIA and AMD RX/Pro GPUs are dedicated; Intel and AMD non-RX are integrated
                        if (gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                            gpuName.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                            gpuName.Contains("Quadro", StringComparison.OrdinalIgnoreCase) ||
                            gpuName.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
                            gpuName.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase) ||
                            gpuName.Contains("Radeon Pro", StringComparison.OrdinalIgnoreCase))
                        {
                            isDedicated = true;
                        }
                        // AMD: integrated if it contains "Radeon" but not "RX" (e.g. Radeon 610M, Radeon Graphics)
                        // Intel: always integrated

                        if (isDedicated)
                            dedicatedGpus.Add(gpuName);
                        else
                            integratedGpus.Add(gpuName);
                    }

                    // Build combined GPU string: "dGPU / iGPU" or just whichever exists
                    var allGpus = new List<string>();
                    allGpus.AddRange(dedicatedGpus);
                    allGpus.AddRange(integratedGpus);

                    GPUName = allGpus.Count > 0 ? string.Join(" / ", allGpus) : "";
                }
                catch (COMException)
                {
                    // WMI not available for GPU detection
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine("Error: " + e.Message);
            }

            if (CPUName.Contains("Intel", StringComparison.Ordinal))
            {
                _type = ProcessorType.Intel;
            }
            else
            {
                // Memory timings retrieval not implemented

                // Only FireRange (Zen 5, Family 26, Model 68, HX suffix) is supported.
                if (CPUFamily == Zen5Family && CPUModel == Zen5FireRangeModel && CPUName.Contains("HX", StringComparison.Ordinal))
                    _fam = RyzenFamily.FireRange;

                _type = ProcessorType.Amd_Apu;
                Addresses.setAddresses();
            }
        }
    }
}
