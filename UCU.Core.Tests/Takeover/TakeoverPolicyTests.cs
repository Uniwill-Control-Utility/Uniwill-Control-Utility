using UCU.Core.Takeover;

namespace UCU.Core.Tests.Takeover;

/// <summary>
/// The OEM takeover hardening gate, restore planner, and status decider
/// (story U-203). The gate is the story's whole point: open-revo mutates by
/// bare name, UCU mutates only name AND existing path AND valid signature AND
/// publisher allowlist AND outside UCU's own roots. Every refusal reason is
/// pinned here, including the dev-box-proven self-squat (the box's ONLY
/// "uniwill" process match is UCU itself).
/// </summary>
public sealed class TakeoverPolicyTests
{
    private const string UcuRoot = @"C:\Program Files\Uniwill-Control-Utility\bin";
    private const string OemRoot = @"C:\Program Files\OEM\Control Center\GCUService";

    private static TakeoverPolicy.TargetObservation Obs(
        string name,
        string? path = null,
        bool pathExists = true,
        bool? signatureValid = true,
        string? publisher = "CN=\"Uniwill Computer Corp.\", O=Uniwill Computer Corp., C=TW") =>
        new()
        {
            Name = name,
            Path = path,
            PathExists = pathExists,
            SignatureValid = signatureValid,
            PublisherSubject = publisher,
        };

    // ---- The catalog ----------------------------------------------------

    [Fact]
    public void Catalogs_MatchTheReferenceVocabulary_CaseInsensitively()
    {
        Assert.True(OemTakeoverTargets.IsServiceName("gcubridge"));
        Assert.True(OemTakeoverTargets.IsServiceName("BLDHotKeyService"));
        Assert.False(OemTakeoverTargets.IsServiceName("GCUBridgeDuplex")); // exact names only
        Assert.True(OemTakeoverTargets.IsTaskName("CCU.WinUI"));
        Assert.True(OemTakeoverTargets.IsProcessName("ControlCenterU"));
        Assert.True(OemTakeoverTargets.IsProcessName("蛟龙游戏控制中心"));
        Assert.False(OemTakeoverTargets.IsProcessName("chrome"));
    }

    [Fact]
    public void KeywordMatch_CoversTheOemVocabulary_AndRejectsUnrelatedNames()
    {
        Assert.True(OemTakeoverTargets.MatchesKeyword(@"C:\Program Files\OEM\Control Center\CCU\CCU.exe"));
        Assert.True(OemTakeoverTargets.MatchesKeyword("OSDTpDetect"));
        Assert.True(OemTakeoverTargets.MatchesKeyword("蛟龙游戏控制中心"));
        Assert.False(OemTakeoverTargets.MatchesKeyword("Spooler"));
        Assert.False(OemTakeoverTargets.MatchesKeyword(""));
    }

    [Fact]
    public void PublisherMatch_AcceptsTheOemFamilyKeywords()
    {
        Assert.True(OemTakeoverTargets.MatchesPublisher("CN=\"Uniwill Computer Corp.\", O=Uniwill, C=TW"));
        Assert.True(OemTakeoverTargets.MatchesPublisher("CN=Mechrevo Technology Co., Ltd."));
        Assert.True(OemTakeoverTargets.MatchesPublisher("CN=XMG Computer Systems GmbH"));
        Assert.True(OemTakeoverTargets.MatchesPublisher("CN=PCSpecialist Ltd"));
        Assert.False(OemTakeoverTargets.MatchesPublisher("CN=Contoso Malware Ltd"));
        Assert.False(OemTakeoverTargets.MatchesPublisher(""));
    }

    [Fact]
    public void UcuRootGuard_IsOrdinalInsensitive_AndTheBackupPrefixIsUcusOwn()
    {
        Assert.True(OemTakeoverTargets.IsUnderUcuRoot($@"{UcuRoot}\Uniwill Control Utility.exe"));
        Assert.True(OemTakeoverTargets.IsUnderUcuRoot(@"c:\program files\uniwill-control-utility\x.exe"));
        Assert.False(OemTakeoverTargets.IsUnderUcuRoot($@"{OemRoot}\GCUService.exe"));
        // The reference uses "OpenRevoBackup_"; UCU's backups must never collide with it.
        Assert.Equal("UcuTakeoverBackup_", OemTakeoverTargets.RunKeyBackupPrefix);
    }

    // ---- The eligibility gate -------------------------------------------

