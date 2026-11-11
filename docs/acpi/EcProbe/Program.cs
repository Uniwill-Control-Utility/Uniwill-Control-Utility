using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

/// <summary>
/// Read-first EC probe for the bench questions in
/// artifacts/notes/ucu-mechcontrol-adoption-user-stories.md.
///
/// Reads go straight to UWACPIDriver.sys, independent of UCU's engine, so the tool
/// can run while the app is closed. The only write path is `ramp`, it needs
/// --allow-write, and it touches nothing but 0x0787.
///
///   EcProbe                      facts (default)
///   EcProbe facts
///   EcProbe snapshot             tables + duty + PWM + sensors, pasteable
///   EcProbe trace --seconds 60 --interval 500 [--out t.csv]
///                                0x788 / cTGP / NVML timeline for Test B
///   EcProbe --allow-write ramp 300
///                                0x80 | 3 = enabled, 300 ms (Test A)
///   EcProbe --allow-write ramp off
/// </summary>
internal static class Program
{
    private const string Device = @"\\.\ACPIDriver";
    private const uint IOCTL_EC_READ = 0x9C40A488;
    private const uint IOCTL_EC_WRITE = 0x9C40A48C;
    private const int EcDelayMs = 6;

    // Bench registers.
    private const ushort R_PROJECT_ID = 0x0740;
    private const ushort R_EC_VER_MAJOR = 0x0454;
    private const ushort R_EC_VER_MINOR = 0x047D;
    private const ushort R_SUPPORT_742 = 0x0742;
    private const ushort R_SUPPORT_765 = 0x0765;
    private const ushort R_SUPPORT_766 = 0x0766;
    private const ushort R_SUPPORT_78E = 0x078E;
    private const ushort R_SUPPORT_7A4 = 0x07A4;
    private const ushort R_SYSTEM_ID_456 = 0x0456;
    private const ushort R_AP_OEM_741 = 0x0741;
    private const ushort R_PWR_MGMT_726 = 0x0726;
    private const ushort R_CUSTOM_PROFILE_727 = 0x0727;
    private const ushort R_MANUAL_FAN_CTRL = 0x0751;
    private const ushort R_UNIVERSAL_FAN_CTRL = 0x07C5;
    private const ushort R_AP_OEM_6 = 0x07C6;
    private const ushort R_COOLING_MODE_7C7 = 0x07C7;
    private const ushort R_FAN_SWITCH_SPEED = 0x0787;
    private const ushort R_THERMAL_BUDGET_788 = 0x0788;
    private const ushort R_PL1 = 0x0783;
    private const ushort R_PL2 = 0x0784;
    private const ushort R_PL4 = 0x0785;
    private const ushort R_TGP_DB_CTRL = 0x0743;
    private const ushort R_TGP = 0x0744;
    private const ushort R_DB_TOTAL = 0x0745;
    private const ushort R_DB_MAX = 0x0746;
    private const ushort R_DB_LIVE_7D5 = 0x07D5;
    private const ushort R_CPU_TEMP = 0x043E;
    private const ushort R_GPU_TEMP = 0x044F;
    private const ushort R_CPU_RPM_HI = 0x0464;
    private const ushort R_CPU_RPM_LO = 0x0465;
    private const ushort R_GPU_RPM_HI = 0x046C;
    private const ushort R_GPU_RPM_LO = 0x046D;
    private const ushort R_PWM_CPU = 0x075B;
    private const ushort R_PWM_GPU = 0x075C;
    private const ushort R_LC_FAN_DUTY = 0x07E6;
    private const ushort R_LC_PUMP_DUTY = 0x07E7;
    private const ushort R_MODE_PRESET_7A7 = 0x07A7;
    private const ushort R_MODE_PRESET_7AA = 0x07AA;
    private const ushort R_COOLER_GATE_7C8 = 0x07C8;
    private const ushort R_CUSTOM_PROFILE_727B = 0x0727;

