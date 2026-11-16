using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Ledger
{
    internal partial class LedgerView
    {
        /// <summary>Overview cards for a chosen period: what happened in it rather than library totals.</summary>
        private void BuildPeriodCards(List<GameHistory> games, Data d)
        {
            var real = games.Where(h => !h.IsTool).ToList();
            var played = real.Where(h => PlayedIn(h, d)).ToList();
            var hours = real.Sum(h => HoursOf(h, d));
            var got = real.Where(h => period.Contains(h.Acquired)).ToList();
            var started = real.Where(h => period.Contains(h.FirstPlayed)).ToList();
            var spend = d.Money.Transactions.Where(t => !t.IsWalletTopUp && period.Contains(t.Date)).Sum(t => t.IsRefund ? -t.Total : t.Total);

            var wrap = new WrapPanel { Margin = new Thickness(0, 5, 0, 10) };
            wrap.Children.Add(Card(played.Count.ToString("N0"), "games played"));
            wrap.Children.Add(Card(hours > 0 ? $"{hours:N0}h" : "–", "hours tracked"));
            wrap.Children.Add(Card(got.Count.ToString("N0"), "games got"));
            wrap.Children.Add(Card(started.Count.ToString("N0"), "games started"));
            if (spend != 0) wrap.Children.Add(Card(M(spend, d.Money.Symbol), "spent"));
            root.Children.Add(wrap);
            var caveat = TrackingCaveat(d);
            if (caveat != null) root.Children.Add(Text(caveat, 12, dim: true));
        }

        private void BuildPeriodInsights(List<GameHistory> games, Data d)
        {
            var real = games.Where(h => !h.IsTool).ToList();
            var lines = new List<string>();
            var played = real.Where(h => PlayedIn(h, d)).ToList();
            var byHours = played.Select(h => new { h, hrs = HoursOf(h, d) }).Where(x => x.hrs > 0).OrderByDescending(x => x.hrs).ToList();
            if (byHours.Count > 0)
            {
                var total = byHours.Sum(x => x.hrs);
                lines.Add($"Most played: {byHours[0].h.Game.Name}, {byHours[0].hrs:N0}h ({100 * byHours[0].hrs / total:N0}% of your tracked time).");
                if (byHours.Count > 1) lines.Add($"Then {string.Join(", ", byHours.Skip(1).Take(3).Select(x => $"{x.h.Game.Name} ({x.hrs:N0}h)"))}.");
            }
            var newOnes = played.Where(h => period.Contains(h.FirstPlayed)).ToList();
            if (played.Count > 0) lines.Add($"{played.Count} games played, {newOnes.Count} of them for the first time.");
            var got = real.Where(h => period.Contains(h.Acquired)).ToList();
            if (got.Count > 0)
            {
                var gotPlayed = got.Count(h => h.Played);
                lines.Add($"You got {got.Count} games and have played {gotPlayed} of them ({Pct(gotPlayed, got.Count)}).");
            }
            var genre = played.SelectMany(h => (h.Game.Genres ?? new List<Playnite.SDK.Models.Genre>()).Select(x => new { x.Name, hrs = Math.Max(HoursOf(h, d), 0.01) }))
                              .GroupBy(x => x.Name).OrderByDescending(x => x.Sum(y => y.hrs)).FirstOrDefault();
            if (genre != null) lines.Add($"Top genre: {genre.Key}.");
            if (lines.Count == 0) lines.Add("Nothing recorded in this period yet.");

            Section($"Insights · {period.Name}", null);
            foreach (var l in lines)
            {
                var tb = Text("•  " + l, 14);
                tb.Margin = new Thickness(0, 3, 0, 3);
                root.Children.Add(tb);
            }
        }
    }
}