    [Fact]
    public void Evaluate_NonMatchingName_IsNotTarget()
    {
        var verdict = TakeoverPolicy.Evaluate(
            Obs("PrintSpooler", path: @"C:\Windows\System32\spoolsv.exe"),
            keywordMatchedName: false);
        Assert.Equal(TakeoverPolicy.Eligibility.NotTarget, verdict);
    }

    [Fact]
    public void Evaluate_FullyVerifiedTarget_IsEligible()
    {
        var obs = Obs("GCUService", path: $@"{OemRoot}\GCUService.exe");
        Assert.Equal(TakeoverPolicy.Eligibility.Eligible, TakeoverPolicy.Evaluate(obs, keywordMatchedName: true));
    }

    [Fact]
    public void Evaluate_UcuOwnTree_IsSelfGuarded_EvenFullySigned()
    {
        // Field-proven: on the dev box the ONLY "uniwill" process match is
        // UCU itself. A keyword-only kill would decapitate the app. The guard
        // outranks everything, so it fires even for a valid OEM publisher.
        var obs = Obs("Uniwill Control Utility", path: $@"{UcuRoot}\Uniwill Control Utility.exe");
        Assert.Equal(TakeoverPolicy.Eligibility.UcuSelf, TakeoverPolicy.Evaluate(obs, keywordMatchedName: true));
    }

    [Fact]
    public void Evaluate_MissingPath_IsSquatSuspect()
    {
        var obs = Obs("GCUService", path: $@"{OemRoot}\GCUService.exe", pathExists: false);
        Assert.Equal(TakeoverPolicy.Eligibility.SquatSuspect, TakeoverPolicy.Evaluate(obs, keywordMatchedName: true));
    }

    [Fact]
    public void Evaluate_NoPath_IsSquatSuspect()
    {
        // A service with no ImagePath at all has no executable identity to verify.
        var obs = Obs("GCUService", path: null);
        Assert.Equal(TakeoverPolicy.Eligibility.SquatSuspect, TakeoverPolicy.Evaluate(obs, keywordMatchedName: true));
    }

    [Fact]
    public void Evaluate_UnsignedImage_IsSquatSuspect_NeverTouched()
    {
        var obs = Obs("GCUBridge", path: @"C:\Users\attacker\AppData\Local\GCUBridge.exe", signatureValid: false);
        Assert.Equal(TakeoverPolicy.Eligibility.SquatSuspect, TakeoverPolicy.Evaluate(obs, keywordMatchedName: true));
    }

    [Fact]
    public void Evaluate_SignedByForeignPublisher_IsSquatSuspect()
    {
        var obs = Obs(
            "ControlCenter",
            path: @"C:\Users\attacker\bin\ControlCenter.exe",
            publisher: "CN=Contoso Malware Ltd, O=Contoso, C=US");
        Assert.Equal(TakeoverPolicy.Eligibility.SquatSuspect, TakeoverPolicy.Evaluate(obs, keywordMatchedName: true));
    }

    [Fact]
    public void Evaluate_RegistryStateScope_IsSignatureExempt_ButStillSelfGuarded()
    {
        // UWP AppModel / StartupApproved flags: registry bytes, no file identity
        // (Path null, SignatureValid null) -> eligible. This is squat-immune
        // scope (package-identified), documented in TakeoverPolicy.
        var flag = Obs(
            @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\ControlCenter3_wrbgcf7aesyd8\ControlCenterTray",
            path: null,
            signatureValid: null);
        Assert.Equal(TakeoverPolicy.Eligibility.Eligible, TakeoverPolicy.Evaluate(flag, keywordMatchedName: true));

        // The self-guard applies to the registry scope too: a UCU-owned path
        // text in the target name never gets touched.
        var ucuFlag = Obs(@"Software\Uniwill-Control-Utility\Run\ControlCenter", path: null, signatureValid: null);
        Assert.Equal(TakeoverPolicy.Eligibility.UcuSelf, TakeoverPolicy.Evaluate(ucuFlag, keywordMatchedName: true));
    }

