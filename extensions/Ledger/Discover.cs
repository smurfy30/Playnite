using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;

namespace Ledger
{
    public class FreeGame
    {
        public string Title { get; set; }
        public string Store { get; set; }
        public string Url { get; set; }
        public DateTime? Starts { get; set; }
        public DateTime? Ends { get; set; }
        public string Worth { get; set; }
        public string ImageUrl { get; set; }
        public string Note { get; set; }      // e.g. "requires account on X"
        public bool Upcoming => Starts.HasValue && Starts > DateTime.Now;
    }

    public class Rec
    {
        public string AppId { get; set; }
        public string Name { get; set; }
        public double Score { get; set; }
        public int Positive { get; set; }
        public int Negative { get; set; }
        public int ReviewPct => Positive + Negative == 0 ? 0 : (int)Math.Round(100.0 * Positive / (Positive + Negative));
        public decimal? PriceUsd { get; set; }
        public int Discount { get; set; }
        public List<string> Because { get; set; } = new List<string>();   // matching tags
        public string LikeGame { get; set; }                              // "more like X" row
    }

    /// <summary>
    /// Discover: things outside the library.
    ///  - Free right now: Epic's official promotions feed + GamerPower giveaways (Steam, GOG). Daily.
    ///  - Recommendations: SteamSpy tag profile of your most-played Steam games → top games per tag,
    ///    excluding anything you own (any store), ranked by tag match × reviews × popularity. Weekly.
    /// All network results are cached in state/; the view only ever reads the cache.
    /// </summary>
    internal class Discover
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly IPlayniteAPI api;
        private readonly string state;

        public Discover(IPlayniteAPI api, string dataDir)
        {
            this.api = api;
            state = Path.Combine(dataDir, "state");
        }

        private string FreePath => Path.Combine(state, "free-games.json");
        private string RecsPath => Path.Combine(state, "recommendations.json");
        private string SpyDir => Path.Combine(state, "steamspy");
        public string CoverDir => Path.Combine(state, "covers");

        private static string Get(string url)
        {
            using (var wc = new WebClient())
            {
                wc.Encoding = System.Text.Encoding.UTF8;
                wc.Headers[HttpRequestHeader.UserAgent] = "Ledger-Playnite/0.1 (personal library tool)";
                return wc.DownloadString(url);
            }
        }

        // ---------------- free games ----------------

        public List<FreeGame> LoadFree()
        {
            try { return File.Exists(FreePath) ? Serialization.FromJsonFile<List<FreeGame>>(FreePath) ?? new List<FreeGame>() : new List<FreeGame>(); }
            catch { return new List<FreeGame>(); }
        }

        /// <summary>Fetch current + upcoming freebies; returns the ones not seen in the previous fetch.</summary>
        public List<FreeGame> FetchFree()
        {
            var list = new List<FreeGame>();
            try { list.AddRange(EpicFree()); } catch (Exception e) { logger.Warn($"Ledger: Epic free games failed: {e.Message}"); }
            foreach (var (platform, store) in new[] { ("steam", "Steam"), ("gog", "GOG") })
            {
                try { list.AddRange(GamerPower(platform, store)); } catch (Exception e) { logger.Warn($"Ledger: GamerPower {platform} failed: {e.Message}"); }
            }
            var before = new HashSet<string>(LoadFree().Select(f => f.Store + "|" + f.Title));
            Directory.CreateDirectory(state);
            File.WriteAllText(FreePath, Serialization.ToJson(list, true));
            return list.Where(f => !f.Upcoming && !before.Contains(f.Store + "|" + f.Title)).ToList();
        }

