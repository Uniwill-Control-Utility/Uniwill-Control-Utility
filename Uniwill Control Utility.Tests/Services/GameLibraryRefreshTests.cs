using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Uniwill_Control_Utility.Scripts;
using Uniwill_Control_Utility.Scripts.Misc;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The installed-games list used to be scanned once at startup and never again, so a game
/// installed with the app running stayed invisible (Resident Evil 9, 2026-11-18: absent
/// from Adaptive Mode's picker, and from the overlay's list path until 19cd9eb0). These
/// pin the refresh contract: a manifest burst is one debounced scan, an on-demand scan is
/// capped, a refresh keeps the owner's selection, and a launcher that is not installed is
/// a logged no-op.
/// </summary>
[Collection("Game library state")]
public sealed class GameLibraryRefreshTests
{
    private static readonly TimeSpan FastDebounce = TimeSpan.FromMilliseconds(20);

    [Fact]
    public async Task AManifestBurstIsExactlyOneRefresh()
    {
        int scans = 0;
        using var refresh = NewRefresh(() => scans++, debounce: FastDebounce);

        for (int i = 0; i < 6; i++)
            refresh.NotifyManifestChanged();

        await refresh.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));

        // The burst lands in one scan; WhenIdle only completes after the loop is idle.
        Assert.Equal(1, scans);
    }

    [Fact]
    public void ARefreshAddsNewGamesWithoutLosingTheSelection()
    {
        List<Game_Manager.GameLauncherItem>? previous = Game_Manager.installedGames;
        try
        {
            Game_Manager.installedGames = new List<Game_Manager.GameLauncherItem>
            {
                new() { gameName = "Doom Eternal", gameID = "782330", appType = "Steam" }
            };

            (int total, int added) = Game_Manager.ApplyGameLibraryScan(new List<Game_Manager.GameLauncherItem>
            {
                new() { gameName = "Doom Eternal", gameID = "782330", appType = "Steam" },
                new() { gameName = "RESIDENT EVIL 9", gameID = "2622600", appType = "Steam" }
            });

            Assert.Equal(2, total);
            Assert.Equal(1, added);

            // The game picked before the refresh is still picked afterwards...
            (List<string> items, int selected) = Game_Manager.BuildGamePickerItems(
                Game_Manager.installedGames, "Doom Eternal", "Default");
            Assert.Equal(items.IndexOf("Doom Eternal"), selected);
            Assert.Equal("Doom Eternal", items[selected]);

            // ... and the game installed mid-session is in the list.
            Assert.Contains("RESIDENT EVIL 9", items);

            // A selection that the refresh removed falls back to the Default entry.
            (List<string> after, int fallback) = Game_Manager.BuildGamePickerItems(
                new List<Game_Manager.GameLauncherItem> { new() { gameName = "Doom Eternal" } },
                "RESIDENT EVIL 9", "Default");
            Assert.Equal(0, fallback);
            Assert.Equal("Default", after[0]);
        }
        finally
        {
            Game_Manager.installedGames = previous;
        }
    }

    [Fact]
    public void AnEmptyScanKeepsThePublishedList()
    {
        List<Game_Manager.GameLauncherItem>? previous = Game_Manager.installedGames;
        try
        {
            Game_Manager.installedGames = new List<Game_Manager.GameLauncherItem>
            {
                new() { gameName = "Doom Eternal", gameID = "782330", appType = "Steam" }
            };

            (int total, int added) = Game_Manager.ApplyGameLibraryScan(new List<Game_Manager.GameLauncherItem>());

            Assert.Equal(1, total);
            Assert.Equal(0, added);
            Assert.Single(Game_Manager.installedGames!);
        }
        finally
        {
            Game_Manager.installedGames = previous;
        }
    }

    [Fact]
    public async Task TheCapSuppressesARefreshNoManifestChangeJustifies()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-11-18T08:00:00Z"));
        int scans = 0;
        using var refresh = NewRefresh(() => scans++, debounce: FastDebounce, time: clock);

        // Construction is right after the startup scan, so the page open is a no-op.
        Assert.False(await refresh.RefreshAsync("page shown"));
        Assert.Equal(0, scans);

        clock.Advance(GameLibraryRefresh.MinimumInterval);
        Assert.True(await refresh.RefreshAsync("page shown"));
        Assert.Equal(1, scans);

        // A second open inside the window does not re-walk the volumes...
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.False(await refresh.RefreshAsync("page shown"));
        Assert.Equal(1, scans);

        // ... but a manifest change is justification and bypasses the cap.
        refresh.NotifyManifestChanged();
        await refresh.WhenIdle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, scans);
    }

    [Fact]
    public void AMissingManifestRootIsALoggedNoOp()
    {
        string missing = Path.Combine(
            Path.GetTempPath(), "ucu-no-such-steam-root-" + Guid.NewGuid().ToString("N"));
        var logged = new List<string>();

        void Handler(string line)
        {
            if (line.Contains(missing, StringComparison.Ordinal))
                logged.Add(line);
        }

        DiagnosticLogger.OnMessage += Handler;
        try
        {
            int scans = 0;
            using var refresh = new GameLibraryRefresh(
                () => { scans++; return new List<Game_Manager.GameLauncherItem>(); },
                scan => (scan?.Count ?? 0, 0),
                () => new[] { new GameLibraryRefresh.ManifestWatch(missing, "appmanifest_*.acf") },
                debounce: FastDebounce);

            refresh.StartWatching();

            Assert.Equal(0, refresh.WatcherCount);
            Assert.Contains(logged, line => line.Contains("no manifest directory", StringComparison.Ordinal));
            Assert.Equal(0, scans);
        }
        finally
        {
            DiagnosticLogger.OnMessage -= Handler;
        }
    }

    [Fact]
    public void SteamAppsRootsComeFromTheScanAndThePrimaryInstall()
    {
        var games = new List<Game_Manager.GameLauncherItem>
        {
            new() { appType = "Steam", path = @"D:\SteamLibrary\steamapps\common\RESIDENT EVIL 9" },
            new() { appType = "Epic Games", path = @"C:\Program Files\Epic\SomeGame" },
            new() { appType = "Steam", path = "" }
        };

        List<string> roots = Game_Manager
            .SteamAppsRoots(games, @"C:\Program Files (x86)\Steam")
            .ToList();

        Assert.Equal(2, roots.Count);
        Assert.Contains(@"C:\Program Files (x86)\Steam\steamapps", roots);
        Assert.Contains(@"D:\SteamLibrary\steamapps", roots);
    }

    private static GameLibraryRefresh NewRefresh(
        Action onScan, TimeSpan debounce, TimeProvider? time = null)
    {
        return new GameLibraryRefresh(
            () =>
            {
                onScan();
                return new List<Game_Manager.GameLauncherItem>
                {
                    new() { gameName = "Doom Eternal", gameID = "782330", appType = "Steam" }
                };
            },
            scan => (scan?.Count ?? 0, scan?.Count ?? 0),
            () => Array.Empty<GameLibraryRefresh.ManifestWatch>(),
            time,
            debounce);
    }
}

/// <summary>
/// These tests drive the static installed-games list, so they must not run alongside the
/// overlay tests that swap the same list.
/// </summary>
[CollectionDefinition("Game library state", DisableParallelization = true)]
public sealed class GameLibraryStateCollection;
