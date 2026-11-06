using System;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// Win-lock trigger semantics follow-up (after SwitchDiagnostic left the
/// status byte 0x0768 bit0 SET: fire #1 locked, fire #2 appeared ignored).
///
/// Sequence (each step logs every 0x0768 transition):
///   1. read 0x0768/0x0767/0x074E.
///   2. fire trigger (0x0767 |= 0x01), poll 0x0768 for up to 15 s.
///   3. if the status byte is STILL locked: fire (0x0767 = 0x01 full-byte
///      write), poll 15 s; then write 0x0767 = 0x02 (LIGHTBAR lineage bit,
///      NOT winlock), poll; then write 0x0767 = 0x00, poll 5 s.
///   4. final state report. Goal: end with the win-key UNLOCKED.
///
/// Elevated. Read + fire only; no other register is written.
/// </summary>
internal static class Program
{
    private const string AcpiDriverDevice = @"\\.\ACPIDriver";
    private const uint IOCTL_ECREAD = 0x9C40A488;
    private const uint IOCTL_ECWRITE = 0x9C40A48C;
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

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    private static extern bool DeviceIoControlWrite(
        nint hDevice, uint dwIoControlCode,
        int[] lpInBuffer, int nInBufferSize,
        ref int lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, nint lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);

    static int Main()
    {
        Console.WriteLine("Win-lock trigger follow-up.\n");

        nint handle = CreateFileW(
            AcpiDriverDevice, GENERIC_READ_WRITE, FILE_SHARE_RW,
            nint.Zero, OPEN_EXISTING, 0, nint.Zero);

        if (handle == InvalidHandle)
        {
            Console.WriteLine($"[FAIL] Cannot open {AcpiDriverDevice} (error {Marshal.GetLastWin32Error()})");
            return 1;
        }

        try
        {
            byte status = ReadByte(handle, 0x0768);
            Console.WriteLine($"start: 0x0768=0x{status:X2} 0x0767=0x{ReadByte(handle, 0x0767):X2} 0x074E=0x{ReadByte(handle, 0x074E):X2} locked={(status & 1) != 0}");

            // Step 2: standard RMW fire.
            byte trig = ReadByte(handle, 0x0767);
            WriteByte(handle, 0x0767, (byte)(trig | 0x01));
            bool flipped = Poll(handle, "fire RMW (0x0767|=0x01)", seconds: 15);
            Console.WriteLine($"  flip observed: {flipped}");

            status = ReadByte(handle, 0x0768);
            if ((status & 1) != 0)
            {
                // Step 3: full-byte 0x01 write fire.
                WriteByte(handle, 0x0767, 0x01);
                bool flipped2 = Poll(handle, "fire FULL (0x0767=0x01)", seconds: 15);
                Console.WriteLine($"  flip observed: {flipped2}");

                status = ReadByte(handle, 0x0768);
                if ((status & 1) != 0)
                {
                    Console.WriteLine("  still locked; writing 0x0767=0x00 and polling 5s");
                    WriteByte(handle, 0x0767, 0x00);
                    Poll(handle, "clear 0x0767=0x00", seconds: 5);
                }
            }

            byte final = ReadByte(handle, 0x0768);
            Console.WriteLine($"\nfinal: 0x0768=0x{final:X2} locked={(final & 1) != 0}");
            Console.WriteLine(((final & 1) == 0) ? "OUTCOME: win-key UNLOCKED." : "OUTCOME: STILL LOCKED - physical Fn+Win press needed.");
        }
        finally
        {
            CloseHandle(handle);
        }

        return 0;
    }

    private static bool Poll(nint handle, string label, int seconds)
    {
        byte start = ReadByte(handle, 0x0768);
        bool startLocked = (start & 1) != 0;
        Console.WriteLine($"  [{label}] start 0x0768=0x{start:X2}");

        for (int s = 0; s < seconds; s++)
        {
            for (int i = 0; i < 4; i++)
            {
                Thread.Sleep(250);
                byte v = ReadByte(handle, 0x0768);
                if ((v & 1) != (startLocked ? 1 : 0))
                {
                    Console.WriteLine($"  [{label}] t+{s}.{i / 4 + 1}s 0x0768=0x{v:X2} FLIPPED (locked now: {(v & 1) != 0})");
                    return true;
                }
            }

            byte v2 = ReadByte(handle, 0x0768);
            Console.WriteLine($"  [{label}] +{s + 1}s 0x0768=0x{v2:X2}");
        }

        return false;
    }

    private static byte ReadByte(nint handle, ushort address)
    {
        int lpIn = address;
        int lpOut = 0;
        if (!DeviceIoControl(handle, IOCTL_ECREAD, ref lpIn, 4, ref lpOut, 4, out _, nint.Zero))
        {
            Console.WriteLine($"  [FAIL] read 0x{address:X4}: error {Marshal.GetLastWin32Error()}");
            return 0;
        }

        return (byte)(lpOut & 0xFF);
    }

    private static void WriteByte(nint handle, ushort address, byte value)
    {
        int[] inBuf = { address, value };
        int lpOut = 0;
        if (!DeviceIoControlWrite(handle, IOCTL_ECWRITE, inBuf, 8, ref lpOut, 4, out _, nint.Zero))
            Console.WriteLine($"  [FAIL] write 0x{address:X4}=0x{value:X2}: error {Marshal.GetLastWin32Error()}");
    }
}