        private class EpicResp { public EpicData data { get; set; } }
        private class EpicData { public EpicCatalog Catalog { get; set; } }
        private class EpicCatalog { public EpicSearch searchStore { get; set; } }
        private class EpicSearch { public List<EpicEl> elements { get; set; } }
        private class EpicEl
        {
            public string title { get; set; } public string productSlug { get; set; }
            public EpicMapping catalogNs { get; set; } public List<EpicPage> offerMappings { get; set; }
            public EpicPromos promotions { get; set; } public List<EpicImage> keyImages { get; set; }
            public EpicPrice price { get; set; }
        }
        private class EpicMapping { public List<EpicPage> mappings { get; set; } public string pageSlug { get; set; } }
        private class EpicPage { public string pageSlug { get; set; } }
        private class EpicPromos { public List<EpicPromoGroup> promotionalOffers { get; set; } public List<EpicPromoGroup> upcomingPromotionalOffers { get; set; } }
        private class EpicPromoGroup { public List<EpicPromo> promotionalOffers { get; set; } }
        private class EpicPromo { public DateTime startDate { get; set; } public DateTime endDate { get; set; } public EpicDiscount discountSetting { get; set; } }
        private class EpicDiscount { public int discountPercentage { get; set; } }
        private class EpicImage { public string type { get; set; } public string url { get; set; } }
        private class EpicPrice { public EpicTotal totalPrice { get; set; } }
        private class EpicTotal { public EpicFmt fmtPrice { get; set; } }
        private class EpicFmt { public string originalPrice { get; set; } }

        private IEnumerable<FreeGame> EpicFree()
        {
            var json = Get("https://store-site-backend-static.ak.epicgames.com/freeGamesPromotions?locale=en-US&country=GB&allowCountries=GB");
            var els = Serialization.FromJson<EpicResp>(json)?.data?.Catalog?.searchStore?.elements ?? new List<EpicEl>();
            foreach (var e in els)
            {
                // "Free" = a promo at 0% of price (discountPercentage == 0 means 100% off in Epic's scheme).
                var now = e.promotions?.promotionalOffers?.SelectMany(g => g.promotionalOffers ?? new List<EpicPromo>()).FirstOrDefault(p => p.discountSetting?.discountPercentage == 0);
                var next = e.promotions?.upcomingPromotionalOffers?.SelectMany(g => g.promotionalOffers ?? new List<EpicPromo>()).FirstOrDefault(p => p.discountSetting?.discountPercentage == 0);
                var promo = now ?? next;
                if (promo == null) continue;
                var slug = e.catalogNs?.mappings?.FirstOrDefault()?.pageSlug ?? e.offerMappings?.FirstOrDefault()?.pageSlug ?? e.productSlug;
                yield return new FreeGame
                {
                    Title = e.title, Store = "Epic",
                    Url = slug != null ? "https://store.epicgames.com/p/" + slug : "https://store.epicgames.com/free-games",
                    Starts = promo.startDate.ToLocalTime(), Ends = promo.endDate.ToLocalTime(),
                    Worth = e.price?.totalPrice?.fmtPrice?.originalPrice,
                    ImageUrl = e.keyImages?.FirstOrDefault(i => i.type == "OfferImageTall")?.url ?? e.keyImages?.FirstOrDefault()?.url
                };
            }
        }

        private class GpItem { public string title { get; set; } public string worth { get; set; } public string image { get; set; } public string open_giveaway_url { get; set; } public string end_date { get; set; } public string description { get; set; } public string type { get; set; } }

        private IEnumerable<FreeGame> GamerPower(string platform, string store)
        {
            var json = Get($"https://www.gamerpower.com/api/giveaways?platform={platform}&type=game");
            if (json.TrimStart().StartsWith("{")) yield break; // {"status":0,...} = none right now
            foreach (var g in Serialization.FromJson<List<GpItem>>(json) ?? new List<GpItem>())
            {
                DateTime? end = DateTime.TryParse(g.end_date, out var e) ? e : (DateTime?)null;
                var title = System.Text.RegularExpressions.Regex.Replace(g.title ?? "", @"\s*\((Steam|GOG|Epic Games)\)\s*|\s*(Key )?Giveaway\s*$", "").Trim();
                var needsAccount = (g.description ?? "").IndexOf("requires", StringComparison.OrdinalIgnoreCase) >= 0 || (g.description ?? "").IndexOf("Log in to your", StringComparison.OrdinalIgnoreCase) >= 0;
                yield return new FreeGame
                {
                    Title = title, Store = store, Url = g.open_giveaway_url, Ends = end, Worth = g.worth == "N/A" ? null : g.worth, ImageUrl = g.image,
                    Note = needsAccount ? "third-party giveaway (account/points needed)" : null
                };
            }
        }

