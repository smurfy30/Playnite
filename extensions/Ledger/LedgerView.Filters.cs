using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Ledger
{
    /// <summary>The time window the page is looking at. All time = no window.</summary>
    internal class Period
    {
        public string Name { get; set; }
        public DateTime From { get; set; } = DateTime.MinValue;
        public DateTime To { get; set; } = DateTime.MaxValue;
        public bool IsAll => From == DateTime.MinValue;
        public bool Contains(DateTime? d) => d.HasValue && d.Value >= From && d.Value < To;

        public static List<Period> Options(IEnumerable<int> years)
        {
            var now = DateTime.Now;
            var list = new List<Period>
            {
                new Period { Name = "All time" },
                new Period { Name = "Last 30 days", From = now.Date.AddDays(-30), To = now.Date.AddDays(1) },
                new Period { Name = "Last 12 months", From = now.Date.AddYears(-1), To = now.Date.AddDays(1) },
                new Period { Name = "This year", From = new DateTime(now.Year, 1, 1), To = new DateTime(now.Year + 1, 1, 1) },
            };
            foreach (var y in years.Where(y => y < now.Year).OrderByDescending(y => y))
                list.Add(new Period { Name = y.ToString(), From = new DateTime(y, 1, 1), To = new DateTime(y + 1, 1, 1) });
            return list;
        }
    }

    internal partial class LedgerView
    {
        private FilterState filter = new FilterState();
        private Period period = new Period { Name = "All time" };
        private Data last;
        private bool filtersOpen;

        private int StaleDays => Math.Max(7, plugin.Settings.StaleDays);

        private bool IsPaid(GameHistory h) =>
            (last?.Money != null && last.Money.Games.TryGetValue(h.Game.Id, out var m) && m.Paid > 0) ||
            (h.AcquiredVia != null && NormalizeMethod(h.AcquiredVia) == "Bought");

        private List<GameHistory> Scope(List<GameHistory> games) => games.Where(h => filter.Matches(h, IsPaid)).ToList();

        private bool PlayedIn(GameHistory h, Data d) =>
            period.IsAll ? h.Played : d.Activity.HoursIn(h.Game.Id, period.From, period.To) > 0 || period.Contains(h.LastPlayed) || period.Contains(h.FirstPlayed);

        private double HoursOf(GameHistory h, Data d) => period.IsAll ? h.Hours : d.Activity.HoursIn(h.Game.Id, period.From, period.To);

        private string TrackingCaveat(Data d)
        {
            if (period.IsAll) return null;
            var since = d.Activity.TrackedSince;
            if (since == null) return "No play tracking yet. Hours for a period fill in as Ledger records sessions.";
            return since > period.From ? $"Hours tracked since {since:d MMM yyyy}; earlier play in this period isn't counted." : null;
        }

        /// <summary>Change the filter (from any control, chip or chart click) and re-render.</summary>
        private void Edit(Action<FilterState> change)
        {
            var next = filter.Clone();
            change(next);
            filter = next;
            plugin.Settings.Filter = filter;
            plugin.SaveSettings();
            Dispatcher.BeginInvoke((Action)(() => SafeShow(last)));
        }

        private List<SavedView> Views() => BuiltInViews.All.Concat(plugin.Settings.SavedViews ?? new List<SavedView>()).ToList();

        // ---------------- header: title, views, filters toggle, chips ----------------

        private FrameworkElement BuildTopBar(Data d)
        {
            var top = new StackPanel { Margin = new Thickness(30, 18, 30, 0) };

            var row = new DockPanel { LastChildFill = true };
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(Text("Ledger", 26, bold: true));
            DockPanel.SetDock(title, Dock.Left);
            row.Children.Add(title);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var views = Views();
            var viewBox = new ComboBox { Width = 230, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Saved views" };
            viewBox.Items.Add("Views…");
            foreach (var v in views) viewBox.Items.Add(v.Name);
            viewBox.SelectedIndex = 0;
            viewBox.SelectionChanged += (s, e) =>
            {
                if (viewBox.SelectedIndex <= 0) return;
                var v = views[viewBox.SelectedIndex - 1];
                filter = v.Filter.Clone();
                filter.IncludeFamilySharing = plugin.Settings.CurrentFilter().IncludeFamilySharing;
                plugin.Settings.Filter = filter;
                if (!string.IsNullOrEmpty(v.Tab)) selectedTabName = v.Tab;
                plugin.SaveSettings();
                Dispatcher.BeginInvoke((Action)(() => SafeShow(last)));
            };
            actions.Children.Add(viewBox);
            actions.Children.Add(Btn("Save view", SaveView, enabled: !filter.IsEmpty));
            actions.Children.Add(Btn(filtersOpen ? "Filters ▴" : "Filters ▾", () => { filtersOpen = !filtersOpen; SafeShow(last); }, accent: filtersOpen));
            actions.Children.Add(Btn("Reset", () => Edit(f => { var fam = f.IncludeFamilySharing; ResetTo(f); f.IncludeFamilySharing = fam; }), enabled: !filter.IsEmpty));
            actions.Children.Add(Btn("⚙", OpenCustomise, tip: "Customise tabs and sections"));
            actions.Children.Add(Btn("⟳", () => Refresh(), tip: "Reload everything"));
            DockPanel.SetDock(actions, Dock.Right);
            row.Children.Insert(0, actions);
            top.Children.Add(row);

            // Chips: what's applied, each removable. Plus a live count.
            var chips = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            var shown = Scope(d.Visible).Count(h => !h.IsTool);
            var total = d.Visible.Count(h => !h.IsTool && (filter.IncludeFamilySharing || h.Game.Source?.Name != "Steam Family Sharing"));
            var count = Text(filter.IsEmpty ? $"All {total:N0} games" : $"{shown:N0} of {total:N0} games", 13, bold: true);
            count.Margin = new Thickness(0, 0, 12, 6);
            chips.Children.Add(count);
            foreach (var c in filter.Chips())
            {
                var remove = c.Value;
                chips.Children.Add(Chip(c.Key, () => Edit(remove)));
            }
            if (filter.IsEmpty) chips.Children.Add(Text("Tip: click any chart, slice or row to filter by it.", 12, dim: true));
            top.Children.Add(chips);

            if (filtersOpen) top.Children.Add(BuildFilterPanel(d));
            return top;
        }

        private static void ResetTo(FilterState f)
        {
            f.Period = "All time"; f.Stores.Clear(); f.Genres.Clear(); f.Installed = null; f.Played = null; f.Search = null;
            f.MinHours = f.MaxHours = f.MaxHltb = null; f.MinYear = f.MaxYear = null; f.Paid = null;
        }

        private void SaveView()
        {
            var r = api.Dialogs.SelectString("Name this view:", "Ledger · Save view", string.Join(", ", filter.Chips().Select(c => c.Key)));
            if (!r.Result || string.IsNullOrWhiteSpace(r.SelectedString)) return;
            var list = plugin.Settings.SavedViews ?? new List<SavedView>();
            list.RemoveAll(v => v.Name == r.SelectedString.Trim());
            list.Add(new SavedView { Name = r.SelectedString.Trim(), Tab = selectedTabName, Filter = filter.Clone() });
            plugin.Settings.SavedViews = list;
            plugin.SaveSettings();
            SafeShow(last);
        }

        // ---------------- the filter panel ----------------

        private FrameworkElement BuildFilterPanel(Data d)
        {
            var panel = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 4, 0, 4) };
            panel.SetResourceReference(Border.BackgroundProperty, "ControlBackgroundBrush");
            var grid = new Grid();
            for (var i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panel.Child = grid;

            StackPanel Col(int i, string heading)
            {
                var sp = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
                Grid.SetColumn(sp, i);
                grid.Children.Add(sp);
                var h = Text(heading, 12, bold: true); h.Margin = new Thickness(0, 0, 0, 6);
                sp.Children.Add(h);
                return sp;
            }

            // Column 1: when + state
            var c1 = Col(0, "When");
            var years = d.Index.Games.SelectMany(h => new[] { h.Acquired?.Year, h.FirstPlayed?.Year, h.LastPlayed?.Year }).Where(y => y.HasValue).Select(y => y.Value).Distinct();
            c1.Children.Add(Combo(Period.Options(years).Select(p => p.Name).ToList(), filter.Period, v => Edit(f => f.Period = v)));
            c1.Children.Add(Label("Installed"));
            c1.Children.Add(Segmented(new[] { "Any", "Installed", "Not installed" }, filter.Installed ?? "Any", v => Edit(f => f.Installed = v == "Any" ? null : v)));
            c1.Children.Add(Label("Played"));
            c1.Children.Add(Segmented(new[] { "Any", "Played", "Unplayed" }, filter.Played ?? "Any", v => Edit(f => f.Played = v == "Any" ? null : v)));
            c1.Children.Add(Label("Paid"));
            c1.Children.Add(Segmented(new[] { "Any", "Paid for", "Free / key" }, filter.Paid == null ? "Any" : filter.Paid.Value ? "Paid for" : "Free / key",
                v => Edit(f => f.Paid = v == "Any" ? (bool?)null : v == "Paid for")));

            // Column 2: stores
            var c2 = Col(1, "Stores");
            foreach (var s in d.Visible.Where(h => filter.IncludeFamilySharing || h.Game.Source?.Name != "Steam Family Sharing")
                                        .GroupBy(h => h.Game.Source?.Name ?? "(none)").OrderByDescending(g => g.Count()))
            {
                var name = s.Key;
                c2.Children.Add(Check($"{name}  ({s.Count()})", filter.Stores.Contains(name), on => Edit(f => { f.Stores.Remove(name); if (on) f.Stores.Add(name); })));
            }
            c2.Children.Add(Check("Include Steam Family Sharing", filter.IncludeFamilySharing, on => { plugin.Settings.IncludeFamilySharing = on; Edit(f => f.IncludeFamilySharing = on); }));

            // Column 3: genres (scrollable)
            var c3 = Col(2, "Genres");
            var gl = new StackPanel();
            foreach (var g in d.Visible.SelectMany(h => h.Game.Genres?.Select(x => x.Name) ?? Enumerable.Empty<string>()).GroupBy(x => x).OrderByDescending(x => x.Count()))
            {
                var name = g.Key;
                gl.Children.Add(Check($"{name}  ({g.Count()})", filter.Genres.Contains(name), on => Edit(f => { f.Genres.Remove(name); if (on) f.Genres.Add(name); })));
            }
            c3.Children.Add(new ScrollViewer { Content = gl, MaxHeight = 170, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            // Column 4: ranges + search
            var c4 = Col(3, "Ranges");
            c4.Children.Add(Label("Hours played"));
            c4.Children.Add(MinMax(filter.MinHours, filter.MaxHours, (lo, hi) => Edit(f => { f.MinHours = lo; f.MaxHours = hi; })));
            c4.Children.Add(Label("HowLongToBeat ≤ (hours)"));
            c4.Children.Add(Segmented(new[] { "Any", "5", "10", "20", "40" }, filter.MaxHltb?.ToString(CultureInfo.InvariantCulture) ?? "Any",
                v => Edit(f => f.MaxHltb = v == "Any" ? (double?)null : double.Parse(v, CultureInfo.InvariantCulture))));
            c4.Children.Add(Label("Release year"));
            c4.Children.Add(MinMax(filter.MinYear, filter.MaxYear, (lo, hi) => Edit(f => { f.MinYear = (int?)lo; f.MaxYear = (int?)hi; })));
            c4.Children.Add(Label("Name contains"));
            var search = new TextBox { Text = filter.Search ?? "", Margin = new Thickness(0, 0, 0, 4) };
            search.KeyUp += (s, e) => { if (e.Key == Key.Enter) Edit(f => f.Search = string.IsNullOrWhiteSpace(search.Text) ? null : search.Text); };
            search.LostFocus += (s, e) => { if ((search.Text ?? "") != (filter.Search ?? "")) Edit(f => f.Search = string.IsNullOrWhiteSpace(search.Text) ? null : search.Text); };
            c4.Children.Add(search);
            return panel;
        }

        // ---------------- small controls ----------------

        private TextBlock Label(string s) { var t = Text(s, 11, dim: true); t.Margin = new Thickness(0, 8, 0, 3); return t; }

        private FrameworkElement Combo(IList<string> items, string current, Action<string> changed)
        {
            var box = new ComboBox { ItemsSource = items, SelectedItem = items.Contains(current) ? current : items[0] };
            box.SelectionChanged += (s, e) => changed((string)box.SelectedItem);
            return box;
        }

        private FrameworkElement Check(string label, bool on, Action<bool> changed)
        {
            var cb = new CheckBox { Content = label, IsChecked = on, Margin = new Thickness(0, 2, 0, 2) };
            cb.Click += (s, e) => changed(cb.IsChecked == true);
            return cb;
        }

        /// <summary>Segmented control: one highlighted option, click to pick.</summary>
        private FrameworkElement Segmented(string[] options, string current, Action<string> changed)
        {
            var row = new WrapPanel();
            foreach (var o in options)
            {
                var opt = o;
                var b = new ToggleButton { Content = o, IsChecked = o == current, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 4, 4) };
                b.Click += (s, e) => changed(opt);
                row.Children.Add(b);
            }
            return row;
        }

        private FrameworkElement MinMax(double? lo, double? hi, Action<double?, double?> changed)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var a = new TextBox { Width = 60, Text = lo?.ToString(CultureInfo.InvariantCulture) ?? "", ToolTip = "min" };
            var b = new TextBox { Width = 60, Text = hi?.ToString(CultureInfo.InvariantCulture) ?? "", ToolTip = "max", Margin = new Thickness(6, 0, 0, 0) };
            double? P(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;
            void Commit()
            {
                var nlo = P(a.Text); var nhi = P(b.Text);
                if (nlo != lo || nhi != hi) changed(nlo, nhi);
            }
            foreach (var t in new[] { a, b })
            {
                t.KeyUp += (s, e) => { if (e.Key == Key.Enter) Commit(); };
                t.LostFocus += (s, e) => Commit();
            }
            row.Children.Add(a);
            row.Children.Add(Text("  to", 11, dim: true));
            row.Children.Add(b);
            return row;
        }

        private FrameworkElement Chip(string label, Action remove)
        {
            var b = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3, 8, 3), Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand };
            b.SetResourceReference(Border.BackgroundProperty, "ControlBackgroundBrush");
            b.SetResourceReference(Border.BorderBrushProperty, "GlyphBrush");
            b.BorderThickness = new Thickness(1);
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(Text(label, 12));
            var x = Text("  ✕", 11, dim: true);
            sp.Children.Add(x);
            b.Child = sp;
            b.ToolTip = "Remove this filter";
            b.MouseLeftButtonUp += (s, e) => remove();
            return b;
        }

        private Button Btn(string label, Action click, bool enabled = true, bool accent = false, string tip = null)
        {
            var b = new Button { Content = label, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0), IsEnabled = enabled, ToolTip = tip };
            if (accent) b.SetResourceReference(Control.BorderBrushProperty, "GlyphBrush");
            b.Click += (s, e) => click();
            return b;
        }

        internal void TestOpenFilters() { filtersOpen = true; SafeShow(last); }
        internal void TestSelectTab(string name) { selectedTabName = name; SafeShow(last); }

        /// <summary>Test hook: re-filter an already-shown page (the path that used to crash).</summary>
        internal void ApplyStoreFilter(string store)
        {
            filter.Stores = new List<string> { store };
            SafeShow(last);
        }

        /// <summary>Never let a rendering bug take Playnite down; show it in the page instead.</summary>
        private void SafeShow(Data d)
        {
            try { Show(d); }
            catch (Exception e)
            {
                Playnite.SDK.LogManager.GetLogger().Error(e, "Ledger view failed");
                shell.Children.Clear();
                var msg = new StackPanel { Margin = new Thickness(30) };
                msg.Children.Add(Text("Ledger hit an error showing this view: " + e.Message, 13));
                msg.Children.Add(Btn("Reset filters", () => Edit(f => ResetTo(f))));
                shell.Children.Add(msg);
            }
        }
    }
}
