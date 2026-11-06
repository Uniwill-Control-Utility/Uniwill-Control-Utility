using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The guardian's contract store (U-107): notes at the acts that apply,
/// clears at the acts that yield power to the firmware, persists to the
/// store file so a restart keeps the PREVIOUS session's contract (the EC
/// keeps the bytes across restarts), and treats a corrupt file as no
/// contract — healing toward half-parsed bytes is not a thing.
/// </summary>
public sealed class PowerContractStoreTests : IDisposable
{
    private readonly string _file;

    public PowerContractStoreTests()
    {
        _file = Path.Combine(Path.GetTempPath(), "ucu-tests", $"power_contract_{Guid.NewGuid():N}.json");
        PowerContractStore.OverrideFilePathForTests(_file);
    }

    public void Dispose()
    {
        PowerContractStore.OverrideFilePathForTests(null);
        if (File.Exists(_file))
            File.Delete(_file);
    }

    [Fact]
    public void NoteGpu_RecordsTheExactBytes_AndPersists()
    {
        PowerContractStore.NoteGpuApplied(70, 25);

        Assert.True(PowerContractStore.Current.HasGpu);
        Assert.Equal(70, PowerContractStore.Current.GpuCtgpOffset);
        Assert.Equal(25, PowerContractStore.Current.GpuDbOffset);
        Assert.False(PowerContractStore.Current.HasCpu);

        // Survives a restart: Load reads the same contract back.
        PowerContractStore.Load();
        Assert.Equal(70, PowerContractStore.Current.GpuCtgpOffset);
    }

    [Fact]
    public void NoteCpu_KeepsTheGpuHalf_IndependentHalves()
    {
        PowerContractStore.NoteGpuApplied(70, 25);
        PowerContractStore.NoteCpuApplied(75, 85);

        Assert.True(PowerContractStore.Current.HasGpu);
        Assert.True(PowerContractStore.Current.HasCpu);
        Assert.Equal(75, PowerContractStore.Current.Pl1Watts);
        Assert.Equal(85, PowerContractStore.Current.Pl2Watts);
    }

    [Fact]
    public void ClearAll_StockApply_DropsBothHalves_Persisted()
    {
        PowerContractStore.NoteGpuApplied(70, 25);
        PowerContractStore.NoteCpuApplied(75, 85);

        PowerContractStore.ClearAll();

        Assert.True(PowerContractStore.Current.IsEmpty);
        PowerContractStore.Load();
        Assert.True(PowerContractStore.Current.IsEmpty);
    }

    [Fact]
    public void CorruptFile_LoadsAsNoContract()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file, "{ this is not json");

        PowerContractStore.Load();

        Assert.True(PowerContractStore.Current.IsEmpty);
    }

    [Fact]
    public void MissingFile_LoadsAsNoContract()
    {
        PowerContractStore.Load();

        Assert.True(PowerContractStore.Current.IsEmpty);
    }
}

/// <summary>
/// The capture-site pin (source shape, the house style for static-seam
/// wiring): every act that applies the EC power contract must note it, and
/// every act that yields power to the firmware's factory table must clear
/// it. A refactor that re-applies power bytes without noting the contract
/// silently blinds the guardian, so the call sites are pinned here.
/// </summary>
public sealed class PowerContractCaptureSiteTests
{
    [Fact]
    public void SetPowerLimit_Chokepoint_NotesTheGpuContract()
    {
        string source = ReadAppSource("Scripts", "GPUs", "NVIDIA", "NvTuning.cs");

        int activate = source.IndexOf("ActivateGpuPowerControlAsync(ctgpOffset, dbOffset)", StringComparison.Ordinal);
        int note = source.IndexOf("PowerContractStore.NoteGpuApplied(ctgpOffset, dbOffset)", StringComparison.Ordinal);

        Assert.True(activate >= 0, "SetPowerLimit must funnel GPU power writes through the engine verb.");
        Assert.True(note >= 0, "the SetPowerLimit chokepoint must note the contract with the exact bytes it wrote.");
        Assert.True(note > activate, "the note lands with the activation, after it.");
    }

    [Fact]
    public void ApplierAndPages_NoteTheCpuPair_AndClearOnStock()
    {
        string applier = ReadAppSource("Services", "PresetProfileApplier.cs");
        Assert.Contains("PowerContractStore.NoteCpuApplied(verdict.Pl1, verdict.Pl2)", applier, StringComparison.Ordinal);
        Assert.Contains("PowerContractStore.ClearAll()", applier, StringComparison.Ordinal);

        string presets = ReadAppSource("Views", "Pages", "Presets.xaml.cs");
        Assert.Contains("PowerContractStore.NoteCpuApplied(pl1, pl2)", presets, StringComparison.Ordinal);
        Assert.Contains("PowerContractStore.ClearAll()", presets, StringComparison.Ordinal);

        string main = ReadAppSource("Views", "Windows", "MainWindow.xaml.cs");
        Assert.Contains("PowerContractStore.NoteCpuApplied", main, StringComparison.Ordinal);
    }

    [Fact]
    public void App_LoadsTheContract_BeforeTheStartupApply()
    {
        string app = ReadAppSource("App.xaml.cs");

        int load = app.IndexOf("PowerContractStore.Load()", StringComparison.Ordinal);
        int guardian = app.IndexOf("AddHostedService(provider => provider.GetRequiredService<PowerGuardianService>())", StringComparison.Ordinal);

        Assert.True(load >= 0, "the persisted contract must load at startup.");
        Assert.True(guardian >= 0, "the guardian must be hosted.");
    }

    private static string ReadAppSource(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot(), "Uniwill Control Utility" }.Concat(parts).ToArray()));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility.Tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
