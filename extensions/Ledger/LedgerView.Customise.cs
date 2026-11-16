using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Ledger
{
    internal partial class LedgerView
    {
        /// <summary>
        /// Every tab as an ordered list of named sections. Customise hides/reorders tabs and hides
        /// sections by these names ("Overview/Insights"), so the names are part of the settings format.
        /// </summary>
        private Dictionary<string, List<KeyValuePair<string, Action>>> Spec(List<GameHistory> g, Data d)
        {
            KeyValuePair<string, Action> S(string name, Action a) => new KeyValuePair<string, Action>(name, a);
            List<Suggestion> up() => d.UpNext.Where(x => g.Contains(x.H)).ToList();
            return new Dictionary<string, List<KeyValuePair<string, Action>>>
            {
                ["Overview"] = new List<KeyValuePair<string, Action>> {
                    S("Cards", () => BuildCards(g, d)), S("Goals", () => BuildGoals(d)), S("At a glance", () => { if (period.IsAll) BuildOverviewCharts(g); }),
                    S("Insights", () => BuildInsights(g, d)), S("On this day", () => BuildOnThisDay(g)),
                    S("Play next list", () => BuildPlayNextStrip(d)), S("Up next", () => BuildUpNextTiles(up(), 8)), S("Recent activity", BuildRecent) },
                ["Discover"] = new List<KeyValuePair<string, Action>> { S("Discover", () => BuildDiscover(d)) },
                ["Lists"] = new List<KeyValuePair<string, Action>> { S("Lists", () => BuildLists(d)) },
                ["Explorer"] = new List<KeyValuePair<string, Action>> { S("Table", () => BuildExplorer(g, d)) },
                ["Backlog"] = new List<KeyValuePair<string, Action>> {
                    S("Shelves", () => BuildShelves(g)), S("Quick wins", () => BuildQuickWinTiles(g)),
                    S("Up next", () => BuildUpNextTiles(up(), 16)), S("Oldest unplayed", () => BuildOldestUnplayed(g)) },
                ["Installs"] = new List<KeyValuePair<string, Action>> {
                    S("Drives", () => BuildDrives(g)), S("Free space over time", BuildDiskTrend),
                    S("Stale installs", () => BuildStaleInstalls(g)), S("Installed, never played", () => BuildInstalledNeverPlayed(g)) },
                ["Stats"] = new List<KeyValuePair<string, Action>> { S("Stats", () => BuildStats(g, d)) },
                ["Money"] = new List<KeyValuePair<string, Action>> { S("Money", () => BuildMoney(d, g)) },
                ["Wishlist"] = new List<KeyValuePair<string, Action>> { S("Wishlist", () => BuildWishlist(d)) },
                ["Timeline"] = new List<KeyValuePair<string, Action>> {
                    S("Play calendar", () => BuildHeatmap(d, g)), S("Got vs started", () => BuildTimelineColumns(g)),
                    S("Comfort & forgotten", () => BuildComfortTiles(g)), S("Year by year", () => BuildTimeline(g)), S("Most played", () => BuildMostPlayed(g)) },
                ["Mods"] = new List<KeyValuePair<string, Action>> { S("Mods", () => { BuildMods(d.Mods); BuildFooter(); }) },
            };
        }

        /// <summary>Settings order first, then any tabs added since, minus hidden ones.</summary>
        private List<string> OrderedTabs()
        {
            var order = (plugin.Settings.TabOrder ?? new List<string>()).Where(Tabs.Contains).ToList();
            order.AddRange(Tabs.Where(t => !order.Contains(t)));
            var hidden = new HashSet<string>(plugin.Settings.HiddenTabs ?? new List<string>());
            var visible = order.Where(t => !hidden.Contains(t)).ToList();
            return visible.Count > 0 ? visible : new List<string> { "Overview" };
        }

        /// <summary>The top of your Play next list, right on the Overview.</summary>
        private void BuildPlayNextStrip(Data d)
        {
            var list = Lists.Load().FirstOrDefault(l => l.Name == "Play next");
            if (list == null || list.Items.Count == 0) return;
            Section("▶ Play next", "Your queue. Reorder, add notes or remove in the Lists tab.");
            var tiles = list.Items.Take(6).Select((it, i) =>
            {
                var h = it.GameId.HasValue ? d.Index.For(it.GameId.Value) : null;
                return h != null ? TileFor(h, $"{i + 1}. " + (h.Hltb != null ? $"{h.Hltb.Typical:N0}h to beat" : h.Game.IsInstalled ? "installed" : h.Game.Source?.Name), it.Note) : null;
            }).Where(t => t != null);
            root.Children.Add(Charts.Tiles(tiles, 110));
        }

        // ---------------- the Customise window ----------------

        private void OpenCustomise()
        {
            var s = plugin.Settings;
            var order = (s.TabOrder ?? new List<string>()).Where(Tabs.Contains).ToList();
            order.AddRange(Tabs.Where(t => !order.Contains(t)));
            var hiddenTabs = new HashSet<string>(s.HiddenTabs ?? new List<string>());
            var hiddenSections = new HashSet<string>(s.HiddenSections ?? new List<string>());
            var spec = Spec(new List<GameHistory>(), last); // names only; actions never run here

            var window = api.Dialogs.CreateWindow(new Playnite.SDK.WindowCreationOptions { ShowMinimizeButton = false, ShowMaximizeButton = false });
            window.Title = "Customise Ledger";
            window.Width = 560;
            window.Height = 720;
            window.Owner = api.Dialogs.GetCurrentAppWindow();
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var panel = new StackPanel { Margin = new Thickness(20) };
            void Render()
            {
                panel.Children.Clear();
                panel.Children.Add(Text("Tabs and sections", 18, bold: true));
                panel.Children.Add(Text("Untick to hide. Arrows reorder tabs. Changes apply when you close this window.", 12, dim: true));
                for (var i = 0; i < order.Count; i++)
                {
                    var tab = order[i];
                    var idx = i;
                    var row = new DockPanel { Margin = new Thickness(0, 12, 0, 2), LastChildFill = false };
                    var cb = new CheckBox { Content = tab, IsChecked = !hiddenTabs.Contains(tab), FontSize = 15, FontWeight = FontWeights.SemiBold };
                    cb.Click += (o, e) => { if (cb.IsChecked == true) hiddenTabs.Remove(tab); else hiddenTabs.Add(tab); Render(); };
                    DockPanel.SetDock(cb, Dock.Left);
                    row.Children.Add(cb);
                    var arrows = new StackPanel { Orientation = Orientation.Horizontal };
                    var upB = new Button { Content = "↑", Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 4, 0), IsEnabled = idx > 0 };
                    upB.Click += (o, e) => { order.RemoveAt(idx); order.Insert(idx - 1, tab); Render(); };
                    var dnB = new Button { Content = "↓", Padding = new Thickness(8, 0, 8, 0), IsEnabled = idx < order.Count - 1 };
                    dnB.Click += (o, e) => { order.RemoveAt(idx); order.Insert(idx + 1, tab); Render(); };
                    arrows.Children.Add(upB); arrows.Children.Add(dnB);
                    DockPanel.SetDock(arrows, Dock.Right);
                    row.Children.Add(arrows);
                    panel.Children.Add(row);

                    if (!hiddenTabs.Contains(tab) && spec.TryGetValue(tab, out var secs) && secs.Count > 1)
                    {
                        var wrap = new WrapPanel { Margin = new Thickness(26, 2, 0, 0) };
                        foreach (var sec in secs)
                        {
                            var key = tab + "/" + sec.Key;
                            var sc = new CheckBox { Content = sec.Key, IsChecked = !hiddenSections.Contains(key), Margin = new Thickness(0, 2, 14, 2) };
                            sc.Click += (o, e) => { if (sc.IsChecked == true) hiddenSections.Remove(key); else hiddenSections.Add(key); };
                            wrap.Children.Add(sc);
                        }
                        panel.Children.Add(wrap);
                    }
                }
                var reset = new Button { Content = "Reset to default", Margin = new Thickness(0, 20, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 4, 12, 4) };
                reset.Click += (o, e) => { order = Tabs.ToList(); hiddenTabs.Clear(); hiddenSections.Clear(); Render(); };
                panel.Children.Add(reset);
            }
            Render();
            window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            window.ShowDialog();

            s.TabOrder = order;
            s.HiddenTabs = hiddenTabs.ToList();
            s.HiddenSections = hiddenSections.ToList();
            plugin.SaveSettings();
            SafeShow(last);
        }
    }
}
