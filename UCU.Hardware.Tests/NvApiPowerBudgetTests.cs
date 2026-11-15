using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using UCU.Hardware.Gpu;

namespace UCU.Hardware.Tests;

/// <summary>
/// The laptop power budget (the driver's F7 value) and its platform (QBOOST) half. Every
/// pointer and code page here is synthetic: the field machine's nvapi64 is never touched.
/// The fake supplies the two power interfaces' bodies, the dispatch entry the reference
/// disassembles out of them, and the packet answers — so the refusal paths (a signature
/// that is not there, an ABI this code does not know, GET and SET pointing elsewhere, a
/// driver that writes past the buffer) are all exercised as data, not as hardware.
/// </summary>
public sealed class NvApiPowerBudgetTests
{
    private const uint GetMethod = NvApiF7Layout.GetMethod;
    private const uint SetMethod = NvApiF7Layout.SetMethod;

    [Fact]
    public void Raise_TakesTheTargetAndGrowsThePlatformByTheSameDelta()
    {
        // Measured 2026-11-15: f7 150000 + platform 425000, raise to 225 W -> 225000 / 500000.
        var baseline = new GpuPowerBudget(150_000, 425_000);

        Assert.True(baseline.TryRaise(225_000, out GpuPowerBudget raised));
        Assert.Equal(new GpuPowerBudget(225_000, 500_000), raised);

        // Already above the target: neither half moves (raising is never a lowering).
        var higher = new GpuPowerBudget(250_000, 500_000);
        Assert.True(higher.TryRaise(225_000, out GpuPowerBudget unchanged));
        Assert.Equal(higher, unchanged);

        // Overflow is a refusal, never a wrap: the two halves are one shared budget.
        var huge = new GpuPowerBudget(150_000, int.MaxValue);
        Assert.False(huge.TryRaise(225_000, out _));

        Assert.False(baseline.TryRaise(0, out _));
        Assert.False(baseline.TryRaise(-1, out _));
    }

    [Fact]
    public void Read_AnswersTheBudgetTheDriverCarries()
    {
        var fake = new FakeF7Calls { F7 = 150_000, Platform = 425_000 };

        GpuPowerBudgetState state = new NvApiPowerBudget(fake).Read();

        Assert.True(state.Succeeded, state.Reason);
        Assert.Equal(new GpuPowerBudget(150_000, 425_000), state.Budget);
    }

    [Fact]
    public void Set_RaisesThePlatformFirst_PutsTheF7BudgetNext_AndLoweresThePlatformLast()
    {
        var fake = new FakeF7Calls { F7 = 150_000, Platform = 425_000 };
        var budget = new NvApiPowerBudget(fake);

        GpuPowerBudgetState state = budget.Set(new GpuPowerBudget(225_000, 500_000));

        Assert.True(state.Succeeded, state.Reason);
        Assert.Equal(new GpuPowerBudget(225_000, 500_000), state.Budget);

        // Platform headroom first, then F7 (set + its own readback), then any decrease.
        int platformUp = fake.Calls.IndexOf("platform-write:500000");
        int f7Set = fake.Calls.IndexOf($"dispatch:{SetMethod:X8}");
        int f7Readback = fake.Calls.LastIndexOf($"dispatch:{GetMethod:X8}");
        Assert.True(platformUp >= 0 && platformUp < f7Set, string.Join(",", fake.Calls));
        Assert.True(f7Set < f7Readback, string.Join(",", fake.Calls));

        // The lower direction: F7 down first, the platform's headroom last.
        fake.Calls.Clear();
        Assert.True(budget.Set(new GpuPowerBudget(200_000, 475_000)).Succeeded);
        int f7Down = fake.Calls.IndexOf($"dispatch:{SetMethod:X8}");
        int platformDown = fake.Calls.IndexOf("platform-write:475000");
        Assert.True(f7Down >= 0 && f7Down < platformDown, string.Join(",", fake.Calls));
    }

    [Fact]
    public void Set_RefusesATargetOutsideTheBoardPowerPolicy()
    {
        var fake = new FakeF7Calls { F7 = 150_000, Platform = 425_000, PolicyMax = 250_000 };

        GpuPowerBudgetState state = new NvApiPowerBudget(fake).Set(new GpuPowerBudget(300_000, 575_000));

        Assert.False(state.Succeeded);
        Assert.Equal("the F7 budget 300000 mW is outside the board-power policy 5000-250000 mW", state.Reason);
        Assert.DoesNotContain(fake.Calls, call => call == $"dispatch:{SetMethod:X8}");
    }

