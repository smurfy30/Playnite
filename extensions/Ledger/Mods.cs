using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Ledger
{
    internal class ModEntry
    {
        public string Name { get; set; }
        public string Source { get; set; }   // "Workshop" | "Vortex" | "r2modman"
        public string Id { get; set; }        // workshop id / folder / thunderstore name
        public long? Size { get; set; }
        public DateTime? Updated { get; set; }
        public bool? Enabled { get; set; }
        public string Url { get; set; }
    }

    internal class GameMods
    {
        public Game Game { get; set; }
        public string Label { get; set; }     // manager's own name for the game, if unmatched
        public List<ModEntry> Mods { get; } = new List<ModEntry>();
        public long TotalSize => Mods.Sum(m => m.Size ?? 0);
        public DateTime? LastUpdated => Mods.Max(m => m.Updated);
    }

    /// <summary>
    /// Read-only index of installed mods across managers: Steam Workshop (appworkshop_*.acf in every
    /// library), Vortex (staging folders; hash-named deployment caches skipped, as pc-status does),
    /// and r2modman/Thunderstore profiles (mods.yml). Never writes to game or manager folders.
    /// </summary>
    internal static class ModIndex
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly Regex VortexHashDir = new Regex(@"^[0-9a-f]{2}_[0-9a-f]{2}_[0-9a-f]{2}_[0-9a-f-]{36}$", RegexOptions.IgnoreCase);

        // Vortex game id → Steam app id (to find the Playnite game). Extend as needed.
        private static readonly Dictionary<string, string> VortexToSteam = new Dictionary<string, string>
        {
            ["skyrimse"] = "489830", ["skyrim"] = "72850", ["fallout4"] = "377160", ["falloutnv"] = "22380",
            ["fallout3"] = "22370", ["fallout3_orig"] = "22300", ["witcher3"] = "292030", ["baldursgate3"] = "1086940", ["palworld"] = "1623730",
            ["batmanarkhamknight"] = "208650", ["borderlands4"] = "1285190", ["cyberpunk2077"] = "1091500",
            ["stardewvalley"] = "413150", ["oblivion"] = "22330", ["starfield"] = "1716740"
        };

        public static List<GameMods> Build(IPlayniteAPI api, string ledgerDir)
        {
            var steamGames = api.Database.Games.Where(g => g.PluginId == HistoryIndex.SteamPluginId && g.GameId != null)
                                               .GroupBy(g => g.GameId).ToDictionary(g => g.Key, g => g.First());
            var byNorm = api.Database.Games.GroupBy(g => LicenseParsing.Norm(g.Name)).ToDictionary(g => g.Key, g => g.First());
            var result = new Dictionary<string, GameMods>();

            GameMods For(Game game, string label)
            {
                var key = game?.Id.ToString() ?? "x:" + label;
                if (!result.TryGetValue(key, out var gm)) result[key] = gm = new GameMods { Game = game, Label = label };
                return gm;
            }

            Safe("workshop", () => Workshop(steamGames, For, ledgerDir));
            Safe("vortex", () => Vortex(steamGames, byNorm, For));
            Safe("r2modman", () => R2modman(byNorm, For));
            return result.Values.Where(g => g.Mods.Count > 0).OrderByDescending(g => g.Mods.Count).ToList();
        }

        private static void Safe(string what, Action a)
        {
            try { a(); }
            catch (Exception e) { logger.Error(e, $"Ledger mods: {what} scan failed"); }
        }

        // ---------- Steam Workshop ----------

        private static IEnumerable<string> SteamLibraries()
        {
            var steam = SteamLocalStats.SteamDir();
            var libs = new List<string> { steam };
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s*\"([^\"]+)\""))
                {
                    libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
                }
            }
            return libs.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Name from steamapps/appmanifest_{appId}.acf in any library, else "Steam app {id}".</summary>
        private static string AppName(string appId)
        {
            foreach (var lib in SteamLibraries())
            {
                var acf = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
                if (!File.Exists(acf)) continue;
                var m = Regex.Match(File.ReadAllText(acf), "\"name\"\\s*\"([^\"]+)\"");
                if (m.Success) return m.Groups[1].Value + " (not in library)";
            }
            return "Steam app " + appId;
        }

        private static readonly Regex AcfItem = new Regex(
            "\"(?<id>\\d+)\"\\s*\\{\\s*\"size\"\\s*\"(?<size>\\d+)\"\\s*\"timeupdated\"\\s*\"(?<t>\\d+)\"", RegexOptions.Singleline);

        private static void Workshop(Dictionary<string, Game> steamGames, Func<Game, string, GameMods> forGame, string ledgerDir)
        {
            var titles = WorkshopTitles.Load(ledgerDir);
            foreach (var lib in SteamLibraries())
            {
                var dir = Path.Combine(lib, "steamapps", "workshop");
                if (!Directory.Exists(dir)) continue;
                foreach (var acf in Directory.GetFiles(dir, "appworkshop_*.acf"))
                {
                    var appId = Path.GetFileNameWithoutExtension(acf).Substring("appworkshop_".Length);
                    var text = File.ReadAllText(acf);
                    var installed = text.IndexOf("\"WorkshopItemsInstalled\"", StringComparison.Ordinal);
                    var details = text.IndexOf("\"WorkshopItemDetails\"", StringComparison.Ordinal);
                    if (installed < 0) continue;
                    var block = details > installed ? text.Substring(installed, details - installed) : text.Substring(installed);
                    steamGames.TryGetValue(appId, out var game);
                    foreach (Match m in AcfItem.Matches(block))
                    {
                        var id = m.Groups["id"].Value;
                        forGame(game, AppName(appId)).Mods.Add(new ModEntry
                        {
                            Source = "Workshop",
                            Id = id,
                            Name = titles.TryGetValue(id, out var t) ? t : "Workshop item " + id,
                            Size = long.Parse(m.Groups["size"].Value),
                            Updated = DateTimeOffset.FromUnixTimeSeconds(long.Parse(m.Groups["t"].Value)).LocalDateTime,
                            Url = "https://steamcommunity.com/sharedfiles/filedetails/?id=" + id
                        });
                    }
                }
            }
        }

        // ---------- Vortex ----------

        private static void Vortex(Dictionary<string, Game> steamGames, Dictionary<string, Game> byNorm, Func<Game, string, GameMods> forGame)
        {
            var roots = new List<KeyValuePair<string, string>>(); // gameId → mods dir
            foreach (var root in new[] { @"D:\Vortex Mods", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vortex") })
            {
                if (!Directory.Exists(root)) continue;
                foreach (var gameDir in Directory.GetDirectories(root))
                {
                    var id = Path.GetFileName(gameDir).ToLowerInvariant();
                    var mods = root.EndsWith("Vortex") ? Path.Combine(gameDir, "mods") : gameDir;
                    if (Directory.Exists(mods) && (VortexToSteam.ContainsKey(id) || root.EndsWith("Vortex Mods")))
                    {
                        if (!roots.Any(r => r.Key == id)) roots.Add(new KeyValuePair<string, string>(id, mods));
                    }
                }
            }

            foreach (var root in roots)
            {
                var gameId = root.Key;
                var dir = root.Value;
                Game game = null;
                if (VortexToSteam.TryGetValue(gameId, out var app)) steamGames.TryGetValue(app, out game);
                if (game == null && VortexToSteam.TryGetValue(gameId + "_orig", out var app2)) steamGames.TryGetValue(app2, out game);
                if (game == null) byNorm.TryGetValue(gameId, out game);
                foreach (var modDir in Directory.GetDirectories(dir))
                {
                    var name = Path.GetFileName(modDir);
                    if (VortexHashDir.IsMatch(name) || name.StartsWith("__") || name.Equals("mods", StringComparison.OrdinalIgnoreCase)) continue;
                    forGame(game, "Vortex: " + gameId).Mods.Add(new ModEntry
                    {
                        Source = "Vortex",
                        Id = name,
                        Name = CleanVortexName(name),
                        Updated = Directory.GetLastWriteTime(modDir)
                    });
                }
            }
        }

        /// <summary>"SkyUI-12604-5-2SE-1572466209" → "SkyUI" (Nexus archive naming: name-modid-version-timestamp).</summary>
        private static string CleanVortexName(string folder)
        {
            var m = Regex.Match(folder, @"^(?<name>.+?)-\d{2,7}-[\d-]+(\w*)-\d{9,11}$");
            var name = m.Success ? m.Groups["name"].Value : folder;
            name = Regex.Replace(name, @"\.(zip|7z|rar)$", "", RegexOptions.IgnoreCase);
            name = Regex.Replace(name, "-{2,}", " - ").Replace('_', ' ');
            return Regex.Replace(name, @"\s{2,}", " ").Trim();
        }

        // ---------- r2modman / Thunderstore ----------

        private static void R2modman(Dictionary<string, Game> byNorm, Func<Game, string, GameMods> forGame)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (var root in new[] { Path.Combine(appData, "r2modmanPlus-local"), Path.Combine(appData, "Thunderstore Mod Manager", "DataFolder") })
            {
                if (!Directory.Exists(root)) continue;
                foreach (var gameDir in Directory.GetDirectories(root))
                {
                    var profiles = Path.Combine(gameDir, "profiles");
                    if (!Directory.Exists(profiles)) continue;
                    var gameName = Path.GetFileName(gameDir);
                    byNorm.TryGetValue(LicenseParsing.Norm(gameName), out var game);
                    foreach (var profile in Directory.GetDirectories(profiles))
                    {
                        var yml = Path.Combine(profile, "mods.yml");
                        if (!File.Exists(yml)) continue;
                        foreach (var mod in ParseModsYml(File.ReadAllText(yml)))
                        {
                            mod.Name += Path.GetFileName(profile) == "Default" ? "" : $"  ({Path.GetFileName(profile)})";
                            forGame(game, "r2modman: " + gameName).Mods.Add(mod);
                        }
                    }
                }
            }
        }

        private static IEnumerable<ModEntry> ParseModsYml(string yml)
        {
            // Not a general YAML parser: mods.yml is a flat list of "- manifestVersion:" records.
            foreach (var rec in Regex.Split(yml, @"^- ", RegexOptions.Multiline).Skip(1))
            {
                string Field(string k)
                {
                    var m = Regex.Match(rec, $@"^\s*{k}:\s*(?:>-\s*\n\s*)?(?<v>.+)$", RegexOptions.Multiline);
                    return m.Success ? m.Groups["v"].Value.Trim().Trim('\'', '"') : null;
                }
                var installed = long.TryParse(Field("installedAtTime"), out var ms) ? (DateTime?)DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime : null;
                var major = Regex.Match(rec, @"major:\s*(\d+)\s*\n\s*minor:\s*(\d+)\s*\n\s*patch:\s*(\d+)");
                yield return new ModEntry
                {
                    Source = "r2modman",
                    Id = Field("name"),
                    Name = (Field("displayName") ?? Field("name")) + (major.Success ? $" {major.Groups[1].Value}.{major.Groups[2].Value}.{major.Groups[3].Value}" : ""),
                    Updated = installed,
                    Enabled = Field("enabled") != "false",
                    Url = Field("websiteUrl")
                };
            }
        }
    }

    /// <summary>
    /// Workshop item titles via ISteamRemoteStorage/GetPublishedFileDetails (public, no API key),
    /// cached in workshop-titles.json so it's one network round per new item, ever.
    /// </summary>
    internal static class WorkshopTitles
    {
        public class Details { public string publishedfileid { get; set; } public int result { get; set; } public string title { get; set; } }
        public class DetailsList { public List<Details> publishedfiledetails { get; set; } }
        public class DetailsResponse { public DetailsList response { get; set; } }

        private static string PathFor(string dir) => Path.Combine(dir, "workshop-titles.json");

        public static Dictionary<string, string> Load(string dir)
        {
            var p = PathFor(dir);
            try { return File.Exists(p) ? Serialization.FromJsonFile<Dictionary<string, string>>(p) ?? new Dictionary<string, string>() : new Dictionary<string, string>(); }
            catch { return new Dictionary<string, string>(); }
        }

        /// <returns>number of titles newly fetched</returns>
        public static int FetchMissing(string dir, IEnumerable<string> ids)
        {
            var cache = Load(dir);
            var missing = ids.Where(i => !cache.ContainsKey(i)).Distinct().ToList();
            var fetched = 0;
            for (var i = 0; i < missing.Count; i += 100)
            {
                var batch = missing.Skip(i).Take(100).ToList();
                var body = new StringBuilder("itemcount=" + batch.Count);
                for (var j = 0; j < batch.Count; j++) body.Append($"&publishedfileids[{j}]={batch[j]}");
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded";
                    var json = wc.UploadString("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/", body.ToString());
                    var resp = Serialization.FromJson<DetailsResponse>(json);
                    foreach (var d in resp?.response?.publishedfiledetails ?? new List<Details>())
                    {
                        if (d.result == 1 && !string.IsNullOrEmpty(d.title))
                        {
                            cache[d.publishedfileid] = d.title;
                            fetched++;
                        }
                    }
                }
            }
            if (fetched > 0)
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(PathFor(dir), Serialization.ToJson(cache, true));
            }
            return fetched;
        }
    }
}