    private static readonly (string Name, ushort Base)[] Tables =
    {
        ("CPU tempUp  (0xF00)", 0x0F00),
        ("CPU tempDown(0xF10)", 0x0F10),
        ("CPU duty    (0xF20)", 0x0F20),
        ("GPU tempUp  (0xF30)", 0x0F30),
        ("GPU tempDown(0xF40)", 0x0F40),
        ("GPU duty    (0xF50)", 0x0F50),
    };

    private static nint _handle = new(-1);

    // ---- transport ---------------------------------------------------------

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFileW(string name, uint access, uint share,
        nint sec, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(nint dev, uint code,
        ref int inBuf, int inSize, ref int outBuf, int outSize, out int returned, nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(nint dev, uint code,
        int[] inBuf, int inSize, ref int outBuf, int outSize, out int returned, nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    private static extern int NvmlInit();

    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
    private static extern int NvmlShutdown();

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int NvmlHandle(uint index, out nint device);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")]
    private static extern int NvmlPower(nint device, out uint milliwatts);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")]
    private static extern int NvmlEnforced(nint device, out uint milliwatts);

    private static bool Open()
    {
        _handle = CreateFileW(Device, 0xC0000000, 3, 0, 3, 0, 0);
        if (_handle == new nint(-1))
        {
            int err = Marshal.GetLastWin32Error();
            Console.Error.WriteLine($"Cannot open {Device} (Win32 {err}). " +
                "Run elevated and check UWACPIDriver.sys is installed.");
            return false;
        }
        return true;
    }

    private static int Read(ushort address)
    {
        int addressValue = address;
        int value = 0;
        int returned;
        Thread.Sleep(EcDelayMs);
        return DeviceIoControl(_handle, IOCTL_EC_READ, ref addressValue, 4,
            ref value, 4, out returned, 0) ? value & 0xFF : -1;
    }

    private static bool Write(ushort address, byte value)
    {
        // The driver takes {address, value} as two ints; a byte buffer is silently dropped.
        int[] inBuf = { address, value };
        int outValue = 0;
        int returned;
        Thread.Sleep(EcDelayMs);
        return DeviceIoControl(_handle, IOCTL_EC_WRITE, inBuf, 8, ref outValue, 4, out returned, 0);
    }

    // ---- presentation ------------------------------------------------------

    private static string Hex(int value) => value < 0 ? "--" : $"0x{value:X2}";
    private static string Bits(int value) => value < 0 ? "--" : Convert.ToString(value, 2).PadLeft(8, '0');

    private static void Line(string name, ushort address, int value, string? note = null)
    {
        string decoded = value < 0 ? "" : $" ({value,3})";
        Console.WriteLine($"  {name,-34} 0x{address:X4}  {Hex(value)}{decoded,-6} [{Bits(value)}]{(note is null ? "" : "  " + note)}");
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', Math.Max(title.Length, 60)));
    }

    // ---- commands ----------------------------------------------------------

    private static void Facts()
    {
        Console.WriteLine($"EcProbe  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  machine={Environment.MachineName}");
        using (RegistryKey? bios = Registry.LocalMachine.OpenSubKey(
            @"HARDWARE\DESCRIPTION\System\BIOS"))
        {
            string Get(string n) => bios?.GetValue(n)?.ToString() ?? "(unknown)";
            Console.WriteLine($"  baseboard  {Get("BaseBoardProduct")}");
            Console.WriteLine($"  system     {Get("SystemManufacturer")} / {Get("SystemProductName")}");
            Console.WriteLine($"  BIOS       {Get("BIOSVersion")}  {Get("BIOSReleaseDate")}");
        }

        int projectRaw = Read(R_PROJECT_ID);
        int ecMajor = Read(R_EC_VER_MAJOR);
        int ecMinor = Read(R_EC_VER_MINOR);

        Section("Identity");
        Line("project ID (raw >> 1)", R_PROJECT_ID, projectRaw,
            projectRaw < 0 ? null : $"enum {projectRaw >> 1}");
        Line("EC version major", R_EC_VER_MAJOR, ecMajor);
        Line("EC version minor", R_EC_VER_MINOR, ecMinor,
            ecMajor < 0 ? null : $"=> {ecMajor}.{ecMinor}");
        Line("system ID", R_SYSTEM_ID_456, Read(R_SYSTEM_ID_456),
            (Read(R_SYSTEM_ID_456) & 0x80) != 0 ? "dGPU present" : "no dGPU (fan 2 follows CPU table)");

        Section("Support bytes");
        foreach (ushort reg in new[] { R_SUPPORT_742, R_SUPPORT_765, R_SUPPORT_766, R_SUPPORT_78E, R_SUPPORT_7A4 })
            Line("support", reg, Read(reg),
                reg == R_SUPPORT_7A4
                    ? ((Read(reg) & 0x80) != 0 ? "bit7 set = liquid cooling supported" : "bit7 clear = no liquid cooling")
                    : null);

        Section("Q4 — liquid cooler flag and OCPL");
        int cooling = Read(R_COOLING_MODE_7C7);
        Line("cooling mode 0x07C7", R_COOLING_MODE_7C7, cooling,
            cooling < 0 ? null
                : $"LCSE(bit1)={((cooling & 0x02) != 0 ? "SET" : "clear")}, OCPL(bits2-4)={((cooling >> 2) & 0x07)}");
        Line("0x07A4 support", R_SUPPORT_7A4, Read(R_SUPPORT_7A4),
            (Read(R_SUPPORT_7A4) & 0x80) != 0 ? "liquid cooling supported" : "NOT supported");
        Line("LC fan duty 0x07E6", R_LC_FAN_DUTY, Read(R_LC_FAN_DUTY),
            "EC-generated; non-zero does not prove the cooler is attached");
        Line("LC pump duty 0x07E7", R_LC_PUMP_DUTY, Read(R_LC_PUMP_DUTY));
        Console.Write("  mode preset 0x07A7-0x07AA         ");
        for (ushort r = R_MODE_PRESET_7A7; r <= R_MODE_PRESET_7AA; r++)
            Console.Write($"0x{Read(r):X2} ");
        Console.WriteLine(" (CPU/CPU/CPU/GPU limits the EC would apply)");

        Section("Q1/Q2 — 0x787 and 0x788");
        int switchSpeed = Read(R_FAN_SWITCH_SPEED);
        Line("fan switch speed 0x0787", R_FAN_SWITCH_SPEED, switchSpeed,
            switchSpeed < 0 ? null
                : (switchSpeed & 0x80) != 0
                    ? $"enabled, {(switchSpeed & 0x7F) * 100} ms"
                    : "disabled");
        int budget = Read(R_THERMAL_BUDGET_788);
        Line("0x0788 (thermal budget?)", R_THERMAL_BUDGET_788, budget);
        Line("DB live 0x07D5", R_DB_LIVE_7D5, Read(R_DB_LIVE_7D5));

        Section("Power / mode");
        Line("PL1 0x0783", R_PL1, Read(R_PL1));
        Line("PL2 0x0784", R_PL2, Read(R_PL2));
        Line("PL4 0x0785", R_PL4, Read(R_PL4));
        Line("TGP/DB control 0x0743", R_TGP_DB_CTRL, Read(R_TGP_DB_CTRL));
        Line("TGP offset 0x0744", R_TGP, Read(R_TGP));
        Line("DB total 0x0745", R_DB_TOTAL, Read(R_DB_TOTAL));
        Line("DB max 0x0746", R_DB_MAX, Read(R_DB_MAX));
        Line("0x0726 (PECI/custom gate)", R_PWR_MGMT_726, Read(R_PWR_MGMT_726));
        Line("0x0727 (CUME)", R_CUSTOM_PROFILE_727, Read(R_CUSTOM_PROFILE_727));
        Line("0x0741 (AP_OEM)", R_AP_OEM_741, Read(R_AP_OEM_741),
            (Read(R_AP_OEM_741) & 0x01) != 0 ? "activated" : "NOT activated");

        Section("Fans / sensors");
        Line("0x0751 fan ctrl", R_MANUAL_FAN_CTRL, Read(R_MANUAL_FAN_CTRL));
        Line("0x07C5 universal fan ctrl", R_UNIVERSAL_FAN_CTRL, Read(R_UNIVERSAL_FAN_CTRL),
            (Read(R_UNIVERSAL_FAN_CTRL) & 0x80) != 0 ? "split tables" : "shared tables");
        Line("0x07C6 fan curve latch", R_AP_OEM_6, Read(R_AP_OEM_6),
            (Read(R_AP_OEM_6) & 0x04) != 0 ? "custom tables live" : "custom tables ignored");
        Line("CPU temp", R_CPU_TEMP, Read(R_CPU_TEMP));
        Line("GPU temp", R_GPU_TEMP, Read(R_GPU_TEMP));
        Console.WriteLine($"  CPU RPM  {(Read(R_CPU_RPM_HI) << 8) | Read(R_CPU_RPM_LO)}   " +
                          $"GPU RPM  {(Read(R_GPU_RPM_HI) << 8) | Read(R_GPU_RPM_LO)}");
        Console.WriteLine($"  PWM CPU  {Read(R_PWM_CPU)}/200   PWM GPU  {Read(R_PWM_GPU)}/200");

        Console.WriteLine();
        Console.WriteLine("Paste this block back. Nothing was written.");
    }

    private static void Snapshot(string? outFile)
    {
        var lines = new List<string>
        {
            $"EcProbe snapshot {DateTime.Now:yyyy-MM-dd HH:mm:ss} {Environment.MachineName}",
            $"duty[0]: CPU=0x{Read(0x0F20):X2} GPU=0x{Read(0x0F50):X2}  " +
            $"(0x00 = the EC accepts the tables; non-zero = it ignores them)",
            $"latch 0x07C6=0x{Read(R_AP_OEM_6):X2}  enable 0x07C5=0x{Read(R_UNIVERSAL_FAN_CTRL):X2}",
            $"PWM CPU={Read(R_PWM_CPU)}/200 GPU={Read(R_PWM_GPU)}/200  " +
            $"CPU {Read(R_CPU_TEMP)}C {((Read(R_CPU_RPM_HI) << 8) | Read(R_CPU_RPM_LO))}rpm  " +
            $"GPU {Read(R_GPU_TEMP)}C {((Read(R_GPU_RPM_HI) << 8) | Read(R_GPU_RPM_LO))}rpm",
            $"0x0787=0x{Read(R_FAN_SWITCH_SPEED):X2}  0x0788=0x{Read(R_THERMAL_BUDGET_788):X2}  " +
            $"0x07C7=0x{Read(R_COOLING_MODE_7C7):X2}",
            ""
        };

        foreach ((string name, ushort @base) in Tables)
        {
            var bytes = new List<string>();
            for (int i = 0; i < 16; i++)
                bytes.Add($"{Read((ushort)(@base + i)):X2}");
            lines.Add($"{name}  {string.Join(' ', bytes)}");
        }

        foreach (string line in lines)
            Console.WriteLine(line);
        if (outFile is not null)
        {
            File.WriteAllLines(outFile, lines);
            Console.WriteLine($"\nwritten to {outFile}");
        }
    }

    private static void Trace(int seconds, int intervalMs, string? outFile)
    {
        int nvml = NvmlInit();
        nint gpu = 0;
        if (nvml == 0)
            NvmlHandle(0, out gpu);

        var csv = new List<string>
        {
            "t_s,0x788,0x7D5,0x743,0x744,0x745,0x746,pl1,pl2,cpuC,gpuC,pwmCpu,pwmGpu," +
            "rpmCpu,rpmGpu,0x787,0x7C7,nvml_mw,nvml_limit_mw"
        };

        Console.WriteLine($"t_s, 0x788, 0x7D5, 0x743, 0x744, 0x745, 0x746, PL1, PL2, cpuC, gpuC, " +
                          "pwmC, pwmG, rpmC, rpmG, 0x787, 0x7C7, gpuW, gpuLimitW");
        Console.WriteLine("Put the GPU under load now; Ctrl+C to stop early.");

        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < seconds)
        {
            double t = Math.Round(clock.Elapsed.TotalSeconds, 2);
            int r744 = Read(R_TGP);
            int r788 = Read(R_THERMAL_BUDGET_788);
            int r7D5 = Read(R_DB_LIVE_7D5);
            int r743 = Read(R_TGP_DB_CTRL);
            int r745 = Read(R_DB_TOTAL);
            int r746 = Read(R_DB_MAX);
            int pl1 = Read(R_PL1);
            int pl2 = Read(R_PL2);
            int cpuC = Read(R_CPU_TEMP);
            int gpuC = Read(R_GPU_TEMP);
            int pwmC = Read(R_PWM_CPU);
            int pwmG = Read(R_PWM_GPU);
            int rpmC = (Read(R_CPU_RPM_HI) << 8) | Read(R_CPU_RPM_LO);
            int rpmG = (Read(R_GPU_RPM_HI) << 8) | Read(R_GPU_RPM_LO);
            int sw = Read(R_FAN_SWITCH_SPEED);
            int cool = Read(R_COOLING_MODE_7C7);

            string gpuW = "", gpuLim = "";
            if (nvml == 0 && NvmlPower(gpu, out uint mw) == 0)
            {
                gpuW = (mw / 1000.0).ToString("F1", CultureInfo.InvariantCulture);
                if (NvmlEnforced(gpu, out uint lim) == 0)
                    gpuLim = (lim / 1000.0).ToString("F1", CultureInfo.InvariantCulture);
            }

            string row = string.Join(", ",
                t, r788, r7D5, r743, r744, r745, r746, pl1, pl2, cpuC, gpuC,
                pwmC, pwmG, rpmC, rpmG, sw, cool, gpuW, gpuLim);
            Console.WriteLine(row);
            csv.Add(string.Join(",",
                t, r788, r7D5, r743, r744, r745, r746, pl1, pl2, cpuC, gpuC,
                pwmC, pwmG, rpmC, rpmG, sw, cool, gpuW, gpuLim));

            Thread.Sleep(Math.Max(50, intervalMs));
        }

        if (nvml == 0)
            NvmlShutdown();
        if (outFile is not null)
        {
            File.WriteAllLines(outFile, csv);
            Console.WriteLine($"\nwritten to {outFile}");
        }
    }

    // ---- liquid cooler flag (US-04, bit 1 of 0x07C7) ------------------------

    private static void PrintCoolerState(string label)
    {
        int support = Read(R_SUPPORT_7A4);
        int cooling = Read(R_COOLING_MODE_7C7);
        string supported = (support & 0x80) != 0 ? "liquid cooling supported" : "NOT supported";
        Console.WriteLine($"{label}");
        Console.WriteLine($"  0x07A4 support     0x{support:X2}  {supported}");
        Console.WriteLine($"  0x07C7 cooling     0x{cooling:X2}  LCSE={(cooling & 0x02) != 0} OCPL={(cooling >> 2) & 0x07}");
        Console.Write("  0x07A7-0x07AA      ");
        for (ushort r = R_MODE_PRESET_7A7; r <= R_MODE_PRESET_7AA; r++)
            Console.Write($"0x{Read(r):X2} ");
        Console.WriteLine();
        Console.WriteLine($"  0x07E6/0x07E7      LC fan 0x{Read(R_LC_FAN_DUTY):X2}  pump 0x{Read(R_LC_PUMP_DUTY):X2}");
        Console.Write("  0x0730-0x0736      gaming ");
        for (ushort r = 0x0730; r <= 0x0733; r++) Console.Write($"{Read(r)}/");
        Console.Write("  office ");
        for (ushort r = 0x0734; r <= 0x0736; r++) Console.Write($"{Read(r)}/");
        Console.WriteLine(" (PL1/PL2/PL4 defaults)");
        int gate = Read(R_COOLER_GATE_7C8);
        Console.WriteLine($"  0x07C8 gate        0x{gate:X2}  bit5={(gate & 0x20) != 0}   (third cooler gate on AMD firmware)");
        Console.WriteLine($"  0x0783/0x0784      PL1 0x{Read(0x0783):X2} ({Read(0x0783)} W)  PL2 0x{Read(0x0784):X2} ({Read(0x0784)} W)");
        Console.WriteLine($"  0x0726/0x0727      0x{Read(R_PWR_MGMT_726):X2} / 0x{Read(R_CUSTOM_PROFILE_727B):X2}  (custom gate / custom profile)");
        Console.WriteLine($"  0x043E/0x044F      CPU {Read(0x043E)} C  GPU {Read(0x044F)} C   " +
                          $"fans {((Read(R_CPU_RPM_HI) << 8) | Read(R_CPU_RPM_LO))} / {((Read(R_GPU_RPM_HI) << 8) | Read(R_GPU_RPM_LO))} rpm");
    }

    private static int Cooler(string argument, bool allowWrite, bool confirmed, bool argsWaitStock, int waitSeconds)
    {
        if (argument.Length == 0 || argument.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            PrintCoolerState("liquid cooler state (read-only)");
            return 0;
        }

        if (!allowWrite)
        {
            Console.Error.WriteLine("cooler on|off writes 0x07C7 bit 1. Pass --allow-write to proceed.");
            return 4;
        }

        bool attached = argument.Equals("on", StringComparison.OrdinalIgnoreCase);
        if (!attached && !argument.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("cooler takes 'status', 'on' or 'off'.");
            return 2;
        }

        int support = Read(R_SUPPORT_7A4);
        if ((support & 0x80) == 0)
        {
            Console.Error.WriteLine($"refused: 0x07A4=0x{support:X2} does not declare liquid cooling.");
            return 5;
        }

        // Optional gate: wait for a stock preset (custom profile off, power gate clear)
        // so the firmware's own preset record is the one in use and its change is
        // measurable. 0x0727 bit6 = custom profile, 0x0726 bit7 = custom power gate.
        if (argsWaitStock)
        {
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while ((Read(R_CUSTOM_PROFILE_727) & 0x40) != 0 || (Read(R_PWR_MGMT_726) & 0x80) != 0)
            {
                if (wait.Elapsed.TotalSeconds > waitSeconds)
                {
                    Console.Error.WriteLine($"no stock preset within {waitSeconds} s " +
                        $"(0x0727=0x{Read(R_CUSTOM_PROFILE_727):X2} 0x0726=0x{Read(R_PWR_MGMT_726):X2}); nothing written.");
                    return 6;
                }
                Thread.Sleep(2000);
            }
            Console.WriteLine($"stock preset detected after {wait.Elapsed.TotalSeconds:F0} s " +
                $"(0x0727=0x{Read(R_CUSTOM_PROFILE_727):X2} 0x0726=0x{Read(R_PWR_MGMT_726):X2})");
        }

        int previous = Read(R_COOLING_MODE_7C7);
        int desired = attached ? (previous | 0x02) : (previous & ~0x02);
        if (desired == previous)
        {
            Console.WriteLine($"already {(attached ? "set" : "clear")} (0x07C7=0x{previous:X2}); nothing written.");
            PrintCoolerState("state");
            return 0;
        }

        PrintCoolerState("before");
        Console.WriteLine($"about to write 0x07C7 = 0x{desired:X2} (bit 1 {(attached ? "set" : "cleared")}; OCPL bits preserved)");
        if (!confirmed)
        {
            Console.Write("type 'yes' to write: ");
            if (Console.ReadLine()?.Trim() != "yes")
            {
                Console.WriteLine("aborted.");
                return 1;
            }
        }

        if (!Write(R_COOLING_MODE_7C7, (byte)desired))
        {
            Console.Error.WriteLine($"write failed (Win32 {Marshal.GetLastWin32Error()}).");
            return 3;
        }

        Thread.Sleep(2000);
        PrintCoolerState("after");
        Console.WriteLine();
        Console.WriteLine("The EC republishes its preset records within ~1 s. Compare 0x07A7/0x0783/0x0784 above;");
        Console.WriteLine("revert with: EcProbe --allow-write cooler " + (attached ? "off" : "on"));
        return 0;
    }

    // ---- the single write path (Test A) ------------------------------------

    private static int Ramp(string argument, bool confirmed)
    {
        int encoding;
        string meaning;
        if (argument.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            encoding = 0x00;
            meaning = "disabled";
        }
        else if (int.TryParse(argument, out int ms) && ms is >= 100 and <= 12700 && ms % 100 == 0)
        {
            encoding = 0x80 | (ms / 100);
            meaning = $"enabled, {ms} ms";
        }
        else
        {
            Console.Error.WriteLine("ramp takes 'off' or a whole number of ms, 100..12700 in steps of 100.");
            return 2;
        }

        int before = Read(R_FAN_SWITCH_SPEED);
        Console.WriteLine($"0x0787 before : 0x{before:X2}" +
            (before < 0 ? "" : (before & 0x80) != 0
                ? $" (enabled, {(before & 0x7F) * 100} ms)"
                : " (disabled)"));
        Console.WriteLine($"about to write 0x0787 = 0x{encoding:X2} ({meaning})");
        if (!confirmed)
        {
            Console.Write("type 'yes' to write: ");
            if (Console.ReadLine()?.Trim() != "yes")
            {
                Console.WriteLine("aborted.");
                return 1;
            }
        }

        if (!Write(R_FAN_SWITCH_SPEED, (byte)encoding))
        {
            Console.Error.WriteLine($"write failed (Win32 {Marshal.GetLastWin32Error()}).");
            return 3;
        }
        int after = Read(R_FAN_SWITCH_SPEED);
        int budget = Read(R_THERMAL_BUDGET_788);
        Console.WriteLine($"0x0787 after  : 0x{after:X2}" +
            (after < 0 ? "" : (after & 0x80) != 0
                ? $" (enabled, {(after & 0x7F) * 100} ms)"
                : " (disabled)"));
        // The whole point of the test: a u16 writer would have touched this.
        Console.WriteLine($"0x0788 now    : 0x{budget:X2}  (must be unchanged)");
        Console.WriteLine();
        Console.WriteLine("Now change preset/curve so the fan must step, and watch the ramp.");
        Console.WriteLine("Revert by re-applying any preset, or: EcProbe --allow-write ramp off");
        return 0;
    }

    private static int Main(string[] args)
    {
        bool allowWrite = args.Contains("--allow-write");
        bool assumeYes = args.Contains("--yes");
        string command = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "facts";

        if (command == "help" || args.Contains("--help"))
        {
            Console.WriteLine("EcProbe facts | snapshot [--out f] | trace [--seconds N] [--interval MS] [--out f]");
            Console.WriteLine("        --allow-write ramp <ms|off> [--yes]");
            Console.WriteLine("        --allow-write cooler <status|on|off> [--yes]   (0x07C7 bit 1)");
            return 0;
        }

        foreach (string proc in new[] { "Uniwill Control Utility", "Uniwill Control Utility Engine" })
            if (Process.GetProcessesByName(proc).Length > 0)
                Console.WriteLine($"warning: {proc} is running; close it so the EC is quiet.\n");

        if (!Open())
            return 3;

        try
        {
            switch (command)
            {
                case "facts":
                    Facts();
                    return 0;
                case "snapshot":
                    Snapshot(Arg(args, "--out"));
                    return 0;
                case "trace":
                    Trace(
                        int.TryParse(Arg(args, "--seconds"), out int s) ? s : 60,
                        int.TryParse(Arg(args, "--interval"), out int i) ? i : 500,
                        Arg(args, "--out"));
                    return 0;
                case "cooler":
                    string coolerArg = args.SkipWhile(a => a != "cooler").Skip(1)
                        .FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "";
                    return Cooler(coolerArg, allowWrite, assumeYes, args.Contains("--wait-stock"), int.TryParse(Arg(args, "--timeout"), out int wt) ? wt : 300);
                case "ramp":
                    if (!allowWrite)
                    {
                        Console.Error.WriteLine("ramp writes 0x0787. Pass --allow-write to proceed.");
                        return 4;
                    }
                    string value = args.SkipWhile(a => a != "ramp").Skip(1)
                        .FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "";
                    return Ramp(value, assumeYes);
                default:
                    Console.Error.WriteLine($"unknown command '{command}'. Try --help.");
                    return 2;
            }
        }
        finally
        {
            if (_handle != new nint(-1))
                CloseHandle(_handle);
        }
    }

    private static string? Arg(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