    [Fact]
    public void Set_RequiresTheExactF7Readback()
    {
        // A driver that accepts the set and keeps the old value is not a success.
        var fake = new FakeF7Calls { F7 = 150_000, Platform = 425_000, IgnoreSet = true };

        GpuPowerBudgetState state = new NvApiPowerBudget(fake).Set(new GpuPowerBudget(225_000, 500_000));

        Assert.False(state.Succeeded);
        Assert.Equal("the F7 budget did not read back as written (150000 mW)", state.Reason);
        Assert.Equal(150_000, state.F7Milliwatts);
    }

    [Fact]
    public void Resolve_RefusesAMissingAbiSignature()
    {
        var fake = new FakeF7Calls { OmitAbiHead = true };

        GpuPowerBudgetState state = new NvApiPowerBudget(fake).Read();

        Assert.False(state.Succeeded);
        Assert.Equal("the F7 ABI head signature was not found in this nvapi64", state.Reason);
        Assert.DoesNotContain(fake.Calls, call => call.StartsWith("dispatch:", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_RefusesAnAmbiguousSignature()
    {
        // Two matches mean this is not the nvapi64 the checks were measured on: taking the
        // first would dispatch into the wrong function.
        var fake = new FakeF7Calls { DuplicateAbiHead = true };

        GpuPowerBudgetState state = new NvApiPowerBudget(fake).Read();

        Assert.False(state.Succeeded);
        Assert.Equal("the F7 ABI head signature is ambiguous in this nvapi64", state.Reason);
    }

    [Fact]
    public void Resolve_RefusesAnUnsupportedAbiSize()
    {
        var fake = new FakeF7Calls { AbiSize = 0x1234 };

        GpuPowerBudgetState state = new NvApiPowerBudget(fake).Read();

        Assert.False(state.Succeeded);
        Assert.Equal("unsupported F7 ABI 0x1234", state.Reason);
    }

    [Fact]
    public void Resolve_RefusesADispatchTheTwoInterfacesDisagreeOn()
    {
        var fake = new FakeF7Calls { SetTargetDelta = 0x40 };

        GpuPowerBudgetState state = new NvApiPowerBudget(fake).Read();

        Assert.False(state.Succeeded);
        Assert.Equal("the GET and SET dispatch targets differ", state.Reason);
    }

    [Fact]
    public void Resolve_RefusesADispatchOutsideTheImage()
    {
        // The pointers came out of nvapi64, so a target in another image is somebody
        // else's code: nothing is called.
        var fake = new FakeF7Calls { DispatchImage = 0x180000000 + 0x100000 };

        GpuPowerBudgetState state = new NvApiPowerBudget(fake).Read();

        Assert.False(state.Succeeded);
        Assert.Equal("the F7 dispatch target is not in the nvapi64 image", state.Reason);
    }

    [Fact]
    public void Resolve_RefusesASetInterfaceInAnotherImage()
    {
        var fake = new FakeF7Calls { SetImage = 0x180000000 + 0x200000 };

        GpuPowerBudgetState state = new NvApiPowerBudget(fake).Read();

        Assert.False(state.Succeeded);
        Assert.Equal("the F7 GET and SET interfaces are in different nvapi64 images", state.Reason);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public void F7_ReportsAnUnreadableBudgetAsSuch(uint answer)
    {
        // Measured after a driver reset on waking: only a GPU restart cleared it, so the
        // reason names that and nothing else.
        var fake = new FakeF7Calls { Unreadable = true, UnreadableResult = answer };

        GpuPowerBudgetState state = new NvApiPowerBudget(fake).Read();

        Assert.False(state.Succeeded);
        Assert.Equal("the laptop power budget is unreadable; restart the GPU", state.Reason);
    }

    [Fact]
    public void F7_RefusesAChangedResponseIdentity()
    {
        var fake = new FakeF7Calls { BreakIdentity = true };

        GpuPowerBudgetState state = new NvApiPowerBudget(fake).Read();

        Assert.False(state.Succeeded);
        Assert.Equal("the F7 response did not echo its handle, method or selector", state.Reason);
    }

    [Fact]
    public void F7_RefusesABufferOverrun()
    {
        var fake = new FakeF7Calls { WritePastBuffer = true };

        GpuPowerBudgetState state = new NvApiPowerBudget(fake).Read();

        Assert.False(state.Succeeded);
        Assert.Equal("the driver wrote past the F7 buffer", state.Reason);
    }

    [Fact]
    public void F7_RefusesADispatchStatus()
    {
        var fake = new FakeF7Calls { DispatchStatus = 7 };

        GpuPowerBudgetState state = new NvApiPowerBudget(fake).Read();

        Assert.False(state.Succeeded);
        Assert.Equal("the F7 dispatch failed (status 7)", state.Reason);
    }

    [Fact]
    public void Platform_RefusesAPacketThatDoesNotEchoItself()
    {
        var fake = new FakeF7Calls { EchoController = false };

        GpuPowerBudgetState state = new NvApiPowerBudget(fake).Read();

        Assert.False(state.Succeeded);
        Assert.Equal("the platform budget packet did not echo its controller, version or target", state.Reason);
    }

    [Fact]
    public void Platform_RequiresTheReadbackOfAWrite()
    {
        var fake = new FakeF7Calls { F7 = 150_000, Platform = 425_000, IgnorePlatformWrite = true };

        GpuPowerBudgetState state = new NvApiPowerBudget(fake).Set(new GpuPowerBudget(225_000, 500_000));

        Assert.False(state.Succeeded);
        Assert.Equal("the platform budget did not read back as written", state.Reason);
    }

    [Fact]
    public void Platform_RefusesAnInitializerThatFails()
    {
        var fake = new FakeF7Calls { PlatformInitStatus = -4 };

        GpuPowerBudgetState state = new NvApiPowerBudget(fake).Read();

        Assert.False(state.Succeeded);
        Assert.Equal("the platform budget could not be initialized (status -4)", state.Reason);
    }

    [Fact]
    public void Restore_PutsEachHalfBackOnlyWhileItIsStillOurs()
    {
        var fake = new FakeF7Calls { F7 = 225_000, Platform = 500_000 };
        var budget = new NvApiPowerBudget(fake);

        // Both halves still carry what we applied: both go back.
        Assert.True(budget.Restore(new GpuPowerBudget(150_000, 425_000), new GpuPowerBudget(225_000, 500_000)).Succeeded);
        Assert.Equal(new GpuPowerBudget(150_000, 425_000), new GpuPowerBudget(fake.F7, fake.Platform));

        // Another writer moved the platform: its value is left alone, our F7 goes back.
        fake.F7 = 225_000;
        fake.Platform = 460_000;
        Assert.True(budget.Restore(new GpuPowerBudget(150_000, 425_000), new GpuPowerBudget(225_000, 500_000)).Succeeded);
        Assert.Equal(new GpuPowerBudget(150_000, 460_000), new GpuPowerBudget(fake.F7, fake.Platform));

        // Nothing of ours is left (both halves already at the baseline): no write at all.
        fake.Calls.Clear();
        Assert.True(budget.Restore(new GpuPowerBudget(150_000, 460_000), new GpuPowerBudget(225_000, 500_000)).Succeeded);
        Assert.DoesNotContain(fake.Calls, call => call.StartsWith("platform-write:", StringComparison.Ordinal));
        Assert.DoesNotContain(fake.Calls, call => call == $"dispatch:{SetMethod:X8}");
    }

    /// <summary>
    /// A synthetic nvapi64: the two power interfaces' bodies, the dispatch entry their
    /// relative calls point at, and packet answers a scripted driver would give.
    /// </summary>
    private sealed class FakeF7Calls : INvApiF7Calls
    {
        private const ulong Image = 0x180000000;

        private static readonly IntPtr Get = (IntPtr)0x40001000;
        private static readonly IntPtr Set = (IntPtr)0x40002000;
        private static readonly IntPtr PlatformReadEntry = (IntPtr)0x40003000;
        private static readonly IntPtr PlatformWriteEntry = (IntPtr)0x40004000;
        private static readonly IntPtr PlatformInitializeEntry = (IntPtr)0x40005000;
        private static readonly IntPtr PolicyEntry = (IntPtr)0x40006000;
        private static readonly IntPtr DispatchEntry = (IntPtr)0x40007000;

        private const int GetTail = 27;      // ABI head (23) + the size (4)
        private const int SetTail = 0x20;

        private bool _built;

        public int F7 { get; set; } = 150_000;

        public int Platform { get; set; } = 425_000;

        public int PolicyMin { get; set; } = 5_000;

        public int PolicyMax { get; set; } = 250_000;

        public int BoardIndex { get; set; } = 2;

        public uint AbiSize { get; set; } = NvApiF7Layout.NativeSizeAda;

        public bool OmitAbiHead { get; set; }

        public bool DuplicateAbiHead { get; set; }

        public int SetTargetDelta { get; set; }

        public ulong DispatchImage { get; set; } = Image;

        public ulong SetImage { get; set; } = Image;

        public bool Unreadable { get; set; }

        public uint UnreadableResult { get; set; }

        public bool BreakIdentity { get; set; }

        public bool WritePastBuffer { get; set; }

        public int DispatchStatus { get; set; }

        public bool EchoController { get; set; } = true;

        public bool IgnoreSet { get; set; }

        public bool IgnorePlatformWrite { get; set; }

        public int PlatformInitStatus { get; set; }

        public List<string> Calls { get; } = new();

        public bool TryQuery(uint id, out IntPtr entry)
        {
            entry = id switch
            {
                NvApiPowerLayout.LimitReadId => Get,
                NvApiPowerLayout.LimitSetId => Set,
                NvApiF7Layout.PlatformReadId => PlatformReadEntry,
                NvApiF7Layout.PlatformWriteId => PlatformWriteEntry,
                NvApiF7Layout.PlatformInitializeId => PlatformInitializeEntry,
                NvApiPowerLayout.PolicyTableId => PolicyEntry,
                _ => IntPtr.Zero,
            };

            return entry != IntPtr.Zero;
        }

        public bool TryGetGpu(out IntPtr gpu, out string? reason)
        {
            gpu = (IntPtr)0x1234;
            reason = null;
            return true;
        }

        public bool TryBody(IntPtr entry, out byte[] code, out ulong image, out string? reason)
        {
            Build();

            reason = null;
            code = Array.Empty<byte>();
            image = 0;

            if (entry == Get)
            {
                code = _get;
                image = Image;
                return true;
            }

            if (entry == Set)
            {
                code = _set;
                image = SetImage;
                return true;
            }

            if (entry == DispatchEntry)
            {
                code = new byte[0x20];
                Array.Fill(code, (byte)0xCC);
                image = DispatchImage;
                return true;
            }

            reason = "the F7 transport is not a function entry in this nvapi64";
            return false;
        }

        public bool TryGpuCall(IntPtr entry, IntPtr gpu, IntPtr buffer, out int status, out string? reason)
        {
            status = 0;
            reason = null;

            if (entry != PolicyEntry)
            {
                reason = $"unexpected GPU call to 0x{entry.ToInt64():X}";
                return false;
            }

            Calls.Add("policy");

            byte[] table = new byte[NvApiPowerLayout.PolicyBufferLength];
            Write32(table, 0, NvApiPowerLayout.PolicyVersion);
            Write32(table, 4, 1u << BoardIndex);

            int at = 0x28 + BoardIndex * 0x38;
            Write32(table, at, 0);
            Write32(table, at + 4, 0);
            Write32(table, at + 8, (uint)PolicyMin);
            Write32(table, at + 12, 175_000);
            Write32(table, at + 16, (uint)PolicyMax);

            Marshal.Copy(table, 0, buffer, table.Length);
            return true;
        }

        public bool TryPlainCall(IntPtr entry, IntPtr buffer, out int status, out string? reason)
        {
            status = 0;
            reason = null;

            if (entry == PlatformReadEntry)
            {
                Calls.Add("platform-read");
                Marshal.WriteInt32(buffer, 0, EchoController ? 1 : 0);
                Marshal.WriteInt32(buffer, 4, unchecked((int)NvApiF7Layout.PlatformVersion));
                Marshal.WriteByte(buffer, 8, EchoController ? (byte)1 : (byte)0);
                Marshal.WriteInt32(buffer, NvApiF7Layout.CompletionOffset, Platform);
                return true;
            }

            if (entry == PlatformWriteEntry)
            {
                int target = Marshal.ReadInt32(buffer, NvApiF7Layout.CompletionOffset);
                Calls.Add($"platform-write:{target}");

                if (!IgnorePlatformWrite)
                    Platform = target;

                return true;
            }

            reason = $"unexpected plain call to 0x{entry.ToInt64():X}";
            return false;
        }

        public bool TryInitializeEx(IntPtr entry, uint flag, out int status, out string? reason)
        {
            reason = null;
            status = entry == PlatformInitializeEntry && flag == 1 ? PlatformInitStatus : -1;
            return entry == PlatformInitializeEntry;
        }

        public bool TryDispatch(IntPtr target, uint method, IntPtr buffer, uint size, out int status, out string? reason)
        {
            status = DispatchStatus;
            reason = null;

            if (target != DispatchEntry)
            {
                reason = $"dispatched to 0x{target.ToInt64():X}";
                return false;
            }

            // The first argument is the dispatch method; the packet's own method (get or
            // set) is the field at 0x34.
            uint packetMethod = (uint)Marshal.ReadInt32(buffer, NvApiF7Layout.MethodOffset);
            Calls.Add($"dispatch:{packetMethod:X8}");

            int handle = Marshal.ReadInt32(buffer, NvApiF7Layout.HandleOffset);
            uint selector = (uint)Marshal.ReadInt32(buffer, NvApiF7Layout.SelectorOffset);
            int index = BitOperations.TrailingZeroCount(selector);
            int offset = NvApiF7Layout.ValueOffset + index * NvApiF7Layout.ValueStride;

            if (packetMethod == SetMethod && !IgnoreSet)
                F7 = Marshal.ReadInt32(buffer, offset);

            uint answer = Unreadable ? UnreadableResult : unchecked((uint)F7);
            Marshal.WriteInt32(buffer, offset, unchecked((int)answer));
            Marshal.WriteInt32(buffer, NvApiF7Layout.CompletionOffset, 1);

            if (BreakIdentity)
                Marshal.WriteInt32(buffer, NvApiF7Layout.HandleOffset, handle + 1);

            if (WritePastBuffer)
                Marshal.WriteInt32(buffer, -4, 0);

            return true;
        }

        private byte[] _get = Array.Empty<byte>();
        private byte[] _set = Array.Empty<byte>();

        /// <summary>
        /// Both bodies as the measured reference finds them: the ABI head, the size that
        /// follows it, the dispatch tail and its relative call, plus each interface's own
        /// identity pattern. Filler is int3 so no pattern appears by accident.
        /// </summary>
        private void Build()
        {
            if (_built)
                return;

            _built = true;

            _get = new byte[0x80];
            Array.Fill(_get, (byte)0xCC);
            _set = new byte[0x60];
            Array.Fill(_set, (byte)0xCC);

            if (!OmitAbiHead)
                F7Head.CopyTo(_get, 0);

            Write32(_get, 23, AbiSize);
            F7Tail.CopyTo(_get, GetTail);
            Write32(_get, GetTail + F7Tail.Length, unchecked((uint)(int)(DispatchEntry.ToInt64() - (Get.ToInt64() + GetTail + F7Tail.Length + 4))));
            GetIdentity.CopyTo(_get, 0x40);

            if (DuplicateAbiHead)
                F7Head.CopyTo(_get, 0x60);

            SetIdentity.CopyTo(_set, 0x10);
            F7Tail.CopyTo(_set, SetTail);
            Write32(
                _set,
                SetTail + F7Tail.Length,
                unchecked((uint)(int)(DispatchEntry.ToInt64() + SetTargetDelta - (Set.ToInt64() + SetTail + F7Tail.Length + 4))));
        }

        private static readonly byte[] F7Head =
        {
            0xC7, 0x44, 0x24, 0x30, 0x00, 0x00, 0x00, 0x00,
            0xC6, 0x44, 0x24, 0x28, 0x00,
            0xC6, 0x44, 0x24, 0x20, 0x01,
            0x45, 0x33, 0xC9, 0x41, 0xB8,
        };

        private static readonly byte[] F7Tail =
        {
            0x49, 0x8B, 0xD6, 0xB9, 0x46, 0x00, 0x00, 0x07, 0xE8,
        };

        private static readonly byte[] GetIdentity =
        {
            0x45, 0x89, 0x7E, 0x30, 0x41, 0xC7, 0x46, 0x34, 0x32, 0xA6, 0x80, 0x20,
        };

        private static readonly byte[] SetIdentity = { 0x33, 0xE6, 0x80, 0x20 };

        private static uint Read32(byte[] buffer, int offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));

        private static void Write32(byte[] buffer, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), value);
    }
}
