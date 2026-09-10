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

        public static List<GameLauncherItem>? installedGames;

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
                                            foreach (string arr in array)
                                            {
                                                if (exeName.Contains(arr, StringComparison.OrdinalIgnoreCase))
                                                {
                                                    launcherItem.path = game.InstallDir;
                                                    launcherItem.exe = exeName;
                                                    break;
                                                }
                                            }
                                            if (launcherItem.path is not null)
                                            {
                                                break;
                                            }
                                        }
                                    }
                                    if (launcherItem.path == "" || launcherItem.exe == "")
                                    {
                                        launcherItem.path = Path.GetFileNameWithoutExtension(game.Executables.Last());
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

                //microsoft store apps below

                PackageManager packageManager = new PackageManager();
                IEnumerable<Windows.ApplicationModel.Package> packages = packageManager.FindPackages();

                DriveInfo[] allDrives = DriveInfo.GetDrives();

                foreach (DriveInfo d in allDrives)
                {
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
                                    foreach (Package package in packages)
                                    {
                                        string install = package.InstalledLocation.Path;
                                        string sig = package.SignatureKind.ToString();

                                        if (install.Contains("WindowsApps") && sig == "Store" && package.IsFramework == false && strings.Contains(package.DisplayName))
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
                MessageBox.Show(ex.ToString());
                return null;
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
                            Run_CLI.RunCommand(" --exec=\"launch " + gameID.ToUpper() + "\"", false, battlenetfile, 3000, true);
                        }
                        else
                        {
                            RunGame(battlenetfile);
                            Thread.Sleep(15000);
                            Run_CLI.RunCommand(" --exec=\"launch " + gameID.ToUpper() + "\"", false, battlenetfile, 3000, true);
                        }
                        break;
                    case "GOG Galaxy":
                        string pfGog = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? "C:\\Program Files (x86)";
                        Run_CLI.RunCommand(" /command=runGame /gameId=" + gameID, false, Path.Combine(pfGog, "GOG Galaxy", "GalaxyClient.exe"));
                        break;
                    case "Microsoft Store":
                        PackageManager pm = new PackageManager();
                        _ = pm.FindPackage(gameID).GetAppListEntries().First().LaunchAsync();
                        break;
                }
            }
        }

        private static void RunGame(string command)
        {
            try
            {
                if (File.Exists(command))
                {
                    Process.Start(new ProcessStartInfo()
                    {
                        UseShellExecute = true,
                        FileName = Path.GetFullPath(command),
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
