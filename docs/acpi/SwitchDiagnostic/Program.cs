using System;
using System.Runtime.InteropServices;
using System.Threading;

/// <summary>
/// Field probe for the U-108 device switches (Win-key lock, Fn-lock, AC
/// recovery) on the lab box. Reversible by construction: every write is a
/// bit-scoped read-modify-write of ONE bit that is toggled and then restored
/// to the byte read before the test, so the EC ends in the state it was in.
///
/// Targets (open-revo registry facts to verify on THIS chassis family):
///
///   0x0726 AC_RECOVERY — bit 3 (mask 0x08) "turn on when AC is restored".
///        The register's bit 7 is UCU's power-management gate: the toggle
///        must preserve every other bit (the handshake coexistence rule,
///        acceptance b).
///   0x074E FN_LOCK — bit 4 (0x10) locked / 0x01 unlocked (open-revo
///        wmi_ec.rs). Toggle + restore; observe whether the firmware
///        consumes/echoes the bit.
///   0x0767 TRIGGER_BYTE / 0x0768 STATUS_BYTE — STATUS_WINLOCK = bit 0
///        (read the lock state), TRIGGER_WINLOCK = 1 (write 0x0767 | 0x01
///        to fire the toggle; the firmware consumes the trigger bit).
///        Two fires restore the original lock state.
///   0x0765/0x0766 SUPPORT_1/SUPPORT_2 — the capability lineage the story
///        names for gating (open-revo registry 1893/1894).
///
/// Run as Administrator (the driver device admits the elevated token).
/// Single pass, no interactive input.
/// </summary>
internal static class Program
{
    #region Driver P/Invoke

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

    #endregion

    static int Main(string[] args)
    {
        Console.Title = "EC Switch Diagnostic (U-108, reversible toggles)";
        Console.WriteLine("EC device-switch probe — bit-scoped toggle + restore, single pass.\n");

        nint handle = OpenDriver();
        if (handle == InvalidHandle)
            return 1;

        try
        {
            byte projId = ReadByte(handle, 0x0740, "PROJECT_ID");
            Console.WriteLine($"  PROJECT_ID: 0x{projId:X2} ({projId})");
            Console.WriteLine();

            byte support1 = ReadByte(handle, 0x0765, "SUPPORT_1");
            byte support2 = ReadByte(handle, 0x0766, "SUPPORT_2");
            Console.WriteLine($"  SUPPORT_1 (0x0765): 0x{support1:X2}");
            Console.WriteLine($"  SUPPORT_2 (0x0766): 0x{support2:X2}");
            Console.WriteLine();

            Console.WriteLine("  Scan 0x0720-0x072F:");
            Scan(handle, 0x0720, 0x072F);
            Console.WriteLine("  Scan 0x0740-0x074F:");
            Scan(handle, 0x0740, 0x074F);
            Console.WriteLine("  Scan 0x0760-0x076F:");
            Scan(handle, 0x0760, 0x076F);
            Console.WriteLine();

            // --- Test A: AC recovery (0x0726 bit 3), bit-7 coexistence ----------
            ToggleBit(handle, "AC_RECOVERY", 0x0726, 0x08,
                protectMask: 0x80, protectName: "power-mgmt bit7");

            // --- Test B: Fn lock (0x074E bit 4) ---------------------------------
            ToggleBit(handle, "FN_LOCK", 0x074E, 0x10,
                protectMask: 0, protectName: null);

            // --- Test C: Win-key lock (status 0x0768 bit 0, trigger 0x0767) -----
            WinLockTest(handle);

            Console.WriteLine();
            Console.WriteLine("All tests restore the byte they read. If any 'restored' line FAILs,");
            Console.WriteLine("re-apply the shown original byte manually before continuing.");
        }
        finally
        {
            CloseHandle(handle);
        }

        return 0;
    }

