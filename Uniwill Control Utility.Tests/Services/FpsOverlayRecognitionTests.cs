using System.Collections.Generic;
using System.Diagnostics;
using Uniwill_Control_Utility.Scripts;
using Uniwill_Control_Utility.Services.Performance;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// Two 0.9.13 field defects, pinned from the 2026-11-18 log.
/// (1) Resident Evil 9 (re9.exe, Steam) was hooked by RTSS — foreground PID and
/// RTSS entry matched, frames live — yet the OSD showed nothing, because the only
/// acceptance path was Game_Manager.installedGames and re9 was not in it. RTSS
/// tracking the foreground process is the strongest "something renders here"
/// signal, so it must accept the game; the installed list is an extra path.
/// (2) When the foreground PID had no RTSS entry, the reader substituted another
/// hooked process ("using fallback [30016] webwallpaper64.exe FPS=139"), so a
/// wallpaper engine's frame rate could be shown as the game's. No entry means no
/// FPS this tick, never a borrowed number.
/// </summary>
public sealed class FpsOverlayRecognitionTests
{
    private const string Re9Path = @"C:\Program Files (x86)\Steam\steamapps\common\RESIDENT EVIL requiem BIOHAZARD re9\re9.exe";

    [Fact]
    public void AHookedGameMissingFromTheInstalledListIsAccepted()
    {
        // The field case: RTSS tracks the foreground process, the list does not know it.
        Assert.Equal("rtss-tracked", FpsMonitoringService.AcceptanceRule("re9", Re9Path, rtssTracked: true));
    }

    [Fact]
    public void TheInstalledListIsAnExtraPathNotAGate()
    {
        List<Game_Manager.GameLauncherItem>? previous = Game_Manager.installedGames;
        try
        {
            Game_Manager.installedGames = new List<Game_Manager.GameLauncherItem>
            {
                new() { path = @"D:\Games\Doom", exe = "doometernal" }
            };

            // Listed game, no RTSS data, not a Steam path: the list is the rule.
            Assert.Equal("installed list",
                FpsMonitoringService.AcceptanceRule("doometernal", @"D:\Games\Doom\doometernal.exe", rtssTracked: false));

            // Unknown to the list but tracked by RTSS: still accepted.
            Assert.Equal("rtss-tracked",
                FpsMonitoringService.AcceptanceRule("re9", Re9Path, rtssTracked: true));
        }
        finally
        {
            Game_Manager.installedGames = previous;
        }
    }

    [Fact]
    public void ASteamLibraryPathIsAcceptedWithoutRtssData()
    {
        Assert.Equal("steam-library path",
            FpsMonitoringService.AcceptanceRule("re9", Re9Path, rtssTracked: false));
    }

    [Fact]
    public void NullOrEmptyInstalledListNeitherBlocksNorAllowsBlindly()
    {
        List<Game_Manager.GameLauncherItem>? previous = Game_Manager.installedGames;
        try
        {
            foreach (List<Game_Manager.GameLauncherItem>? list in new[]
                     {
                         null,
                         new List<Game_Manager.GameLauncherItem>()
                     })
            {
                Game_Manager.installedGames = list;

                // A genuinely tracked game is not blocked by the missing/empty list.
                Assert.Equal("rtss-tracked",
                    FpsMonitoringService.AcceptanceRule("re9", Re9Path, rtssTracked: true));

                // ... and an unrelated process is not allowed by it either.
                Assert.Null(FpsMonitoringService.AcceptanceRule("notepad", @"C:\Windows\notepad.exe", rtssTracked: false));
            }
        }
        finally
        {
            Game_Manager.installedGames = previous;
        }
    }

    [Fact]
    public void TheDesktopExplorerAndOurOwnProcessAreRejected()
    {
        Assert.Null(FpsMonitoringService.AcceptanceRule("explorer", @"C:\Windows\explorer.exe", rtssTracked: true));
        Assert.Null(FpsMonitoringService.AcceptanceRule("progman", @"C:\Windows\System32\progman.exe", rtssTracked: true));
        Assert.Null(FpsMonitoringService.AcceptanceRule("winlogon", @"C:\Windows\System32\winlogon.exe", rtssTracked: true));

        // "Our own process" is whichever process hosts the service — the test host here,
        // the app in the field. The name is captured when the guard set is built.
        string own = Process.GetCurrentProcess().ProcessName;
        Assert.Null(FpsMonitoringService.AcceptanceRule(own, null, rtssTracked: true));
    }

    [Fact]
    public void NoRtssEntryNeverYieldsAnotherProcessesFrameData()
    {
        // The field shape: foreground 18808 has no RTSS entry; the only entry is the
        // wallpaper engine 30016. Nothing may be reported for 18808.
        var apps = new List<(int Entry, int Pid)> { (0, 30016) };

        Assert.Equal(-1, RtssReader.PickEntryIndex(apps, 18808));

        // And the reader still answers for the process it does track.
        apps.Add((1, 46852));
        Assert.Equal(1, RtssReader.PickEntryIndex(apps, 46852));
        Assert.Equal(-1, RtssReader.PickEntryIndex(apps, 0));
    }
}
