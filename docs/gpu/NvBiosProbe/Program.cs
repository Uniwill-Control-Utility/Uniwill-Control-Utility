using System.Runtime.InteropServices;
using System.Text;

namespace NvBiosProbe;

/// <summary>
/// READ-ONLY probe of the NVIDIA VBIOS image and the power-policy layout inside it.
///
/// Everything comes from MechControl:
///   Api::bios  (src/nvidia_api.cpp:299-310) -> 0xfc13ee11, packet(16, 0x20010),
///               capacity u32 at 4, pinned 1024000-byte image pointer at 8, actual size back at 4
///   Bios::parse (src/nvidia_bios.cpp) -> the image chain, BIT directory, power policy table
///
/// This tool never calls a setter and never writes the ROM back; the image only lands in a
/// local file so it can be inspected again later.
/// </summary>
internal static class Program
{
    private const int BiosCapacity = 1024000;
    private const uint BiosPacketVersion = 0x20010;
    private const int BiosPacketSize = 16;

    private const uint FnInitialize = 0x0150E828;
    private const uint FnEnumPhysicalGpus = 0xe5ac921f;
    private const uint FnBios = 0xfc13ee11;

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvStatus();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvEnumGpus(IntPtr handles, ref uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvCall(IntPtr gpu, IntPtr data);

    private static int Main(string[] args)
    {
        string path = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "gpu-vbios.rom");
        Console.WriteLine("NvBiosProbe - read-only VBIOS dump + power policy layout check");
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

        NvEnumGpus? enumerate = Bind<NvEnumGpus>(FnEnumPhysicalGpus);
        NvCall? bios = Bind<NvCall>(FnBios);
        if (enumerate is null || bios is null)
        {
            Console.Error.WriteLine("Required NVAPI interfaces are not exposed by this driver.");
            return 4;
        }

        IntPtr handles = Marshal.AllocHGlobal(64 * IntPtr.Size);
        uint count = 0;
        try
        {
            status = enumerate(handles, ref count);
            Console.WriteLine($"NvAPI_EnumPhysicalGPUs -> {status}, {count} adapter(s)");
            if (status != 0 || count == 0)
                return 5;
            if (count != 1)
                Console.WriteLine("  warning: MechControl expects exactly one NVIDIA adapter; using the first");
            IntPtr gpu = Marshal.ReadIntPtr(handles);

            byte[] image = new byte[BiosCapacity];
            GCHandle imageHandle = GCHandle.Alloc(image, GCHandleType.Pinned);
            byte[] packet = new byte[BiosPacketSize];
            GCHandle packetHandle = GCHandle.Alloc(packet, GCHandleType.Pinned);
            try
            {
                WriteU32(packet, 0, BiosPacketVersion);
                WriteU32(packet, 4, BiosCapacity);
                WriteU64(packet, 8, (ulong)imageHandle.AddrOfPinnedObject());
                status = bios(gpu, packetHandle.AddrOfPinnedObject());
            }
            finally
            {
                packetHandle.Free();
                imageHandle.Free();
            }

            Console.WriteLine($"VBIOS call 0x{FnBios:X8} -> {status}");
            if (status != 0)
            {
                Console.Error.WriteLine("The VBIOS call failed; nothing else can be read.");
                return 6;
            }

            uint size = ReadU32(packet, 4);
            Console.WriteLine($"image length -> {size} (capacity {BiosCapacity})");
            if (size < 512 || size > BiosCapacity)
            {
                Console.Error.WriteLine("Unsupported NVIDIA BIOS: VBIOS image length");
                return 7;
            }

            byte[] rom = image.AsSpan(0, (int)size).ToArray();
            try
            {
                File.WriteAllBytes(path, rom);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"cannot write {path}: {e.Message}");
                return 8;
            }

            Console.WriteLine($"written to {Path.GetFullPath(path)}");
            Console.WriteLine($"first 16 bytes: {Hex(rom.AsSpan(0, 16))}");
            Console.WriteLine($"starts with 55 AA: {BitConverter.ToUInt16(rom, 0) == 0xAA55} " +
                              $"(u16 0x{BitConverter.ToUInt16(rom, 0):X4})");
            Console.WriteLine();

            BiosReport report = Bios.Parse(rom);
            Report(report);
            return report.Refusal is null ? 0 : 9;
        }
        finally
        {
            Marshal.FreeHGlobal(handles);
        }
    }

    private static void Report(BiosReport r)
    {
        Console.WriteLine("image chain");
        if (r.Images.Count == 0)
            Console.WriteLine("  (no image parsed)");
        foreach (ChainImage image in r.Images)
            Console.WriteLine($"  #{image.Index}  type 0x{image.Type:X2}  length {image.Length} " +
                              $"checksum {(image.Type is 0 or 3 ? (image.ChecksumOk ? "ok" : "BAD") : "n/a (E0)")}");
        Console.WriteLine($"  legacy length {r.Legacy}, EFI length {r.Efi}");
        Console.WriteLine();

        Console.WriteLine(r.BitFound ? $"BIT directory @ 0x{r.BitOffset:X}" : "BIT directory not found");
        if (r.BitHeader is not null)
            Console.WriteLine($"  header {Hex(r.BitHeader)}");
        if (r.TokenCount >= 0)
            Console.WriteLine($"  raw token count {r.TokenCount}");
        if (r.TokenCount >= 0)
        {
            Console.WriteLine("  name  version  offset  size");
            foreach (TokenRow token in r.Tokens)
                Console.WriteLine($"  {(char)token.Name}     {token.Version,3}    " +
                                  $"{(token.Offset < 0 ? "--" : $"0x{token.Offset:X}"),-8} {token.Size}");
        }

        Console.WriteLine();
        Console.WriteLine($"VBIOS version {FormatVersion(r.VersionBytes)}");

        if (r.TableHeader is not null)
        {
            string layout = r.Ada ? "RTX 40 (Ada) layout" : r.Rtx50 ? "RTX 50 layout" : "UNRECOGNISED layout";
            Console.WriteLine($"power policy table @ 0x{r.TableOffset:X}  header {Hex(r.TableHeader)}  {layout}");
        }

        Field("power (index 2)", r.Power);
        Field("NVVDD (index 13)", r.Nv);
        Field("MSVDD (index 14)", r.Ms);
        Field("board input (index 9)", r.Input);
        Console.WriteLine($"PCI device 0x{(r.Device is null ? "----" : r.Device.Value.ToString("X4"))}");
        Console.WriteLine($"BIOS profile block {r.ProfileCheck}");
        Console.WriteLine();

        if (r.Refusal is null)
        {
            Console.WriteLine("PARSE OK: the image matches the layout the product code expects.");
            return;
        }

        Console.Error.WriteLine($"REFUSED: {r.Refusal}");
    }

    private static void Field(string label, Field? field)
    {
        if (field is not { } f)
        {
            Console.WriteLine($"{label,-22} absent");
            return;
        }

        Console.WriteLine($"{label,-22} min {f.Min} default {f.Def} max {f.Max}  (offset 0x{f.Offset:X})");
    }

    private static string FormatVersion(byte[]? bytes)
    {
        if (bytes is null)
            return "(unknown)";
        StringBuilder text = new();
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
                text.Append('.');
            text.Append($"{bytes[i]:X2}");
        }

        return text.ToString();
    }

    private static string Hex(ReadOnlySpan<byte> bytes) =>
        string.Join(' ', bytes.ToArray().Select(b => b.ToString("X2")));

    private static T? Bind<T>(uint id) where T : Delegate
    {
        IntPtr address = QueryInterface(id);
        return address == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static uint ReadU32(byte[] buffer, int offset) => BitConverter.ToUInt32(buffer, offset);

    private static void WriteU32(byte[] buffer, int offset, uint value) =>
        BitConverter.GetBytes(value).CopyTo(buffer, offset);

    private static void WriteU64(byte[] buffer, int offset, ulong value) =>
        BitConverter.GetBytes(value).CopyTo(buffer, offset);
}
