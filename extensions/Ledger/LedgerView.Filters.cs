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

        /// <summary>Store + family-sharing scope, applied to every tab.</summary>
        private List<GameHistory> Scope(List<GameHistory> games) => games
            .Where(h => plugin.Settings.IncludeFamilySharing || h.Game.Source?.Name != "Steam Family Sharing")
            .Where(h => storeFilter == "All stores" || (h.Game.Source?.Name ?? "(none)") == storeFilter)
            .ToList();

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
            var pBox = new ComboBox { Width = 160, Margin = new Thickness(0, 0, 10, 0), ItemsSource = periods.Select(p => p.Name).ToList() };
            pBox.SelectedItem = periods.Any(p => p.Name == period.Name) ? period.Name : "All time";

            var stores = new[] { "All stores" }.Concat(d.Index.Games.Select(h => h.Game.Source?.Name ?? "(none)").Distinct().OrderBy(x => x)).ToList();
            var sBox = new ComboBox { Width = 180, Margin = new Thickness(0, 0, 10, 0), ItemsSource = stores };
            sBox.SelectedItem = stores.Contains(storeFilter) ? storeFilter : "All stores";

            var fam = new CheckBox { Content = "Include Family Sharing", IsChecked = plugin.Settings.IncludeFamilySharing, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };

            void Apply()
            {
                period = periods.First(p => p.Name == (string)pBox.SelectedItem);
                storeFilter = (string)sBox.SelectedItem;
                plugin.Settings.Period = period.Name;
                plugin.Settings.Store = storeFilter;
                plugin.Settings.IncludeFamilySharing = fam.IsChecked == true;
                plugin.SaveSettings();
                Show(last); // re-filter in memory, no reload
            }
            pBox.SelectionChanged += (s, e) => Apply();
            sBox.SelectionChanged += (s, e) => Apply();
            fam.Click += (s, e) => Apply();

            bar.Children.Add(Text("Period", 12, dim: true));
            ((TextBlock)bar.Children[0]).Margin = new Thickness(0, 0, 6, 0);
            bar.Children.Add(pBox);
            var st = Text("Store", 12, dim: true); st.Margin = new Thickness(10, 0, 6, 0);
            bar.Children.Add(st);
            bar.Children.Add(sBox);
            bar.Children.Add(fam);
            return bar;
        }
    }
}
