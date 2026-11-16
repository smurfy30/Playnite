using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Ledger
{
    internal partial class LedgerView
    {
        /// <summary>Progress for one goal: (value, display, detail). Computed from records only.</summary>
        private (double value, string shown, string detail) Measure(Goal g, Data d)
        {
            var from = new DateTime(g.Year, 1, 1);
            var to = from.AddYears(1);
            bool In(DateTime? x) => x.HasValue && x >= from && x < to;
            var real = d.Visible.Where(h => !h.IsTool && (plugin.Settings.IncludeFamilySharing || h.Game.Source?.Name != "Steam Family Sharing")).ToList();
            switch (g.Kind)
            {
                case "beat":
                    {
                        var ids = Tracking.LoadEvents(plugin.GetPluginUserDataPath())
                            .Where(e => e.Type == "status" && In(e.At) && e.To != null && (e.To.IndexOf("Beaten", StringComparison.OrdinalIgnoreCase) >= 0 || e.To.IndexOf("Completed", StringComparison.OrdinalIgnoreCase) >= 0))
                            .Select(e => e.GameId).Distinct().ToList();
                        return (ids.Count, ids.Count.ToString(), $"of {g.Target:0} games");
                    }
                case "start":
                    {
                        // Owned before the year began, first played during it.
                        var n = real.Count(h => In(h.FirstPlayed) && (!h.Acquired.HasValue || h.Acquired < from || h.FirstPlayed.Value > h.Acquired.Value.AddDays(30)));
                        return (n, n.ToString(), $"of {g.Target:0} from the backlog");
                    }
                case "shrink":
                    {
                        // Never-played count now vs at the year's first snapshot (or the first one we have).
                        var start = FirstSnapshotNeverPlayed(from);
                        var now = real.Count(h => !h.Played);
                        var shrunk = start.HasValue ? Math.Max(0, start.Value - now) : 0;
                        return (shrunk, shrunk.ToString(), start.HasValue ? $"{start} → {now} unplayed" : "baseline from first snapshot");
                    }
                case "spend":
                    {
                        var spent = d.Money.Transactions.Where(t => !t.IsWalletTopUp && In(t.Date)).Sum(t => t.IsRefund ? -t.Total : t.Total);
                        return ((double)spent, M(spent, d.Money.Symbol), $"budget {d.Money.Symbol}{g.Target:N0}");
                    }
                case "hours":
                    {
                        var h = d.Activity.Daily.Values.SelectMany(x => x).Where(kv => In(kv.Key)).Sum(kv => kv.Value);
                        return (h, $"{h:N0}h", $"cap {g.Target:N0}h (tracked since {d.Activity.TrackedSince:MMM yyyy})");
                    }
            }
            return (0, "?", "");
        }

        private int? FirstSnapshotNeverPlayed(DateTime from)
        {
            var dir = System.IO.Path.Combine(plugin.GetPluginUserDataPath(), "snapshots");
            var first = Snapshots.Ordered(dir).FirstOrDefault(f => string.CompareOrdinal(System.IO.Path.GetFileName(f).Substring(0, 10), from.ToString("yyyy-MM-dd")) >= 0 && !f.EndsWith(".d.jsonl"))
                        ?? Snapshots.Ordered(dir).FirstOrDefault(f => !f.EndsWith(".d.jsonl"));
            if (first == null) return null;
            var n = 0;
            foreach (var line in System.IO.File.ReadLines(first))
            {
                if (line.Contains("\"playtimeSec\":0,") && !line.Contains("\"hidden\":true") && !line.Contains("Steam Family Sharing")) n++;
            }
            return n;
        }

        private void BuildGoals(Data d)
        {
            var goals = Lists.Goals().Where(g => g.Year == DateTime.Now.Year).ToList();
            var head = new DockPanel { LastChildFill = false };
            Section($"{DateTime.Now.Year} goals", goals.Count == 0 ? "Set a target and Ledger tracks it from what it records: status changes, first plays, spending, sessions." : null);
            var add = Btn("＋ Goal", EditGoal);
            add.HorizontalAlignment = HorizontalAlignment.Left;
            if (goals.Count == 0) { root.Children.Add(add); return; }

            var wrap = new WrapPanel();
            foreach (var g in goals)
            {
                var (value, shown, detail) = Measure(g, d);
                var limit = g.Kind == "spend" || g.Kind == "hours";
                var ring = Charts.Ring(value, g.Target, shown, Goal.Kinds.TryGetValue(g.Kind, out var k) ? k.Replace(" (mark Beaten/Completed)", "").Replace(" (on games + in-game)", "").Replace(" (hours, tracked)", "") : g.Kind,
                                       detail + ExpectedPace(g, value), overIsBad: limit);
                var goal = g;
                var menu = new ContextMenu();
                menu.Items.Add(Item("Change target…", () => { var r = api.Dialogs.SelectString("New target:", "Ledger", goal.Target.ToString(CultureInfo.InvariantCulture)); if (r.Result && double.TryParse(r.SelectedString, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) { var all = Lists.Goals(); all.First(x => x.Id == goal.Id).Target = t; Lists.SaveGoals(all); SafeShow(last); } }));
                menu.Items.Add(Item("Delete goal", () => { var all = Lists.Goals(); all.RemoveAll(x => x.Id == goal.Id); Lists.SaveGoals(all); SafeShow(last); }));
                ((FrameworkElement)ring).ContextMenu = menu;
                ((FrameworkElement)ring).ToolTip = "Right-click to change or delete";
                wrap.Children.Add(ring);
            }
            root.Children.Add(wrap);
            root.Children.Add(add);
        }

        /// <summary>" · on track" / " · 3 behind pace" based on how much of the year has passed.</summary>
        private static string ExpectedPace(Goal g, double value)
        {
            if (g.Year != DateTime.Now.Year || g.Target <= 0) return "";
            var yearFrac = (DateTime.Now - new DateTime(g.Year, 1, 1)).TotalDays / 365.25;
            var expected = g.Target * yearFrac;
            if (g.Kind == "spend" || g.Kind == "hours")
                return value > g.Target ? " · over" : value > expected * 1.1 ? " · ahead of budget pace" : " · within pace";
            if (value >= g.Target) return " · done!";
            var gap = expected - value;
            return gap <= 0.5 ? " · on track" : $" · {Math.Ceiling(gap):0} behind pace";
        }

        private void EditGoal()
        {
            var window = api.Dialogs.CreateWindow(new Playnite.SDK.WindowCreationOptions { ShowMinimizeButton = false, ShowMaximizeButton = false });
            window.Title = "New goal";
            window.Width = 460; window.Height = 260;
            window.Owner = api.Dialogs.GetCurrentAppWindow();
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var sp = new StackPanel { Margin = new Thickness(20) };
            sp.Children.Add(Text("What do you want to do this year?", 14, bold: true));
            var kind = new ComboBox { ItemsSource = Goal.Kinds.Values.ToList(), SelectedIndex = 0, Margin = new Thickness(0, 8, 0, 8) };
            sp.Children.Add(kind);
            sp.Children.Add(Text("Target", 12, dim: true));
            var target = new TextBox { Text = "12", Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
            sp.Children.Add(target);
            var ok = new Button { Content = "Add goal", Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(14, 4, 14, 4), HorizontalAlignment = HorizontalAlignment.Left };
            ok.Click += (s, e) =>
            {
                if (!double.TryParse(target.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) || t <= 0) return;
                var all = Lists.Goals();
                all.Add(new Goal { Kind = Goal.Kinds.Keys.ElementAt(kind.SelectedIndex), Target = t });
                Lists.SaveGoals(all);
                window.Close();
            };
            sp.Children.Add(ok);
            window.Content = sp;
            window.ShowDialog();
            SafeShow(last);
        }
    }
}
