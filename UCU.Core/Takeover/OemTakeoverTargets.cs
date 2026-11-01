using System;
using System.Collections.Generic;

namespace UCU.Core.Takeover
{
    /// <summary>
    /// The OEM Control Center target vocabulary and the HARDENING gate
    /// (story U-203, hardened per the plan: audit weakness D.7). open-revo
    /// identifies its takeover targets by bare name (takeover/lifecycle.rs,
    /// sandbox.rs); a LocalSystem service that kills/stops by bare name can
    /// be hijacked by any user who names a process or service the same.
    /// UCU therefore mutates a target only when ALL of these hold:
    ///
    /// 1. The name hits this catalog (services/tasks: exact names;
    ///    processes/run-keys/UWP: the OEM keyword set).
    /// 2. The target carries an image path (service ImagePath, process
    ///    MainModule, run-key command, task action) and that file EXISTS.
    /// 3. The file is signed, chain-valid (the executor observes
    ///    WinVerifyTrust), and its signer subject CN / company carries an
    ///    allowlist keyword - the publisher evidence the plan mandates.
    ///    Unsigned = NEVER touched (the OEM stack is field-observed signed:
    ///    wiki runbook, the signed-XMG-vs-unsigned-UCU AV contrast).
    /// 4. The path is outside UCU's own install roots - the box runs
    ///    "Uniwill Control Utility" processes; a takeover that kills by the
    ///    "uniwill" keyword would be self-decapitation (field-proven by the
    ///    dev-box probe: UCU's own processes are the ONLY "uniwill" match).
    ///
    /// Two scopes are signature-exempt because their identity is not the file:
    /// registry state bytes (UWP AppModel / StartupApproved - package/task
    /// identity) and MSIX package payloads under the WindowsApps root (the
    /// AppX package signature covers them; per-file Authenticode does not).
    ///
    /// Targets that match the name catalog but fail 2-4 are never touched;
    /// they surface as squat-suspects in the status (audit trail, not action).
    /// </summary>
    public static class OemTakeoverTargets
    {
        /// <summary>The companion services (reference lifecycle.rs:21-28).
        /// Service identification is exact-name + gate: a squatting service
        /// that shares the name is refused at the publisher step.</summary>
        public static readonly IReadOnlyList<string> ServiceNames = new[]
        {
            "GCUBridge",
            "GCUService",
            "AiStoneService",
            "UniwillService",
            "OEMService",
            "BLDHotKeyService",
        };

        /// <summary>The scheduled tasks (reference sandbox.rs:24-37).</summary>
        public static readonly IReadOnlyList<string> TaskNames = new[]
        {
            "ControlCenter",
            "ControlCenterTray",
            "GamingCenter",
            "GCUService",
            "GCUBridge",
            "AiStone",
            "AiStoneService",
            "OSDTpDetect",
            "UniwillService",
            "CCU",
            "CCU.WinUI",
            "OEMService",
        };

        /// <summary>Processes to sweep on pause (reference lifecycle.rs:30-49).
        /// The Chinese-named CC keeps its literal: process identity is fully
        /// path+publisher-gated before a kill is even considered.</summary>
        public static readonly IReadOnlyList<string> ProcessNames = new[]
        {
            "gamingcenter",
            "gamingcenteru",
            "controlcenter",
            "controlcenteru",
            "ccu",
            "ccu.winui",
            "gcuservice",
            "gcubridge",
            "aistone",
            "aistoneservice",
            "mycontrolcenter",
            "uniwillservice",
            "osdtpdetect",
            "systraycomponent",
            "bldhotkeyservice",
            "bldfnhotkeyutility",
            "蛟龙游戏控制中心",
            "jiaolongcc",
        };

        /// <summary>The OEM autostart keyword set (reference sandbox.rs:144-153
        /// plus the reference process vocabulary). Matches are value/path-gated,
        /// never acted on bare.</summary>
        public static readonly IReadOnlyList<string> Keywords = new[]
        {
            "controlcenter",
            "gamingcenter",
            "gcuservice",
            "gcubridge",
            "gcuutil",
            "aistone",
            "mycontrolcenter",
            "uniwill",
            "osdtp",
            "systraycomponent",
            "ccu",
            "jiaolong",
            "蛟龙",
        };

