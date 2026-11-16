using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace Ledger
{
    /// <summary>One line in events.jsonl. Things nobody else records, kept from day one.</summary>
    internal class LedgerEvent
    {
        public DateTime At { get; set; }
        public string Type { get; set; }        // installed | uninstalled | status | wishlist-added | wishlist-removed | mod-added | mod-removed | rated
        public Guid? GameId { get; set; }
        public string Name { get; set; }
        public string From { get; set; }
        public string To { get; set; }
        public long? Size { get; set; }      // bytes for installs; count for mod diffs
        public string Drive { get; set; }
        public string Note { get; set; }
        public int? Rating { get; set; }
    }

    /// <summary>
    /// Event log + once-a-day collectors. Lightweight by design: event hooks append one line; the daily
    /// job runs on a background thread at most once per calendar day (checked hourly).
    ///   events.jsonl        install/uninstall, completion-status changes, wishlist and mod diffs, ratings
    ///   daily/yyyy-MM-dd.json   disk free per drive, wishlist (appid → price), owned-unplayed prices
    ///   state/              last wishlist + mod state, to diff against
    /// </summary>
    internal class Tracking : IDisposable
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly Ledger plugin;
        private readonly IPlayniteAPI api;
        private readonly string dir;
        private Timer timer;
        private readonly object fileLock = new object();
        private int running;

        public string EventsPath => Path.Combine(dir, "events.jsonl");
        private string DailyDir => Path.Combine(dir, "daily");
        private string StateDir => Path.Combine(dir, "state");

        public Tracking(Ledger plugin, IPlayniteAPI api, string dataDir)
        {
            this.plugin = plugin;
            this.api = api;
            dir = dataDir;
        }

        public void Start()
        {
            api.Database.Games.ItemUpdated += OnGamesUpdated;
            timer = new Timer(_ => RunDailyIfDue(), null, TimeSpan.FromMinutes(2), TimeSpan.FromHours(1));
        }

        public void Dispose()
        {
            timer?.Dispose();
            api.Database.Games.ItemUpdated -= OnGamesUpdated;
        }

        public void Log(LedgerEvent e)
        {
            lock (fileLock)
            {
                try { File.AppendAllText(EventsPath, Serialization.ToJson(e, false) + Environment.NewLine, new UTF8Encoding(false)); }
                catch (Exception ex) { logger.Warn($"Ledger: couldn't log event: {ex.Message}"); }
            }
        }

        public static List<LedgerEvent> LoadEvents(string dataDir)
        {
            var p = Path.Combine(dataDir, "events.jsonl");
            var list = new List<LedgerEvent>();
            if (!File.Exists(p)) return list;
            foreach (var line in File.ReadLines(p))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { list.Add(Serialization.FromJson<LedgerEvent>(line)); } catch { }
            }
            return list;
        }

        // ---------------- event hooks ----------------

        private static string DriveOf(Game g) => string.IsNullOrEmpty(g.InstallDirectory) ? null : Path.GetPathRoot(g.InstallDirectory)?.TrimEnd('\\');

        public void Installed(Game g) => Log(new LedgerEvent { At = DateTime.Now, Type = "installed", GameId = g.Id, Name = g.Name, Size = (long?)g.InstallSize, Drive = DriveOf(g) });
        public void Uninstalled(Game g) => Log(new LedgerEvent { At = DateTime.Now, Type = "uninstalled", GameId = g.Id, Name = g.Name, Size = (long?)g.InstallSize, Drive = DriveOf(g) });

        /// <summary>Completion status changes, with the date (Playnite only keeps the current value).</summary>
        private void OnGamesUpdated(object sender, ItemUpdatedEventArgs<Game> e)
        {
            foreach (var u in e.UpdatedItems)
            {
                if (u.OldData.CompletionStatusId == u.NewData.CompletionStatusId) continue;
                var from = u.OldData.CompletionStatus?.Name ?? api.Database.CompletionStatuses.Get(u.OldData.CompletionStatusId)?.Name;
                var to = u.NewData.CompletionStatus?.Name ?? api.Database.CompletionStatuses.Get(u.NewData.CompletionStatusId)?.Name;
                Log(new LedgerEvent { At = DateTime.Now, Type = "status", GameId = u.NewData.Id, Name = u.NewData.Name, From = from, To = to });
                if (plugin.Settings.AskForVerdict && IsEnding(to))
                {
                    var game = u.NewData;
                    api.MainView.UIDispatcher.BeginInvoke((Action)(() => AskVerdict(game, to)));
                }
            }
        }

        private static bool IsEnding(string status) =>
            status != null && new[] { "Beaten", "Completed", "Abandoned" }.Any(s => status.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>One-shot "how was it?" when a game is finished or dropped: rating → UserScore, note → Notes.</summary>
        public void AskVerdict(Game game, string status)
        {
            var r = api.Dialogs.SelectString($"{game.Name} → {status}.\n\nRate it 1–10 (blank to skip):", "Ledger · How was it?", "");
            if (!r.Result) return;
            int? rating = int.TryParse(r.SelectedString?.Trim(), out var v) && v >= 1 && v <= 10 ? v : (int?)null;
            var n = api.Dialogs.SelectString(status.IndexOf("Abandon", StringComparison.OrdinalIgnoreCase) >= 0
                ? "Why did you stop? (one line, optional)" : "One line to remember it by (optional)", "Ledger · How was it?", "");
            var note = n.Result ? n.SelectedString?.Trim() : null;
            if (rating == null && string.IsNullOrEmpty(note)) return;

            var g = api.Database.Games.Get(game.Id);
            if (g != null)
            {
                if (rating.HasValue) g.UserScore = rating.Value * 10;
                if (!string.IsNullOrEmpty(note))
                    g.Notes = (string.IsNullOrEmpty(g.Notes) ? "" : g.Notes + Environment.NewLine) + $"[{DateTime.Now:yyyy-MM-dd} {status}] {note}";
                api.Database.Games.Update(g);
            }
            Log(new LedgerEvent { At = DateTime.Now, Type = "rated", GameId = game.Id, Name = game.Name, To = status, Rating = rating, Note = note });
        }

        // ---------------- daily collectors ----------------

        public void RunDailyIfDue(bool force = false)
        {
            if (Interlocked.Exchange(ref running, 1) == 1) return;
            try
            {
                Directory.CreateDirectory(DailyDir);
                var today = Path.Combine(DailyDir, DateTime.Now.ToString("yyyy-MM-dd") + ".json");
                if (File.Exists(today) && !force) return;

                var day = new Dictionary<string, object> { ["takenAt"] = DateTime.Now };
                Try("disks", () => day["disks"] = Disks());
                Try("wishlist", () => day["wishlist"] = Wishlist());
                Try("prices", () => day["prices"] = WatchPrices(day.TryGetValue("wishlist", out var w) ? (Dictionary<string, WishItem>)w : null));
                Try("mods", ModDiff);
                File.WriteAllText(today, Serialization.ToJson(day, true));
                logger.Info($"Ledger daily collect written: {today}");
            }
            catch (Exception e) { logger.Error(e, "Ledger daily collect failed"); }
            finally { Interlocked.Exchange(ref running, 0); }
        }

        private static void Try(string what, Action a)
        {
            try { a(); } catch (Exception e) { LogManager.GetLogger().Warn($"Ledger daily {what} failed: {e.Message}"); }
        }

        private static Dictionary<string, object> Disks() =>
            DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .ToDictionary(d => d.Name.TrimEnd('\\'), d => (object)new { free = d.AvailableFreeSpace, total = d.TotalSize });

        public class WishItem { public string appid { get; set; } public long date_added { get; set; } public int priority { get; set; } }
        private class WishResp { public WishList response { get; set; } }
        private class WishList { public List<WishItem> items { get; set; } }

        /// <summary>Public wishlist (no key). Diffs against yesterday's → wishlist-added/removed events.</summary>
        private Dictionary<string, WishItem> Wishlist()
        {
            var id = SteamId();
            if (id == null) return null;
            string json;
            using (var wc = new WebClient()) json = wc.DownloadString($"https://api.steampowered.com/IWishlistService/GetWishlist/v1/?steamid={id}");
            var items = Serialization.FromJson<WishResp>(json)?.response?.items ?? new List<WishItem>();
            var now = items.GroupBy(i => i.appid).ToDictionary(g => g.Key, g => g.First());

            Directory.CreateDirectory(StateDir);
            var statePath = Path.Combine(StateDir, "wishlist.json");
            var before = File.Exists(statePath) ? Serialization.FromJsonFile<Dictionary<string, WishItem>>(statePath) : null;
            if (before != null)
            {
                foreach (var a in now.Keys.Except(before.Keys)) Log(new LedgerEvent { At = DateTime.Now, Type = "wishlist-added", Name = a });
                foreach (var r in before.Keys.Except(now.Keys))
                {
                    // Removed from wishlist: bought (now owned) or gave up.
                    var owned = api.Database.Games.Any(g => g.PluginId == HistoryIndex.SteamPluginId && g.GameId == r);
                    Log(new LedgerEvent { At = DateTime.Now, Type = "wishlist-removed", Name = r, To = owned ? "bought" : "dropped" });
                }
            }
            File.WriteAllText(statePath, Serialization.ToJson(now, true));
            return now;
        }

        /// <summary>Today's price for wishlist + owned-unplayed Steam games: our own price history, from today on.</summary>
        private Dictionary<string, Price> WatchPrices(Dictionary<string, WishItem> wishlist)
        {
            var ids = new HashSet<string>(wishlist?.Keys ?? Enumerable.Empty<string>());
            foreach (var g in api.Database.Games.Where(g => g.PluginId == HistoryIndex.SteamPluginId && g.Playtime == 0 && g.GameId != null)) ids.Add(g.GameId);
            var tmp = new Money(api, Path.Combine(dir, "state", "pricewatch"));
            tmp.FetchPrices(ids, plugin.Settings.PriceRegion ?? "gb"); // 7-day cache inside; today's values are what we want
            var all = tmp.LoadPrices();
            return ids.Where(all.ContainsKey).ToDictionary(i => i, i => all[i]);
        }

        /// <summary>Mods added/removed since the last run, per game.</summary>
        private void ModDiff()
        {
            var mods = ModIndex.Build(api, dir);
            var now = mods.ToDictionary(m => m.Game?.Id.ToString() ?? m.Label, m => m.Mods.Select(x => x.Source + ":" + x.Id).Distinct().ToList());
            Directory.CreateDirectory(StateDir);
            var statePath = Path.Combine(StateDir, "mods.json");
            var before = File.Exists(statePath) ? Serialization.FromJsonFile<Dictionary<string, List<string>>>(statePath) : null;
            if (before != null)
            {
                foreach (var gm in mods)
                {
                    var key = gm.Game?.Id.ToString() ?? gm.Label;
                    var prev = before.TryGetValue(key, out var p) ? new HashSet<string>(p) : new HashSet<string>();
                    var cur = new HashSet<string>(now[key]);
                    var added = cur.Except(prev).ToList();
                    var removed = prev.Except(cur).ToList();
                    if (added.Count > 0) Log(new LedgerEvent { At = DateTime.Now, Type = "mod-added", GameId = gm.Game?.Id, Name = gm.Game?.Name ?? gm.Label, Note = string.Join(",", added.Take(50)), Size = added.Count });
                    if (removed.Count > 0) Log(new LedgerEvent { At = DateTime.Now, Type = "mod-removed", GameId = gm.Game?.Id, Name = gm.Game?.Name ?? gm.Label, Note = string.Join(",", removed.Take(50)), Size = removed.Count });
                }
            }
            File.WriteAllText(statePath, Serialization.ToJson(now, false));
        }

        private string SteamId()
        {
            var acct = SteamLocalStats.AccountIds(SteamLocalStats.SteamDir()).FirstOrDefault();
            return acct == 0 ? null : (acct + 76561197960265728).ToString();
        }
    }
}