        // ---------------- recommendations ----------------

        public List<Rec> LoadRecs()
        {
            try { return File.Exists(RecsPath) ? Serialization.FromJsonFile<List<Rec>>(RecsPath) ?? new List<Rec>() : new List<Rec>(); }
            catch { return new List<Rec>(); }
        }

        private class SpyApp { public string appid { get; set; } public string name { get; set; } public int positive { get; set; } public int negative { get; set; } public string owners { get; set; } public string price { get; set; } public string discount { get; set; } public object tags { get; set; }
            public Dictionary<string, int> Tags()
            {
                try { return tags == null ? new Dictionary<string, int>() : Serialization.FromJson<Dictionary<string, int>>(Serialization.ToJson(tags)) ?? new Dictionary<string, int>(); }
                catch { return new Dictionary<string, int>(); } // [] = no tags
            }
        }

        /// <summary>Tags on nearly every game. They say nothing about taste and swamp the profile.</summary>
        private static readonly HashSet<string> GenericTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Singleplayer", "Multiplayer", "Action", "Adventure", "Indie", "Casual", "Co-op", "Online Co-Op", "Early Access",
            "Free to Play", "Great Soundtrack", "Atmospheric", "3D", "2D", "Colorful", "Cute", "Funny", "Fantasy", "Sci-fi",
            "Controller", "Classic", "Masterpiece", "Beautiful", "Family Friendly", "Difficult", "Replay Value", "Story Rich",
            "Third Person", "First-Person", "Female Protagonist", "Violent", "Gore", "Mature", "Nudity", "Sexual Content",
            "PvP", "PvE", "Online PvP", "Massively Multiplayer", "Local Co-Op", "Local Multiplayer", "Split Screen",
            "Realistic", "Stylized", "Cartoony", "Anime", "Pixel Graphics", "Retro", "Old School", "Hand-drawn", "Moddable",
            "Character Customization", "Exploration", "Building", "Crafting", "Combat", "Inventory Management", "Loot"
        };

        private T Spy<T>(string key, string url, TimeSpan maxAge) where T : class
        {
            Directory.CreateDirectory(SpyDir);
            var p = Path.Combine(SpyDir, key + ".json");
            if (File.Exists(p) && DateTime.Now - File.GetLastWriteTime(p) < maxAge)
            {
                try { return Serialization.FromJsonFile<T>(p); } catch { }
            }
            Thread.Sleep(1100); // SteamSpy: 1 request/second
            var json = Get(url);
            File.WriteAllText(p, json);
            return Serialization.FromJson<T>(json);
        }

        private static long OwnersLow(string owners)
        {
            var first = (owners ?? "0").Split('.')[0].Replace(",", "").Trim();
            return long.TryParse(first, out var v) ? v : 0;
        }

        /// <summary>Rebuild recommendations from your play history. Network-heavy (≈40 SteamSpy calls first time).</summary>
        public int BuildRecs(IEnumerable<GameHistory> library)
        {
            var lib = library.ToList();
            var steamPlayed = lib.Where(h => h.Game.PluginId == HistoryIndex.SteamPluginId && h.Game.GameId != null && h.Hours >= 3 && !h.IsTool)
                                 .OrderByDescending(h => h.Hours).Take(25).ToList();
            if (steamPlayed.Count == 0) return 0;

            // Tag profile: each game's tag votes (normalised) × log(hours).
            var profile = new Dictionary<string, double>();
            var gameVec = new Dictionary<string, Dictionary<string, double>>();
            foreach (var h in steamPlayed)
            {
                SpyApp app;
                try { app = Spy<SpyApp>("app-" + h.Game.GameId, $"https://steamspy.com/api.php?request=appdetails&appid={h.Game.GameId}", TimeSpan.FromDays(60)); }
                catch (Exception e) { logger.Warn($"Ledger: SteamSpy {h.Game.Name}: {e.Message}"); continue; }
                var tags = app?.Tags().Where(t => !GenericTags.Contains(t.Key)).ToDictionary(t => t.Key, t => t.Value);
                if (tags == null || tags.Count == 0) continue;
                var max = Math.Max(1, tags.Values.Max());
                var weight = Math.Log(1 + h.Hours);
                foreach (var t in tags) profile[t.Key] = (profile.TryGetValue(t.Key, out var v) ? v : 0) + weight * t.Value / max;
                if (gameVec.Count < 6) gameVec[h.Game.Name] = tags.ToDictionary(t => t.Key, t => (double)t.Value / max);
            }
            var topTags = profile.OrderByDescending(kv => kv.Value).Take(12).Select(kv => kv.Key).ToList();

            // Exclusions: owned anywhere (Steam app id or same base name on another store).
            var ownedIds = new HashSet<string>(lib.Where(h => h.Game.PluginId == HistoryIndex.SteamPluginId).Select(h => h.Game.GameId));
            var ownedNames = new HashSet<string>(lib.Select(h => UpNext.BaseName(h.Game.Name)));

            var candidates = new Dictionary<string, Rec>();
            foreach (var tag in topTags)
            {
                Dictionary<string, SpyApp> apps;
                try { apps = Spy<Dictionary<string, SpyApp>>("tag-" + Uri.EscapeDataString(tag), $"https://steamspy.com/api.php?request=tag&tag={Uri.EscapeDataString(tag)}", TimeSpan.FromDays(7)); }
                catch (Exception e) { logger.Warn($"Ledger: SteamSpy tag {tag}: {e.Message}"); continue; }
                foreach (var a in (apps ?? new Dictionary<string, SpyApp>()).Values)
                {
                    if (a.appid == null || ownedIds.Contains(a.appid) || ownedNames.Contains(UpNext.BaseName(a.name))) continue;
                    if (a.positive + a.negative < 500) continue; // too few reviews to trust
                    if (!candidates.TryGetValue(a.appid, out var r))
                    {
                        candidates[a.appid] = r = new Rec
                        {
                            AppId = a.appid, Name = a.name, Positive = a.positive, Negative = a.negative,
                            PriceUsd = decimal.TryParse(a.price, out var pr) ? pr / 100m : (decimal?)null,
                            Discount = int.TryParse(a.discount, out var dc) ? dc : 0
                        };
                    }
                    r.Because.Add(tag);
                    r.Score += profile[tag];
                }
            }
            // Pre-rank by tag-list hits × quality, then verify the best 150 against their *own* top tags.
            // (SteamSpy tag lists are loose: a joke tag puts a visual novel in "Strategy".)
            double Quality(Rec r) => Math.Pow(r.ReviewPct / 100.0, 3) * Math.Log10(10 + r.Positive + r.Negative);
            var shortlist = candidates.Values.OrderByDescending(r => r.Score * Quality(r)).Take(150).ToList();
            var vectors = new Dictionary<string, Dictionary<string, double>>();
            foreach (var r in shortlist)
            {
                try
                {
                    var app = Spy<SpyApp>("app-" + r.AppId, $"https://steamspy.com/api.php?request=appdetails&appid={r.AppId}", TimeSpan.FromDays(60));
                    var t = app?.Tags().Where(x => !GenericTags.Contains(x.Key)).ToDictionary(x => x.Key, x => (double)x.Value);
                    if (t == null || t.Count == 0) continue;
                    var mx = t.Values.Max();
                    vectors[r.AppId] = t.OrderByDescending(x => x.Value).Take(12).ToDictionary(x => x.Key, x => x.Value / mx);
                }
                catch (Exception e) { logger.Warn($"Ledger: SteamSpy {r.Name}: {e.Message}"); }
            }

            var prof = Normalise(profile.OrderByDescending(kv => kv.Value).Take(25).ToDictionary(kv => kv.Key, kv => kv.Value));
            var scored = new List<Rec>();
            foreach (var r in shortlist.Where(x => vectors.ContainsKey(x.AppId)))
            {
                var v = vectors[r.AppId];
                var taste = Cosine(prof, v);
                r.Score = Math.Pow(taste, 1.5) * Quality(r);
                r.Because = v.Keys.Where(prof.ContainsKey).OrderByDescending(k => prof[k] * v[k]).Take(3).ToList();
                if (r.Because.Count > 0) scored.Add(r);
            }
            var result = scored.OrderByDescending(r => r.Score).Take(60).ToList();

            // "More like X": closest tag vectors to each of your top games.
            foreach (var kv in gameVec)
            {
                var gv = Normalise(kv.Value);
                foreach (var r in scored.Select(x => new { x, sim = Cosine(gv, vectors[x.AppId]) }).Where(z => z.sim > 0.35).OrderByDescending(z => z.sim * Quality(z.x)).Take(8))
                {
                    result.Add(new Rec { AppId = r.x.AppId, Name = r.x.Name, Score = r.sim, Positive = r.x.Positive, Negative = r.x.Negative, PriceUsd = r.x.PriceUsd, Discount = r.x.Discount,
                                         Because = vectors[r.x.AppId].Keys.Where(gv.ContainsKey).OrderByDescending(k => gv[k]).Take(3).ToList(), LikeGame = kv.Key });
                }
            }
            Directory.CreateDirectory(state);
            File.WriteAllText(RecsPath, Serialization.ToJson(result, true));
            File.WriteAllText(Path.Combine(state, "taste-profile.json"), Serialization.ToJson(profile.OrderByDescending(kv => kv.Value).Take(30).ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 2)), true));
            logger.Info($"Ledger: recommendations built: {result.Count} from {candidates.Count} candidates, tags {string.Join(", ", topTags)}");
            return result.Count;
        }

        private static Dictionary<string, double> Normalise(Dictionary<string, double> v)
        {
            var n = Math.Sqrt(v.Values.Sum(x => x * x));
            return n == 0 ? v : v.ToDictionary(kv => kv.Key, kv => kv.Value / n);
        }

        private static double Cosine(Dictionary<string, double> a, Dictionary<string, double> b)
        {
            var nb = Math.Sqrt(b.Values.Sum(x => x * x));
            if (nb == 0) return 0;
            return a.Where(kv => b.ContainsKey(kv.Key)).Sum(kv => kv.Value * b[kv.Key]) / nb; // a is pre-normalised
        }

        public Dictionary<string, double> LoadProfile()
        {
            var p = Path.Combine(state, "taste-profile.json");
            try { return File.Exists(p) ? Serialization.FromJsonFile<Dictionary<string, double>>(p) : new Dictionary<string, double>(); }
            catch { return new Dictionary<string, double>(); }
        }

        // ---------------- covers ----------------

        /// <summary>Local path for a Steam app's portrait cover, downloading it once if missing.</summary>
        public string SteamCover(string appId)
        {
            Directory.CreateDirectory(CoverDir);
            var p = Path.Combine(CoverDir, appId + ".jpg");
            if (File.Exists(p)) return p;
            foreach (var url in new[] { $"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg",
                                        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg",
                                        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg" })
            {
                try { using (var wc = new WebClient()) wc.DownloadFile(url, p); if (new FileInfo(p).Length > 2000) return p; } catch { }
                try { File.Delete(p); } catch { }
            }
            return null;
        }

        public string UrlCover(string key, string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            Directory.CreateDirectory(CoverDir);
            var p = Path.Combine(CoverDir, "u-" + Math.Abs(key.GetHashCode()) + ".jpg");
            if (File.Exists(p)) return p;
            try { using (var wc = new WebClient()) wc.DownloadFile(url, p); return p; } catch { return null; }
        }

        /// <summary>Pre-download covers for everything Discover shows, so the UI never waits on the network.</summary>
        public void WarmCovers()
        {
            foreach (var r in LoadRecs().Select(r => r.AppId).Distinct()) SteamCover(r);
            foreach (var f in LoadFree()) UrlCover(f.Store + f.Title, f.ImageUrl);
        }
    }
}
