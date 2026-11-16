using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Ledger
{
    /// <summary>
    /// The Ledger sidebar page. Built in code instead of XAML: it's a read-only report that is
    /// rebuilt from scratch each time it opens, and code keeps all the queries next to their rows.
    /// Uses Playnite theme resources (TextBrush, GlyphBrush, ...) so it follows the active theme.
    /// </summary>
    internal class LedgerView : UserControl
    {
        private readonly Ledger plugin;
        private readonly IPlayniteAPI api;
        private readonly StackPanel root = new StackPanel { Margin = new Thickness(30, 20, 30, 30) };
        private const int StaleDays = 180;

        public LedgerView(Ledger plugin, IPlayniteAPI api)
        {
            this.plugin = plugin;
            this.api = api;
            Content = new ScrollViewer
            {
                Content = root,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Refresh();
        }

        public void Refresh()
        {
            root.Children.Clear();
            HistoryIndex idx;
            try
            {
                idx = HistoryIndex.Build(api, plugin.GetPluginUserDataPath());
            }
            catch (Exception e)
            {
                root.Children.Add(Text("Ledger couldn't build its index: " + e.Message, 14));
                return;
            }

            var games = idx.Games.Where(h => !h.Game.Hidden).ToList();
            BuildHeader(games);
            BuildCards(games);
            BuildOnThisDay(games);
            BuildTimeline(games);
            BuildMostPlayed(games);
            BuildBacklog(games);
            BuildStaleInstalls(games);
            BuildFooter();
        }

        // ---------- sections ----------

        private void BuildHeader(List<GameHistory> games)
        {
            var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 10) };
            var title = new StackPanel();
            title.Children.Add(Text("Ledger", 28, bold: true));
            title.Children.Add(Text("Your library's history: what you own, when you got it, when you started playing.", 13, dim: true));
            DockPanel.SetDock(title, Dock.Left);
            row.Children.Add(title);

            var refresh = new Button { Content = "Refresh", Padding = new Thickness(12, 4, 12, 4), VerticalAlignment = VerticalAlignment.Center };
            refresh.Click += (s, e) => Refresh();
            DockPanel.SetDock(refresh, Dock.Right);
            row.Children.Add(refresh);
            root.Children.Add(row);
        }

        private void BuildCards(List<GameHistory> games)
        {
            var played = games.Count(h => h.Game.Playtime > 0);
            var wrap = new WrapPanel { Margin = new Thickness(0, 5, 0, 10) };
            wrap.Children.Add(Card(games.Count.ToString("N0"), "games"));
            wrap.Children.Add(Card(games.Sum(h => h.Hours).ToString("N0"), "hours played"));
            wrap.Children.Add(Card(Pct(played, games.Count), $"played ({played:N0})"));
            wrap.Children.Add(Card(games.Count(h => h.Game.IsInstalled).ToString("N0"), "installed"));
            wrap.Children.Add(Card(games.Count(h => h.FirstPlayed.HasValue).ToString("N0"), "first-play dates"));
            wrap.Children.Add(Card(games.Count(h => h.Acquired.HasValue).ToString("N0"), "acquisition dates"));
            var waits = games.Where(h => h.DaysToFirstPlay.HasValue).Select(h => h.DaysToFirstPlay.Value).OrderBy(d => d).ToList();
            if (waits.Count >= 5)
            {
                var median = waits[waits.Count / 2];
                wrap.Children.Add(Card(median == 0 ? "same day" : $"{median:N0} days", $"median wait, purchase → first play ({waits.Count})"));
            }
            root.Children.Add(wrap);
        }

        private void BuildOnThisDay(List<GameHistory> games)
        {
            var today = DateTime.Today;
            bool Same(DateTime? d) => d.HasValue && d.Value.Month == today.Month && d.Value.Day == today.Day && d.Value.Year < today.Year;
            var events = games.Where(h => !h.IsTool)
                .SelectMany(h => new[]
                {
                    Same(h.FirstPlayed) ? new { h, when = h.FirstPlayed.Value, what = "started" } : null,
                    Same(h.Acquired) ? new { h, when = h.Acquired.Value, what = "got" } : null
                })
                .Where(e => e != null).OrderByDescending(e => e.when).ToList();
            if (events.Count == 0) return;

            Section("On this day", today.ToString("d MMMM"));
            foreach (var e in events.Take(8))
            {
                var years = today.Year - e.when.Year;
                var tb = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
                tb.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                tb.Inlines.Add(new Run($"{years} year{(years == 1 ? "" : "s")} ago you {e.what} "));
                tb.Inlines.Add(GameLink(e.h, e.h.Hours > 0 ? $" · {e.h.Hours:N0}h since" : " · still unplayed"));
                root.Children.Add(tb);
            }
        }

        private void BuildTimeline(List<GameHistory> games)
        {
            Section("Timeline", "Per year: games you started (first Steam achievement) vs games you bought (Steam licenses).");
            var dated = games.Where(h => h.FirstPlayed.HasValue && !h.IsTool).ToList();
            if (dated.Count == 0)
            {
                root.Children.Add(Text("No first-play dates yet. They come from Steam's local achievement cache; launch a game through Steam once and it appears.", 13, dim: true));
                return;
            }

            var started = dated.GroupBy(h => h.FirstPlayed.Value.Year).ToDictionary(g => g.Key, g => g.ToList());
            var bought = games.Where(h => h.Acquired.HasValue && !h.IsTool).GroupBy(h => h.Acquired.Value.Year).ToDictionary(g => g.Key, g => g.Count());
            var years = started.Keys.Union(bought.Keys).OrderByDescending(y => y).ToList();
            var max = Math.Max(started.Values.Select(v => v.Count).DefaultIfEmpty(0).Max(), bought.Values.DefaultIfEmpty(0).Max());
            foreach (var year in years)
            {
                var list = started.TryGetValue(year, out var l) ? l : new List<GameHistory>();
                var got = bought.TryGetValue(year, out var b) ? b : 0;
                var top = list.OrderByDescending(h => h.Hours).Take(6).ToList();
                var grid = Row(new GridLength(60), new GridLength(170), new GridLength(1, GridUnitType.Star));
                Place(grid, Text(year.ToString(), 16, bold: true), 0);
                var bars = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                bars.Children.Add(Bar(list.Count, max, $"{list.Count} started"));
                var gotBar = Bar(got, max, $"{got} bought");
                gotBar.Opacity = 0.6;
                gotBar.Margin = new Thickness(0, 2, 12, 0);
                bars.Children.Add(gotBar);
                Place(grid, bars, 1);
                var names = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
                names.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                for (var i = 0; i < top.Count; i++)
                {
                    if (i > 0) names.Inlines.Add(new Run(" · "));
                    names.Inlines.Add(GameLink(top[i], $" ({top[i].Hours:N0}h)"));
                }
                if (list.Count > top.Count) names.Inlines.Add(new Run($"  +{list.Count - top.Count} more"));
                Place(grid, names, 2);
                grid.Margin = new Thickness(0, 4, 0, 4);
                root.Children.Add(grid);
            }
        }

        private void BuildMostPlayed(List<GameHistory> games)
        {
            Section("Most played", "All-time playtime as reported by each store.");
            var top = games.Where(h => h.Game.Playtime > 0 && !h.IsTool).OrderByDescending(h => h.Game.Playtime).Take(12).ToList();
            if (top.Count == 0) return;
            var max = top[0].Hours;
            foreach (var h in top)
            {
                var grid = Row(new GridLength(280), new GridLength(1, GridUnitType.Star), new GridLength(220));
                Place(grid, LinkBlock(h), 0);
                Place(grid, Bar(h.Hours, max, $"{h.Hours:N0}h"), 1);
                Place(grid, Text(Since(h), 12, dim: true), 2);
                root.Children.Add(grid);
            }
        }

        private void BuildBacklog(List<GameHistory> games)
        {
            var backlog = games.Where(h => h.Game.Playtime == 0 && !h.FirstPlayed.HasValue && !h.IsTool).ToList();
            Section($"Backlog · {backlog.Count:N0}", "Never played. Oldest acquisitions first, then installed ones.");
            var list = backlog
                .OrderBy(h => h.Acquired.HasValue ? 0 : 1).ThenBy(h => h.Acquired)
                .ThenByDescending(h => h.Game.IsInstalled).ThenBy(h => h.Game.Name)
                .Take(20).ToList();
            foreach (var h in list)
            {
                var grid = Row(new GridLength(110), new GridLength(1, GridUnitType.Star), new GridLength(160));
                Place(grid, Text(h.Acquired?.ToString("yyyy-MM-dd") ?? "", 12, dim: true), 0);
                Place(grid, LinkBlock(h), 1);
                Place(grid, Text((h.Game.Source?.Name ?? "") + (h.Game.IsInstalled ? " · installed" : ""), 12, dim: true), 2);
                root.Children.Add(grid);
            }
            if (backlog.Count > list.Count)
            {
                root.Children.Add(Text($"+{backlog.Count - list.Count:N0} more. Filter the library by Playtime = 0 to see them all.", 12, dim: true));
            }
        }

        private void BuildStaleInstalls(List<GameHistory> games)
        {
            var cutoff = DateTime.Now.AddDays(-StaleDays);
            var stale = games.Where(h => h.Game.IsInstalled && (h.LastPlayed ?? DateTime.MinValue) < cutoff)
                             .OrderByDescending(h => h.Game.InstallSize ?? 0).ToList();
            var total = stale.Sum(h => (double)(h.Game.InstallSize ?? 0));
            Section($"Stale installs · {stale.Count} · {Size(total)}", $"Installed but not played in {StaleDays} days: uninstall candidates, biggest first.");
            foreach (var h in stale.Take(15))
            {
                var grid = Row(new GridLength(90), new GridLength(1, GridUnitType.Star), new GridLength(200));
                Place(grid, Text(Size(h.Game.InstallSize ?? 0), 12, bold: true), 0);
                Place(grid, LinkBlock(h, h.IsTool ? "tool" : null), 1);
                Place(grid, Text(h.LastPlayed.HasValue ? "last played " + h.LastPlayed.Value.ToString("yyyy-MM-dd") : "never played", 12, dim: true), 2);
                root.Children.Add(grid);
            }
            if (stale.Any(h => !h.Game.InstallSize.HasValue))
            {
                root.Children.Add(Text("Some sizes are unknown. Playnite scans install sizes in the background after imports.", 12, dim: true));
            }
        }

        private void BuildFooter()
        {
            Section("Data", null);
            var snaps = plugin.Snapshots.Dir;
            var files = Directory.Exists(snaps) ? Directory.GetFiles(snaps, "*.jsonl").OrderBy(f => f).ToList() : new List<string>();
            root.Children.Add(Text(files.Count == 0
                ? "No snapshots yet."
                : $"{files.Count} daily snapshot(s), {System.IO.Path.GetFileNameWithoutExtension(files.First())} → {System.IO.Path.GetFileNameWithoutExtension(files.Last())}. Query them with tools/ledger.py.", 12, dim: true));

            var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(ActionButton("Import Steam acquisition dates", () => { plugin.SteamAcquisitions.RunInteractive(); Refresh(); }));
            buttons.Children.Add(ActionButton("Write snapshot now", () => { plugin.Snapshots.TryWriteToday(force: true); Refresh(); }));
            buttons.Children.Add(ActionButton("Open data folder", () => System.Diagnostics.Process.Start(plugin.GetPluginUserDataPath())));
            root.Children.Add(buttons);
        }

        // ---------- building blocks ----------

        private void Section(string title, string subtitle)
        {
            var sep = new Rectangle { Height = 1, Margin = new Thickness(0, 22, 0, 10) };
            sep.SetResourceReference(Shape.FillProperty, "PanelSeparatorBrush");
            root.Children.Add(sep);
            root.Children.Add(Text(title, 18, bold: true));
            if (subtitle != null)
            {
                var sub = Text(subtitle, 12, dim: true);
                sub.Margin = new Thickness(0, 0, 0, 8);
                root.Children.Add(sub);
            }
        }

        private FrameworkElement Card(string value, string label)
        {
            var border = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 10, 16, 10),
                Margin = new Thickness(0, 0, 10, 10),
                MinWidth = 130
            };
            border.SetResourceReference(Border.BackgroundProperty, "ControlBackgroundBrush");
            var sp = new StackPanel();
            var v = Text(value, 24, bold: true);
            v.SetResourceReference(TextBlock.ForegroundProperty, "GlyphBrush");
            sp.Children.Add(v);
            sp.Children.Add(Text(label, 12, dim: true));
            border.Child = sp;
            return border;
        }

        private FrameworkElement Bar(double value, double max, string label)
        {
            var grid = new Grid { Height = 18, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(value, 0.0001), GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(max - value, 0.0001), GridUnitType.Star) });
            var fill = new Border { CornerRadius = new CornerRadius(3), Opacity = 0.55 };
            fill.SetResourceReference(Border.BackgroundProperty, "GlyphBrush");
            grid.Children.Add(fill);
            var text = Text(label, 11);
            text.Margin = new Thickness(6, 0, 0, 0);
            text.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumnSpan(text, 2);
            grid.Children.Add(text);
            return grid;
        }

        private TextBlock LinkBlock(GameHistory h, string badge = null)
        {
            var tb = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
            tb.Inlines.Add(GameLink(h, badge == null ? null : $"  [{badge}]"));
            return tb;
        }

        private Inline GameLink(GameHistory h, string suffix)
        {
            var link = new Hyperlink(new Run(h.Game.Name)) { TextDecorations = null, Cursor = Cursors.Hand };
            link.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
            link.ToolTip = Tooltip(h);
            link.Click += (s, e) =>
            {
                api.MainView.SwitchToLibraryView();
                api.MainView.SelectGame(h.Game.Id);
            };
            if (suffix == null) return link;
            var span = new Span();
            span.Inlines.Add(link);
            var sfx = new Run(suffix);
            sfx.SetResourceReference(TextElement.ForegroundProperty, "TextBrushDarker");
            span.Inlines.Add(sfx);
            return span;
        }

        internal static string Tooltip(GameHistory h)
        {
            var lines = new List<string> { h.Game.Name };
            if (h.Acquired.HasValue) lines.Add($"Acquired     {h.Acquired:yyyy-MM-dd}" + (h.AcquiredVia != null ? $"  ({h.AcquiredVia})" : ""));
            if (h.FirstPlayed.HasValue) lines.Add($"First played {h.FirstPlayed:yyyy-MM-dd}  (first of {h.Unlocks} achievements)");
            if (h.LastPlayed.HasValue) lines.Add($"Last played  {h.LastPlayed:yyyy-MM-dd}");
            lines.Add($"Playtime     {h.Hours:N1}h");
            if (h.DaysToFirstPlay.HasValue) lines.Add($"Sat in the backlog {h.DaysToFirstPlay} days before first play");
            return string.Join(Environment.NewLine, lines);
        }

        private static string Since(GameHistory h)
        {
            if (h.FirstPlayed.HasValue) return $"since {h.FirstPlayed:MMM yyyy}" + (h.LastPlayed.HasValue ? $" · last {h.LastPlayed:MMM yyyy}" : "");
            return h.LastPlayed.HasValue ? $"last played {h.LastPlayed:MMM yyyy}" : "";
        }

        private Button ActionButton(string label, Action action)
        {
            var b = new Button { Content = label, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 10, 0) };
            b.Click += (s, e) => action();
            return b;
        }

        private static Grid Row(params GridLength[] cols)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            foreach (var c in cols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = c });
            return g;
        }

        private static void Place(Grid g, UIElement e, int col)
        {
            Grid.SetColumn(e, col);
            g.Children.Add(e);
        }

        private static TextBlock Text(string s, double size, bool bold = false, bool dim = false)
        {
            var tb = new TextBlock
            {
                Text = s,
                FontSize = size,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, dim ? "TextBrushDarker" : "TextBrush");
            return tb;
        }

        private static string Pct(int n, int of) => of == 0 ? "–" : (100.0 * n / of).ToString("0", CultureInfo.InvariantCulture) + "%";

        private static string Size(double bytes)
        {
            if (bytes <= 0) return "?";
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            var i = 0;
            while (bytes >= 1024 && i < u.Length - 1) { bytes /= 1024; i++; }
            return bytes.ToString(i >= 3 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + u[i];
        }
    }
}
