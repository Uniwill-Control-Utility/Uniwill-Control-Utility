using RyzenSmu;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using Universal_x86_Tuning_Utility.Scripts.AMD_Backend;
using Universal_x86_Tuning_Utility.Scripts.Intel_Backend;

namespace Universal_x86_Tuning_Utility.Scripts
{
    public class Family
    {
        public enum RyzenFamily
        {
            Unknown = -1,
            SummitRidge,
            PinnacleRidge,
            RavenRidge,
            Dali,
            Pollock,
            Picasso,
            FireFlight,
            Matisse,
            Renoir,
            Lucienne,
            VanGogh,
            Mendocino,
            Vermeer,
            Cezanne_Barcelo,
            Rembrandt,
            Raphael,
            DragonRange,
            PhoenixPoint,
            PhoenixPoint2,
            HawkPoint,
            HawkPoint2,
            SonomaValley,
            GraniteRidge,
            FireRange,
            StrixHalo,
            StrixPoint,
            KrackanPoint,
            KrackanPoint2,
        }

        public static RyzenFamily FAM = RyzenFamily.Unknown;

        public enum ProcessorType
        {
            Unknown = -1,
            Amd_Apu,
            Amd_Desktop_Cpu,
            Amd_Laptop_Cpu,
            Intel,
        }

        public static ProcessorType TYPE = ProcessorType.Unknown;


        public static string CPUName = "";
        public static string GPUName = "";
        public static string LaptopModel = "";
        public static int CPUFamily = 0, CPUModel = 0, CPUStepping = 0;

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
                if (name.Contains("H ") || name.EndsWith("H"))
                    return 45;

                // Generic U series (ultralow)
                if (name.Contains("U ") || name.EndsWith("U"))
                    return 15;

                // Unknown Intel fallback
                return watercoolerConnected ? 80 : 65;
            }

            // --- AMD ---
            if (TYPE == ProcessorType.Amd_Desktop_Cpu)
                return 86;

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
            if (name.Contains("U ") || name.EndsWith("U"))
                return 15;

