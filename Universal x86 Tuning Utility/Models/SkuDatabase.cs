using System.Collections.Generic;
using Newtonsoft.Json;

namespace Universal_x86_Tuning_Utility.Models
{
    public class SkuDatabase
    {
        [JsonProperty("skus")]
        public List<SkuEntry> Skus { get; set; } = new();
    }

    public class SkuEntry
    {
        [JsonProperty("id")]
        public string Id { get; set; } = "";

        [JsonProperty("name")]
        public string Name { get; set; } = "";

        [JsonProperty("cpu")]
        public SkuCpu Cpu { get; set; } = new();

        [JsonProperty("gpu")]
        public SkuGpu Gpu { get; set; } = new();

        [JsonProperty("power_limits")]
        public SkuPowerLimits PowerLimits { get; set; } = new();

        [JsonProperty("profiles")]
        public SkuProfiles Profiles { get; set; } = new();
    }

    public class SkuCpu
    {
        [JsonProperty("vendor")]
        public string Vendor { get; set; } = "";

        [JsonProperty("model")]
        public string Model { get; set; } = "";
    }

    public class SkuGpu
    {
        [JsonProperty("model")]
        public string Model { get; set; } = "";

        [JsonProperty("nvml_name_pattern")]
        public string NvmlNamePattern { get; set; } = "";
    }

    public class SkuPowerLimits
    {
        [JsonProperty("air")]
        public SkuPowerLimitsCooling Air { get; set; } = new();

        [JsonProperty("water")]
        public SkuPowerLimitsCooling Water { get; set; } = new();
    }

    public class SkuPowerLimitsCooling
    {
        [JsonProperty("cpu_pl1_sustained")]
        public int CpuPl1Sustained { get; set; }

        [JsonProperty("cpu_pl2_boost")]
        public int CpuPl2Boost { get; set; }

        [JsonProperty("gpu_tgp_baseline")]
        public int GpuTgpBaseline { get; set; }

        [JsonProperty("gpu_tgp_with_db")]
        public int GpuTgpWithDb { get; set; }

        [JsonProperty("combined_cpu_gpu")]
        public int CombinedCpuGpu { get; set; }

        [JsonProperty("dynamic_boost_max")]
        public int DynamicBoostMax { get; set; }
    }

    public class SkuProfiles
    {
        [JsonProperty("office")]
        public SkuProfileEntry Office { get; set; } = new();

        [JsonProperty("gaming")]
        public SkuProfileEntry Gaming { get; set; } = new();

        [JsonProperty("turbo")]
        public SkuProfileEntry Turbo { get; set; } = new();
    }

    public class SkuProfileEntry
    {
        [JsonProperty("cpu_pl1")]
        public int CpuPl1 { get; set; }

        [JsonProperty("cpu_pl2")]
        public int CpuPl2 { get; set; }

        [JsonProperty("cpu_pl4")]
        public int CpuPl4 { get; set; }

        [JsonProperty("gpu_tgp")]
        public int GpuTgp { get; set; }

        [JsonProperty("dynamic_boost")]
        public bool DynamicBoost { get; set; }

        [JsonProperty("dynamic_boost_offset")]
        public int DynamicBoostOffset { get; set; }

        [JsonProperty("uses_cpu_max")]
        public bool UsesCpuMax { get; set; }
    }
}
