using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Ledger
{
    internal partial class LedgerView
    {
        // ---------- shared visual pieces ----------

        private string Cover(GameHistory h) =>
            string.IsNullOrEmpty(h.Game.CoverImage) ? null : api.Database.GetFullFilePath(h.Game.CoverImage);

        private Charts.Tile TileFor(GameHistory h, string line1 = null, string line2 = null) => new Charts.Tile
        {
            Title = h.Game.Name,
            Line1 = line1,
            Line2 = line2,
            CoverPath = Cover(h),
            Tip = Tooltip(h),
            OnClick = () => OpenDrawer(h.Game.Id),
            Menu = MenuFor(h)
        };

        /// <summary>A collapsible area that charts above it fill with the games behind a clicked segment.</summary>
        private class Drill
        {
            public StackPanel Host = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            public string Current;
        }

        private Drill NewDrill()
        {
            var d = new Drill();
            root.Children.Add(d.Host);
            return d;
        }

        private void ShowDrill(Drill d, string title, IEnumerable<GameHistory> games, Func<GameHistory, string> line1 = null)
        {
            // Clicking the same thing again closes it.
            if (d.Current == title) { d.Host.Children.Clear(); d.Current = null; return; }
            d.Current = title;
            d.Host.Children.Clear();
            var list = games.OrderByDescending(h => h.Hours).ThenBy(h => h.Game.Name).ToList();
            var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 6, 0, 0) };
            var t = Text($"{title} · {list.Count} games", 15, bold: true);
            DockPanel.SetDock(t, Dock.Left);
            head.Children.Add(t);
            var close = new Button { Content = "✕", Padding = new Thickness(8, 0, 8, 0) };
            close.Click += (s, e) => { d.Host.Children.Clear(); d.Current = null; };
            DockPanel.SetDock(close, Dock.Right);
            head.Children.Add(close);
            d.Host.Children.Add(head);
            d.Host.Children.Add(Charts.Tiles(list.Take(60).Select(h => TileFor(h, (line1 ?? (x => x.Hours > 0 ? $"{x.Hours:N0}h" : "unplayed"))(h), h.Game.Source?.Name)), 100));
            if (list.Count > 60) d.Host.Children.Add(Text($"+{list.Count - 60} more", 12, dim: true));
        }

        private static WrapPanel Panels() => new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };

        // ---------- overview charts ----------

        private void BuildOverviewCharts(List<GameHistory> games)
        {
            var real = games.Where(h => !h.IsTool).ToList();
            Section("Your library at a glance", "Click a slice or legend entry to filter the whole page by it.");
            var row = Panels();
            root.Children.Add(row);
            var drill = NewDrill();

            var buckets = new (string, Func<GameHistory, bool>, Action<FilterState>)[]
            {
                ("Never played", h => !h.Played, f => { f.Played = "Unplayed"; f.MinHours = f.MaxHours = null; }),
                ("Tried (<2h)", h => h.Played && h.Hours < 2, f => { f.Played = "Played"; f.MinHours = null; f.MaxHours = 2; }),
                ("2–20h", h => h.Hours >= 2 && h.Hours < 20, f => { f.Played = null; f.MinHours = 2; f.MaxHours = 20; }),
                ("20–100h", h => h.Hours >= 20 && h.Hours < 100, f => { f.Played = null; f.MinHours = 20; f.MaxHours = 100; }),
                ("100h+", h => h.Hours >= 100, f => { f.Played = null; f.MinHours = 100; f.MaxHours = null; }),
            };
            row.Children.Add(Charts.Donut("How much you've played", buckets.Select(b => new Slice
            {
                Label = b.Item1, Value = real.Count(b.Item2), OnClick = () => Edit(b.Item3)
            }), Pct(real.Count(h => h.Played), real.Count), "played", sort: false));

            row.Children.Add(Charts.Donut("Where it's from", real.GroupBy(h => h.Origin).Select(g => new Slice
            {
                Label = g.Key, Value = g.Count(), Detail = $"{g.Sum(h => h.Hours):N0}h played", OnClick = () => Edit(f => f.Stores = new List<string> { g.Key })
            }), real.Count.ToString("N0"), "games"));

            var withMethod = real.Where(h => h.AcquiredVia != null).ToList();
            if (withMethod.Count > 0)
                row.Children.Add(Charts.Donut("How you got it", withMethod.GroupBy(h => NormalizeMethod(h.AcquiredVia)).Select(g => new Slice
                {
                    Label = g.Key, Value = g.Count(), Detail = $"{Pct(g.Count(h => h.Played), g.Count())} played", OnClick = () => Edit(f => f.Paid = g.Key == "Bought")
                }), withMethod.Count.ToString("N0"), "with a date"));
        }

        private void BuildUpNextTiles(List<Suggestion> ranked, int count)
        {
            if (ranked == null || ranked.Count == 0) return;
            Section("Up next", "Unplayed games that match what you actually play (hours per genre), with good reviews. Installed ones get a boost.");
            var pick = new Button { Content = "🎲  Pick for me", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
            pick.Click += (s, e) => PickForMe(ranked);
            root.Children.Add(pick);
            root.Children.Add(Charts.Tiles(ranked.Take(count).Select(s => TileFor(s.H,
                string.Join(" · ", new[] { s.H.Hltb != null ? $"{s.H.Hltb.Typical:N0}h" : null, s.Rating.HasValue ? $"★{s.Rating}" : null, s.H.Game.IsInstalled ? "installed" : null }.Where(x => x != null)),
                string.Join(", ", s.MatchedGenres.Take(2))))));
        }

        private void BuildQuickWinTiles(List<GameHistory> games)
        {
            var wins = games.Where(h => !h.IsTool && !h.Played && h.Hltb != null && h.Hltb.MainHours > 0.3)
                            .OrderByDescending(h => h.Game.IsInstalled).ThenBy(h => h.Hltb.MainHours).Take(12).ToList();
            Section("Quick wins", "Unplayed and short (HowLongToBeat main story). Installed first, so you can start tonight.");
            if (wins.Count == 0) { root.Children.Add(Text("No HowLongToBeat times yet.", 12, dim: true)); return; }
            root.Children.Add(Charts.Tiles(wins.Select(h => TileFor(h, $"{h.Hltb.MainHours:0.#}h to beat", h.Game.IsInstalled ? "installed" : h.Game.Source?.Name))));
        }

        // ---------- stats charts ----------

        private void BuildStatsCharts(List<GameHistory> games, Data d)
        {
            var real = games.Where(h => !h.IsTool).ToList();
            var drill = (Drill)null;

            var ranges = new (string, double, double)[] { ("<1h", 0.01, 1), ("1–5", 1, 5), ("5–10", 5, 10), ("10–20", 10, 20), ("20–50", 20, 50), ("50–100", 50, 100), ("100–250", 100, 250), ("250h+", 250, double.MaxValue) };
            var dist = ranges.Select(r => new Charts.Column
            {
                Label = r.Item1,
                A = real.Count(h => HoursOf(h, d) >= r.Item2 && HoursOf(h, d) < r.Item3),
                OnClick = () => Edit(f => { f.MinHours = r.Item2 <= 0.01 ? (double?)null : r.Item2; f.MaxHours = r.Item3 == double.MaxValue ? (double?)null : r.Item3; f.Played = r.Item2 <= 0.01 ? "Played" : null; })
            }).ToList();

            var yearsBack = DateTime.Now.Year - 24;
            var byYear = real.Where(h => h.Game.ReleaseYear >= yearsBack).GroupBy(h => h.Game.ReleaseYear.Value).OrderBy(g => g.Key).Select(g => new Charts.Column
            {
                Label = "'" + (g.Key % 100).ToString("00"),
                A = g.Count(),
                B = g.Count(h => h.Played),
                Tip = $"Released {g.Key}: {g.Count()} owned, {g.Count(h => h.Played)} played",
                OnClick = () => Edit(f => { f.MinYear = g.Key; f.MaxYear = g.Key; })
            }).ToList();

            var row = Panels();
            root.Children.Add(row);
            row.Children.Add(Charts.Columns("How long you play things", "Games per playtime bucket. Click a column.", dist, "games", fmt: v => v.ToString("N0")));
            row.Children.Add(Charts.Columns("Release year", "Owned vs played, last 25 years", byYear, "owned", "played", v => v.ToString("N0")));
            drill = NewDrill();
        }

        // ---------- money / timeline charts ----------

        private void BuildSpendColumns(MoneyIndex mi)
        {
            decimal Sum(IEnumerable<Transaction> ts) => ts.Sum(t => t.IsRefund ? -t.Total : t.Total);
            var cols = mi.Transactions.Where(t => !t.IsWalletTopUp).GroupBy(t => t.Date.Year).OrderBy(g => g.Key).Select(g => new Charts.Column
            {
                Label = g.Key.ToString(),
                A = (double)Sum(g.Where(t => !t.IsInGame)),
                B = (double)Sum(g.Where(t => t.IsInGame)),
                Tip = $"{g.Key}: {M(Sum(g.Where(t => !t.IsInGame)), mi.Symbol)} on games, {M(Sum(g.Where(t => t.IsInGame)), mi.Symbol)} in-game, {g.Count(t => !t.IsInGame && !t.IsRefund)} purchases"
            }).ToList();
            if (cols.Count == 0) return;
            root.Children.Add(Charts.Columns("Spend per year", null, cols, "games", "in-game", v => mi.Symbol + v.ToString("N0"), 180));
        }

        private void BuildTimelineColumns(List<GameHistory> games)
        {
            var real = games.Where(h => !h.IsTool).ToList();
            var years = real.Select(h => h.FirstPlayed?.Year).Concat(real.Select(h => h.Acquired?.Year)).Where(y => y.HasValue).Select(y => y.Value).Distinct().OrderBy(y => y).ToList();
            if (years.Count == 0) return;
            var drill = (Drill)null;
            var cols = years.Select(y => new Charts.Column
            {
                Label = y.ToString(),
                A = real.Count(h => h.Acquired?.Year == y),
                B = real.Count(h => h.FirstPlayed?.Year == y),
                Tip = $"{y}: got {real.Count(h => h.Acquired?.Year == y)}, started {real.Count(h => h.FirstPlayed?.Year == y)}",
                OnClick = () => ShowDrill(drill, $"Started in {y}", real.Where(h => h.FirstPlayed?.Year == y))
            }).ToList();
            root.Children.Add(Charts.Columns("Got vs started, per year", "Click a year to see what you started.", cols, "got", "started", v => v.ToString("N0"), 180));
            drill = NewDrill();
        }

        private void BuildComfortTiles(List<GameHistory> games)
        {
            var real = games.Where(h => !h.IsTool && h.Hours >= 30).ToList();
            var comfort = real.Where(h => (h.LastPlayed ?? DateTime.MinValue) > DateTime.Now.AddDays(-90) && h.FirstPlayed.HasValue && h.FirstPlayed < DateTime.Now.AddYears(-1))
                              .OrderByDescending(h => h.Hours).Take(10).ToList();
            var forgotten = real.Where(h => h.LastPlayed.HasValue && h.LastPlayed < DateTime.Now.AddYears(-1)).OrderByDescending(h => h.Hours).Take(10).ToList();
            if (comfort.Count > 0)
            {
                Section("Comfort games", "30h+, started over a year ago, still played in the last 90 days.");
                root.Children.Add(Charts.Tiles(comfort.Select(h => TileFor(h, $"{h.Hours:N0}h", h.FirstPlayed.HasValue ? $"since {h.FirstPlayed:yyyy}" : null)), 100));
            }
            if (forgotten.Count > 0)
            {
                Section("Forgotten favourites", "30h+ but untouched for a year. Worth a revisit?");
                root.Children.Add(Charts.Tiles(forgotten.Select(h => TileFor(h, $"{h.Hours:N0}h", $"last {h.LastPlayed:MMM yyyy}")), 100));
            }
        }
    }
}
