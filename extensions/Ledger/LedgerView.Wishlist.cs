using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace Ledger
{
    internal partial class LedgerView
    {
        private void BuildWishlist(Data d)
        {
            var w = d.Wishlist ?? new List<WishEntry>();
            var sym = d.Money.Symbol;
            if (w.Count == 0)
            {
                Section("Wishlist", null);
                root.Children.Add(Text("No wishlist data yet. Ledger reads your public Steam wishlist once a day.", 13, dim: true));
                return;
            }
            var sale = w.Where(x => x.DiscountPct > 0).OrderByDescending(x => x.DiscountPct).ToList();
            var wrap = new WrapPanel { Margin = new Thickness(0, 5, 0, 10) };
            wrap.Children.Add(Card(w.Count.ToString(), "on your Steam wishlist"));
            wrap.Children.Add(Card(M(w.Sum(x => x.List ?? 0), sym), "at full price"));
            wrap.Children.Add(Card(M(w.Sum(x => x.Now ?? 0), sym), "at today's prices"));
            wrap.Children.Add(Card(sale.Count.ToString(), "on sale today"));
            var owned = w.Where(x => x.OwnedElsewhere != null).ToList();
            if (owned.Count > 0) wrap.Children.Add(Card(owned.Count.ToString(), "already owned on another store"));
            root.Children.Add(wrap);
            var tracked = w.Max(x => x.DaysTracked);
            root.Children.Add(Text($"Prices watched daily by Ledger ({tracked} day{(tracked == 1 ? "" : "s")} so far). \"Lowest seen\" gets more useful as the history grows.", 11, dim: true));

            if (owned.Count > 0)
            {
                Section("Already in your library", "On your Steam wishlist, but you own it on another store. Probably safe to remove.");
                foreach (var x in owned) WishRow(x, sym, $"owned on {x.OwnedElsewhere.Game.Source?.Name}");
            }
            if (sale.Count > 0)
            {
                Section("On sale now", "Biggest discount first.");
                foreach (var x in sale.Take(20))
                {
                    var atLow = x.LowestSeen.HasValue && x.Now <= x.LowestSeen && x.DaysTracked > 7;
                    WishRow(x, sym, $"-{x.DiscountPct}% · {M(x.Now ?? 0, sym)} (was {M(x.List ?? 0, sym)})" + (atLow ? " · lowest we've seen" : ""));
                }
            }
            Section("Waiting longest", "Oldest wishlist entries. Still want them?");
            foreach (var x in w.OrderBy(x => x.Added).Take(15))
                WishRow(x, sym, $"added {x.Added:MMM yyyy} · {(x.Now.HasValue ? M(x.Now.Value, sym) : "no price")}");
        }

        /// <summary>Recent things that happened (events.jsonl), newest first.</summary>
        private void BuildRecent()
        {
            var events = Tracking.LoadEvents(plugin.GetPluginUserDataPath());
            if (events.Count == 0) return;
            var names = WishlistIndex.Names(plugin.GetPluginUserDataPath(), Enumerable.Empty<string>());
            string N(LedgerEvent e) => e.Type.StartsWith("wishlist") && names.TryGetValue(e.Name ?? "", out var n) ? n : e.Name;
            string Describe(LedgerEvent e)
            {
                switch (e.Type)
                {
                    case "installed": return $"Installed {N(e)}" + (e.Size > 0 ? $" ({Size(e.Size.Value)} on {e.Drive})" : "");
                    case "uninstalled": return $"Uninstalled {N(e)}" + (e.Size > 0 ? $", freed {Size(e.Size.Value)}" : "");
                    case "status": return $"{N(e)}: {e.From ?? "?"} → {e.To}";
                    case "rated": return $"Rated {N(e)}" + (e.Rating.HasValue ? $" {e.Rating}/10" : "") + (string.IsNullOrEmpty(e.Note) ? "" : $": \"{e.Note}\"");
                    case "wishlist-added": return $"Wishlisted {N(e)}";
                    case "wishlist-removed": return $"{(e.To == "bought" ? "Bought" : "Dropped")} {N(e)} from your wishlist";
                    case "mod-added": return $"{N(e)}: {e.Size} mod{(e.Size == 1 ? "" : "s")} added";
                    case "mod-removed": return $"{N(e)}: {e.Size} mod{(e.Size == 1 ? "" : "s")} removed";
                    default: return $"{e.Type}: {N(e)}";
                }
            }
            Section("Recent activity", "Things Ledger noticed: installs, status changes, ratings, wishlist and mod changes.");
            foreach (var e in events.OrderByDescending(x => x.At).Take(12))
            {
                var grid = Row(new GridLength(130), new GridLength(1, GridUnitType.Star));
                Place(grid, Text(e.At.ToString("d MMM HH:mm"), 12, dim: true), 0);
                Place(grid, Text(Describe(e), 13), 1);
                root.Children.Add(grid);
            }
        }

        private class DiskDay { public Dictionary<string, DiskInfo> disks { get; set; } public DateTime takenAt { get; set; } }
        private class DiskInfo { public long free { get; set; } public long total { get; set; } }

        /// <summary>Free space per drive over time (daily collect), with a simple linear forecast.</summary>
        private void BuildDiskTrend()
        {
            var dir = System.IO.Path.Combine(plugin.GetPluginUserDataPath(), "daily");
            if (!System.IO.Directory.Exists(dir)) return;
            var days = System.IO.Directory.GetFiles(dir, "*.json").OrderBy(f => f).Select(f =>
            {
                try { return Playnite.SDK.Data.Serialization.FromJsonFile<DiskDay>(f); } catch { return null; }
            }).Where(x => x?.disks != null).ToList();
            if (days.Count == 0) return;
            Section("Free space over time", days.Count < 7 ? $"Recorded daily; trend and forecast appear after a week ({days.Count} day{(days.Count == 1 ? "" : "s")} so far)." : "Recorded daily. Forecast is a straight line through the last 30 days.");
            foreach (var drive in days.Last().disks.Keys.OrderBy(k => k))
            {
                var series = days.Where(x => x.disks.ContainsKey(drive)).Select(x => new { x.takenAt, x.disks[drive].free, x.disks[drive].total }).ToList();
                var lastPoint = series.Last();
                string forecast = "";
                var recent = series.Where(p => p.takenAt > DateTime.Now.AddDays(-30)).ToList();
                if (recent.Count >= 7)
                {
                    var daysSpan = (recent.Last().takenAt - recent.First().takenAt).TotalDays;
                    var perDay = daysSpan > 0 ? (recent.First().free - recent.Last().free) / daysSpan : 0;
                    forecast = perDay > 1e8 ? $" · losing {Size(perDay * 7)}/week, full in ~{lastPoint.free / perDay / 7:N0} weeks"
                             : perDay < -1e8 ? $" · gaining {Size(-perDay * 7)}/week" : " · stable";
                }
                var grid = Row(new GridLength(60), new GridLength(1, GridUnitType.Star), new GridLength(380));
                Place(grid, Text(drive, 15, bold: true), 0);
                Place(grid, Bar(lastPoint.total - lastPoint.free, lastPoint.total, $"{Size(lastPoint.free)} free"), 1);
                Place(grid, Text($"of {Size(lastPoint.total)}{forecast}", 12, dim: true), 2);
                root.Children.Add(grid);
            }
        }

        private void WishRow(WishEntry x, string sym, string meta)
        {
            var grid = Row(new GridLength(1, GridUnitType.Star), new GridLength(380));
            var tb = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var link = new Hyperlink(new Run(x.Name)) { TextDecorations = null, Cursor = Cursors.Hand };
            link.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
            var url = "https://store.steampowered.com/app/" + x.AppId;
            link.Click += (s, e) => System.Diagnostics.Process.Start(url);
            tb.Inlines.Add(link);
            Place(grid, tb, 0);
            Place(grid, Text(meta, 12, dim: true), 1);
            root.Children.Add(grid);
        }
    }
}