        /// <summary>Publisher CN / company keywords accepted at the signature
        /// step (Uniwill ODM identities: field evidence is "the stack is
        /// signed", wiki runbook L151; the exact CNs come from the laptop
        /// probe - the keyword layer accepts the OEM family, a probe adds
        /// exact CNs on demand). "gcu"/"control center" ride the OEM
        /// convention that these stacks self-name their product line.</summary>
        public static readonly IReadOnlyList<string> PublisherKeywords = new[]
        {
            "uniwill",
            "mechrevo",
            "mechrevolution",
            "xmg",
            "aistone",
            "gaming center",
            "control center",
            "gcu",
            "pcspecialist",
            "tongfang",
        };

        /// <summary>Install roots owned by UCU: nothing under these is ever a
        /// takeover target, keyword match included (self-squat guard).</summary>
        public const string UcuOwnRootHint = "uniwill-control-utility";

        /// <summary>The journal prefix run-key backups carry (reference
        /// sandbox.rs:161 uses "OpenRevoBackup_"; UCU keeps its own identity
        /// so its backups never collide with a reference install's).</summary>
        public const string RunKeyBackupPrefix = "UcuTakeoverBackup_";

        public static bool IsServiceName(string name) =>
            ContainsExact(ServiceNames, name);

        public static bool IsTaskName(string name) =>
            ContainsExact(TaskNames, name);

        /// <summary>Process names compare WITHOUT the .exe suffix (the
        /// reference vocabulary is suffix-free; observations normalize first).</summary>
        public static bool IsProcessName(string nameWithoutExtension) =>
            ContainsExact(ProcessNames, nameWithoutExtension);

        public static bool MatchesKeyword(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            foreach (string keyword in Keywords)
                if (text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        public static bool MatchesPublisher(string publisherSubject)
        {
            if (string.IsNullOrEmpty(publisherSubject))
                return false;
            foreach (string keyword in PublisherKeywords)
                if (publisherSubject.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        /// <summary>True for paths under UCU's own install roots (ordinal-insensitive,
        /// the Windows path convention).</summary>
        public static bool IsUnderUcuRoot(string path) =>
            !string.IsNullOrEmpty(path)
            && path.IndexOf(UcuOwnRootHint, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>True for paths under the MSIX/Store package install root
        /// (the UWP Control Center's own payload, e.g.
        /// <c>...\Program Files\WindowsApps\ControlCenter3_*_x64_...\Win32\</c>).
        /// The packages subsystem is the only writer of that tree (package
        /// identity, ACL-protected), so a user cannot name-squat inside it -
        /// the same squat-immunity the registry-byte scopes carry. Package
        /// payloads are covered by the AppX package signature, NOT per-file
        /// Authenticode, so the file-identity clauses cannot apply there
        /// (field-proven on the tester laptop: SystrayComponent inside the
        /// package reads as unsigned).</summary>
        public static bool IsUnderPackageRoot(string path) =>
            !string.IsNullOrEmpty(path)
            && path.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>True for paths under the Windows system directory
        /// (System32). Executable identity there belongs to the OS: a user
        /// cannot write to it without administrator rights, so a task whose
        /// actions are all system binaries is name-squat-immune in the same
        /// way a package payload is (field-proven: the OEM Control Center's
        /// "ControlCenter" task runs "powershell.exe" with its script as the
        /// argument - the task is OEM, the executable is Windows', and the
        /// executable has no embedded signature to read because inbox
        /// binaries are catalog-signed).</summary>
        public static bool IsUnderWindowsSystemDirectory(string path) =>
            !string.IsNullOrEmpty(path)
            && path.IndexOf(@"\System32\", StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool ContainsExact(IReadOnlyList<string> list, string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            foreach (string item in list)
                if (string.Equals(item, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}
