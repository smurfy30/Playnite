using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Ledger
{
    /// <summary>
    /// Repairs names where a ™/®/© was mangled into "?" on import (Epic does this: "Rocket League?").
    /// A trailing/embedded "?" is only treated as mangled when a store record (Epic order / Steam
    /// license) has the same name with a symbol in that spot, so "What The Dub?!" is left alone.
    /// Also strips real ™/®/© so names match metadata sources. Renames are logged with the old name.
    /// </summary>
    internal class TidyNames
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly Regex Symbols = new Regex("[™®©]");
        private readonly IPlayniteAPI api;
        private readonly string dataDir;
        private string LogPath => Path.Combine(dataDir, "renames.json");

        public class Rename { public Guid GameId { get; set; } public string From { get; set; } public string To { get; set; } public DateTime At { get; set; } }

        public TidyNames(IPlayniteAPI api, string dataDir)
        {
            this.api = api;
            this.dataDir = dataDir;
        }

        private HashSet<string> StoreNames()
        {
            var names = new HashSet<string>();
            foreach (var f in new[] { "epic-orders.json", "steam-licenses.json" })
            {
                var p = Path.Combine(dataDir, f);
                if (!File.Exists(p)) continue;
                try
                {
                    foreach (var l in Serialization.FromJsonFile<List<SteamLicense>>(p) ?? new List<SteamLicense>()) names.Add(l.Name);
                }
                catch { }
            }
            return names;
        }

        /// <summary>Proposed renames, not applied.</summary>
        public List<Rename> Plan()
        {
            var store = StoreNames();
            var result = new List<Rename>();
            foreach (var g in api.Database.Games)
            {
                var name = g.Name ?? "";
                var fixedName = name;
                if (name.Contains("?"))
                {
                    // Does some store record read the same with a symbol where we have "?"?
                    var pattern = "^" + Regex.Escape(name).Replace(@"\?", "[™®©]") + "$";
                    if (store.Any(s => Regex.IsMatch(s, pattern))) fixedName = name.Replace("?", "");
                }
                fixedName = Regex.Replace(Symbols.Replace(fixedName, ""), @"\s{2,}", " ").Trim();
                // Emulated games (no store plugin) carry Redump/No-Intro catalogue names, which metadata
                // sources don't match: "Simpsons, The - Hit & Run" → "The Simpsons: Hit & Run".
                if (g.PluginId == Guid.Empty) fixedName = CatalogueToTitle(fixedName);
                if (fixedName != name && fixedName.Length > 0)
                {
                    result.Add(new Rename { GameId = g.Id, From = name, To = fixedName, At = DateTime.Now });
                }
            }
            return result;
        }

        internal static string CatalogueToTitle(string name)
        {
            var m = Regex.Match(name, @"^(?<main>.+?), (?<art>The|A|An)(?<rest>( - .*)?)$");
            if (m.Success) name = $"{m.Groups["art"].Value} {m.Groups["main"].Value}{m.Groups["rest"].Value}";
            return Regex.Replace(name, @" - ", ": ");
        }

        public List<Rename> Apply()
        {
            var plan = Plan();
            if (plan.Count == 0) return plan;
            using (api.Database.BufferedUpdate())
            {
                foreach (var r in plan)
                {
                    var g = api.Database.Games.Get(r.GameId);
                    if (g == null) continue;
                    g.Name = r.To;
                    api.Database.Games.Update(g);
                }
            }
            var log = File.Exists(LogPath) ? Serialization.FromJsonFile<List<Rename>>(LogPath) ?? new List<Rename>() : new List<Rename>();
            log.AddRange(plan);
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(LogPath, Serialization.ToJson(log, true));
            logger.Info("Ledger tidy names: " + string.Join("; ", plan.Select(r => $"{r.From} → {r.To}")));
            return plan;
        }
    }
}
