using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// Standalone EC fan diagnostic tool.
/// Reads fan RPM, PWM duty, fan control mode, and curve table state directly
/// from the Uniwill EC via UWACPIDriver.sys — independent of UCU's telemetry
/// caching layer.
///
/// Use this when the UCU dashboard shows "-" for fan speeds to determine
/// whether the EC is still providing data or if the driver handle has failed.
/// </summary>
internal static class Program
{
    #region Driver P/Invoke

    private const string AcpiDriverDevice = @"\\.\ACPIDriver";
    private const uint IOCTL_ECREAD = 0x9C40A488;
    private const uint GENERIC_READ_WRITE = 0xC0000000;
    private const uint FILE_SHARE_RW = 3;
    private const uint OPEN_EXISTING = 3;
    private static readonly nint InvalidHandle = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        nint lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        nint hDevice, uint dwIoControlCode,
        ref int lpInBuffer, int nInBufferSize,
        ref int lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, nint lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    #endregion

    #region EC Register Addresses

    // Fan RPM
    private const ushort REG_MAIN_FAN_RPM_LO = 0x0464;
    private const ushort REG_MAIN_FAN_RPM_HI = 0x0465;
    private const ushort REG_SECOND_FAN_RPM_LO = 0x046C;
    private const ushort REG_SECOND_FAN_RPM_HI = 0x046B;

    // PWM readback (0-200 scale)
    private const ushort REG_PWM_1 = 0x075B;
    private const ushort REG_PWM_2 = 0x075C;

    // PWM writeable (for comparison)
    private const ushort REG_PWM_1_WRITEABLE = 0x1804;
    private const ushort REG_PWM_2_WRITEABLE = 0x1809;

    // Fan control
    private const ushort REG_MANUAL_FAN_CTRL = 0x0751;
    private const ushort REG_UNIVERSAL_FAN_CTRL = 0x07C5;
    private const ushort REG_AP_OEM_6 = 0x07C6;
    private const ushort REG_FAN_SWITCH_SPEED = 0x0787;

    // Fan curve tables
    private const ushort REG_CPU_FAN_SPEED_TABLE = 0x0F20;
    private const ushort REG_GPU_FAN_SPEED_TABLE = 0x0F50;

    // Temperatures
    private const ushort REG_CPU_TEMP = 0x043E;
    private const ushort REG_GPU_TEMP = 0x044F;

    // Project ID (sanity check)
    private const ushort REG_PROJECT_ID = 0x0740;

    #endregion

    static int Main(string[] args)
    {
        bool continuous = args.Length > 0 && args[0] == "--watch";
        int watchIntervalMs = 2000;
        if (args.Length > 1 && int.TryParse(args[1], out int interval))
            watchIntervalMs = interval * 1000;

        Console.Title = "EC Fan Diagnostic";

        if (continuous)
            Console.WriteLine($"EC Fan Diagnostic — continuous mode (every {watchIntervalMs / 1000}s). Press Ctrl+C to stop.\n");
        else
            Console.WriteLine("EC Fan Diagnostic — single read.\n");

        nint handle = OpenDriver();
        if (handle == InvalidHandle)
            return 1;

        try
        {
            do
            {
                TryRead(handle, continuous);
                if (!Debugger.IsAttached)
                    Thread.Sleep(watchIntervalMs);
            }
            while (continuous);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C
        }
        finally
        {
            CloseHandle(handle);
        }

        return 0;
    }

    private static nint OpenDriver()
    {
        nint handle = CreateFileW(
            AcpiDriverDevice, GENERIC_READ_WRITE, FILE_SHARE_RW,
            nint.Zero, OPEN_EXISTING, 0, nint.Zero);

        if (handle == InvalidHandle)
        {
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine($"[FAIL] Cannot open {AcpiDriverDevice} (Windows error {err})");
            Console.WriteLine();
            Console.WriteLine("Possible causes:");
            Console.WriteLine("  - Not running as Administrator");
            Console.WriteLine("  - UWACPIDriver.sys not installed");
            Console.WriteLine("  - Driver service not running");
            Console.WriteLine();
            Console.WriteLine("Fix: Install XMG Control Center (or your OEM's control center),");
            Console.WriteLine("     then you may uninstall it. The driver persists on disk.");
            Console.WriteLine("     Run this tool as Administrator.");
        }

        return handle;
    }

