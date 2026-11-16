using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Ledger
{
    /// <summary>One row in the Explorer table. Plain properties so DataGrid sorting just works.</summary>
    public class ExplorerRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Store { get; set; }
        public double Hours { get; set; }
        public DateTime? LastPlayed { get; set; }
        public DateTime? FirstPlayed { get; set; }
        public DateTime? Acquired { get; set; }
        public string HowGot { get; set; }
        public decimal? Paid { get; set; }
        public decimal? PerHour { get; set; }
        public double? Hltb { get; set; }
        public double? Progress { get; set; }      // hours / HLTB main, %
        public double? SizeGB { get; set; }
        public bool Installed { get; set; }
        public int? Rating { get; set; }
        public int? Year { get; set; }
        public string Genres { get; set; }
    }

    internal partial class LedgerView
    {
        private readonly Border drawer = new Border { Width = 0 };

        private void BuildExplorer(List<GameHistory> games, Data d)
        {
            var rows = games.Where(h => !h.IsTool).Select(h =>
            {
                d.Money.Games.TryGetValue(h.Game.Id, out var m);
                return new ExplorerRow
                {
                    Id = h.Game.Id,
                    Name = h.Game.Name,
                    Store = h.Origin,
                    Hours = Math.Round(h.Hours, 1),
                    LastPlayed = h.LastPlayed,
                    FirstPlayed = h.FirstPlayed,
                    Acquired = h.Acquired,
                    HowGot = h.AcquiredVia == null ? null : NormalizeMethod(h.AcquiredVia),
                    Paid = m != null && m.Total > 0 ? Math.Round(m.Total, 2) : (decimal?)null,
                    PerHour = m?.PerHour.HasValue == true ? Math.Round(m.PerHour.Value, 2) : (decimal?)null,
                    Hltb = h.Hltb != null ? Math.Round(h.Hltb.Typical, 1) : (double?)null,
                    Progress = h.Hltb != null && h.Hltb.MainHours > 0 ? Math.Round(Math.Min(999, 100 * h.Hours / h.Hltb.MainHours)) : (double?)null,
                    SizeGB = h.Game.InstallSize.HasValue && h.Game.IsInstalled ? Math.Round(h.Game.InstallSize.Value / 1073741824.0, 1) : (double?)null,
                    Installed = h.Game.IsInstalled,
                    Rating = h.Game.UserScore.HasValue ? h.Game.UserScore / 10 : (h.Game.CommunityScore ?? h.Game.CriticScore),
                    Year = h.Game.ReleaseYear,
                    Genres = string.Join(", ", h.Game.Genres?.Select(x => x.Name).Take(3) ?? Enumerable.Empty<string>())
                };
            }).OrderByDescending(r => r.Hours).ToList();

            root.Children.Add(Text($"{rows.Count:N0} games. Click a column header to sort, a row to open its details. Filters above apply.", 12, dim: true));
            var grid = new DataGrid
            {
                ItemsSource = rows,
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserSortColumns = true,
                CanUserResizeColumns = true,
                SelectionMode = DataGridSelectionMode.Single,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.None,
                RowHeight = 26,
                Height = 640,
                Margin = new Thickness(0, 8, 0, 0),
                EnableRowVirtualization = true
            };
            grid.SetResourceReference(Control.BackgroundProperty, "ControlBackgroundBrush");
            grid.SetResourceReference(Control.ForegroundProperty, "TextBrush");
            grid.RowBackground = Brushes.Transparent;
            grid.AlternatingRowBackground = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255));

            void Col(string header, string path, string fmt = null, double width = 0, bool right = false)
            {
                var c = new DataGridTextColumn { Header = header, Binding = new Binding(path) { StringFormat = fmt, ConverterCulture = CultureInfo.CurrentCulture }, SortMemberPath = path };
                if (width > 0) c.Width = width; else { c.Width = new DataGridLength(1, DataGridLengthUnitType.Star); c.MinWidth = 160; }
                if (right) c.ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right) } };
                grid.Columns.Add(c);
            }
            var sym = d.Money.Symbol;
            Col("Game", nameof(ExplorerRow.Name), width: 260);
            Col("Store", nameof(ExplorerRow.Store), width: 110);
            Col("Hours", nameof(ExplorerRow.Hours), "N1", 70, true);
            Col("HLTB", nameof(ExplorerRow.Hltb), "0.#'h'", 60, true);
            Col("Done %", nameof(ExplorerRow.Progress), "0'%'", 65, true);
            Col("Last played", nameof(ExplorerRow.LastPlayed), "yyyy-MM-dd", 95);
            Col("Got", nameof(ExplorerRow.Acquired), "yyyy-MM-dd", 95);
            Col("How", nameof(ExplorerRow.HowGot), width: 80);
            Col("Paid", nameof(ExplorerRow.Paid), sym + "0.00", 70, true);
            Col(sym + "/h", nameof(ExplorerRow.PerHour), sym + "0.00", 60, true);
            Col("GB", nameof(ExplorerRow.SizeGB), "0.0", 55, true);
            Col("Year", nameof(ExplorerRow.Year), null, 50, true);
            Col("★", nameof(ExplorerRow.Rating), null, 40, true);
            Col("Genres", nameof(ExplorerRow.Genres));

            grid.SelectionChanged += (s, e) =>
            {
                if (grid.SelectedItem is ExplorerRow r) OpenDrawer(r.Id);
            };
            root.Children.Add(grid);
        }

        // ---------------- detail drawer ----------------

        private void CloseDrawer()
        {
            drawer.Child = null;
            drawer.Width = 0;
        }

        /// <summary>Right-side panel with everything Ledger knows about one game.</summary>
        internal void OpenDrawer(Guid id)
        {
            var d = last;
            var h = d?.Index.For(id);
            if (h == null) return;
            var g = h.Game;
            var sym = d.Money.Symbol;
            d.Money.Games.TryGetValue(id, out var money);

            drawer.Width = 430;
            drawer.SetResourceReference(Border.BackgroundProperty, "PopupBackgroundBrush");
            drawer.SetResourceReference(Border.BorderBrushProperty, "PanelSeparatorBrush");
            drawer.BorderThickness = new Thickness(1, 0, 0, 0);
            var sp = new StackPanel { Margin = new Thickness(20, 16, 20, 30) };
            drawer.Child = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            var head = new DockPanel { LastChildFill = false };
            var close = Btn("✕", CloseDrawer, tip: "Close");
            DockPanel.SetDock(close, Dock.Right);
            head.Children.Add(close);
            sp.Children.Add(head);

            // Cover + title + actions
            var top = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 10) };
            var cover = new Border { Width = 120, Height = 180, CornerRadius = new CornerRadius(6), ClipToBounds = true, Margin = new Thickness(0, 0, 14, 0) };
            cover.SetResourceReference(Border.BackgroundProperty, "ControlBackgroundBrush");
            var img = Charts.LoadImage(Cover(h), 240);
            if (img != null) cover.Child = new Image { Source = img, Stretch = Stretch.UniformToFill };
            top.Children.Add(cover);
            var info = new StackPanel { Width = 250 };
            var title = Text(g.Name, 20, bold: true); title.TextWrapping = TextWrapping.Wrap;
            info.Children.Add(title);
            info.Children.Add(Text(string.Join(" · ", new[] { g.Source?.Name, g.ReleaseYear?.ToString(), g.Developers?.FirstOrDefault()?.Name }.Where(x => x != null)), 12, dim: true));
            info.Children.Add(Text(string.Join(", ", g.Genres?.Select(x => x.Name).Take(4) ?? Enumerable.Empty<string>()), 12, dim: true));
            var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            if (g.IsInstalled) actions.Children.Add(Btn("▶ Play", () => api.StartGame(id)));
            else actions.Children.Add(Btn("⬇ Install", () => api.InstallGame(id)));
            actions.Children.Add(Btn("Show in library", () => { api.MainView.SwitchToLibraryView(); api.MainView.SelectGame(id); }));
            var listBtn = Btn("＋ List", () => { });
            listBtn.ContextMenu = new ContextMenu();
            listBtn.ContextMenu.Items.Add(AddToListMenu(() => new ListItem { GameId = id, Name = g.Name }));
            listBtn.Click += (s, e) => { listBtn.ContextMenu.PlacementTarget = listBtn; listBtn.ContextMenu.IsOpen = true; };
            actions.Children.Add(listBtn);
            var inLists = Lists.Load().Where(l => l.Items.Any(i => i.GameId == id)).Select(l => l.Icon + " " + l.Name).ToList();
            info.Children.Add(actions);
            if (inLists.Count > 0) info.Children.Add(Text("In: " + string.Join(", ", inLists), 11, dim: true));
            top.Children.Add(info);
            sp.Children.Add(top);

            // Key numbers
            var facts = new WrapPanel();
            void Fact(string v, string l) { var c = Card(v, l); ((FrameworkElement)c).MinWidth = 120; facts.Children.Add(c); }
            Fact($"{h.Hours:N1}h", "played");
            if (h.Hltb != null) Fact($"{h.Hltb.MainHours:0.#}h", "to beat (main)");
            if (money != null && money.Total > 0) Fact(M(money.Total, sym), money.InGame > 0 ? $"incl. {M(money.InGame, sym)} in-game" : "paid");
            if (money?.PerHour != null) Fact(M(money.PerHour.Value, sym), "per hour");
            if (money?.ListPrice > 0) Fact(M(money.ListPrice.Value, sym), "full price today");
            if (g.IsInstalled && g.InstallSize > 0) Fact(Size(g.InstallSize.Value), "on " + (h.Drive ?? "disk"));
            sp.Children.Add(facts);

            // HLTB progress
            if (h.Hltb != null && h.Hltb.MainHours > 0)
            {
                sp.Children.Add(SubHead("Progress vs HowLongToBeat"));
                foreach (var (label, target) in new[] { ("Main story", h.Hltb.MainHours), ("Main + extras", h.Hltb.ExtraHours), ("Completionist", h.Hltb.CompletionistHours) })
                {
                    if (target <= 0) continue;
                    var row = Row(new GridLength(110), new GridLength(1, GridUnitType.Star));
                    Place(row, Text(label, 12, dim: true), 0);
                    Place(row, Bar(Math.Min(h.Hours, target), target, $"{Math.Min(999, 100 * h.Hours / target):0}% of {target:0.#}h"), 1);
                    sp.Children.Add(row);
                }
            }

            // Timeline: everything with a date, newest first
            var events = new List<KeyValuePair<DateTime, string>>();
            void E(DateTime? at, string what) { if (at.HasValue) events.Add(new KeyValuePair<DateTime, string>(at.Value, what)); }
            E(h.Acquired, "Got it" + (h.AcquiredVia != null ? $" ({NormalizeMethod(h.AcquiredVia)})" : ""));
            E(h.FirstPlayed, $"First played (first of {h.Unlocks} achievements)");
            E(h.LastPlayed, "Last played");
            foreach (var ev in Tracking.LoadEvents(plugin.GetPluginUserDataPath()).Where(x => x.GameId == id))
                E(ev.At, ev.Type == "status" ? $"{ev.From ?? "?"} → {ev.To}" : ev.Type == "rated" ? $"Rated {ev.Rating}/10 {ev.Note}" : char.ToUpper(ev.Type[0]) + ev.Type.Substring(1));
            var sessions = ActivityTracker.Load(plugin.GetPluginUserDataPath()).Where(x => x.GameId == id).OrderByDescending(x => x.Start).ToList();
            foreach (var s in sessions.Take(8)) E(s.Start, $"Played {s.Seconds / 3600:0.#}h");
            if (money != null)
                foreach (var t in d.Money.Transactions.Where(t => !t.IsWalletTopUp && t.Items.Any(i => LicenseParsing.Norm(i).StartsWith(LicenseParsing.Norm(g.Name)))).OrderByDescending(t => t.Date).Take(8))
                    E(t.Date, $"{(t.IsRefund ? "Refund" : t.IsInGame ? "In-game purchase" : "Purchase")} {M(t.Total, sym)}: {string.Join(", ", t.Items.Take(2))}");
            if (events.Count > 0)
            {
                sp.Children.Add(SubHead("Timeline"));
                foreach (var e in events.OrderByDescending(x => x.Key))
                {
                    var row = Row(new GridLength(90), new GridLength(1, GridUnitType.Star));
                    Place(row, Text(e.Key.ToString("d MMM yyyy"), 11, dim: true), 0);
                    Place(row, Text(e.Value, 12), 1);
                    sp.Children.Add(row);
                }
            }

            // Mods
            var gm = d.Mods.FirstOrDefault(m => m.Game?.Id == id);
            if (gm != null)
            {
                sp.Children.Add(SubHead("Mods"));
                sp.Children.Add(Text(string.Join(" · ", gm.Mods.GroupBy(m => m.Source).Select(x => $"{x.Count()} {x.Key}")) + (gm.LastUpdated.HasValue ? $" · updated {gm.LastUpdated:d MMM yyyy}" : ""), 12));
                sp.Children.Add(Btn("Open mod list", () => ModsView.Show(api, gm, plugin.GetPluginUserDataPath())));
            }

            if (!string.IsNullOrWhiteSpace(g.Notes))
            {
                sp.Children.Add(SubHead("Notes"));
                var n = Text(g.Notes, 12); n.TextWrapping = TextWrapping.Wrap;
                sp.Children.Add(n);
            }
        }

        private TextBlock SubHead(string s)
        {
            var t = Text(s, 14, bold: true);
            t.Margin = new Thickness(0, 16, 0, 6);
            return t;
        }
    }
}
