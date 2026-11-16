using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace Ledger
{
    /// <summary>
    /// Registers installed emulators (Playnite built-in definitions) and one scanner per system folder.
    /// Idempotent: existing emulators/scanners (matched by built-in id / folder) are left alone.
    /// Source of truth for layout: E:\Emulators\&lt;Name&gt; (tools/install-emulators.py) and
    /// E:\emulator_games\&lt;system&gt; (tools/organise-roms.py).
    /// </summary>
    internal static class EmulatorSetup
    {
        private class Spec
        {
            public string Name, ConfigId, InstallDir;
            public string[] Profiles;
            public (string profile, string folder)[] Scanners;
        }

        private const string Games = @"E:\emulator_games";

        private static readonly Spec[] Specs =
        {
            new Spec { Name = "PCSX2", ConfigId = "pcsx2", InstallDir = @"C:\Program Files\PCSX2", Profiles = new[] { "Default QT" }, Scanners = new[] { ("Default QT", "ps2") } },
            new Spec { Name = "RPCS3", ConfigId = "rpcs3", InstallDir = @"E:\Emulators", Profiles = new[] { "Default QT6" }, Scanners = new[] { ("Default QT6", "ps3") } },
            new Spec { Name = "DuckStation", ConfigId = "duckstation", InstallDir = @"E:\Emulators\DuckStation", Profiles = new[] { "Default" }, Scanners = new[] { ("Default", "ps1") } },
            new Spec { Name = "PPSSPP", ConfigId = "ppsspp", InstallDir = @"E:\Emulators\PPSSPP", Profiles = new[] { "64bit" }, Scanners = new[] { ("64bit", "psp") } },
            new Spec { Name = "Dolphin", ConfigId = "dolphin", InstallDir = @"E:\Emulators\Dolphin", Profiles = new[] { "Nintendo GameCube", "Nintendo Wii" }, Scanners = new[] { ("Nintendo GameCube", "gc"), ("Nintendo Wii", "wii") } },
            new Spec { Name = "Cemu", ConfigId = "cemu", InstallDir = @"E:\Emulators\Cemu", Profiles = new[] { "Default" }, Scanners = new[] { ("Default", "wiiu") } },
            new Spec { Name = "melonDS", ConfigId = "melonds", InstallDir = @"E:\Emulators\melonDS", Profiles = new[] { "Default" }, Scanners = new[] { ("Default", "nds") } },
            new Spec { Name = "mGBA", ConfigId = "mgba", InstallDir = @"E:\Emulators\mGBA", Profiles = new[] { "Nintendo Game Boy", "Nintendo Game Boy Color", "Nintendo Game Boy Advance" },
                       Scanners = new[] { ("Nintendo Game Boy", "gb"), ("Nintendo Game Boy Color", "gbc"), ("Nintendo Game Boy Advance", "gba") } },
            new Spec { Name = "Azahar", ConfigId = "azahar", InstallDir = @"E:\Emulators\Azahar", Profiles = new[] { "Default" }, Scanners = new[] { ("Default", "3ds") } },
            new Spec { Name = "Eden", ConfigId = "eden", InstallDir = @"E:\Emulators\Eden", Profiles = new[] { "Default" }, Scanners = new[] { ("Default", "switch") } },
            // RetroArch: registered, but its per-core profiles only work once cores are downloaded in RetroArch.
            new Spec { Name = "RetroArch", ConfigId = "retroarch", InstallDir = @"E:\Emulators\RetroArch", Profiles = new string[0], Scanners = new (string, string)[0] },
        };

        public static string Run(IPlayniteAPI api)
        {
            var log = new List<string>();
            using (api.Database.BufferedUpdate())
            {
                foreach (var s in Specs)
                {
                    if (!Directory.Exists(s.InstallDir)) { log.Add($"skip {s.Name}: {s.InstallDir} missing"); continue; }
                    var emu = api.Database.Emulators.FirstOrDefault(e => e.BuiltInConfigId == s.ConfigId);
                    if (emu == null)
                    {
                        emu = new Emulator(s.Name) { BuiltInConfigId = s.ConfigId, InstallDir = s.InstallDir, BuiltinProfiles = new ObservableCollection<BuiltInEmulatorProfile>() };
                        foreach (var p in s.Profiles) emu.BuiltinProfiles.Add(new BuiltInEmulatorProfile { Name = p, BuiltInProfileName = p });
                        api.Database.Emulators.Add(emu);
                        log.Add($"added emulator {s.Name}");
                    }
                    foreach (var (profile, folder) in s.Scanners)
                    {
                        var dir = Path.Combine(Games, folder);
                        Directory.CreateDirectory(dir);
                        if (api.Database.GameScanners.Any(g => string.Equals(g.Directory, dir, StringComparison.OrdinalIgnoreCase))) continue;
                        var prof = emu.BuiltinProfiles?.FirstOrDefault(p => p.BuiltInProfileName == profile);
                        if (prof == null) { log.Add($"no profile {profile} on {s.Name}"); continue; }
                        api.Database.GameScanners.Add(new GameScannerConfig
                        {
                            Name = $"{s.Name}: {folder}",
                            EmulatorId = emu.Id,
                            EmulatorProfileId = prof.Id,
                            Directory = dir,
                            InGlobalUpdate = true,        // new files import on every library update
                            ScanSubfolders = true,
                            MergeRelatedFiles = true,     // multi-disc / cue+bin as one game
                            ExcludeOnlineFiles = true
                        });
                        log.Add($"added scanner {s.Name} → {folder}");
                    }
                }
            }
            var summary = string.Join("; ", log);
            LogManager.GetLogger().Info("Ledger emulator setup: " + summary);
            return summary;
        }
    }
}
