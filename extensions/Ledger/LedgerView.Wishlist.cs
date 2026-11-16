using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace Ledger
{
    internal partial class LedgerView
    {
        private void BuildWishlist(Data d)
        {
            var w = d.Wishlist ?? new List<WishEntry>();
            var sym = d.Money.Symbol;
            if (w.Count == 0)
            {
                Section("Wishlist", null);
                root.Children.Add(Text("No wishlist data yet. Ledger reads your public Steam wishlist once a day.", 13, dim: true));
                return;
            }
            var sale = w.Where(x => x.DiscountPct > 0).OrderByDescending(x => x.DiscountPct).ToList();
            var wrap = new WrapPanel { Margin = new Thickness(0, 5, 0, 10) };
            wrap.Children.Add(Card(w.Count.ToString(), "on your Steam wishlist"));
            wrap.Children.Add(Card(M(w.Sum(x => x.List ?? 0), sym), "at full price"));
            wrap.Children.Add(Card(M(w.Sum(x => x.Now ?? 0), sym), "at today's prices"));
            wrap.Children.Add(Card(sale.Count.ToString(), "on sale today"));
            var owned = w.Where(x => x.OwnedElsewhere != null).ToList();
            if (owned.Count > 0) wrap.Children.Add(Card(owned.Count.ToString(), "already owned on another store"));
            root.Children.Add(wrap);
            var tracked = w.Max(x => x.DaysTracked);
            root.Children.Add(Text($"Prices watched daily by Ledger ({tracked} day{(tracked == 1 ? "" : "s")} so far). \"Lowest seen\" gets more useful as the history grows.", 11, dim: true));

            if (owned.Count > 0)
            {
                Section("Already in your library", "On your Steam wishlist, but you own it on another store. Probably safe to remove.");
                foreach (var x in owned) WishRow(x, sym, $"owned on {x.OwnedElsewhere.Game.Source?.Name}");
            }
            if (sale.Count > 0)
            {
                Section("On sale now", "Biggest discount first.");
                foreach (var x in sale.Take(20))
                {
                    var atLow = x.LowestSeen.HasValue && x.Now <= x.LowestSeen && x.DaysTracked > 7;
                    WishRow(x, sym, $"-{x.DiscountPct}% · {M(x.Now ?? 0, sym)} (was {M(x.List ?? 0, sym)})" + (atLow ? " · lowest we've seen" : ""));
                }
            }
            Section("Waiting longest", "Oldest wishlist entries. Still want them?");
            foreach (var x in w.OrderBy(x => x.Added).Take(15))
                WishRow(x, sym, $"added {x.Added:MMM yyyy} · {(x.Now.HasValue ? M(x.Now.Value, sym) : "no price")}");
        }

        private void WishRow(WishEntry x, string sym, string meta)
        {
            var grid = Row(new GridLength(1, GridUnitType.Star), new GridLength(380));
            var tb = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var link = new Hyperlink(new Run(x.Name)) { TextDecorations = null, Cursor = Cursors.Hand };
            link.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
            var url = "https://store.steampowered.com/app/" + x.AppId;
            link.Click += (s, e) => System.Diagnostics.Process.Start(url);
            tb.Inlines.Add(link);
            Place(grid, tb, 0);
            Place(grid, Text(meta, 12, dim: true), 1);
            root.Children.Add(grid);
        }
    }
}
