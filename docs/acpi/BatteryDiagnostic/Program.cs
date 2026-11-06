using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// READ-ONLY battery-control EC probe (U-103 field verification).
///
/// Reads the battery charge-control register family directly from the Uniwill
/// EC via UWACPIDriver.sys — no writes, nothing is modified:
///
///   0x07A6  BATTERY_PROTECTION_MODE — the encoding-divergence crux:
///           UCU chassis family: mode field = bits 3-5 (mask 0x38), values
///           0x08/0x18/0x28 = 100/80/60 %. Open-revo chassis family: mode
///           values 0x01/0x11/0x21 (LONGLIFE/BALANCE/STATION) with bit 3 as
///           TOUCHPAD_BITMASK — the two families claim the SAME bit 3 for
///           different things, which is why per-chassis verification is the
///           gate for enabling anything beyond UCU's legacy mode UI.
///   0x07C9  BATTERY_LIMIT_UP    — charge-up threshold, percent (open-revo
///                                  reads it masked & 0x7F)
///   0x07D0  BATTERY_LIMIT_DOWN  — charge-down threshold, percent
///   0x0770  BATTERY_PLATFORM_OVERRIDE — 5 = pinned, 0xFF = reset
///   0x0742  SUPPORT_5 / GATE_STATUS — bit 4: UCU decodes FAN_TURBO_SUPPORTED,
///                                  open-revo registry names it GATE_ACTIVE_BIT
///   0x0765/0x0766  SUPPORT_1/SUPPORT_2 — capability bits (RGB bit on 0x0766)
///
/// Plausibility cross-checks ride along: PROJECT_ID (0x0740), SOC (0x04AB),
/// power source (0x0490), design/full-charge capacity (0x0402/0x0404), and a
/// neighborhood scan 0x0760-0x077F / 0x07C0-0x07DF so the threshold pair's
/// layout is visible in context.
///
/// Run as Administrator (the driver device admits the elevated token).
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

    static int Main(string[] args)
    {
        Console.Title = "EC Battery Diagnostic (read-only)";
        Console.WriteLine("EC Battery Control Diagnostic — READ ONLY, single pass.\n");

        nint handle = OpenDriver();
        if (handle == InvalidHandle)
            return 1;

        try
        {
            byte projId = ReadByte(handle, 0x0740, "PROJECT_ID");
            Console.WriteLine($"  PROJECT_ID:            0x{projId:X2} ({projId})");
            Console.WriteLine();

            // --- the mode register, decoded for BOTH chassis families ------------
            byte mode = ReadByte(handle, 0x07A6, "BATTERY_PROTECTION_MODE");
            Console.WriteLine($"  BATTERY_PROTECTION_MODE 0x{mode:X4} 0x{mode:X2}");
            Console.WriteLine($"    UCU family   (field bits 3-5, mask 0x38):");
            int ucuField = mode & 0x38;
            string ucuMode = ucuField switch
            {
                0x08 => "High Capacity (100 %)",
                0x18 => "Balanced (80 %)",
                0x28 => "Stationary (60 %)",
                _ => $"unrecognised field 0x{ucuField:X2} (codec falls back to 100 %)",
            };
            Console.WriteLine($"      field 0x{ucuField:X2} -> {ucuMode}");
            Console.WriteLine($"    open-revo family (modes 0x01/0x11/0x21, bit 3 = touchpad):");
            string orMode = mode switch
            {
                0x01 => "LONGLIFE",
                0x11 => "BALANCE",
                0x21 => "STATION",
                _ => $"no exact mode-byte match (low bits 0x{mode & 0x07:X2}, bit4 {((mode & 0x10) != 0 ? "set" : "clear")}, bit5 {((mode & 0x20) != 0 ? "set" : "clear")})",
            };
            Console.WriteLine($"      raw 0x{mode:X2} -> {orMode}");
            Console.WriteLine($"    bit 3 (touchpad bit in the open-revo family): {((mode & 0x08) != 0 ? "SET" : "clear")}");
            Console.WriteLine();

            // --- threshold pair + override + gate --------------------------------
            byte limitUp = ReadByte(handle, 0x07C9, "BATTERY_LIMIT_UP");
            byte limitDown = ReadByte(handle, 0x07D0, "BATTERY_LIMIT_DOWN");
            Console.WriteLine($"  BATTERY_LIMIT_UP       0x07C9 = {limitUp} (0x{limitUp:X2}), masked &0x7F = {limitUp & 0x7F}");
            Console.WriteLine($"  BATTERY_LIMIT_DOWN     0x07D0 = {limitDown} (0x{limitDown:X2})");
            Console.WriteLine($"    pair sane (60..100, down < up): {(limitUp is >= 60 and <= 100 && limitDown is >= 60 and <= 100 && limitDown < limitUp ? "YES" : "no — registry semantics do NOT describe this chassis")}");

            byte overrideVal = ReadByte(handle, 0x0770, "PLATFORM_OVERRIDE");
            Console.WriteLine($"  PLATFORM_OVERRIDE      0x0770 = {overrideVal} (0x{overrideVal:X2})  [5 = pinned, 0xFF = reset]");

            byte support5 = ReadByte(handle, 0x0742, "SUPPORT_5/GATE");
            Console.WriteLine($"  SUPPORT_5/GATE_STATUS  0x0742 = 0x{support5:X2}  bit4 (UCU turbo / open-revo gate) {((support5 & 0x10) != 0 ? "SET" : "clear")}");

            byte support1 = ReadByte(handle, 0x0765, "SUPPORT_1");
            byte support2 = ReadByte(handle, 0x0766, "SUPPORT_2");
            Console.WriteLine($"  SUPPORT_1              0x0765 = 0x{support1:X2}");
            Console.WriteLine($"  SUPPORT_2              0x0766 = 0x{support2:X2}  RGB keyboard bit2 {((support2 & 0x04) != 0 ? "SET" : "clear")}");
            Console.WriteLine();

            // --- plausibility context --------------------------------------------
            int soc = ReadByte(handle, 0x04AB, "BATTERY_SOC");
            byte powerSource = ReadByte(handle, 0x0490, "POWER_SOURCE");
            int designCap = ReadUInt16(handle, 0x0402);
            int fullCap = ReadUInt16(handle, 0x0404);
            Console.WriteLine($"  SOC 0x04AB = {soc} %, POWER_SOURCE 0x0490 = 0x{powerSource:X2} (AC: {(powerSource & 1) != 0})");
            Console.WriteLine($"  Design {designCap} mAh / Full-charge {fullCap} mAh (health {(designCap > 0 ? (100.0 * fullCap / designCap).ToString("F1") : "?")} %)");
            Console.WriteLine();

            // --- neighborhood scans (layout context for the threshold pair) ------
            Console.WriteLine("  Scan 0x0760-0x077F:");
            Scan(handle, 0x0760, 0x077F);
            Console.WriteLine("  Scan 0x07C0-0x07DF:");
            Scan(handle, 0x07C0, 0x07DF);

            Console.WriteLine();
            Console.WriteLine("No writes performed.");
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
            Console.WriteLine("Run as Administrator; UWACPIDriver.sys must be loaded.");
        }

        return handle;
    }

    private static void Scan(nint handle, ushort from, ushort to)
    {
        for (ushort a = from; a <= to; a += 8)
        {
            Console.Write($"    0x{a:X4}:");
            for (int i = 0; i < 8; i++)
            {
                byte v = ReadByte(handle, (ushort)(a + i), "?");
                Console.Write($" {v:X2}");
            }
            Console.WriteLine();
        }
    }

    private static int ReadUInt16(nint handle, ushort address)
    {
        int lo = ReadByte(handle, address, "?");
        int hi = ReadByte(handle, (ushort)(address + 1), "?");
        return lo | (hi << 8);
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
            Console.WriteLine($"  {name,-25} 0x{address:X4} = FAILED (error {err})");
            return 0xFF; // Sentinel for failed reads
        }

        Thread.Sleep(6); // EC delay
        return value;
    }
}
