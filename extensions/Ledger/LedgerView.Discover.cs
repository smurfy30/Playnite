using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Ledger
{
    internal partial class LedgerView
    {
        private string CachedCover(string fileName)
        {
            var p = Path.Combine(plugin.Discover.CoverDir, fileName);
            return File.Exists(p) ? p : null;
        }

        private static void Open(string url) { if (!string.IsNullOrEmpty(url)) System.Diagnostics.Process.Start(url); }

        private void BuildDiscover(Data d)
        {
            var free = plugin.Discover.LoadFree();
            var dismissed = Lists.Dismissed();
            var recs = plugin.Discover.LoadRecs().Where(r => !dismissed.Contains(r.AppId)).ToList();
            var profile = plugin.Discover.LoadProfile();
            var ownedNames = new HashSet<string>(d.Index.Games.Select(h => UpNext.BaseName(h.Game.Name)));

            var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            bar.Children.Add(Btn("Refresh free games & picks", () =>
            {
                System.Threading.Tasks.Task.Run(() => plugin.RefreshDiscover(force: true)).ContinueWith(_ => Dispatcher.Invoke(() => Refresh()));
                api.Dialogs.ShowMessage("Refreshing in the background (picks can take a few minutes the first time). Ledger reloads when it's done.", "Ledger");
            }));
            bar.Children.Add(Text("Free games update daily; picks weekly. You get a notification when something new is free.", 11, dim: true));
            root.Children.Add(bar);

            // ---- free right now ----
            var now = free.Where(f => !f.Upcoming && (!f.Ends.HasValue || f.Ends > DateTime.Now)).ToList();
            var next = free.Where(f => f.Upcoming).ToList();
            Section("Free right now", now.Count == 0 ? "Nothing free to keep at the moment." : "Free to keep. Claim before it ends.");
            if (now.Count > 0)
                root.Children.Add(Charts.Tiles(now.Select(f => new Charts.Tile
                {
                    Title = f.Title,
                    Line1 = f.Store + (f.Ends.HasValue ? $" · until {f.Ends:ddd d MMM}" : ""),
                    Line2 = ownedNames.Contains(UpNext.BaseName(f.Title)) ? "✓ already in your library" : f.Note ?? (f.Worth != null ? $"worth {f.Worth}" : null),
                    CoverPath = CachedCover("u-" + Math.Abs((f.Store + f.Title).GetHashCode()) + ".jpg"),
                    Tip = f.Note ?? "Click to open the store page",
                    OnClick = () => Open(f.Url)
                }), 130));
            if (next.Count > 0)
            {
                Section("Free next", "Announced, not claimable yet.");
                root.Children.Add(Charts.Tiles(next.Select(f => new Charts.Tile
                {
                    Title = f.Title,
                    Line1 = $"{f.Store} · from {f.Starts:ddd d MMM}",
                    Line2 = f.Ends.HasValue ? $"until {f.Ends:ddd d MMM}" : null,
                    CoverPath = CachedCover("u-" + Math.Abs((f.Store + f.Title).GetHashCode()) + ".jpg"),
                    OnClick = () => Open(f.Url)
                }), 110));
            }

            if (recs.Count == 0)
            {
                Section("Picks for you", null);
                root.Children.Add(Text("No recommendations yet. Click Refresh above (first build takes a few minutes).", 12, dim: true));
            }
            else
            {
                // ---- taste profile ----
                if (profile.Count > 0)
                {
                    Section("Your taste", "Steam tags of what you play most, weighted by hours. This drives the picks below.");
                    var tp = profile.Take(12).ToList();
                    var max = tp.Max(kv => kv.Value);
                    var wrap = new WrapPanel();
                    foreach (var kv in tp)
                    {
                        var cell = new StackPanel { Width = 210, Margin = new Thickness(0, 0, 16, 6) };
                        cell.Children.Add(Bar(kv.Value, max, kv.Key));
                        wrap.Children.Add(cell);
                    }
                    root.Children.Add(wrap);
                }

                Charts.Tile RecTile(Rec r) => new Charts.Tile
                {
                    Title = r.Name,
                    Line1 = $"{r.ReviewPct}% positive" + (r.PriceUsd > 0 ? $" · ${r.PriceUsd:0.##}" + (r.Discount > 0 ? $" (-{r.Discount}%)" : "") : r.PriceUsd == 0 ? " · free" : ""),
                    Line2 = string.Join(", ", r.Because.Take(2)),
                    CoverPath = CachedCover(r.AppId + ".jpg"),
                    Tip = $"{r.Name}\n{r.Positive + r.Negative:N0} reviews, {r.ReviewPct}% positive\nMatches: {string.Join(", ", r.Because)}\nClick to open on Steam",
                    OnClick = () => Open("https://store.steampowered.com/app/" + r.AppId),
                    Menu = MenuFor(r)
                };

                var main = recs.Where(r => r.LikeGame == null).ToList();
                Section("Picks for you", "Games you don't own (on any store) that match your taste, with strong reviews. Click for Steam; right-click to save to a list or dismiss.");
                root.Children.Add(Charts.Tiles(main.Take(14).Select(RecTile)));

                foreach (var tag in profile.Keys.Take(4))
                {
                    var row = main.Where(r => r.Because.Contains(tag)).Take(7).ToList();
                    if (row.Count < 3) continue;
                    Section($"Because you like {tag}", null);
                    root.Children.Add(Charts.Tiles(row.Select(RecTile), 105));
                }
                foreach (var g in recs.Where(r => r.LikeGame != null).GroupBy(r => r.LikeGame))
                {
                    Section($"More like {g.Key}", null);
                    root.Children.Add(Charts.Tiles(g.Take(7).Select(RecTile), 105));
                }
            }

            // ---- your own unplayed, by genre ----
            var own = (d.UpNext ?? new List<Suggestion>()).Where(s => s.MatchedGenres.Count > 0).ToList();
            var genres = own.GroupBy(s => s.MatchedGenres[0]).OrderByDescending(g => g.Count()).Take(4).ToList();
            if (genres.Count > 0)
            {
                Section("Already yours: unplayed, by genre", "You own these. Best matches for your taste in each genre.");
                foreach (var g in genres)
                {
                    var h = Text(g.Key, 14, bold: true); h.Margin = new Thickness(0, 8, 0, 0);
                    root.Children.Add(h);
                    root.Children.Add(Charts.Tiles(g.Take(7).Select(s => TileFor(s.H,
                        string.Join(" · ", new[] { s.H.Hltb != null ? $"{s.H.Hltb.Typical:N0}h" : null, s.Rating.HasValue ? $"★{s.Rating}" : null }.Where(x => x != null)),
                        s.H.Game.IsInstalled ? "installed" : s.H.Game.Source?.Name)), 100));
                }
            }
        }
    }
}