    private static void TryRead(nint handle, bool continuous)
    {
        if (!continuous)
            Console.WriteLine("=== EC Fan Status ===");
        else
            Console.WriteLine($"--- {DateTime.Now:HH:mm:ss} ---");

        // Sanity check — PROJECT_ID
        byte projId = ReadByte(handle, REG_PROJECT_ID, "PROJECT_ID");
        Console.WriteLine($"  PROJECT_ID:          0x{projId:X2} ({projId})");

        // Temperatures
        int cpuTemp = ReadByte(handle, REG_CPU_TEMP, "CPU_TEMP");
        int gpuTemp = ReadByte(handle, REG_GPU_TEMP, "GPU_TEMP");
        Console.WriteLine($"  CPU Temperature:     {cpuTemp}°C");
        Console.WriteLine($"  GPU Temperature:     {gpuTemp}°C");
        Console.WriteLine();

        // Fan RPM
        int mainRpm = ReadFanRpm(handle, REG_MAIN_FAN_RPM_LO, REG_MAIN_FAN_RPM_HI, "Main");
        int secondRpm = ReadFanRpm(handle, REG_SECOND_FAN_RPM_LO, REG_SECOND_FAN_RPM_HI, "Second");
        Console.WriteLine($"  Main Fan RPM:        {FormatRpm(mainRpm)}");
        Console.WriteLine($"  Second Fan RPM:      {FormatRpm(secondRpm)}");
        Console.WriteLine();

        // PWM duty readback (0-200 scale)
        int pwm1 = ReadByte(handle, REG_PWM_1, "PWM_1");
        int pwm2 = ReadByte(handle, REG_PWM_2, "PWM_2");
        int pwm1w = ReadByte(handle, REG_PWM_1_WRITEABLE, "PWM_1_WRITEABLE");
        int pwm2w = ReadByte(handle, REG_PWM_2_WRITEABLE, "PWM_2_WRITEABLE");

        int pwm1Percent = (int)Math.Round(pwm1 * 100.0 / 200.0);
        int pwm2Percent = (int)Math.Round(pwm2 * 100.0 / 200.0);

        Console.WriteLine($"  CPU Fan PWM duty:    {pwm1}/200  ({pwm1Percent}%)  [readback 0x075B]");
        Console.WriteLine($"  GPU Fan PWM duty:    {pwm2}/200  ({pwm2Percent}%)  [readback 0x075C]");
        Console.WriteLine($"  CPU Fan PWM write:   {pwm1w}/200  [writeable 0x1804]");
        Console.WriteLine($"  GPU Fan PWM write:   {pwm2w}/200  [writeable 0x1809]");
        Console.WriteLine();

        // Fan control mode
        byte fanCtrl = ReadByte(handle, REG_MANUAL_FAN_CTRL, "MANUAL_FAN_CTRL");
        byte ufCtrl = ReadByte(handle, REG_UNIVERSAL_FAN_CTRL, "UNIVERSAL_FAN_CTRL");
        byte apOem6 = ReadByte(handle, REG_AP_OEM_6, "AP_OEM_6");

        Console.WriteLine($"  MANUAL_FAN_CTRL:     0x{fanCtrl:X2}");
        PrintFanMode(fanCtrl);
        Console.WriteLine($"  UNIVERSAL_FAN_CTRL:  0x{ufCtrl:X2}  (split tables: {((ufCtrl & 0x80) != 0 ? "yes" : "no")})");
        Console.WriteLine($"  AP_OEM_6:            0x{apOem6:X2}  (universal fan enabled: {((apOem6 & 0x04) != 0 ? "yes" : "no")})");
        Console.WriteLine();

        // Fan curve table summary
        Console.WriteLine("  CPU Fan Curve (0x0F20):");
        PrintFanCurve(handle, REG_CPU_FAN_SPEED_TABLE, "    ");
        Console.WriteLine("  GPU Fan Curve (0x0F50):");
        PrintFanCurve(handle, REG_GPU_FAN_SPEED_TABLE, "    ");
        Console.WriteLine();

        // Diagnostics
        Console.WriteLine("  --- Diagnostics ---");
        bool pwmPlausible = (pwm1 >= 0 && pwm1 <= 200) && (pwm2 >= 0 && pwm2 <= 200);
        bool rpmPlausible = mainRpm > 0 || secondRpm > 0;
        bool pwmAboveMin = pwm1 >= 50 || pwm2 >= 50; // 25% minimum = 50/200

        Console.WriteLine($"  PWM values plausible:  {pwmPlausible}");
        Console.WriteLine($"  RPM > 0 detected:      {rpmPlausible}");
        Console.WriteLine($"  PWM above 25% minimum: {pwmAboveMin}");

        if (!pwmPlausible)
            Console.WriteLine("  !! WARNING: PWM values out of 0-200 range — EC may be in an unexpected state");
        if (pwm1 < 50 && pwm2 < 50 && pwmPlausible)
            Console.WriteLine("  !! WARNING: Both fans below 25% minimum — EC fan control may be disabled");
        if (mainRpm == 0 && secondRpm == 0)
            Console.WriteLine("  !! WARNING: Both fans report 0 RPM — fans stopped or EC not reporting");

        Console.WriteLine();
    }