    private static void ToggleBit(nint handle, string name, ushort address, byte mask,
        byte protectMask, string? protectName)
    {
        Console.WriteLine($"--- {name}: toggle 0x{address:X4} bit mask 0x{mask:X2} ---");

        byte original = ReadByte(handle, address, name);
        byte flipped = (byte)(original ^ mask);
        Console.WriteLine($"  read:    0x{original:X2}  (bit set: {((original & mask) != 0)})");

        WriteByte(handle, address, flipped);
        Thread.Sleep(120);
        byte after = ReadByte(handle, address, name);

        bool bitMoved = (after & mask) == (flipped & mask);
        bool otherBits = (after & ~mask) == (original & ~mask);
        Console.WriteLine($"  after:   0x{after:X2}  bit moved: {(bitMoved ? "YES" : "NO (firmware owns the bit?)")}  other bits preserved: {(otherBits ? "YES" : "NO")}");

        if (protectMask != 0 && protectName is not null)
        {
            bool protected_ = (after & protectMask) == (original & protectMask);
            Console.WriteLine($"  {protectName} preserved: {(protected_ ? "YES" : "NO")}");
        }

        WriteByte(handle, address, original);
        Thread.Sleep(120);
        byte restored = ReadByte(handle, address, name);
        bool restoredOk = restored == original;
        Console.WriteLine($"  restored: 0x{restored:X2} == original 0x{original:X2}: {(restoredOk ? "PASS" : "FAIL")}");
        Console.WriteLine();
    }

    private static void WinLockTest(nint handle)
    {
        Console.WriteLine("--- WIN_LOCK: status 0x0768 bit 0, trigger 0x0767 |= 0x01 ---");

        byte trigger0 = ReadByte(handle, 0x0767, "TRIGGER_BYTE");
        byte status0 = ReadByte(handle, 0x0768, "STATUS_BYTE");
        bool locked0 = (status0 & 0x01) != 0;
        Console.WriteLine($"  trigger: 0x{trigger0:X2}  status: 0x{status0:X2}  win-locked: {locked0}");

        WriteByte(handle, 0x0767, (byte)(trigger0 | 0x01));
        byte status1 = ReadStatusAfterTrigger(handle, locked0);
        bool locked1 = (status1 & 0x01) != 0;
        Console.WriteLine($"  after fire #1: status 0x{status1:X2}  win-locked: {locked1}  flipped: {(locked1 != locked0 ? "YES" : "NO (trigger ignored?)")}");

        if (locked1 != locked0)
        {
            byte trigger1 = ReadByte(handle, 0x0767, "TRIGGER_BYTE");
            WriteByte(handle, 0x0767, (byte)(trigger1 | 0x01));
            byte status2 = ReadStatusAfterTrigger(handle, locked1);
            bool locked2 = (status2 & 0x01) != 0;
            Console.WriteLine($"  after fire #2: status 0x{status2:X2}  win-locked: {locked2}  restored: {(locked2 == locked0 ? "PASS" : "FAIL")}");
        }

        byte triggerEnd = ReadByte(handle, 0x0767, "TRIGGER_BYTE");
        Console.WriteLine($"  trigger byte at end: 0x{triggerEnd:X2} (bit0 self-consumed: {((triggerEnd & 0x01) == 0 ? "YES" : "NO")})");
        Console.WriteLine();
    }

    private static byte ReadStatusAfterTrigger(nint handle, bool expectFlip)
    {
        // The firmware debounce: poll 0x0768 for up to ~2.4 s for the flip.
        byte last = ReadByte(handle, 0x0768, "STATUS_BYTE");
        for (int i = 0; i < 12; i++)
        {
            Thread.Sleep(200);
            last = ReadByte(handle, 0x0768, "STATUS_BYTE");
            if (((last & 0x01) != 0) != expectFlip)
                break;
        }

        return last;
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

    private static byte ReadByte(nint handle, ushort address, string name)
    {
        int lpIn = address;
        int lpOut = 0;
        bool ok = DeviceIoControl(
            handle, IOCTL_ECREAD, ref lpIn, 4, ref lpOut, 4, out _, nint.Zero);

        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine($"  [FAIL] read 0x{address:X4} ({name}): Windows error {err}");
            return 0;
        }

        return (byte)(lpOut & 0xFF);
    }

    private static void WriteByte(nint handle, ushort address, byte value)
    {
        int[] inBuf = { address, value };
        int lpOut = 0;
        bool ok = DeviceIoControlWrite(
            handle, IOCTL_ECWRITE, inBuf, 8, ref lpOut, 4, out _, nint.Zero);

        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine($"  [FAIL] write 0x{address:X4}=0x{value:X2}: Windows error {err}");
        }
    }
}