    [Fact]
    public void Evaluate_MsixPackagePayload_IsPackageIdentified_AndSignatureExempt()
    {
        // Field-proven on the tester laptop (pack #23 round): the UWP Control
        // Center's Win32 payload carries the AppX package signature, NOT
        // per-file Authenticode - WinVerifyTrust answers unsigned for every
        // file inside the package, and the gate refused the stack's own tray
        // helper. Package identity is squat-immune by construction: the
        // packages subsystem is the only writer of the WindowsApps tree.
        var payload = Obs(
            "SystrayComponent",
            path: @"C:\Program Files\WindowsApps\ControlCenter3_5.55.51.47_x64__wrbgcf7aesyd8\Win32\SystrayComponent.exe",
            signatureValid: false,
            publisher: null);
        Assert.Equal(TakeoverPolicy.Eligibility.Eligible, TakeoverPolicy.Evaluate(payload, keywordMatchedName: true));

        // A package path that no longer exists is not a target either.
        var gone = Obs(
            "SystrayComponent",
            path: @"C:\Program Files\WindowsApps\Gone_1.0_x64__x\Win32\SystrayComponent.exe",
            pathExists: false,
            signatureValid: false,
            publisher: null);
        Assert.Equal(TakeoverPolicy.Eligibility.SquatSuspect, TakeoverPolicy.Evaluate(gone, keywordMatchedName: true));
    }

    [Fact]
    public void PackageRoot_IsDetectedCaseInsensitively_AndNeverConfusedWithNormalProgramFiles()
    {
        Assert.True(OemTakeoverTargets.IsUnderPackageRoot(
            @"C:\Program Files\WindowsApps\ControlCenter3_5.55.51.47_x64__wrbgcf7aesyd8\Win32\SystrayComponent.exe"));
        Assert.True(OemTakeoverTargets.IsUnderPackageRoot(@"c:\program files\windowsapps\x\y.exe"));
        Assert.False(OemTakeoverTargets.IsUnderPackageRoot(@"C:\Program Files\OEM\Control Center\CCU\CCU.exe"));
        Assert.False(OemTakeoverTargets.IsUnderPackageRoot(string.Empty));

        // The ODM identity the Recoil 3 (TongFang) family ships under.
        Assert.True(OemTakeoverTargets.MatchesPublisher("CN=TongFang Computer Systems, O=TongFang, C=CN"));
    }

