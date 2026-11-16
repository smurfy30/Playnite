using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Ledger
{
    internal partial class LedgerView
    {
        private string M(decimal v, string sym) => sym + v.ToString(v >= 1000 ? "N0" : "N2", CultureInfo.InvariantCulture);

        private void BuildMoney(Data d, List<GameHistory> scope)
        {
            var mi = d.Money;
            var sym = mi.Symbol;
            if (!mi.HasSpend && !mi.HasPrices)
            {
                Section("Money", null);
                root.Children.Add(Text("No price or purchase data yet. Use the buttons below.", 13, dim: true));
                BuildMoneyButtons();
                return;
            }
            var ids = new HashSet<Guid>(scope.Select(h => h.Game.Id));
            var games = mi.Games.Values.Where(g => ids.Contains(g.H.Game.Id)).ToList();
            var tx = mi.Transactions.Where(t => !t.IsWalletTopUp && (period.IsAll || period.Contains(t.Date))).ToList();

            decimal Sum(IEnumerable<Transaction> ts) => ts.Sum(t => t.IsRefund ? -t.Total : t.Total);
            var gameSpend = Sum(tx.Where(t => !t.IsInGame));
            var inGame = Sum(tx.Where(t => t.IsInGame));
            var saved = tx.Where(t => !t.IsRefund && t.ListPrice.HasValue && t.ListPrice > t.Total).Sum(t => t.ListPrice.Value - t.Total);
            var owned = scope.Where(h => h.Game.Source?.Name == "Steam").Select(h => h.Game.Id).ToHashSet();
            var value = games.Where(g => owned.Contains(g.H.Game.Id)).Sum(g => g.ListPrice ?? 0);
            var freeValue = games.Where(g => g.H.AcquiredVia != null && NormalizeMethod(g.H.AcquiredVia) == "Free").Sum(g => g.ListPrice ?? 0);
            var paidGames = games.Where(g => g.Total > 0).ToList();
            var paidHours = paidGames.Sum(g => g.H.Hours);
            var shelf = paidGames.Where(g => !g.H.Played).ToList();

            var wrap = new WrapPanel { Margin = new Thickness(0, 5, 0, 10) };
            wrap.Children.Add(Card(M(gameSpend, sym), "spent on games" + (period.IsAll ? "" : $" ({period.Name})")));
            wrap.Children.Add(Card(M(inGame, sym), "in-game purchases"));
            wrap.Children.Add(Card(M(saved, sym), "saved by buying on sale"));
            if (period.IsAll) wrap.Children.Add(Card(M(value, sym), "Steam library at today's full price"));
            if (period.IsAll && freeValue > 0) wrap.Children.Add(Card(M(freeValue, sym), "worth of games you got free"));
            if (paidHours > 0) wrap.Children.Add(Card(M((gameSpend + inGame) / (decimal)Math.Max(1, paidHours), sym), "per hour on paid games"));
            if (shelf.Count > 0) wrap.Children.Add(Card(M(shelf.Sum(g => g.Paid), sym), $"paid for {shelf.Count} games never played"));
            root.Children.Add(wrap);
            root.Children.Add(Text($"Steam purchase history + Epic order totals; prices from the Steam store ({sym}). Refunds subtracted; wallet top-ups ignored (the spend is counted when used)." +
                (mi.Unattributed > 0 ? $" {M(mi.Unattributed, sym)} couldn't be matched to a game in your library (DLC for games you no longer own, gifts…)." : ""), 11, dim: true));

            // Spend per year (games vs in-game), always all years so the trend is visible.
            var row = Panels();
            root.Children.Add(row);
            var outer = root; root = new StackPanel(); BuildSpendColumns(mi); row.Children.Add(root); root = outer;
            var shares = games.Where(g => g.Total > 0).OrderByDescending(g => g.Total).ToList();
            if (shares.Count > 0)
                row.Children.Add(Charts.Donut("Where the money went", shares.Take(7).Select(g => new Slice { Label = g.H.Game.Name, Value = (double)g.Total })
                    .Concat(new[] { new Slice { Label = "Everything else", Value = (double)shares.Skip(7).Sum(g => g.Total), Detail = $"{shares.Count - 7} other games" } }),
                    M(shares.Sum(g => g.Total), sym), "attributed", 180, sort: false, fmt: x => sym + x.ToString("N0")));

            MoneyList("Biggest spends", "Base game + DLC + in-game, lifetime.", games.Where(g => g.Total > 0).OrderByDescending(g => g.Total).Take(12),
                g => M(g.Total, sym), g => string.Join(" · ", new[] { g.InGame > 0 ? $"{M(g.Paid, sym)} game + {M(g.InGame, sym)} in-game" : null, g.PerHour.HasValue ? $"{M(g.PerHour.Value, sym)}/h" : null, $"{g.H.Hours:N0}h" }.Where(x => x != null)));
            MoneyList("Best value", "Lowest cost per hour (paid, 2h+).", games.Where(g => g.Total > 0 && g.H.Hours >= 2).OrderBy(g => g.PerHour).Take(12),
                g => $"{M(g.PerHour ?? 0, sym)}/h", g => $"{M(g.Total, sym)} for {g.H.Hours:N0}h");
            MoneyList("Money on the shelf", "Paid for, never played. Most expensive first.", shelf.OrderByDescending(g => g.Paid).Take(12),
                g => M(g.Paid, sym), g => g.H.Acquired.HasValue ? $"bought {g.H.Acquired:MMM yyyy}" : "");
            MoneyList("Most valuable unplayed", "Never played, by today's full Steam price.", games.Where(g => !g.H.Played && g.ListPrice > 0 && owned.Contains(g.H.Game.Id)).OrderByDescending(g => g.ListPrice).Take(12),
                g => M(g.ListPrice ?? 0, sym), g => g.H.AcquiredVia != null ? NormalizeMethod(g.H.AcquiredVia) : "");
            BuildMoneyButtons();
        }

        private void MoneyList(string title, string sub, IEnumerable<GameMoney> rows, Func<GameMoney, string> amount, Func<GameMoney, string> meta)
        {
            var list = rows.ToList();
            if (list.Count == 0) return;
            Section(title, sub);
            foreach (var g in list)
            {
                var grid = Row(new GridLength(100), new GridLength(1, GridUnitType.Star), new GridLength(320));
                Place(grid, Text(amount(g), 13, bold: true), 0);
                Place(grid, LinkBlock(g.H), 1);
                Place(grid, Text(meta(g), 12, dim: true), 2);
                root.Children.Add(grid);
            }
        }

        private void BuildMoneyButtons()
        {
            var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
            buttons.Children.Add(ActionButton("Refresh Steam purchase history", () => { plugin.Money.FetchSpend(); Refresh(); }));
            buttons.Children.Add(ActionButton("Refresh Steam prices", () => { plugin.FetchPrices(); Refresh(); }));
            buttons.Children.Add(ActionButton("Refresh Epic orders", () => { plugin.EpicAcquisitions.Run(); Refresh(); }));
            root.Children.Add(buttons);
        }

        /// <summary>GitHub-style calendar of hours played per day for the last year, from tracked activity.</summary>
        private void BuildHeatmap(Data d, List<GameHistory> scope)
        {
            var ids = new HashSet<Guid>(scope.Select(h => h.Game.Id));
            var byDay = d.Activity.Daily.Where(kv => ids.Contains(kv.Key)).SelectMany(kv => kv.Value)
                         .GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Sum(x => x.Value));
            Section("Play calendar", d.Activity.TrackedSince.HasValue
                ? $"Hours per day over the last year. Tracked since {d.Activity.TrackedSince:d MMM yyyy}; it fills in as you play."
                : "Hours per day. Fills in as Ledger tracks sessions (Playnite running in the tray is enough).");

            var end = DateTime.Today;
            var start = end.AddDays(-7 * 52 - (int)end.DayOfWeek);
            var max = byDay.Values.DefaultIfEmpty(0).Max();
            var grid = new UniformGrid { Rows = 7, Columns = 53, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 0) };
            // UniformGrid fills row by row; we want columns = weeks, rows = weekdays.
            var cells = new Border[7, 53];
            for (var r = 0; r < 7; r++)
                for (var c = 0; c < 53; c++)
                {
                    var b = new Border { Width = 13, Height = 13, Margin = new Thickness(1.5), CornerRadius = new CornerRadius(2) };
                    b.SetResourceReference(Border.BackgroundProperty, "GlyphBrush");
                    b.Opacity = 0.08;
                    cells[r, c] = b;
                    grid.Children.Add(b);
                }
            for (var day = start; day <= end; day = day.AddDays(1))
            {
                var c = (int)((day - start).TotalDays / 7);
                var r = (int)day.DayOfWeek;
                if (c >= 53) continue;
                var h = byDay.TryGetValue(day, out var v) ? v : 0;
                var cell = cells[r, c];
                if (h > 0) cell.Opacity = 0.25 + 0.75 * Math.Min(1, h / Math.Max(1, max));
                cell.ToolTip = $"{day:ddd d MMM yyyy}: {(h > 0 ? $"{h:0.#}h" : "no tracked play")}";
            }
            // Month labels above the week columns, weekday labels on the left, less→more legend below.
            const double pitch = 16; // 13 + margins
            var months = new Canvas { Height = 16, Width = 53 * pitch, Margin = new Thickness(30, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            for (var day = start; day <= end; day = day.AddDays(1))
            {
                if (day.Day != 1) continue;
                var m = Text(day.ToString("MMM"), 10, dim: true);
                Canvas.SetLeft(m, (int)((day - start).TotalDays / 7) * pitch);
                months.Children.Add(m);
            }
            var dayLabels = new Grid { Width = 30 };
            for (var r = 0; r < 7; r++) dayLabels.RowDefinitions.Add(new RowDefinition { Height = new GridLength(pitch) });
            foreach (var (r, label) in new[] { (1, "Mon"), (3, "Wed"), (5, "Fri") })
            {
                var t = Text(label, 10, dim: true); Grid.SetRow(t, r); dayLabels.Children.Add(t);
            }
            var body = new StackPanel { Orientation = Orientation.Horizontal };
            body.Children.Add(dayLabels);
            body.Children.Add(grid);
            root.Children.Add(months);
            root.Children.Add(body);
            var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(30, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            legend.Children.Add(Text("less ", 10, dim: true));
            foreach (var o in new[] { 0.08, 0.3, 0.55, 0.8, 1.0 })
            {
                var b = new Border { Width = 11, Height = 11, Margin = new Thickness(1.5), CornerRadius = new CornerRadius(2), Opacity = o };
                b.SetResourceReference(Border.BackgroundProperty, "GlyphBrush");
                legend.Children.Add(b);
            }
            legend.Children.Add(Text(" more", 10, dim: true));
            root.Children.Add(legend);

            if (byDay.Count > 0)
            {
                var days = byDay.Count(kv => kv.Value > 0);
                var streak = 0; var best = 0;
                for (var day = start; day <= end; day = day.AddDays(1))
                {
                    if (byDay.TryGetValue(day, out var v) && v > 0) { streak++; best = Math.Max(best, streak); } else streak = 0;
                }
                root.Children.Add(Text($"{days} days played · longest streak {best} days · busiest day {byDay.OrderByDescending(kv => kv.Value).First().Key:d MMM} ({byDay.Values.Max():0.#}h)", 12, dim: true));
            }
        }
    }
}
