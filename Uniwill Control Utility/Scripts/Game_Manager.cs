using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using GameLib;
using GameLib.Core;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Uniwill_Control_Utility.Scripts.Intel_Backend;

namespace Uniwill_Control_Utility.Scripts
{
    internal class Game_Manager
    {
        public class GameLauncherItem
        {
            public string gameID { get; set; } = string.Empty;
            public string gameName { get; set; } = string.Empty;
            public string appType { get; set; } = string.Empty;
            public string launchCommand { get; set; } = string.Empty;
            public string path { get; set; } = string.Empty;
            public string exe { get; set; } = string.Empty;
            public string imageLocation { get; set; } = string.Empty;
            public string iconPath { get; set; } = string.Empty;
        }

        private static List<GameLauncherItem>? _installedGames;
        internal static List<GameLauncherItem>? InstalledGames
        {
            get => _installedGames;
            set => _installedGames = value;
        }
        // Keep backward compatibility reference
        internal static List<GameLauncherItem>? installedGames
        {
            get => _installedGames;
            set => _installedGames = value;
        }

        public static List<GameLauncherItem>? syncGame_Library(bool isAdaptive = false)
        {
            try
            {
                List<GameLauncherItem> list = new List<GameLauncherItem>();
                //gamelauncher
                LauncherManager gameLauncher = new LauncherManager(new GameLib.Core.LauncherOptions() { QueryOnlineData = false });

                foreach (var launcher in gameLauncher.GetLaunchers())
                {
                    switch (launcher.Name)
                    {
                        case "Steam":
                            foreach (var game in launcher.Games)
                            {
                                if (!game.Name.Contains("Steamworks") && !game.Name.Contains("SteamVR") && !game.Name.Contains("Google Earth") && !game.Name.Contains("Wallpaper Engine") && !game.Name.Contains("tModLoader") && !game.Name.Contains("- Original Soundtrack") && game.Id != "228980")
                                {
                                    // Skip games with no executables — nothing to launch
                                    if (!game.Executables.Any())
                                    {
                                        continue;
                                    }

                                    GameLauncherItem launcherItem = new GameLauncherItem();
                                    launcherItem.gameName = game.Name;
                                    launcherItem.gameID = game.Id;
                                    launcherItem.launchCommand = $"{launcher.Name}-{game.LaunchString}-{game.Id}-{game.Name}";

                                    if (game.Executables.Count() == 1)
                                    {
                                        launcherItem.path = game.InstallDir;
                                        launcherItem.exe = Path.GetFileNameWithoutExtension(game.Executables.First());
                                    }
                                    else
                                    {
                                        string[] array = launcherItem.gameName.Split(' ');
                                        foreach (string exe in game.Executables)
                                        {
                                            string exeName = Path.GetFileNameWithoutExtension(exe);
                                            if (game.Name.Contains("Call of duty", StringComparison.OrdinalIgnoreCase) && exeName.Contains("cod", StringComparison.OrdinalIgnoreCase))
                                            {
                                                launcherItem.path = game.InstallDir;
                                                launcherItem.exe = exeName;
                                                break;
                                            }
                                            if (array.Any(arr => exeName.Contains(arr, StringComparison.OrdinalIgnoreCase)))
                                            {
                                                launcherItem.path = game.InstallDir;
                                                launcherItem.exe = exeName;
                                            }
                                            if (launcherItem.path is not null)
                                            {
                                                break;
                                            }
                                        }
                                    }
                                    // Always fall back to InstallDir for path — it's reliable for Steam games.
                                    // Only fall back to the last exe name when we couldn't determine the exe.
                                    if (launcherItem.path == "")
                                    {
                                        launcherItem.path = game.InstallDir;
                                    }
                                    if (launcherItem.exe == "")
                                    {
                                        launcherItem.exe = Path.GetFileNameWithoutExtension(game.Executables.Last());
                                    }

                                    launcherItem.appType = launcher.Name;
                                    list.Add(launcherItem);
                                }
                            }
                            break;
                        case "Battle.net":
                            foreach (var game in launcher.Games)
                            {
                                GameLauncherItem launcherItem = new GameLauncherItem();
                                launcherItem.gameName = game.Name;
                                launcherItem.gameID = game.Id;
                                launcherItem.launchCommand = $"{launcher.Name}-{game.LaunchString}-{game.Id}-{game.Name}";
                                switch (game.Name)
                                {
                                    case "Call of Duty Black Ops Cold War":
                                        launcherItem.path = game.InstallDir;
                                        launcherItem.exe = "BlackOpsColdWar";
                                        break;

                                    default:
                                        launcherItem.path = game.InstallDir;
                                        launcherItem.exe = Path.GetFileNameWithoutExtension(launcherItem.path);
                                        break;
                                }

                                launcherItem.appType = launcher.Name;
                                list.Add(launcherItem);

                            }
                            break;
                        case "Epic Games":
                            foreach (var game in launcher.Games)
                            {
                                GameLauncherItem launcherItem = new GameLauncherItem();
                                launcherItem.gameName = game.Name;
                                launcherItem.gameID = game.Id;
                                launcherItem.launchCommand = $"{launcher.Name}-{game.LaunchString}-{game.Id}-{game.Name}";
                                launcherItem.path = game.InstallDir;
                                launcherItem.exe = Path.GetFileNameWithoutExtension(launcherItem.path);
                                launcherItem.appType = launcher.Name;
                                list.Add(launcherItem);

                            }
                            break;

                        default:
                            foreach (var game in launcher.Games)
                            {
                                GameLauncherItem launcherItem = new GameLauncherItem();
                                launcherItem.gameName = game.Name;
                                launcherItem.gameID = game.Id;
                                launcherItem.launchCommand = $"{launcher.Name}-{game.LaunchString}-{game.Id}-{game.Name}";
                                launcherItem.path = game.InstallDir;
                                launcherItem.exe = Path.GetFileNameWithoutExtension(launcherItem.path);
                                launcherItem.appType = launcher.Name;
                                list.Add(launcherItem);
                            }
                            break;

                    }

                }

                // Microsoft Store / Xbox entries: matched against each drive's XboxGames
                // folder below. Enumerated for the current user: PackageManager.FindPackages()
                // is the all-users enumeration and is documented to require administrative
                // privileges, which the app has not had since the engine service took the
                // privileged work - unelevated it threw AccessDenied out of the whole library
                // sync, so the game list came back empty behind a raw exception dialog.
                List<Package> storePackages = EnumerateCurrentUserStorePackages();

                DriveInfo[] allDrives = DriveInfo.GetDrives();

                foreach (DriveInfo d in allDrives)
                {
                    // Xbox Games live on local volumes. A disconnected mapped network
                    // drive makes Directory.Exists block in the redirector until it times
                    // out - measured 21-63 s per process pass on a dead host - and this
                    // loop runs on the startup path, so that is the startup stall this
                    // skip removes. A network share never carries an Xbox Games library.
                    if (d.DriveType == DriveType.Network)
                        continue;

                    try
                    {
                        string xboxGameDirectory = Path.Combine(d.Name, "XboxGames");
                        string[] filesInDirectory;
                        if (Directory.Exists(xboxGameDirectory))
                        {
                            filesInDirectory = Directory.GetDirectories(xboxGameDirectory);

                            if (filesInDirectory.Length > 0)
                            {
                                string[] strings = filesInDirectory.Select(x => Path.GetFileName(x)).ToArray();

                                if (strings.Length > 0)
                                {
                                    foreach (Package package in storePackages)
                                    {
                                        string install = package.InstalledLocation.Path;
                                        string sig = package.SignatureKind.ToString();

                                        if (install.Contains("WindowsApps") && sig == "Store" && !package.IsFramework && strings.Contains(package.DisplayName))
                                        {
                                            GameLauncherItem launcherItem = new GameLauncherItem();
                                            launcherItem.gameName = package.DisplayName;
                                            launcherItem.gameID = package.Id.FullName;
                                            launcherItem.launchCommand = $"Microsoft Store-{package.Id.FullName}-{package.DisplayName}";
                                            launcherItem.path = package.InstalledPath;
                                            launcherItem.appType = "Microsoft Store";
                                            launcherItem.imageLocation = package.Logo.AbsolutePath;
                                            list.Add(launcherItem);
                                        }

                                    }
                                }

                            }
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Skip inaccessible drives
                    }
                    catch (Exception ex) when (ex is not UnauthorizedAccessException)
                    {
                        Misc.DiagnosticLogger.LogError(ex, "Failed to process game library entry");
                    }
                }

                list = list.OrderBy(item => item.gameName).ToList();

                if (isAdaptive)
                {
                    var extraApps = new GameLauncherItem
                    {
                        gameName = "Yuzu",
                        path = "yuzu.exe",
                    };
                    list.Add(extraApps);

                    extraApps = new GameLauncherItem
                    {
                        gameName = "RPCS3",
                        path = "rpcs3.exe",
                    };
                    list.Add(extraApps);

                    extraApps = new GameLauncherItem
                    {
                        gameName = "Cemu",
                        path = "cemu.exe",
                    };
                    list.Add(extraApps);

                    extraApps = new GameLauncherItem
                    {
                        gameName = "Dolphin",
                        path = "Dolphin.exe",
                    };
                    list.Add(extraApps);

                    extraApps = new GameLauncherItem
                    {
                        gameName = "Citra",
                        path = "Citra.exe",
                    };
                    list.Add(extraApps);
                }

                var distinctGameLauncherItems = list.Distinct(new GameLauncherItemEqualityComparer()).ToList();
                return distinctGameLauncherItems;
            }
            catch (COMException ex)
            {
                // Windows Package Manager or GamesEnumeration COM calls can fail on restricted systems
                Misc.DiagnosticLogger.LogError(ex, "COM error while syncing game library");
                return null;
            }
            catch (Exception ex) when (ex is not COMException)
            {
                // A library sync runs on a startup thread with no user to answer a
                // dialog, and null means "no games" to every consumer: log the cause
                // and degrade.
                Misc.DiagnosticLogger.LogError(ex, "Unexpected error while syncing game library");
                return null;
            }
        }

        /// <summary>
        /// The current user's Microsoft Store packages, used by the XboxGames match.
        /// FindPackagesForUser with an empty SID is the documented current-user form
        /// and needs no privileges (the all-users FindPackages() does). Any failure
        /// degrades to an empty list and is logged: the Store entries are an addition to
        /// the launcher list, never a reason to lose it.
        /// </summary>
        private static List<Package> EnumerateCurrentUserStorePackages()
        {
            try
            {
                return new PackageManager().FindPackagesForUser(string.Empty).ToList();
            }
            catch (Exception ex)
            {
                Misc.DiagnosticLogger.LogError(ex, "Microsoft Store package enumeration unavailable");
                return new List<Package>();
            }
        }

        /// <summary>
        /// Launch a Microsoft Store app by package full name. FindPackageForUser with
        /// an empty SID is the current-user form and needs no privileges; FindPackage
        /// is the all-users lookup and is documented to require administrative ones, so
        /// unelevated it threw AccessDenied and the Launch button did nothing useful.
        /// A package with no app entry is a logged no-op: the list is built from the same
        /// enumeration, so that is a package removed after the list was built.
        /// </summary>
        private static void LaunchStoreApp(string packageFullName)
        {
            try
            {
                Package? package = new PackageManager().FindPackageForUser(string.Empty, packageFullName);
                var entries = package?.GetAppListEntries();
                if (entries is null || !entries.Any())
                {
                    Misc.DiagnosticLogger.LogInfo($"Microsoft Store package '{packageFullName}' has no launchable app entry");
                    return;
                }

                _ = entries[0].LaunchAsync();
            }
            catch (Exception ex)
            {
                Misc.DiagnosticLogger.LogError(ex, "Failed to launch Microsoft Store app");
            }
        }

        public static void LaunchApp(string gameID, string appType, string launchcommand, string appLocation)
        {
            if (appType == "Exe" && File.Exists(appLocation))
            {
                RunGame(appLocation);
            }
            else if (gameID != "")
            {
                switch (appType)
                {
                    case "Epic Games":
                    case "Steam":
                        RunLaunchString(launchcommand);
                        break;
                    case "Battle.net":
                        string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "C:\\Program Files (x86)";
                        string battlenetfile = Path.Combine(pf86, "Battle.net\\Battle.net.exe");
                        if (BattleNetRunning())
                        {
                            RunGame(battlenetfile, " --exec=\"launch " + gameID.ToUpper() + "\"");
                        }
                        else
                        {
                            RunGame(battlenetfile);
                            Thread.Sleep(15000);
                            RunGame(battlenetfile, " --exec=\"launch " + gameID.ToUpper() + "\"");
                        }
                        break;
                    case "GOG Galaxy":
                        string pfGog = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "C:\\Program Files (x86)";
                        RunGame(Path.Combine(pfGog, "GOG Galaxy", "GalaxyClient.exe"), " /command=runGame /gameId=" + gameID);
                        break;
                    case "Microsoft Store":
                        LaunchStoreApp(gameID);
                        break;
                }
            }
        }

        private static void RunGame(string command)
        {
            RunGame(command, string.Empty);
        }

        private static void RunGame(string command, string arguments)
        {
            // Shell-execute launch with arguments. This replaced Run_CLI's
            // RunCommand for the game-client CLI launches: Run_CLI set
            // Verb="runas" under UseShellExecute=false, where the verb is
            // inert (no elevation, no prompt) - the decoration is gone, the
            // launch shape is what it always effectively was.
            try
            {
                if (File.Exists(command))
                {
                    Process.Start(new ProcessStartInfo()
                    {
                        UseShellExecute = true,
                        FileName = Path.GetFullPath(command),
                        Arguments = arguments,
                        WorkingDirectory = Path.GetDirectoryName(command)
                    });
                }
            }
            catch (Exception ex)
            {
                Misc.DiagnosticLogger.LogError(ex, "Failed to launch game process");
            }
        }

        public static void RunLaunchString(string command)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo()
                {
                    UseShellExecute = true,
                    FileName = command
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                Misc.DiagnosticLogger.LogError(ex, "Failed to run launch string");
            }
        }

        public static bool BattleNetRunning()
        {
            return Process.GetProcessesByName("Battle.net.exe").Length > 0;
        }

        public class GameLauncherItemEqualityComparer : IEqualityComparer<GameLauncherItem>
        {
            public bool Equals(GameLauncherItem? x, GameLauncherItem? y)
            {
                // Check if two GameLauncherItems are equal based on gameID, gameName, and appType.
                if (x is null && y is null) return true;
                if (x is null || y is null) return false;
                return x.gameID == y.gameID && x.gameName == y.gameName && x.appType == y.appType;
            }

            public int GetHashCode(GameLauncherItem obj)
            {
                // Generate a hash code based on gameID, gameName, and appType.
                return (obj.gameID + obj.gameName + obj.appType).GetHashCode();
            }
        }

    }
}
