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
    /// The Ledger sidebar page: tabs of read-only reports, rebuilt from scratch on open/refresh.
    /// Built in code (not XAML) so each query sits next to the rows it produces. Uses Playnite theme
    /// resources (TextBrush, GlyphBrush, ...) so it follows the active theme.
    /// </summary>
    internal partial class LedgerView : UserControl
    {
        public static readonly string[] Tabs = { "Overview", "Explorer", "Backlog", "Installs", "Stats", "Money", "Wishlist", "Timeline", "Mods" };

        private readonly Ledger plugin;
        private readonly IPlayniteAPI api;
        private readonly Grid shell = new Grid();
        private readonly TabControl tabs = new TabControl();
        private StackPanel root; // the panel the Build* helpers currently write into
        private int selectedTab;

        public LedgerView(Ledger plugin, IPlayniteAPI api, bool sync = false)
        {
            this.plugin = plugin;
            this.api = api;
            filter = plugin.Settings.CurrentFilter().Clone();
            selectedTab = Math.Max(0, Array.IndexOf(Tabs, plugin.Settings.LastTab ?? "Overview"));
            Content = shell;
            shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            shell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            tabs.Margin = new Thickness(20, 6, 20, 0);
            tabs.SelectionChanged += (s, e) =>
            {
                if (e.Source != tabs || tabs.SelectedIndex < 0) return;
                selectedTab = tabs.SelectedIndex;
                plugin.Settings.LastTab = Tabs[selectedTab];
                EnsureBuilt(selectedTab); // lazy: a tab is built the first time it's shown
            };
            Refresh(sync);
        }

        // ---------- data ----------

        private class Data
        {
            public HistoryIndex Index;
            public List<GameHistory> Visible;
            public List<GameMods> Mods;
            public List<Suggestion> UpNext;
            public ActivityIndex Activity;
            public MoneyIndex Money;
            public List<WishEntry> Wishlist;
            public string Error;
        }

        private Data Load()
        {
            var d = new Data();
            try
            {
                d.Index = HistoryIndex.Build(api, plugin.GetPluginUserDataPath());
                d.Visible = d.Index.Games.Where(h => !h.Game.Hidden).ToList();
                d.UpNext = global::Ledger.UpNext.Rank(d.Index.Games);
                d.Mods = ModIndex.Build(api, plugin.GetPluginUserDataPath());
                d.Activity = ActivityIndex.Build(api, plugin.GetPluginUserDataPath());
                d.Money = MoneyIndex.Build(plugin.Money, d.Index.Games, plugin.GetPluginUserDataPath());
                try { d.Wishlist = WishlistIndex.Build(api, plugin.GetPluginUserDataPath(), d.Index.Games); } catch { d.Wishlist = new List<WishEntry>(); }
            }
            catch (Exception e)
            {
                d.Error = e.Message;
            }
            return d;
        }

        /// <summary>Loads off the UI thread, then builds. sync=true does it inline (headless renders).</summary>
        public void Refresh(bool sync = false)
        {
            if (sync)
            {
                SafeShow(Load());
                return;
            }
            shell.Children.Clear();
            var loading = new StackPanel { Margin = new Thickness(30) };
            loading.Children.Add(Text("Ledger", 28, bold: true));
            loading.Children.Add(Text("Reading your library…", 13, dim: true));
            shell.Children.Add(loading);
            System.Threading.Tasks.Task.Run(() => Load()).ContinueWith(t => Dispatcher.Invoke(() => SafeShow(t.Result)));
        }

        private readonly List<StackPanel> pages = new List<StackPanel>();

        private Action[] builders = new Action[0];
        private readonly bool[] built = new bool[16];

        private void Show(Data d)
        {
            last = d;
            shell.Children.Clear();
            pages.Clear();
            if (d == null || d.Error != null || d.Index == null)
            {
                shell.Children.Add(Text("Ledger couldn't build its index: " + d?.Error, 14));
                return;
            }
            var years = d.Index.Games.SelectMany(h => new[] { h.Acquired?.Year, h.FirstPlayed?.Year, h.LastPlayed?.Year }).Where(y => y.HasValue).Select(y => y.Value).Distinct();
            period = Period.Options(years).FirstOrDefault(p => p.Name == filter.Period) ?? new Period { Name = "All time" };

            var g = Scope(d.Visible);
            builders = new Action[]
            {
                () => { BuildCards(g, d); if (period.IsAll) BuildOverviewCharts(g); BuildInsights(g, d); BuildOnThisDay(g); BuildUpNextTiles(d.UpNext.Where(x => g.Contains(x.H)).ToList(), 8); BuildRecent(); },
                () => { BuildExplorer(g, d); },
                () => { BuildShelves(g); BuildQuickWinTiles(g); BuildUpNextTiles(d.UpNext.Where(x => g.Contains(x.H)).ToList(), 16); BuildOldestUnplayed(g); },
                () => { BuildDrives(g); BuildDiskTrend(); BuildStaleInstalls(g); BuildInstalledNeverPlayed(g); },
                () => { BuildStats(g, d); },
                () => { BuildMoney(d, g); },
                () => { BuildWishlist(d); },
                () => { BuildHeatmap(d, g); BuildTimelineColumns(g); BuildComfortTiles(g); BuildTimeline(g); BuildMostPlayed(g); },
                () => { BuildMods(d.Mods); BuildFooter(); },
            };
            Array.Clear(built, 0, built.Length);

            tabs.Items.Clear();
            for (var i = 0; i < Tabs.Length; i++)
            {
                pages.Add(new StackPanel { Margin = new Thickness(10, 10, 10, 30) });
                tabs.Items.Add(new TabItem
                {
                    Header = Tabs[i],
                    // Explorer manages its own scrolling (DataGrid); everything else scrolls as a page.
                    Content = Tabs[i] == "Explorer" ? (object)pages[i] : new ScrollViewer { Content = pages[i], VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
                });
            }
            var sel = Math.Min(selectedTab, Tabs.Length - 1);
            EnsureBuilt(sel);
            tabs.SelectedIndex = sel;

            var top = BuildTopBar(d);
            DockPanel.SetDock(top, Dock.Top);
            var dock = new DockPanel();
            dock.Children.Add(top);
            (tabs.Parent as Panel)?.Children.Remove(tabs); // reused across re-filters
            dock.Children.Add(tabs);
            Grid.SetColumn(dock, 0);
            shell.Children.Add(dock);
            (drawer.Parent as Panel)?.Children.Remove(drawer);
            Grid.SetColumn(drawer, 1);
            shell.Children.Add(drawer);
        }

        private void EnsureBuilt(int i)
        {
            if (i < 0 || i >= builders.Length || built[i]) return;
            built[i] = true;
            root = pages[i];
            try { builders[i](); }
            catch (Exception e) { root.Children.Add(Text($"This tab failed: {e.Message}", 12, dim: true)); }
        }

        private void BuildStats(List<GameHistory> all, Data d)
        {
            // In a period: rows over games played in it, hours = hours in it. All time: everything.
            var g = period.IsAll ? all : all.Where(h => PlayedIn(h, d)).ToList();
            var caveat = TrackingCaveat(d);
            if (!period.IsAll)
            {
                Section($"{period.Name}", $"{g.Count} games played in this period.");
                if (caveat != null) root.Children.Add(Text(caveat, 12, dim: true));
            }
            Func<GameHistory, double> hrs = h => HoursOf(h, d);
            BuildStatsCharts(g, d);
            BuildBreakdown("Genres", g, h => h.Game.Genres?.Select(x => x.Name), hours: hrs);
            BuildBreakdown("Stores", g, h => new[] { h.Game.Source?.Name ?? "(none)" }, hours: hrs);
            BuildBreakdown("Release decade", g, h => h.Game.ReleaseYear.HasValue ? new[] { (h.Game.ReleaseYear.Value / 10 * 10) + "s" } : null, sortByKey: true, hours: hrs);
            BuildBreakdown("Developers", g, h => h.Game.Developers?.Select(x => x.Name), hours: hrs, top: 10);
            if (g.Select(h => h.Game.CompletionStatus?.Name).Distinct().Count() > 2)
                BuildBreakdown("Completion status", g, h => new[] { h.Game.CompletionStatus?.Name ?? "(none)" }, hours: hrs);
            var gotIn = period.IsAll ? all : all.Where(h => period.Contains(h.Acquired)).ToList();
            BuildBreakdown(period.IsAll ? "How you got it" : $"Got in {period.Name}", gotIn, h => h.AcquiredVia == null ? null : new[] { NormalizeMethod(h.AcquiredVia) }, hours: hrs);
        }

        /// <summary>For headless renders: the built content of one tab (call after a sync Refresh).</summary>
        public StackPanel DetachPage(int i)
        {
            i = Math.Max(0, Math.Min(i, pages.Count - 1));
            EnsureBuilt(i);
            var page = pages[i];
            if (page.Parent is ScrollViewer sv) sv.Content = null;
            if (page.Parent is TabItem ti) ti.Content = null;
            return page;
        }

        /// <summary>
        /// Not played since cutoff, *as far as we know*. Played games with no LastActivity (Epic doesn't
        /// report it) are unknown, not stale. Never-played games count as stale.
        /// </summary>
        private static bool StaleSince(GameHistory h, DateTime cutoff) =>
            h.LastPlayed.HasValue ? h.LastPlayed.Value < cutoff : !h.Played;

        private static bool LastPlayedUnknown(GameHistory h) => h.Played && !h.LastPlayed.HasValue;

        // ---------- header / overview ----------

        private FrameworkElement BuildHeader(List<GameHistory> games)
        {
            var row = new DockPanel { LastChildFill = false, Margin = new Thickness(30, 20, 30, 0) };
            var title = new StackPanel();
            title.Children.Add(Text("Ledger", 28, bold: true));
            title.Children.Add(Text("What you own, when you got it, what you play, and what's just taking up space.", 13, dim: true));
            DockPanel.SetDock(title, Dock.Left);
            row.Children.Add(title);
            var refresh = new Button { Content = "Refresh", Padding = new Thickness(12, 4, 12, 4), VerticalAlignment = VerticalAlignment.Center };
            refresh.Click += (s, e) => Refresh();
            DockPanel.SetDock(refresh, Dock.Right);
            row.Children.Add(refresh);
            return row;
        }

        private void BuildCards(List<GameHistory> games, Data d)
        {
            if (!period.IsAll) { BuildPeriodCards(games, d); return; }
            var real = games.Where(h => !h.IsTool).ToList();
            var played = real.Count(h => h.Played);
            var backlog = real.Where(h => !h.Played).ToList();
            var backlogHours = backlog.Where(h => h.Hltb != null).Sum(h => h.Hltb.Typical);
            var installedBytes = real.Where(h => h.Game.IsInstalled).Sum(h => (double)(h.Game.InstallSize ?? 0));

            var wrap = new WrapPanel { Margin = new Thickness(0, 5, 0, 10) };
            wrap.Children.Add(Card(real.Count.ToString("N0"), "games"));
            wrap.Children.Add(Card(real.Sum(h => h.Hours).ToString("N0"), "hours played"));
            wrap.Children.Add(Card(Pct(played, real.Count), $"played ({played:N0})"));
            wrap.Children.Add(Card(backlog.Count.ToString("N0"), "never played"));
            if (backlogHours > 0) wrap.Children.Add(Card($"{backlogHours:N0}h", $"main story, {backlog.Count(h => h.Hltb != null)} of {backlog.Count} unplayed timed"));
            wrap.Children.Add(Card(Size(installedBytes), $"installed ({real.Count(h => h.Game.IsInstalled)})"));
            root.Children.Add(wrap);
        }

        /// <summary>Auto-written facts, Steam-Replay style. Each line is computed, never stored.</summary>
        private void BuildInsights(List<GameHistory> games, Data d)
        {
            if (!period.IsAll) { BuildPeriodInsights(games, d); return; }
            var real = games.Where(h => !h.IsTool).ToList();
            var lines = new List<string>();
            var year = DateTime.Now.Year;

            var thisYear = real.Where(h => h.LastPlayed?.Year == year).ToList();
            if (thisYear.Count > 0)
            {
                var top = thisYear.OrderByDescending(h => h.Hours).First();
                lines.Add($"You've played {thisYear.Count} different games in {year}. The one you keep coming back to is {top.Game.Name} ({top.Hours:N0}h all-time).");
            }

            var gotThisYear = real.Count(h => h.Acquired?.Year == year);
            var startedThisYear = real.Count(h => h.FirstPlayed?.Year == year);
            if (gotThisYear > 0) lines.Add($"In {year} you got {gotThisYear} games and started {startedThisYear}.");

            var backlog = real.Where(h => !h.Played).ToList();
            var timed = backlog.Where(h => h.Hltb != null).ToList();
            if (timed.Count > 10)
            {
                // Pace: hours played in the last 12 months is unknown before snapshots accumulate, so use
                // lifetime hours ÷ years since the first known play as a rough rate.
                var whole = (last?.Visible ?? real).Where(h => !h.IsTool).ToList();
                var firstEver = whole.Where(h => h.FirstPlayed.HasValue).Select(h => h.FirstPlayed.Value).DefaultIfEmpty(DateTime.Now.AddYears(-1)).Min();
                var yearsActive = Math.Max(1, (DateTime.Now - firstEver).TotalDays / 365.25);
                // Pace is a property of you, not of the filter: use the whole (unfiltered) library.
                var perYear = (last?.Visible ?? real).Where(h => !h.IsTool).Sum(h => h.Hours) / yearsActive;
                var est = timed.Sum(h => h.Hltb.Typical) / timed.Count * backlog.Count;
                lines.Add($"Finishing the main story of everything unplayed would take about {est:N0} hours (HowLongToBeat, with the {backlog.Count - timed.Count} untimed games estimated at the average). At your usual {perYear:N0} hours a year, that's {est / Math.Max(1, perYear):N1} years.");
            }

            var oldest = backlog.Where(h => h.Acquired.HasValue).OrderBy(h => h.Acquired).FirstOrDefault();
            if (oldest != null) lines.Add($"Your oldest unplayed game is {oldest.Game.Name}, owned since {oldest.Acquired:MMMM yyyy}.");

            var stale = real.Where(h => h.Game.IsInstalled && StaleSince(h, DateTime.Now.AddDays(-StaleDays))).ToList();
            if (stale.Count > 0)
                lines.Add($"{stale.Count} installed games haven't been touched in {StaleDays / 30} months: {Size(stale.Sum(h => (double)(h.Game.InstallSize ?? 0)))} you could reclaim.");

            var genre = real.Where(h => h.Hours > 0).SelectMany(h => (h.Game.Genres ?? new List<Playnite.SDK.Models.Genre>()).Select(x => new { x.Name, h.Hours }))
                            .GroupBy(x => x.Name).OrderByDescending(x => x.Sum(y => y.Hours)).FirstOrDefault();
            if (genre != null) lines.Add($"Your most-played genre is {genre.Key}: {genre.Sum(x => x.Hours):N0} hours.");

            var free = real.Count(h => h.AcquiredVia != null && NormalizeMethod(h.AcquiredVia) == "Free");
            var freePlayed = real.Count(h => h.AcquiredVia != null && NormalizeMethod(h.AcquiredVia) == "Free" && h.Played);
            if (free > 20) lines.Add($"{free} of your games were free. You've played {Pct(freePlayed, free)} of them.");

            if (lines.Count == 0) return;
            Section("Insights", null);
            foreach (var l in lines)
            {
                var tb = Text("•  " + l, 14);
                tb.Margin = new Thickness(0, 3, 0, 3);
                root.Children.Add(tb);
            }
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

        private void BuildUpNext(List<Suggestion> ranked, int count, bool withPick)
        {
            if (ranked == null || ranked.Count == 0) return;
            Section("Up next", "Unplayed games ranked by your taste (hours per genre), review score and whether it's installed.");
            if (withPick)
            {
                var pick = new Button { Content = "🎲  Pick for me", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Left };
                pick.Click += (s, e) => PickForMe(ranked);
                root.Children.Add(pick);
            }
            foreach (var sug in ranked.Take(count))
            {
                var grid = Row(new GridLength(300), new GridLength(1, GridUnitType.Star), new GridLength(70), new GridLength(70), new GridLength(80));
                Place(grid, LinkBlock(sug.H), 0);
                Place(grid, Text(string.Join(" · ", sug.MatchedGenres), 12, dim: true), 1);
                Place(grid, Text(sug.H.Hltb != null ? $"{sug.H.Hltb.Typical:N0}h" : "", 12, dim: true), 2);
                Place(grid, Text(sug.Rating.HasValue ? $"★ {sug.Rating}" : "", 12, dim: true), 3);
                Place(grid, Text(sug.H.Game.IsInstalled ? "installed" : "", 12, dim: true), 4);
                root.Children.Add(grid);
            }
        }

        private void PickForMe(List<Suggestion> ranked)
        {
            var p = UpNext.Pick(ranked);
            if (p == null) return;
            api.MainView.SwitchToLibraryView();
            api.MainView.SelectGame(p.H.Game.Id);
            var nl = Environment.NewLine;
            api.Dialogs.ShowMessage(
                p.H.Game.Name + nl + nl +
                (p.MatchedGenres.Count > 0 ? $"You've put a lot of hours into {string.Join(", ", p.MatchedGenres)}." + nl : "") +
                (p.Rating.HasValue ? $"Rated {p.Rating}/100." + nl : "") +
                (p.H.Hltb != null ? $"About {p.H.Hltb.Typical:N0}h to beat." + nl : "") +
                (p.H.Game.IsInstalled ? "Already installed." : "Not installed yet.") +
                (p.H.Acquired.HasValue ? nl + $"You've owned it since {p.H.Acquired:MMMM yyyy}." : ""),
                "Ledger · Pick for me");
        }

        // ---------- backlog ----------

        /// <summary>Backloggd-style shelves, derived from playtime and recency (no manual status needed).</summary>
        private void BuildShelves(List<GameHistory> games)
        {
            var real = games.Where(h => !h.IsTool).ToList();
            var yearAgo = DateTime.Now.AddYears(-1);
            var shelves = new[]
            {
                new { Name = "Never played", Tip = "zero playtime, no achievements", Games = real.Where(h => !h.Played).ToList() },
                new { Name = "Tried", Tip = "under 2 hours", Games = real.Where(h => h.Played && h.Hours < 2).ToList() },
                new { Name = "Abandoned", Tip = "2h+, under 70% of HLTB's main story (or under 10h), untouched for a year", Games = real.Where(h => h.Hours >= 2 && h.LastPlayed.HasValue && h.LastPlayed < yearAgo && (h.Hltb != null ? h.Hours < h.Hltb.MainHours * 0.7 : h.Hours < 10)).ToList() },
                new { Name = "Playing", Tip = "played in the last 30 days", Games = real.Where(h => (h.LastPlayed ?? DateTime.MinValue) > DateTime.Now.AddDays(-30)).ToList() },
                new { Name = "Sunk", Tip = "100h+", Games = real.Where(h => h.Hours >= 100).ToList() },
            };
            Section("Shelves", "Your library sorted the way backlog trackers do it, worked out from playtime and dates. Click a shelf to open it.");
            var max = shelves.Max(x => x.Games.Count);
            foreach (var shelf in shelves)
            {
                var grid = Row(new GridLength(150), new GridLength(1, GridUnitType.Star), new GridLength(340));
                Place(grid, Text("▸ " + shelf.Name, 14, bold: true), 0);
                Place(grid, Bar(shelf.Games.Count, max, $"{shelf.Games.Count}"), 1);
                var tip = Text(shelf.Tip, 12, dim: true);
                tip.TextTrimming = TextTrimming.CharacterEllipsis;
                tip.TextWrapping = TextWrapping.NoWrap;
                Place(grid, tip, 2);

                // Expanding list, built lazily on first open (Never played can be 400+ games).
                var list = new WrapPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(150, 2, 0, 10) };
                var members = shelf.Games.OrderByDescending(h => h.LastPlayed ?? h.Acquired ?? DateTime.MinValue).ToList();
                var header = (TextBlock)grid.Children[0];
                grid.Background = Brushes.Transparent; // hit-testable across the whole row
                grid.Cursor = Cursors.Hand;
                grid.MouseLeftButtonUp += (o, e) =>
                {
                    if (list.Children.Count == 0)
                    {
                        foreach (var h in members.Take(200))
                        {
                            var tb = new TextBlock { FontSize = 12, Margin = new Thickness(0, 1, 14, 1) };
                            tb.Inlines.Add(GameLink(h, h.Hours > 0 ? $" {h.Hours:N0}h" : null));
                            list.Children.Add(tb);
                        }
                        if (members.Count > 200) list.Children.Add(Text($"+{members.Count - 200} more", 12, dim: true));
                    }
                    var open = list.Visibility != Visibility.Visible;
                    list.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                    header.Text = (open ? "▾ " : "▸ ") + shelf.Name;
                };
                root.Children.Add(grid);
                root.Children.Add(list);
            }
        }

        private void BuildQuickWins(List<GameHistory> games)
        {
            var wins = games.Where(h => !h.IsTool && !h.Played && h.Hltb != null && h.Hltb.MainHours > 0.3)
                            .OrderByDescending(h => h.Game.IsInstalled).ThenBy(h => h.Hltb.MainHours).Take(12).ToList();
            Section("Quick wins", "Unplayed and short (HowLongToBeat main story). Installed first, so you can start now.");
            if (wins.Count == 0)
            {
                root.Children.Add(Text("No HowLongToBeat times yet. Extensions → Ledger → Fetch HowLongToBeat times.", 12, dim: true));
                return;
            }
            foreach (var h in wins)
            {
                var grid = Row(new GridLength(70), new GridLength(1, GridUnitType.Star), new GridLength(160));
                Place(grid, Text($"{h.Hltb.MainHours:0.#}h", 13, bold: true), 0);
                Place(grid, LinkBlock(h), 1);
                Place(grid, Text((h.Game.Source?.Name ?? "") + (h.Game.IsInstalled ? " · installed" : ""), 12, dim: true), 2);
                root.Children.Add(grid);
            }
        }

        private void BuildOldestUnplayed(List<GameHistory> games)
        {
            var list = games.Where(h => !h.IsTool && !h.Played && h.Acquired.HasValue).OrderBy(h => h.Acquired).Take(15).ToList();
            if (list.Count == 0) return;
            Section("Oldest unplayed", "The pile of shame, by how long you've owned it.");
            foreach (var h in list)
            {
                var age = (DateTime.Now - h.Acquired.Value).TotalDays / 365.25;
                var grid = Row(new GridLength(110), new GridLength(1, GridUnitType.Star), new GridLength(110), new GridLength(160));
                Place(grid, Text(h.Acquired.Value.ToString("yyyy-MM-dd"), 12, dim: true), 0);
                Place(grid, LinkBlock(h), 1);
                Place(grid, Text($"{age:0.#} years", 12, dim: true), 2);
                Place(grid, Text((h.Game.Source?.Name ?? "") + (h.AcquiredVia != null ? " · " + NormalizeMethod(h.AcquiredVia) : ""), 12, dim: true), 3);
                root.Children.Add(grid);
            }
        }

        // ---------- installs ----------

        private void BuildDrives(List<GameHistory> games)
        {
            var installed = games.Where(h => h.Game.IsInstalled && h.Drive != null).ToList();
            Section("Drives", "Installed games per drive: total, and how much of it is stale or never played.");
            var cutoff = DateTime.Now.AddDays(-StaleDays);
            var drives = installed.GroupBy(h => h.Drive).OrderBy(g => g.Key).ToList();
            foreach (var d in drives)
            {
                double total = d.Sum(h => (double)(h.Game.InstallSize ?? 0));
                double stale = d.Where(h => StaleSince(h, cutoff)).Sum(h => (double)(h.Game.InstallSize ?? 0));
                string free = "";
                try { var di = new DriveInfo(d.Key); if (di.IsReady) free = $" · {Size(di.AvailableFreeSpace)} free of {Size(di.TotalSize)}"; } catch { }
                var grid = Row(new GridLength(60), new GridLength(1, GridUnitType.Star), new GridLength(330));
                Place(grid, Text(d.Key, 16, bold: true), 0);
                Place(grid, Bar(stale, Math.Max(total, 1), $"{Size(stale)} stale of {Size(total)}"), 1);
                Place(grid, Text($"{d.Count()} games{free}", 12, dim: true), 2);
                root.Children.Add(grid);
            }
        }

        private void BuildStaleInstalls(List<GameHistory> games)
        {
            var cutoff = DateTime.Now.AddDays(-StaleDays);
            var stale = games.Where(h => h.Game.IsInstalled && StaleSince(h, cutoff))
                             .OrderByDescending(h => h.Game.InstallSize ?? 0).ToList();
            var unknown = games.Where(h => h.Game.IsInstalled && LastPlayedUnknown(h)).ToList();
            Section($"Stale installs · {stale.Count} · {Size(stale.Sum(h => (double)(h.Game.InstallSize ?? 0)))}", $"Installed but not played in {StaleDays} days: uninstall candidates, biggest first.");
            foreach (var h in stale.Take(25))
            {
                var grid = Row(new GridLength(90), new GridLength(1, GridUnitType.Star), new GridLength(60), new GridLength(200));
                Place(grid, Text(Size(h.Game.InstallSize ?? 0), 12, bold: true), 0);
                Place(grid, LinkBlock(h, h.IsTool ? "tool" : null), 1);
                Place(grid, Text(h.Drive ?? "", 12, dim: true), 2);
                Place(grid, Text(h.LastPlayed.HasValue ? "last played " + h.LastPlayed.Value.ToString("yyyy-MM-dd") : "never played", 12, dim: true), 3);
                root.Children.Add(grid);
            }
            if (unknown.Count > 0)
            {
                root.Children.Add(Text($"Not counted: {unknown.Count} installed games with playtime but no last-played date ({string.Join(", ", unknown.Select(h => h.Game.Source?.Name).Distinct())} doesn't report it): " +
                    string.Join(", ", unknown.Take(6).Select(h => h.Game.Name)) + (unknown.Count > 6 ? "…" : ""), 12, dim: true));
            }
        }

        private void BuildInstalledNeverPlayed(List<GameHistory> games)
        {
            var list = games.Where(h => h.Game.IsInstalled && !h.Played && !h.IsTool).OrderByDescending(h => h.Game.InstallSize ?? 0).ToList();
            Section($"Installed, never played · {list.Count} · {Size(list.Sum(h => (double)(h.Game.InstallSize ?? 0)))}", "Either play it or free the space.");
            foreach (var h in list.Take(15))
            {
                var grid = Row(new GridLength(90), new GridLength(1, GridUnitType.Star), new GridLength(80), new GridLength(160));
                Place(grid, Text(Size(h.Game.InstallSize ?? 0), 12, bold: true), 0);
                Place(grid, LinkBlock(h), 1);
                Place(grid, Text(h.Hltb != null ? $"{h.Hltb.Typical:N0}h" : "", 12, dim: true), 2);
                Place(grid, Text(h.Acquired.HasValue ? "owned since " + h.Acquired.Value.ToString("yyyy") : "", 12, dim: true), 3);
                root.Children.Add(grid);
            }
        }

        // ---------- stats ----------

        /// <summary>
        /// One category breakdown: per value, games owned, % played (bar) and hours. Games count once
        /// per value they carry (a game with two genres counts in both).
        /// </summary>
        private void BuildBreakdown(string title, List<GameHistory> games, Func<GameHistory, IEnumerable<string>> keys, bool sortByKey = false, int top = 12, Func<GameHistory, double> hours = null)
        {
            var rows = games.Where(h => !h.IsTool)
                .SelectMany(h => (keys(h) ?? Enumerable.Empty<string>()).Distinct().Select(k => new { k, h }))
                .GroupBy(x => x.k)
                .Select(g => new { Key = g.Key, Owned = g.Count(), Played = g.Count(x => x.h.Played), Hours = g.Sum(x => (hours ?? (h => h.Hours))(x.h)) })
                .ToList();
            if (rows.Count == 0) return;
            rows = sortByKey ? rows.OrderBy(r => r.Key).ToList() : rows.OrderByDescending(r => r.Hours).ThenByDescending(r => r.Owned).ToList();

            Section(title, "Owned · share played (bar) · hours. Click a row for its games.");
            var maxHours = rows.Max(r => r.Hours);
            Drill drill = null;
            foreach (var r in rows.Take(top))
            {
                var grid = Row(new GridLength(220), new GridLength(70), new GridLength(1, GridUnitType.Star), new GridLength(16), new GridLength(1, GridUnitType.Star));
                var key = r.Key;
                grid.Background = System.Windows.Media.Brushes.Transparent;
                Charts.Hover(grid, () =>
                {
                    if (title == "Genres") Edit(f => { if (!f.Genres.Contains(key)) f.Genres.Add(key); });
                    else if (title == "Stores") Edit(f => f.Stores = new List<string> { key });
                    else ShowDrill(drill, $"{title}: {key}", games.Where(h => !h.IsTool && (keys(h) ?? Enumerable.Empty<string>()).Contains(key)));
                });
                Place(grid, Text(r.Key, 13), 0);
                Place(grid, Text(r.Owned.ToString("N0"), 12, dim: true), 1);
                Place(grid, Bar(r.Played, Math.Max(r.Owned, 1), $"{Pct(r.Played, r.Owned)} played"), 2);
                Place(grid, Bar(r.Hours, Math.Max(maxHours, 1), $"{r.Hours:N0}h"), 4);
                root.Children.Add(grid);
            }
            if (rows.Count > top) root.Children.Add(Text($"+{rows.Count - top} more", 12, dim: true));
            drill = NewDrill();
        }

        private static string NormalizeMethod(string m)
        {
            switch ((m ?? "").Trim())
            {
                case "Complimentary": case "Free": return "Free";
                case "Steam Store": case "Purchase": return "Bought";
                case "Retail": return "Key / retail";
                default: return m;
            }
        }

        // ---------- timeline ----------

        private void BuildTimeline(List<GameHistory> games)
        {
            Section("Timeline", "Per year: games you started (first Steam achievement) vs games you got (Steam licenses, Epic orders).");
            var dated = games.Where(h => h.FirstPlayed.HasValue && !h.IsTool).ToList();
            var started = dated.GroupBy(h => h.FirstPlayed.Value.Year).ToDictionary(g => g.Key, g => g.ToList());
            var got = games.Where(h => h.Acquired.HasValue && !h.IsTool).GroupBy(h => h.Acquired.Value.Year).ToDictionary(g => g.Key, g => g.Count());
            var years = started.Keys.Union(got.Keys).OrderByDescending(y => y).ToList();
            if (years.Count == 0) return;
            var max = Math.Max(started.Values.Select(v => v.Count).DefaultIfEmpty(0).Max(), got.Values.DefaultIfEmpty(0).Max());
            foreach (var year in years)
            {
                var list = started.TryGetValue(year, out var l) ? l : new List<GameHistory>();
                var n = got.TryGetValue(year, out var b) ? b : 0;
                var top = list.OrderByDescending(h => h.Hours).Take(6).ToList();
                var grid = Row(new GridLength(60), new GridLength(170), new GridLength(1, GridUnitType.Star));
                Place(grid, Text(year.ToString(), 16, bold: true), 0);
                var bars = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                bars.Children.Add(Bar(list.Count, max, $"{list.Count} started"));
                var gotBar = Bar(n, max, $"{n} got");
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

        private void BuildComfortAndForgotten(List<GameHistory> games)
        {
            var real = games.Where(h => !h.IsTool && h.Hours >= 30).ToList();
            var comfort = real.Where(h => (h.LastPlayed ?? DateTime.MinValue) > DateTime.Now.AddDays(-90) && h.FirstPlayed.HasValue && h.FirstPlayed < DateTime.Now.AddYears(-1))
                              .OrderByDescending(h => h.Hours).Take(8).ToList();
            var forgotten = real.Where(h => h.LastPlayed.HasValue && h.LastPlayed < DateTime.Now.AddYears(-1))
                                .OrderByDescending(h => h.Hours).Take(8).ToList();
            if (comfort.Count > 0)
            {
                Section("Comfort games", "30h+, started over a year ago, still played in the last 90 days.");
                foreach (var h in comfort) root.Children.Add(GameLine(h, Since(h)));
            }
            if (forgotten.Count > 0)
            {
                Section("Forgotten favourites", "30h+ but untouched for a year. Worth a revisit?");
                foreach (var h in forgotten) root.Children.Add(GameLine(h, Since(h)));
            }
        }

        private void BuildMostPlayed(List<GameHistory> games)
        {
            Section("Most played", "All-time playtime as reported by each store.");
            var top = games.Where(h => h.Game.Playtime > 0 && !h.IsTool).OrderByDescending(h => h.Game.Playtime).Take(15).ToList();
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

        // ---------- mods + data ----------

        private void BuildMods(List<GameMods> mods)
        {
            mods = mods ?? new List<GameMods>();
            var total = mods.Sum(m => m.Mods.Count);
            Section($"Mods · {total:N0} across {mods.Count} games", "Installed via Steam Workshop, Vortex and r2modman. Read-only. Click a game for its list.");
            if (mods.Count == 0)
            {
                root.Children.Add(Text("No mods found.", 12, dim: true));
                return;
            }
            var max = mods.Max(m => m.Mods.Count);
            foreach (var gm in mods.Take(30))
            {
                var grid = Row(new GridLength(280), new GridLength(1, GridUnitType.Star), new GridLength(260));
                var name = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                var link = new Hyperlink(new Run(gm.Game?.Name ?? gm.Label)) { TextDecorations = null, Cursor = Cursors.Hand };
                link.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
                var captured = gm;
                link.Click += (s, e) => ModsView.Show(api, captured, plugin.GetPluginUserDataPath());
                name.Inlines.Add(link);
                Place(grid, name, 0);
                var sources = string.Join(" · ", gm.Mods.GroupBy(m => m.Source).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}"));
                Place(grid, Bar(gm.Mods.Count, max, sources), 1);
                var meta = (gm.TotalSize > 0 ? Size(gm.TotalSize) : "") + (gm.LastUpdated.HasValue ? $" · updated {gm.LastUpdated:yyyy-MM-dd}" : "");
                Place(grid, Text(meta.Trim(' ', '·'), 12, dim: true), 2);
                root.Children.Add(grid);
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
            buttons.Children.Add(ActionButton("Import Epic acquisition dates", () => { plugin.EpicAcquisitions.Run(); Refresh(); }));
            buttons.Children.Add(ActionButton("Fetch HowLongToBeat times", () => { plugin.FillHltb(); Refresh(); }));
            buttons.Children.Add(ActionButton("Write snapshot now", () => { plugin.Snapshots.TryWriteToday(force: true); Refresh(); }));
            buttons.Children.Add(ActionButton("Open data folder", () => System.Diagnostics.Process.Start(plugin.GetPluginUserDataPath())));
            root.Children.Add(buttons);
        }

        // ---------- building blocks ----------

        private void Section(string title, string subtitle)
        {
            var sep = new Rectangle { Height = 1, Margin = new Thickness(0, root.Children.Count == 0 ? 0 : 22, 0, 10) };
            sep.SetResourceReference(Shape.FillProperty, "PanelSeparatorBrush");
            if (root.Children.Count > 0) root.Children.Add(sep);
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
            var border = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 0, 10, 10), MinWidth = 130 };
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
            var track = new Border { CornerRadius = new CornerRadius(3), Opacity = 0.12 };
            track.SetResourceReference(Border.BackgroundProperty, "GlyphBrush");
            Grid.SetColumnSpan(track, 2);
            grid.Children.Add(track);
            var fill = new Border { CornerRadius = new CornerRadius(3), Opacity = 0.55 };
            fill.SetResourceReference(Border.BackgroundProperty, "GlyphBrush");
            grid.Children.Add(fill);
            var text = Text(label, 11);
            text.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumnSpan(text, 2);
            grid.Children.Add(text);
            return grid;
        }

        private FrameworkElement GameLine(GameHistory h, string meta)
        {
            var grid = Row(new GridLength(300), new GridLength(80), new GridLength(1, GridUnitType.Star));
            Place(grid, LinkBlock(h), 0);
            Place(grid, Text($"{h.Hours:N0}h", 12, bold: true), 1);
            Place(grid, Text(meta, 12, dim: true), 2);
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
            link.Click += (s, e) => OpenDrawer(h.Game.Id);
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
            if (h.Hltb != null) lines.Add($"To beat      {h.Hltb.MainHours:0.#}h main · {h.Hltb.ExtraHours:0.#}h extra · {h.Hltb.CompletionistHours:0.#}h 100%");
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
            var b = new Button { Content = label, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 10, 8) };
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

        private static TextBlock Text(string s, double size, bool bold = false, bool dim = false) => Ui.Text(s, size, bold, dim);

        private static string Pct(int n, int of) => of == 0 ? "–" : (100.0 * n / of).ToString("0", CultureInfo.InvariantCulture) + "%";

        private static string Size(double bytes) => Ui.Size(bytes);
    }
}
