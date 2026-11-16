using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;

namespace Ledger
{
    internal class WishEntry
    {
        public string AppId { get; set; }
        public string Name { get; set; }
        public DateTime Added { get; set; }
        public int Priority { get; set; }
        public decimal? List { get; set; }
        public decimal? Now { get; set; }
        public int DiscountPct => List > 0 && Now.HasValue ? (int)Math.Round(100 * (1 - Now.Value / List.Value)) : 0;
        public decimal? LowestSeen { get; set; }
        public int DaysTracked { get; set; }
        public GameHistory OwnedElsewhere { get; set; }
    }

    /// <summary>
    /// Steam wishlist with names (IStoreBrowseService/GetItems, public, cached) and price history built
    /// from Ledger's own daily/*.json price watch. "Lowest seen" only covers days we've been watching.
    /// </summary>
    internal static class WishlistIndex
    {
        private class Daily { public Dictionary<string, Tracking.WishItem> wishlist { get; set; } public Dictionary<string, Price> prices { get; set; } }

        public static List<WishEntry> Build(IPlayniteAPI api, string dir, IEnumerable<GameHistory> library)
        {
            var dailyDir = Path.Combine(dir, "daily");
            if (!Directory.Exists(dailyDir)) return new List<WishEntry>();
            var days = Directory.GetFiles(dailyDir, "*.json").OrderBy(f => f).Select(f =>
            {
                try { return Serialization.FromJsonFile<Daily>(f); } catch { return null; }
            }).Where(d => d != null).ToList();
            var latest = days.LastOrDefault(d => d.wishlist != null);
            if (latest == null) return new List<WishEntry>();

            var names = Names(dir, latest.wishlist.Keys);
            var libByNorm = library.Where(h => h.Game.PluginId != HistoryIndex.SteamPluginId)
                                   .GroupBy(h => UpNext.BaseName(h.Game.Name)).ToDictionary(g => g.Key, g => g.First());
            var result = new List<WishEntry>();
            foreach (var kv in latest.wishlist)
            {
                var seen = days.Where(d => d.prices != null && d.prices.ContainsKey(kv.Key) && d.prices[kv.Key].Now.HasValue).Select(d => d.prices[kv.Key]).ToList();
                var cur = seen.LastOrDefault();
                var name = names.TryGetValue(kv.Key, out var n) ? n : "Steam app " + kv.Key;
                result.Add(new WishEntry
                {
                    AppId = kv.Key,
                    Name = name,
                    Added = DateTimeOffset.FromUnixTimeSeconds(kv.Value.date_added).LocalDateTime,
                    Priority = kv.Value.priority,
                    List = cur?.List,
                    Now = cur?.Now,
                    LowestSeen = seen.Count > 0 ? seen.Min(p => p.Now) : null,
                    DaysTracked = seen.Count,
                    OwnedElsewhere = libByNorm.TryGetValue(UpNext.BaseName(name), out var h) ? h : null
                });
            }
            return result;
        }

        private class ItemsResp { public ItemsList response { get; set; } }
        private class ItemsList { public List<Item> store_items { get; set; } }
        private class Item { public string appid { get; set; } public string name { get; set; } }

        /// <summary>appid → store name, cached in state/app-names.json; fetches only unknown ids.</summary>
        public static Dictionary<string, string> Names(string dir, IEnumerable<string> ids)
        {
            var path = Path.Combine(dir, "state", "app-names.json");
            var cache = new Dictionary<string, string>();
            try { if (File.Exists(path)) cache = Serialization.FromJsonFile<Dictionary<string, string>>(path) ?? cache; } catch { }
            var todo = ids.Where(i => !cache.ContainsKey(i)).Distinct().ToList();
            for (var i = 0; i < todo.Count; i += 100)
            {
                var batch = todo.Skip(i).Take(100);
                var input = "{\"ids\":[" + string.Join(",", batch.Select(b => "{\"appid\":" + b + "}")) + "],\"context\":{\"language\":\"english\",\"country_code\":\"GB\"},\"data_request\":{}}";
                try
                {
                    using (var wc = new WebClient())
                    {
                        wc.Encoding = System.Text.Encoding.UTF8;
                        var json = wc.DownloadString("https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" + Uri.EscapeDataString(input));
                        foreach (var it in Serialization.FromJson<ItemsResp>(json)?.response?.store_items ?? new List<Item>())
                        {
                            if (!string.IsNullOrEmpty(it.name)) cache[it.appid] = it.name.Replace("™", "").Replace("®", "").Trim();
                        }
                    }
                }
                catch (Exception e) { LogManager.GetLogger().Warn($"Ledger: app names fetch failed: {e.Message}"); }
            }
            if (todo.Count > 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, Serialization.ToJson(cache, true));
            }
            return cache;
        }
    }
}
