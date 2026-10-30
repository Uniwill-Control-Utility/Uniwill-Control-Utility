---
name: wmi-multi-gpu-detection
description: Report the dedicated GPU first when WMI Win32_VideoController returns dGPU, iGPU and Microsoft Basic Display Adapter - two-pass keyword classification instead of naive first-match
---

# WMI Multi-GPU Detection (Dedicated vs Integrated)

## Problem Pattern

Querying `Win32_VideoController` via WMI returns all graphics adapters on the system — dedicated GPUs (dGPU), integrated GPUs (iGPU), and Microsoft Basic Display Adapter. A naive first-match approach picks whichever GPU WMI enumerates first, which is often the integrated GPU (e.g., "AMD Radeon(TM) 610M") instead of the dedicated GPU (e.g., "NVIDIA GeForce RTX 5090 Mobile").

**Classic symptom:** GPU detection reports the iGPU name when the user expects the dGPU, or only shows one GPU when the system has both.

## Solution: Two-Pass Classification

Query all GPUs once, classify each as dedicated or integrated, then combine them with a separator (dGPU first, iGPU second).

### Classification Rules

| Condition | Classification |
|---|---|
| Contains "NVIDIA", "GeForce", "Quadro", or "RTX" (case-insensitive) | Dedicated |
| Contains "Radeon RX" or "Radeon Pro" (case-insensitive) | Dedicated |
| Contains "Radeon" but not "RX" (e.g., Radeon 610M, Radeon Graphics) | Integrated |
| Contains "Intel" | Integrated |
| Contains "Microsoft" | Skip entirely |

### Implementation

```csharp
List<string> dedicatedGpus = new List<string>();
List<string> integratedGpus = new List<string>();

ManagementObjectSearcher gpuSearcher = new ManagementObjectSearcher(
    "root\\CIMV2", "SELECT * FROM Win32_VideoController");

foreach (ManagementObject gpu in gpuSearcher.Get())
{
    string gpuName = gpu["Name"]?.ToString()?.Trim() ?? "";
    if (string.IsNullOrEmpty(gpuName) || 
        gpuName.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
        continue;

    bool isDedicated = gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                       gpuName.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                       gpuName.Contains("Quadro", StringComparison.OrdinalIgnoreCase) ||
                       gpuName.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
                       gpuName.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase) ||
                       gpuName.Contains("Radeon Pro", StringComparison.OrdinalIgnoreCase);

    if (isDedicated)
        dedicatedGpus.Add(gpuName);
    else
        integratedGpus.Add(gpuName);
}

// dGPU first, then iGPU
var allGpus = new List<string>();
allGpus.AddRange(dedicatedGpus);
allGpus.AddRange(integratedGpus);

string gpuString = allGpus.Count > 0 ? string.Join(" / ", allGpus) : "";
```

### Result Examples

| System | Output |
|---|---|
| NVIDIA dGPU only | `"NVIDIA GeForce RTX 5090 Mobile"` |
| AMD dGPU + AMD iGPU | `"AMD Radeon RX 7800M / AMD Radeon(TM) 610M"` |
| AMD iGPU only (no dGPU) | `"AMD Radeon(TM) 610M"` |
| Intel iGPU only | `"Intel(R) Arc(TM) Graphics"` |

## Bonus: Laptop/System Model Detection

Query `Win32_ComputerSystem.Model` for the system/laptop model name (e.g., "XMG NEO (A25)"):

```csharp
string laptopModel = "";
ManagementObjectSearcher modelSearcher = new ManagementObjectSearcher(
    "root\\CIMV2", "SELECT Model FROM Win32_ComputerSystem");

foreach (ManagementObject mo in modelSearcher.Get())
{
    laptopModel = mo["Model"]?.ToString()?.Trim() ?? "";
}
```

## Why This Works

- Single WMI query, no fallback needed
- Classification is keyword-based, not vendor-ID based (avoids needing `PNPDeviceID` parsing)
- Ordering ensures the most relevant GPU (dedicated) appears first
- Microsoft Basic Display Adapter is always skipped (it's a fallback driver, not real hardware)

## Limitations

- Keyword matching may misclassify future GPU naming schemes (e.g., if NVIDIA releases a non-GeForce mobile product)
- Some AMD APU iGPUs use "Radeon Graphics" naming — correctly classified as integrated since they don't contain "RX"
- Desktop systems with multiple dGPUs (SLC/NVLink) will list all of them