    private static byte ReadByte(nint handle, ushort address, string name)
    {
        int lpIn = address;
        int lpOut = 0;
        bool ok = DeviceIoControl(
            handle, IOCTL_ECREAD,
            ref lpIn, 4, ref lpOut, 4,
            out _, nint.Zero);

        byte value = (byte)(lpOut & 0xFF);

        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine($"  {name,-25} 0x{address:X3} = FAILED (error {err})");
            return 0xFF; // Sentinel for failed reads
        }

        Thread.Sleep(6); // EC delay
        return value;
    }

    private static int ReadFanRpm(nint handle, ushort loAddr, ushort hiAddr, string label)
    {
        // Note: main fan has swapped byte order (0x0464 is high byte, 0x0465 is low byte)
        byte byte1 = ReadByteDirect(handle, loAddr);
        byte byte2 = ReadByteDirect(handle, hiAddr);

        // For main fan: 0x0464 is high byte, 0x0465 is low byte (big-endian with swapped register names)
        // For second fan: 0x046C is low byte, 0x046B is high byte
        bool isMainFan = loAddr == REG_MAIN_FAN_RPM_LO;
        int rpm = isMainFan
            ? ((byte1 << 8) | byte2)
            : ((byte2 << 8) | byte1);

        return rpm;
    }

    private static byte ReadByteDirect(nint handle, ushort address)
    {
        int lpIn = address;
        int lpOut = 0;
        bool ok = DeviceIoControl(
            handle, IOCTL_ECREAD,
            ref lpIn, 4, ref lpOut, 4,
            out _, nint.Zero);

        byte value = (byte)(lpOut & 0xFF);
        if (!ok)
            return 0xFF;
        Thread.Sleep(6);
        return value;
    }

    private static string FormatRpm(int rpm)
    {
        if (rpm == 0xFFFF || rpm == 0)
            return rpm == 0 ? "0 RPM (fan stopped or not reporting)" : $"{rpm} RPM (0xFFFF — invalid)";
        if (rpm > 12700)
            return $"{rpm} RPM (unusually high — possible register misread)";
        return $"{rpm} RPM";
    }

    private static void PrintFanMode(byte fanCtrl)
    {
        if ((fanCtrl & 0x80) != 0) Console.WriteLine("    Mode: USER (custom curve)");
        if ((fanCtrl & 0x40) != 0) Console.WriteLine("    Mode: BOOST");
        if ((fanCtrl & 0x20) != 0) Console.WriteLine("    Mode: HIGH");
        if ((fanCtrl & 0x10) != 0) Console.WriteLine("    Mode: TURBO");

        int level = fanCtrl & 0x07;
        Console.WriteLine($"    Fan level: {level}");
    }

    private static void PrintFanCurve(nint handle, ushort startAddr, string prefix)
    {
        var values = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            values[i] = ReadByteDirect(handle, (ushort)(startAddr + i));
        }

        // Build a simple bar chart
        string line = prefix + "Zone ";
        string barLine = prefix + "      ";
        foreach (byte v in values)
        {
            int percent = (int)Math.Round(v * 100.0 / 200.0);
            line += $"{percent,3}  ";
            int bars = Math.Clamp(percent / 2, 0, 25);
            barLine += new string('#', bars) + new string(' ', 25 - bars) + " ";
        }
        Console.WriteLine(line);
        Console.WriteLine(barLine);
    }
}