            // Unknown AMD mobile fallback
            return 55;
        }

        public static void setCpuFamily()
        {
            try
            {
                string processorIdentifier = System.Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");

                // Split the string into individual words
                string[] words = processorIdentifier.Split(' ');

                // Find the indices of the words "Family", "Model", and "Stepping"
                int familyIndex = Array.IndexOf(words, "Family") + 1;
                int modelIndex = Array.IndexOf(words, "Model") + 1;
                int steppingIndex = Array.IndexOf(words, "Stepping") + 1;

                // Extract the family, model, and stepping values from the corresponding words
                CPUFamily = int.Parse(words[familyIndex]);
                CPUModel = int.Parse(words[modelIndex]);
                CPUStepping = int.Parse(words[steppingIndex].TrimEnd(','));

                ManagementObjectSearcher mos = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_Processor");
                foreach (ManagementObject mo in mos.Get())
                {
                   CPUName = mo["Name"].ToString();
                }

                // Detect laptop/system model from Win32_ComputerSystem
                try
                {
                    ManagementObjectSearcher modelSearcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT Model FROM Win32_ComputerSystem");
                    foreach (ManagementObject mo in modelSearcher.Get())
                    {
                        LaptopModel = mo["Model"]?.ToString()?.Trim() ?? "";
                    }
                }
                catch { /* WMI not available for model detection */ }

                // Detect all GPUs: separate dedicated GPUs from integrated ones,
                // then combine them as "dGPU / iGPU" for display.
                try
                {
                    List<string> dedicatedGpus = new List<string>();
                    List<string> integratedGpus = new List<string>();

                    ManagementObjectSearcher gpuSearcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_VideoController");
                    foreach (ManagementObject gpu in gpuSearcher.Get())
                    {
                        string gpuName = gpu["Name"]?.ToString()?.Trim() ?? "";
                        if (string.IsNullOrEmpty(gpuName) || gpuName.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
                            continue;

                        bool isDedicated = false;

                        // NVIDIA GPUs are always dedicated
                        if (gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                            gpuName.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                            gpuName.Contains("Quadro", StringComparison.OrdinalIgnoreCase) ||
                            gpuName.Contains("RTX", StringComparison.OrdinalIgnoreCase))
                        {
                            isDedicated = true;
                        }
                        // AMD: dedicated if it contains "RX" (Radeon RX series) or "Radeon Pro"
                        else if (gpuName.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase) ||
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
                catch { /* WMI not available for GPU detection */ }
            }
            catch (Exception e)
            {
                Debug.WriteLine("Error: " + e.Message);
            }

            if (CPUName.Contains("Intel"))
            {
                TYPE = ProcessorType.Intel;
            }
            else
            {
                try
                {
                    //App.memTimings = Mem_Timings.RetrieveTimings();
                }
                catch (Exception ex)
                {
                    Misc.DiagnosticLogger.LogError(ex, "Failed to retrieve memory timings");
                }

                //Zen1 - Zen2
                if (CPUFamily == 23)
                {
                    if (CPUModel == 1) FAM = RyzenFamily.SummitRidge;

                    if (CPUModel == 8) FAM = RyzenFamily.PinnacleRidge;

                    if (CPUModel == 17 || CPUModel == 18) FAM = RyzenFamily.RavenRidge;

                    if (CPUModel == 24) FAM = RyzenFamily.Picasso;

                    if (CPUModel == 32 && CPUName.Contains("15e") || CPUModel == 32 && CPUName.Contains("15Ce") || CPUModel == 32 && CPUName.Contains("20e")) FAM = RyzenFamily.Pollock;
                    else if (CPUModel == 32) FAM = RyzenFamily.Dali;

                    if (CPUModel == 80) FAM = RyzenFamily.FireFlight;

                    if (CPUModel == 96) FAM = RyzenFamily.Renoir;

                    if (CPUModel == 104) FAM = RyzenFamily.Lucienne;

                    if (CPUModel == 113) FAM = RyzenFamily.Matisse;

                    if (CPUModel == 144 || CPUModel == 145) FAM = RyzenFamily.VanGogh;

                    if (CPUModel == 160) FAM = RyzenFamily.Mendocino;
                }

                //Zen3 - Zen4
                if (CPUFamily == 25)
                {
                    if (CPUModel == 33) FAM = RyzenFamily.Vermeer;

                    if (CPUModel == 63 || CPUModel == 68) FAM = RyzenFamily.Rembrandt;

                    if (CPUModel == 80) FAM = RyzenFamily.Cezanne_Barcelo;

                    if (CPUModel == 97 && CPUName.Contains("HX")) FAM = RyzenFamily.DragonRange;
                    else if (CPUModel == 97) FAM = RyzenFamily.Raphael;

                    if (CPUModel == 116) FAM = RyzenFamily.PhoenixPoint;

                    if (CPUModel == 120) FAM = RyzenFamily.PhoenixPoint2;

                    if (CPUModel == 117) FAM = RyzenFamily.HawkPoint;

                    if (CPUModel == 124) FAM = RyzenFamily.HawkPoint2;
                }

                // Zen5 - Zen6
                if (CPUFamily == 26)
                {
                    if (CPUModel == 68 && CPUName.Contains("HX")) FAM = RyzenFamily.FireRange;
                    else if (CPUModel == 68) FAM = RyzenFamily.GraniteRidge;

                    if (CPUModel == 96) FAM = RyzenFamily.KrackanPoint;

                    if (CPUModel == 104) FAM = RyzenFamily.KrackanPoint2;

                    if (CPUModel == 32 || CPUModel == 36) FAM = RyzenFamily.StrixPoint;

                    if (CPUModel == 112) FAM = RyzenFamily.StrixHalo;
                }

                if (FAM == RyzenFamily.SummitRidge || FAM == RyzenFamily.PinnacleRidge || FAM == RyzenFamily.Matisse || FAM == RyzenFamily.Vermeer || FAM == RyzenFamily.Raphael || FAM == RyzenFamily.GraniteRidge) TYPE = ProcessorType.Amd_Desktop_Cpu;
                else TYPE = ProcessorType.Amd_Apu;

                Addresses.setAddresses();
            }
        }
    }
}