    [Fact]
    public void System32_IdentityExemption_IsDetectedCaseInsensitively()
    {
        // The OEM Control Center's "ControlCenter" task runs powershell.exe
        // (script-action task, field-proven): the executable is Windows' and
        // catalog-signed (no embedded signature to read), the task name is
        // OEM. System32 identity cannot be squatted without administrator.
        Assert.True(OemTakeoverTargets.IsUnderWindowsSystemDirectory(
            @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"));
        Assert.True(OemTakeoverTargets.IsUnderWindowsSystemDirectory(@"c:\windows\system32\cmd.exe"));
        Assert.False(OemTakeoverTargets.IsUnderWindowsSystemDirectory(
            @"C:\Program Files\OEM\Control Center\powershell.exe"));
        Assert.False(OemTakeoverTargets.IsUnderWindowsSystemDirectory(string.Empty));
    }

    // ---- The restore planner ---------------------------------------------

    private static TakeoverJournalEntry Entry(
        TakeoverMutationKind kind, string target, string before = "", string after = "", int minute = 0) =>
        new()
        {
            Kind = kind,
            Target = target,
            Before = before,
            After = after,
            Utc = new DateTime(2026, 11, 1, 12, minute, 0, DateTimeKind.Utc),
        };

    [Fact]
    public void PlanRestore_KillsDropOut()
    {
        var journal = new[]
        {
            Entry(TakeoverMutationKind.ProcessKilled, @"C:\OEM\CCU.exe"),
            Entry(TakeoverMutationKind.ServiceStart, "GCUBridge", before: "2", after: "3"),
        };

        var plan = TakeoverPolicy.PlanRestore(journal);

        Assert.Single(plan);
        Assert.Equal(TakeoverMutationKind.ServiceStart, plan[0].Kind);
    }

    [Fact]
    public void PlanRestore_DedupsKeepingTheNewestEntryPerTarget()
    {
        // Two pauses journal twice for the same run key; the restore replays
        // once, from the NEWEST entry (the live state).
        var journal = new[]
        {
            Entry(TakeoverMutationKind.RunKey, @"HKCU\Run\ControlCenter", before: "run1", after: "backup1", minute: 1),
            Entry(TakeoverMutationKind.RunKey, @"HKCU\RUN\controlcenter", before: "run2", after: "backup2", minute: 2),
        };

        var plan = TakeoverPolicy.PlanRestore(journal);

        Assert.Single(plan);
        Assert.Equal("run2", plan[0].Before); // newest wins, case-insensitive dedup
    }

    [Fact]
    public void PlanRestore_ServiceStartRestoresComeLast_WatchdogCascade()
    {
        // Re-arming services last prevents the stopped services' watchdogs
        // from re-killing mid-restore (the reference's cascade note).
        var journal = new[]
        {
            Entry(TakeoverMutationKind.ServiceStart, "GCUBridge", before: "2", after: "3"),
            Entry(TakeoverMutationKind.RunKey, @"HKLM\Run\ControlCenter", before: "run", after: "backup"),
            Entry(TakeoverMutationKind.Task, @"\ControlCenterTray", before: "1", after: "0"),
            Entry(TakeoverMutationKind.UwpState, @"ControlCenter3\ControlCenterTray", before: "2", after: "1"),
        };

        var plan = TakeoverPolicy.PlanRestore(journal);

        Assert.Equal(4, plan.Count);
        Assert.Equal(TakeoverMutationKind.ServiceStart, plan[^1].Kind);
        Assert.Equal(
            new[]
            {
                TakeoverMutationKind.RunKey,
                TakeoverMutationKind.Task,
                TakeoverMutationKind.UwpState,
                TakeoverMutationKind.ServiceStart,
            },
            plan.Select(e => e.Kind));
    }

    [Fact]
    public void PlanRestore_EmptyJournal_IsAnEmptyPlan_AndIsIdempotent()
    {
        var plan = TakeoverPolicy.PlanRestore(Array.Empty<TakeoverJournalEntry>());
        Assert.Empty(plan);

        // Feeding the plan back (the journal unchanged) yields the same plan.
        var journal = new[] { Entry(TakeoverMutationKind.Task, @"\GamingCenter", before: "1", after: "0") };
        var first = TakeoverPolicy.PlanRestore(journal);
        var second = TakeoverPolicy.PlanRestore(journal);
        Assert.Equal(first.Select(e => e.Target), second.Select(e => e.Target));
    }

    // ---- The status decider -------------------------------------------------

    private static TakeoverPolicy.Inventory Inv(
        int services = 0, int running = 0, int processes = 0, int journal = 0, int neutralized = 0) =>
        new()
        {
            ServicesInstalled = services,
            ServicesRunning = running,
            OemProcessesRunning = processes,
            JournalCount = journal,
            AutostartNeutralized = neutralized,
        };

    [Fact]
    public void DecideState_NothingInstalledNoJournal_IsNotInstalled()
    {
        // The dev box: probe-proven zero services/tasks/run-keys, zero journal.
        Assert.Equal(TakeoverPolicy.State.NotInstalled, TakeoverPolicy.DecideState(Inv()));
    }

    [Fact]
    public void DecideState_StackLiveNoJournal_IsActive()
    {
        Assert.Equal(TakeoverPolicy.State.Active, TakeoverPolicy.DecideState(Inv(services: 4, running: 4, processes: 3)));
    }

    [Fact]
    public void DecideState_PausedWithEvidence_IsPaused()
    {
        var inv = Inv(services: 4, running: 0, processes: 0, journal: 9, neutralized: 6);
        Assert.Equal(TakeoverPolicy.State.Paused, TakeoverPolicy.DecideState(inv));
    }

    [Fact]
    public void DecideState_EvidenceButSomethingLive_IsPartiallyPaused()
    {
        // A watchdog re-armed a service after the pause: audit-visible.
        var inv = Inv(services: 4, running: 1, processes: 0, journal: 9, neutralized: 6);
        Assert.Equal(TakeoverPolicy.State.PartiallyPaused, TakeoverPolicy.DecideState(inv));

        var procOnly = Inv(services: 4, running: 0, processes: 2, journal: 9);
        Assert.Equal(TakeoverPolicy.State.PartiallyPaused, TakeoverPolicy.DecideState(procOnly));
    }

    [Fact]
    public void DecideState_JournalSurvivesUninstall_IsStale()
    {
        var inv = Inv(services: 0, running: 0, processes: 0, journal: 5);
        Assert.Equal(TakeoverPolicy.State.Stale, TakeoverPolicy.DecideState(inv));
    }

    [Fact]
    public void DecideState_LiveStackWithoutPauseEvidence_IsActiveNotPartial()
    {
        // Someone started the stack manually, UCU never paused it: Active.
        var inv = Inv(services: 4, running: 2, processes: 1, journal: 0, neutralized: 0);
        Assert.Equal(TakeoverPolicy.State.Active, TakeoverPolicy.DecideState(inv));
    }
}
