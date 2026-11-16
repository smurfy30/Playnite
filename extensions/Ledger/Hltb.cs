using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Ledger
{
    internal class HltbTimes
    {
        public double MainHours { get; set; }
        public double ExtraHours { get; set; }
        public double CompletionistHours { get; set; }
        /// <summary>Best single "how long is this" number: main story, else main+extra, else 100%.</summary>
        public double Typical => MainHours > 0 ? MainHours : ExtraHours > 0 ? ExtraHours : CompletionistHours;
    }

    /// <summary>
    /// HowLongToBeat data via the HowLongToBeat add-on (Lacro59): we *read* its cache files
    /// directly, and to *fill* gaps we call its database's Get(id) through reflection. That's the same
    /// call its own "download missing" menu ends up making. No HLTB scraping of our own.
    /// </summary>
    internal static class Hltb
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        public static readonly Guid PluginId = Guid.Parse("e08cd51f-9c9a-4ee3-a094-fde03b55492f");

        private class Data { public long MainStoryClassic { get; set; } public long MainExtraClassic { get; set; } public long CompletionistClassic { get; set; }
                             public long MainStory { get; set; } public long MainExtra { get; set; } public long Completionist { get; set; } }
        private class Item { public Data GameHltbData { get; set; } }
        private class File_ { public List<Item> Items { get; set; } }

        private static string CacheDir(IPlayniteAPI api) =>
            Path.Combine(api.Paths.ExtensionsDataPath, PluginId.ToString(), "HowLongToBeat");

        public static Dictionary<Guid, HltbTimes> ReadAll(IPlayniteAPI api)
        {
            var result = new Dictionary<Guid, HltbTimes>();
            var dir = CacheDir(api);
            if (!Directory.Exists(dir)) return result;
            foreach (var f in Directory.GetFiles(dir, "*.json"))
            {
                if (!Guid.TryParse(Path.GetFileNameWithoutExtension(f), out var id)) continue;
                try
                {
                    var d = Serialization.FromJsonFile<File_>(f)?.Items?.FirstOrDefault()?.GameHltbData;
                    if (d == null) continue;
                    long Pick(long a, long b) => a > 0 ? a : b;
                    var t = new HltbTimes
                    {
                        MainHours = Pick(d.MainStoryClassic, d.MainStory) / 3600.0,
                        ExtraHours = Pick(d.MainExtraClassic, d.MainExtra) / 3600.0,
                        CompletionistHours = Pick(d.CompletionistClassic, d.Completionist) / 3600.0
                    };
                    if (t.Typical > 0) result[id] = t;
                }
                catch { }
            }
            return result;
        }

        /// <summary>Ask the HowLongToBeat add-on to fetch data for these games. Returns how many it tried.</summary>
        public static int Fill(IPlayniteAPI api, IEnumerable<Game> games, Action<int, int> progress = null, System.Threading.CancellationToken cancel = default)
        {
            var plugin = api.Addons.Plugins.FirstOrDefault(p => p.Id == PluginId);
            if (plugin == null) { logger.Warn("Ledger: HowLongToBeat add-on not installed"); return 0; }

            // PluginExtended<TSettings, TDatabase>.PluginDatabase is a static on the generic base.
            object db = null;
            for (var t = plugin.GetType(); t != null && db == null; t = t.BaseType)
            {
                var prop = t.GetProperty("PluginDatabase", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (prop != null) db = prop.GetValue(prop.GetGetMethod(true).IsStatic ? null : plugin);
                var field = db == null ? t.GetField("PluginDatabase", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance) : null;
                if (field != null) db = field.GetValue(field.IsStatic ? null : plugin);
            }
            // AddData(Game) is the add-on's non-interactive path: auto-accepts confident matches, and only
            // opens its picker if the user enabled "show when mismatch" (off by default). Get() always can.
            var get = db?.GetType().GetMethod("AddData", new[] { typeof(Game) });
            if (get == null) { logger.Warn("Ledger: couldn't find HowLongToBeat database AddData(Game); add-on version changed?"); return 0; }

            var have = ReadAll(api);
            var todo = games.Where(g => !have.ContainsKey(g.Id)).ToList();
            var done = 0;
            foreach (var g in todo)
            {
                if (cancel.IsCancellationRequested) break;
                try { get.Invoke(db, new object[] { g }); }
                catch (Exception e) { logger.Warn($"Ledger: HLTB fetch failed for {g.Name}: {e.InnerException?.Message ?? e.Message}"); }
                progress?.Invoke(++done, todo.Count);
                System.Threading.Thread.Sleep(400); // be gentle with howlongtobeat.com
            }
            logger.Info($"Ledger: HLTB fill tried {done} games");
            return done;
        }
    }
}
