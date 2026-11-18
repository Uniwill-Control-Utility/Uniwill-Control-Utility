namespace Uniwill_Control_Utility.Tests.UI;

/// <summary>
/// The Extra GPU power card's apply path, pinned here rather than trusted to review:
///
///   - the unlock aims at the preset's TGP and moves the TGP slider onto the target it got,
///     without stamping a preset;
///   - an apply is followed by a bounded re-read pass that hushes the EC's own republish, and a
///     read that finds the budget short asks for one repair and re-reads once;
///   - the consent statement is what gates the actions, and the rails' Apply carries its own
///     gate because it answers to the sliders it writes.
/// </summary>
public sealed class PresetsUnlockCardTests
{
    [Fact]
    public void TheStatementGatesTheActionsAndApplyLimitsCarriesItsOwnGate()
    {
        string body = MethodBody(ReadPresetsPage(), "private void RenderRomOverride()");

        // The statement the actions need stays on the card while the unlock is applied - and it
        // shares its row with the unlock pair, so with nothing to state the row keeps Restore.
        Assert.Contains(
            "bool statement = (canUnlock || unlocked || uncertain) && !appliedWhileOff;",
            body, StringComparison.Ordinal);
        Assert.Contains(
            "chkRomConsent.Visibility = statement ? Visibility.Visible : Visibility.Collapsed;",
            body, StringComparison.Ordinal);
        Assert.DoesNotContain("brRomConsent", ReadPresetsXaml(), StringComparison.Ordinal);
        Assert.DoesNotContain("brRomConsent", body, StringComparison.Ordinal);

        // "Not unlocked" is said once, by the row's note under the sliders.
        string rail = MethodBody(ReadPresetsPage(), "private void RenderRailLimits()");
        Assert.Contains("Available once Extra GPU power is unlocked.", rail, StringComparison.Ordinal);

        // The action row is never dimmed as a whole: Apply limits answers to the sliders it
        // writes, so its own state is set on the button and on the sliders' panel.
        Assert.Contains("bool usable = applied && bounded && !_railLimitsBusy;", rail, StringComparison.Ordinal);
        Assert.Contains("spRailControls.IsEnabled = usable;", rail, StringComparison.Ordinal);
        Assert.Contains("btnRailApply.IsEnabled = usable;", rail, StringComparison.Ordinal);

        string page = ReadPresetsPage();
        Assert.DoesNotContain("spRomActionRow.IsEnabled", page, StringComparison.Ordinal);
        Assert.DoesNotContain("spRomActionRow.Opacity", page, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMeasurementTooltipIsGoneFromTheCard()
    {
        // The frozen measurement paragraph, the live target, the stock rails and the driver
        // maxima left the UI entirely; the measurements themselves are in the docs.
        Assert.DoesNotContain("setRomInfoTooltip", ReadPresetsPage(), StringComparison.Ordinal);
        Assert.DoesNotContain("tbRomInfo", ReadPresetsXaml(), StringComparison.Ordinal);
        Assert.DoesNotContain("Measured on our test unit", ReadPresetsXaml(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTgpSliderFollowsTheUnlockTarget_WithoutSavingThePreset()
    {
        // Field, 0.9.14: the unlock wrote the EC's cTGP offset and the TGP slider stayed at the
        // preset's 200 W, so the slider - the owner's model of the EC - was wrong about the
        // machine. Every apply with a target moves it onto that target.
        string unlock = MethodBody(ReadPresetsPage(), "private async Task RunRomOverrideAsync(bool restore)");
        Assert.Contains("if (!restore && targetTgp is int target", unlock, StringComparison.Ordinal);
        Assert.Contains("ShowUnlockTarget(target);", unlock, StringComparison.Ordinal);

        // The target is the preset's own TGP, and only a Custom preset carries one.
        Assert.Contains("_currentProfile == \"Custom\"", unlock, StringComparison.Ordinal);
        Assert.Contains("GpuUnlockVerdict.UnlockTarget((int)Math.Round(sdNvPower.Value * 1000))", unlock, StringComparison.Ordinal);

        // The sync touches the slider only. The slot is written by SaveCustomPreset (Save, Clear
        // and the slot switch), which this never calls, and the applied-profile readout keeps the
        // preset's TGP instead of being restamped.
        string sync = MethodBody(ReadPresetsPage(), "private void ShowUnlockTarget(int milliwatts)");
        Assert.Contains("sdNvPower.Value = target;", sync, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveCustomPreset", sync, StringComparison.Ordinal);
        Assert.DoesNotContain("CustomPresetStore", sync, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.Default.Save", sync, StringComparison.Ordinal);
        Assert.DoesNotContain("_profileGpuTgp", sync, StringComparison.Ordinal);

        // And the decoupling holds the other way: a preset apply never moves the slider onto the
        // unlock's target, and never retargets the unlock.
        string preset = MethodBody(ReadPresetsPage(), "private async Task applyProfileAsync()");
        Assert.DoesNotContain("ShowUnlockTarget", preset, StringComparison.Ordinal);
        Assert.DoesNotContain("SetRomOverrideAsync", preset, StringComparison.Ordinal);
    }

    [Fact]
    public void APostApplyVerificationReReadsAndHushesTheTransientShortfall()
    {
        string page = ReadPresetsPage();

        // The EC republishes its own F7 seconds after the write, so one short pass is enough:
        // two reads, three and eight seconds in, then it is done.
        int schedule = page.IndexOf("private static readonly TimeSpan[] PostApplyVerifySchedule =", StringComparison.Ordinal);
        Assert.True(schedule >= 0, "The verification schedule is missing.");
        string scheduleBlock = Slice(page, "private static readonly TimeSpan[] PostApplyVerifySchedule =", "};");
        Assert.Contains("TimeSpan.FromSeconds(3),", scheduleBlock, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(8),", scheduleBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("FromSeconds(30)", scheduleBlock, StringComparison.Ordinal);

        // The apply starts it, and it is only the state read plus a render: the write paths
        // stay the ones that already exist.
        Assert.Contains("BeginPostApplyVerify();", MethodBody(page, "private async Task RunRomOverrideAsync(bool restore)"), StringComparison.Ordinal);

        string verify = MethodBody(page, "private async Task VerifyAfterApplyAsync(int run)");
        Assert.Contains("await RefreshRomOverrideAsync()", verify, StringComparison.Ordinal);
        Assert.DoesNotContain("SetRomOverrideAsync", verify, StringComparison.Ordinal);
        Assert.DoesNotContain("SetRailLimitsAsync", verify, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteRailLimitsAsync", verify, StringComparison.Ordinal);

        // While the window is open the shortfall line is quiet; the pass closes it before the
        // last render, so a gap that survived is still said.
        string render = MethodBody(page, "private void RenderRomOverride()");
        Assert.Contains("_postApplyVerifying || _budgetRepairing);", render, StringComparison.Ordinal);
        Assert.Contains("_postApplyVerifying = i < PostApplyVerifySchedule.Length - 1;", verify, StringComparison.Ordinal);

        // The apply's own read-back may have rendered the republished budget already, so the
        // window renders once when it opens.
        Assert.Contains("RenderRomOverride();", MethodBody(page, "private void BeginPostApplyVerify()"), StringComparison.Ordinal);
    }

    [Fact]
    public void AReadThatFindsTheBudgetShort_ReReadsOnceAndHushesOnlyItsOwnRepair()
    {
        // Field, 0.9.14: the EC republished 175 W and the card was opened inside the drift
        // watch's interval, so it reported a failure that was already being repaired. The read
        // is the ask (the state verb nudges the watch), and one short re-read renders the
        // repaired numbers.
        string page = ReadPresetsPage();

        string refresh = MethodBody(page, "private async Task RefreshRomOverrideAsync()");
        Assert.Contains("GpuUnlockVerdict.BudgetShort(", refresh, StringComparison.Ordinal);
        Assert.Contains("_budgetRepairing = true;", refresh, StringComparison.Ordinal);
        Assert.Contains("RepairBudgetAsync(", refresh, StringComparison.Ordinal);

        // A read is only a read: the repair stays the watch's own write.
        Assert.DoesNotContain("SetRomOverrideAsync", refresh, StringComparison.Ordinal);

        string repair = MethodBody(page, "private async Task RepairBudgetAsync()");
        Assert.Contains("await Task.Delay(BudgetRepairWait)", repair, StringComparison.Ordinal);
        Assert.Contains("await RefreshRomOverrideAsync()", repair, StringComparison.Ordinal);
        Assert.Contains("_budgetRepairing = false;", repair, StringComparison.Ordinal);
        Assert.DoesNotContain("SetRomOverrideAsync", repair, StringComparison.Ordinal);
        Assert.DoesNotContain("SetRailLimitsAsync", repair, StringComparison.Ordinal);

        // The window opens on the read that asked and closes after its one re-read has rendered,
        // so a budget that survived the repair is said: both the card's partial-apply sentence
        // and the shortfall line follow it.
        string render = MethodBody(page, "private void RenderRomOverride()");
        Assert.Contains("_postApplyVerifying || _budgetRepairing);", render, StringComparison.Ordinal);
        Assert.Contains("bool mendingBudget = _budgetRepairing", render, StringComparison.Ordinal);
    }

    private static string Slice(string source, string from, string to)
    {
        int start = source.IndexOf(from, StringComparison.Ordinal);
        int end = source.IndexOf(to, start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start, $"Could not slice {from} up to {to}.");

        return source.Substring(start, end - start);
    }

    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find {signature}.");

        int end = source.IndexOf("\n        }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find the end of {signature}.");

        return source.Substring(start, end - start);
    }

    private static string ReadPresetsPage() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "Uniwill Control Utility", "Views", "Pages", "Presets.xaml.cs"));

    private static string ReadPresetsXaml() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "Uniwill Control Utility", "Views", "Pages", "Presets.xaml"));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Uniwill Control Utility.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
