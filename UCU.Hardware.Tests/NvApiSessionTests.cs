using System.Runtime.InteropServices;
using UCU.Hardware.Gpu;

namespace UCU.Hardware.Tests;

/// <summary>
/// The NVAPI session lifetime and its one retry, driven against a scripted module loader.
/// Field 0.9.12: the GPU unlock's own <c>pnputil /restart-device</c> replaced the driver
/// instance NVAPI had bound to, so every later call in the engine process answered
/// <c>NVAPI_DEVICE_NOT_FOUND</c> (-6) — including the rails step, which left the records
/// staged over stock rails. A session is now one load of nvapi64, and a stale status gets
/// exactly one reload.
/// </summary>
public sealed class NvApiSessionTests
{
    [Fact]
    public void StaleSession_IsReloadedOnce_AndTheRetryAnswers()
    {
        FakeModule stale = new(enumStatus: NvApiModule.DeviceNotFound);
        var loader = new ScriptedLoader(load => load == 1 ? stale : new FakeModule());
        var calls = new NvApiPowerCalls(loader);

        Assert.True(calls.TryGetGpu(out IntPtr gpu, out string? reason), reason);

        Assert.Equal(2, loader.Loads);          // one reload, not a loop
        Assert.True(stale.Disposed);            // the dead module was freed
        Assert.Equal(ScriptedLoader.Handle, gpu.ToInt64());
    }

    [Fact]
    public void StaleSessionThatStaysStale_IsReportedAfterOneRetry()
    {
        var loader = new ScriptedLoader(_ => new FakeModule(enumStatus: NvApiModule.DeviceNotFound));
        var calls = new NvApiPowerCalls(loader);

        Assert.False(calls.TryGetGpu(out IntPtr gpu, out string? reason));

        Assert.Equal(IntPtr.Zero, gpu);
        Assert.Equal("EnumPhysicalGPUs failed (status -6)", reason);
        Assert.Equal(2, loader.Loads);
    }

    [Fact]
    public void AnyOtherStatus_IsNotRetried()
    {
        // -1 is a real refusal, not a lost session: retrying it would hide it.
        var initRefused = new ScriptedLoader(_ => new FakeModule(initStatus: -1));
        Assert.False(new NvApiPowerCalls(initRefused).TryGetGpu(out _, out string? initReason));
        Assert.Equal("nvapi_Initialize failed (status -1)", initReason);
        Assert.Equal(1, initRefused.Loads);

        // Nor is a session that answered but enumerated no usable GPU.
        var noGpu = new ScriptedLoader(_ => new FakeModule(count: 0));
        Assert.False(new NvApiPowerCalls(noGpu).TryGetGpu(out _, out string? gpuReason));
        Assert.Contains("exactly one", gpuReason!, StringComparison.Ordinal);
        Assert.Equal(1, noGpu.Loads);

        // -6 on the initializer is stale as well: the driver instance is gone.
        var staleInit = new ScriptedLoader(_ => new FakeModule(initStatus: NvApiModule.DeviceNotFound));
        Assert.False(new NvApiPowerCalls(staleInit).TryGetGpu(out _, out _));
        Assert.Equal(2, staleInit.Loads);
    }

    [Fact]
    public void ModuleLoadFailure_ReportsTheReason()
    {
        var loader = new ScriptedLoader(_ => null);
        var calls = new NvApiPowerCalls(loader);

        Assert.False(calls.TryGetGpu(out _, out string? reason));

        Assert.Equal("nvapi64.dll could not be loaded (error 126)", reason);
        Assert.Equal(1, loader.Loads);
    }

    [Fact]
    public void AnOperation_LoadsAFreshModule_AndFreesItAtTheEnd()
    {
        // Every operation gets its own session: the module that answered is freed when the
        // operation ends, so no dead session survives into the next one.
        var first = new FakeModule();
        var second = new FakeModule();
        var loader = new ScriptedLoader(load => load == 1 ? first : second);
        var calls = new NvApiPowerCalls(loader);

        using (calls.Open())
        {
            Assert.True(calls.TryGetGpu(out _, out _));
        }

        Assert.True(first.Disposed);

        using (calls.Open())
        {
            Assert.True(calls.TryGetGpu(out _, out _));
        }

        Assert.Equal(2, loader.Loads);
        Assert.True(second.Disposed);
    }

    [Fact]
    public void RailsPath_ReportsTheStaleStatusAfterTheRetry()
    {
        // The rails step is where 0.9.12 failed: the retry is bounded, so the refusal the
        // store turns into "Partly applied" names the reason instead of looping.
        var loader = new ScriptedLoader(_ => new FakeModule(enumStatus: NvApiModule.DeviceNotFound));

        GpuRailLimits refused = new NvApiRailControl(new NvApiPowerCalls(loader)).Set(250_000, 70_000, 14_026);

        Assert.False(refused.Succeeded);
        Assert.Equal("EnumPhysicalGPUs failed (status -6)", refused.Reason);
        Assert.Equal(2, loader.Loads);
    }

    /// <summary>Scripts one module per load; a load it answers null for is a load failure.</summary>
    private sealed class ScriptedLoader : INvApiModuleLoader
    {
        public const long Handle = 7;

        private readonly Func<int, FakeModule?> _next;

        public ScriptedLoader(Func<int, FakeModule?> next) => _next = next;

        public int Loads { get; private set; }

        public bool TryLoad(out INvApiModuleTable? table, out string? reason)
        {
            Loads++;
            table = _next(Loads);
            reason = table == null ? "nvapi64.dll could not be loaded (error 126)" : null;
            return table != null;
        }
    }

    /// <summary>
    /// A loaded nvapi64: real callable pointers for the initializer and the enumeration, so
    /// the transport's own retry logic runs, and a scripted status for each. The delegate
    /// types are the transport's own: a pointer carries the delegate type it was made from.
    /// </summary>
    private sealed class FakeModule : INvApiModuleTable
    {
        private readonly int _initStatus;
        private readonly int _enumStatus;
        private readonly uint _count;

        private readonly NvApiPowerCalls.InitializeCall _initialize;
        private readonly NvApiPowerCalls.EnumPhysicalGpusCall _enumerate;

        public FakeModule(int initStatus = 0, int enumStatus = 0, uint count = 1)
        {
            _initStatus = initStatus;
            _enumStatus = enumStatus;
            _count = count;

            _initialize = () => _initStatus;
            _enumerate = (IntPtr[] handles, out uint found) =>
            {
                found = _count;
                if (handles.Length > 0)
                    handles[0] = new IntPtr(ScriptedLoader.Handle);

                return _enumStatus;
            };
        }

        public bool Disposed { get; private set; }

        public bool TryQuery(uint id, out IntPtr entry)
        {
            entry = id switch
            {
                NvApiPowerLayout.InitializeId => Marshal.GetFunctionPointerForDelegate(_initialize),
                NvApiPowerLayout.EnumPhysicalGpusId => Marshal.GetFunctionPointerForDelegate(_enumerate),
                _ => IntPtr.Zero,
            };

            return entry != IntPtr.Zero;
        }

        public void Dispose() => Disposed = true;
    }
}
