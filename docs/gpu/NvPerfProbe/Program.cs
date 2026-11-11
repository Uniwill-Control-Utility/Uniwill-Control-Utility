using System.Runtime.InteropServices;

/// <summary>
/// READ-ONLY probe of NVIDIA's private PERF_CF policy interface, used to validate the
/// layout the DLPPC toggle depends on before any write is written into product code.
///
/// Everything here comes from MechControl's implementation:
///   Api::find_dlppc  -> 0x1cac3865, packet(0x164c, 0x1164c),
///                       index = u16 at 24, supported mask = u32 at 4,
///                       entry = 76 + index*176, label = u32 at entry+8,
///                       type = u32 at entry, id = u32 at entry+48
///   Api::policy_control -> 0x69de2c54, packet(0x1548, 0x11548),
///                       request mask u32 at 4, type check u32 at 72+index*168,
///                       flag = byte at 72 + index*168 + 4
///
/// This tool never calls 0x4d1c7d6e (the setter). It only reads.
/// </summary>
internal static class Program
{
    private const int PerfCfDumpSize = 0x164c;
    private const int PerfCfDumpVersion = 0x1164c;
    private const int PolicyControlSize = 0x1548;
    private const int PolicyControlVersion = 0x11548;

    private const uint FnInitialize = 0x0150E828;
    private const uint FnPerfCfDump = 0x1cac3865;
    private const uint FnPolicyControlGet = 0x69de2c54;

    private const int PacketPad = 0;

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvStatus();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvCall(IntPtr data);

    private static int Main()
    {
        Console.WriteLine("NvPerfProbe - read-only PERF_CF / DLPPC layout check");
        Console.WriteLine();

        NvStatus? initialize = Bind<NvStatus>(FnInitialize);
        if (initialize is null)
        {
            Console.Error.WriteLine($"nvapi_QueryInterface(0x{FnInitialize:X8}) failed; is an NVIDIA driver installed?");
            return 2;
        }

        int status = initialize();
        Console.WriteLine($"NvAPI_Initialize -> {status}");
        if (status != 0)
            return 3;

        NvCall? dump = Bind<NvCall>(FnPerfCfDump);
        if (dump is null)
        {
            Console.Error.WriteLine($"PERF_CF dump interface 0x{FnPerfCfDump:X8} not exposed by this driver.");
            return 4;
        }

        byte[] buffer = Packet(PerfCfDumpSize, PerfCfDumpVersion);
        status = Invoke(dump, buffer);
        Console.WriteLine($"PERF_CF dump 0x{FnPerfCfDump:X8} -> {status}");
        if (status != 0)
        {
            Console.Error.WriteLine("The dump call failed; nothing else can be read.");
            return 5;
        }

        uint supported = ReadU32(buffer, 4);
        int active = ReadU16(buffer, 24);
        Console.WriteLine($"supported mask 0x{supported:X8}   active index {active}");
        Console.WriteLine();

        Console.WriteLine("  idx  type  label   id          notes");
        for (int p = 0; p < 32; p++)
        {
            if ((supported & (1u << p)) == 0)
                continue;

            int entry = 76 + p * 176;
            if (entry + 52 > buffer.Length)
            {
                Console.WriteLine($"  {p,3}  --    --      --          entry offset {entry} outside the buffer");
                continue;
            }

            uint type = ReadU32(buffer, entry);
            uint label = ReadU32(buffer, entry + 8);
            uint id = ReadU32(buffer, entry + 48);
            string mark = p == active ? "  <- active" : "";
            string labelName = label == 7 ? " (DLPPC_1X)" : "";
            Console.WriteLine($"  {p,3}  {type,4}  {label,5}  0x{id:X8}  {labelName}{mark}");
        }

        Console.WriteLine();
        if (active >= 32 || (supported & (1u << active)) == 0)
        {
            Console.Error.WriteLine("Active index is outside the supported mask; the controls are unusable.");
            return 6;
        }

        int activeEntry = 76 + active * 176;
        uint activeType = ReadU32(buffer, activeEntry);
        uint activeLabel = ReadU32(buffer, activeEntry + 8);
        Console.WriteLine($"active entry: type {activeType}, label {activeLabel} " +
                          $"({(activeType == 0 && activeLabel == 7 ? "matches the DLPPC identity" : "does NOT match type 0 / label 7")})");
        if (activeType != 0 || activeLabel != 7)
            return 7;

        NvCall? control = Bind<NvCall>(FnPolicyControlGet);
        if (control is null)
        {
            Console.Error.WriteLine($"policy-control interface 0x{FnPolicyControlGet:X8} not exposed.");
            return 8;
        }

        byte[] controlBuffer = Packet(PolicyControlSize, PolicyControlVersion);
        WriteU32(controlBuffer, 4, 1u << active);
        status = Invoke(control, controlBuffer);
        Console.WriteLine($"policy control 0x{FnPolicyControlGet:X8} -> {status}");
        if (status != 0)
            return 9;

        uint echoed = ReadU32(controlBuffer, 4);
        uint controlType = ReadU32(controlBuffer, 72 + active * 168);
        int flagOffset = 72 + active * 168 + 4;
        byte flag = controlBuffer[flagOffset];

        Console.WriteLine($"  echoed mask 0x{echoed:X8} (wanted 0x{1u << active:X8}), type at {72 + active * 168} = {controlType}");
        Console.WriteLine($"  DLPPC flag byte at {flagOffset} = {flag}  => {(flag != 0 ? "ACTIVE (downclocking enabled)" : "inactive")}");
        Console.WriteLine();
        Console.WriteLine(echoed == (1u << active) && controlType == 0
            ? "LAYOUT OK: this driver matches the recipe, so a guarded toggle is safe to add."
            : "LAYOUT MISMATCH: do not write; report these numbers instead.");
        return echoed == (1u << active) && controlType == 0 ? 0 : 10;
    }

    private static T? Bind<T>(uint id) where T : Delegate
    {
        IntPtr address = QueryInterface(id);
        return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static byte[] Packet(int size, int version)
    {
        byte[] buffer = new byte[size + PacketPad];
        WriteU32(buffer, 0, (uint)version);
        return buffer;
    }

    private static int Invoke(NvCall call, byte[] buffer)
    {
        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            return call(handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }

    private static uint ReadU32(byte[] buffer, int offset) =>
        BitConverter.ToUInt32(buffer, offset);

    private static int ReadU16(byte[] buffer, int offset) =>
        BitConverter.ToUInt16(buffer, offset);

    private static void WriteU32(byte[] buffer, int offset, uint value) =>
        BitConverter.GetBytes(value).CopyTo(buffer, offset);
}
