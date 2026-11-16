using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

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
            {
                list.Add(new Period { Name = y.ToString(), From = new DateTime(y, 1, 1), To = new DateTime(y + 1, 1, 1) });
            }
            return list;
        }
    }

    internal partial class LedgerView
    {
        private Period period = new Period { Name = "All time" };
        private string storeFilter = "All stores";
        private Data last;

        private int StaleDays => Math.Max(7, plugin.Settings.StaleDays);

        private string genreFilter = "All genres", installFilter = "Any", playFilter = "Any", searchFilter = "";

        /// <summary>Every filter except the period, applied to every tab.</summary>
        private List<GameHistory> Scope(List<GameHistory> games) => games
            .Where(h => plugin.Settings.IncludeFamilySharing || h.Game.Source?.Name != "Steam Family Sharing")
            .Where(h => storeFilter == "All stores" || (h.Game.Source?.Name ?? "(none)") == storeFilter)
            .Where(h => genreFilter == "All genres" || (h.Game.Genres?.Any(x => x.Name == genreFilter) ?? false))
            .Where(h => installFilter == "Any" || (installFilter == "Installed") == h.Game.IsInstalled)
            .Where(h => playFilter == "Any" || (playFilter == "Played") == h.Played)
            .Where(h => string.IsNullOrWhiteSpace(searchFilter) || h.Game.Name.IndexOf(searchFilter.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();

        private bool AnyFilter => storeFilter != "All stores" || genreFilter != "All genres" || installFilter != "Any" || playFilter != "Any" || !string.IsNullOrWhiteSpace(searchFilter) || !period.IsAll;

        /// <summary>"Played in the period": any tracked hours, or last/first played inside it.</summary>
        private bool PlayedIn(GameHistory h, Data d) =>
            period.IsAll ? h.Played : d.Activity.HoursIn(h.Game.Id, period.From, period.To) > 0 || period.Contains(h.LastPlayed) || period.Contains(h.FirstPlayed);

        /// <summary>Hours in the period: tracked activity when we have a window, lifetime otherwise.</summary>
        private double HoursOf(GameHistory h, Data d) => period.IsAll ? h.Hours : d.Activity.HoursIn(h.Game.Id, period.From, period.To);

        /// <summary>Tracking only started recently; say so when a period reaches back before it.</summary>
        private string TrackingCaveat(Data d)
        {
            if (period.IsAll) return null;
            var since = d.Activity.TrackedSince;
            if (since == null) return "No play tracking yet. Hours for a period fill in as Ledger records sessions.";
            return since > period.From ? $"Hours tracked since {since:d MMM yyyy}; earlier play in this period isn't counted." : null;
        }

        private FrameworkElement BuildFilterBar(Data d)
        {
            var bar = new WrapPanel { Margin = new Thickness(30, 10, 30, 0) };
            var years = d.Index.Games.SelectMany(h => new[] { h.Acquired?.Year, h.FirstPlayed?.Year, h.LastPlayed?.Year })
                         .Where(y => y.HasValue).Select(y => y.Value).Distinct();
            var periods = Period.Options(years);
            ComboBox Combo(string label, IList<string> items, string current, double width)
            {
                var t = Text(label, 12, dim: true); t.Margin = new Thickness(0, 0, 6, 0);
                bar.Children.Add(t);
                var box = new ComboBox { Width = width, Margin = new Thickness(0, 0, 14, 6), ItemsSource = items };
                box.SelectedItem = items.Contains(current) ? current : items[0];
                bar.Children.Add(box);
                return box;
            }
            var visible = d.Visible;
            var pBox = Combo("Period", periods.Select(p => p.Name).ToList(), period.Name, 140);
            var sBox = Combo("Store", new[] { "All stores" }.Concat(visible.Select(h => h.Game.Source?.Name ?? "(none)").Distinct().OrderBy(x => x)).ToList(), storeFilter, 160);
            var gBox = Combo("Genre", new[] { "All genres" }.Concat(visible.SelectMany(h => h.Game.Genres?.Select(x => x.Name) ?? Enumerable.Empty<string>()).Distinct().OrderBy(x => x)).ToList(), genreFilter, 170);
            var iBox = Combo("Installed", new[] { "Any", "Installed", "Not installed" }, installFilter, 110);
            var plBox = Combo("Played", new[] { "Any", "Played", "Unplayed" }, playFilter, 100);

            var searchLabel = Text("Search", 12, dim: true); searchLabel.Margin = new Thickness(0, 0, 6, 0);
            bar.Children.Add(searchLabel);
            var search = new TextBox { Width = 160, Text = searchFilter, Margin = new Thickness(0, 0, 14, 6), VerticalContentAlignment = VerticalAlignment.Center };
            bar.Children.Add(search);

            var fam = new CheckBox { Content = "Family Sharing", IsChecked = plugin.Settings.IncludeFamilySharing, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 6) };
            bar.Children.Add(fam);
            var reset = new Button { Content = "Reset", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 14, 6), IsEnabled = AnyFilter };
            bar.Children.Add(reset);

            var shown = Scope(visible).Count(h => !h.IsTool);
            var total = visible.Count(h => !h.IsTool && (plugin.Settings.IncludeFamilySharing || h.Game.Source?.Name != "Steam Family Sharing"));
            var count = Text(AnyFilter ? $"Showing {shown:N0} of {total:N0} games" + (period.IsAll ? "" : $" · {period.Name}") : $"{total:N0} games", 12, dim: true);
            count.Margin = new Thickness(0, 0, 0, 6);
            bar.Children.Add(count);

            var applying = false;
            void Apply()
            {
                if (applying) return;
                applying = true;
                try
                {
                    period = periods.First(p => p.Name == (string)pBox.SelectedItem);
                    storeFilter = (string)sBox.SelectedItem;
                    genreFilter = (string)gBox.SelectedItem;
                    installFilter = (string)iBox.SelectedItem;
                    playFilter = (string)plBox.SelectedItem;
                    searchFilter = search.Text ?? "";
                    var st = plugin.Settings;
                    st.Period = period.Name; st.Store = storeFilter; st.Genre = genreFilter; st.InstallState = installFilter; st.PlayState = playFilter; st.Search = searchFilter;
                    st.IncludeFamilySharing = fam.IsChecked == true;
                    plugin.SaveSettings();
                    // Re-render after this event finishes: rebuilding the bar from inside its own handler is fragile.
                    Dispatcher.BeginInvoke((Action)(() => SafeShow(last)));
                }
                finally { applying = false; }
            }
            foreach (var box in new[] { pBox, sBox, gBox, iBox, plBox }) box.SelectionChanged += (s, e) => Apply();
            fam.Click += (s, e) => Apply();
            search.KeyUp += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) Apply(); };
            search.LostFocus += (s, e) => { if ((search.Text ?? "") != searchFilter) Apply(); };
            reset.Click += (s, e) =>
            {
                pBox.SelectedItem = "All time"; sBox.SelectedItem = "All stores"; gBox.SelectedItem = "All genres";
                iBox.SelectedItem = "Any"; plBox.SelectedItem = "Any"; search.Text = "";
                Apply();
            };
            return bar;
        }

        /// <summary>Test hook: re-filter an already-shown page (the path that used to crash).</summary>
        internal void ApplyStoreFilter(string store)
        {
            storeFilter = store;
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
                shell.Children.Add(Text("Ledger hit an error showing this view: " + e.Message + ". Try Reset or Refresh.", 13));
            }
        }
    }
}
