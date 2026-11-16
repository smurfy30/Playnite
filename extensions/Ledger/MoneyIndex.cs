using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Ledger
{
    internal class GameMoney
    {
        public GameHistory H { get; set; }
        public decimal Paid { get; set; }        // base game + DLC purchases, minus refunds
        public decimal InGame { get; set; }      // microtransactions
        public decimal? ListPrice { get; set; }  // current full store price
        public decimal Total => Paid + InGame;
        public decimal? PerHour => H.Hours >= 1 && Total > 0 ? Total / (decimal)H.Hours : (decimal?)null;
    }

    /// <summary>Per-game money, computed on read from steam-spend.json, epic orders and steam-prices.json.</summary>
    internal class MoneyIndex
    {
        public Dictionary<Guid, GameMoney> Games { get; } = new Dictionary<Guid, GameMoney>();
        public List<Transaction> Transactions { get; } = new List<Transaction>();
        public string Symbol { get; private set; } = "£";
        public decimal Unattributed { get; private set; }
        public bool HasSpend => Transactions.Count > 0;
        public bool HasPrices { get; private set; }

        public static MoneyIndex Build(Money money, IEnumerable<GameHistory> all, string ledgerDir)
        {
            var idx = new MoneyIndex();
            var games = all.ToList();
            var prices = money.LoadPrices();
            idx.HasPrices = prices.Values.Any(p => p.List.HasValue);
            var cur = prices.Values.FirstOrDefault(p => p.Currency != null)?.Currency;
            idx.Symbol = cur == "USD" ? "$" : cur == "EUR" ? "€" : "£";

            GameMoney For(GameHistory h)
            {
                if (!idx.Games.TryGetValue(h.Game.Id, out var gm)) idx.Games[h.Game.Id] = gm = new GameMoney { H = h };
                return gm;
            }

            // Current list price for Steam games.
            foreach (var h in games.Where(x => x.Game.PluginId == HistoryIndex.SteamPluginId && x.Game.GameId != null))
            {
                if (prices.TryGetValue(h.Game.GameId, out var p) && p.List.HasValue) For(h).ListPrice = p.List;
            }

            // Steam transactions → games. Steam-sourced games only (visible or hidden, so hidden dupes still count).
            var steamGames = games.Where(x => x.Game.PluginId == HistoryIndex.SteamPluginId).ToList();
            var byName = steamGames.GroupBy(x => x.Game.Name).ToDictionary(g => g.Key, g => g.First());
            var tx = money.LoadSpend();
            idx.Transactions.AddRange(tx);
            var itemNames = tx.Where(t => !t.IsWalletTopUp).SelectMany(t => t.IsInGame ? t.Items.Take(1) : t.Items).Distinct()
                              .Select(n => new SteamLicense { Name = n, Date = DateTime.MinValue }).ToList();
            var match = LicenseParsing.Match(byName.Keys, itemNames);           // game name → item name
            var itemToGame = new Dictionary<string, GameHistory>();
            foreach (var kv in match)
            {
                if (!itemToGame.ContainsKey(kv.Value.Name)) itemToGame[kv.Value.Name] = byName[kv.Key];
            }
            // DLC / season passes: item starts with a game name (longest wins) even if the game claimed another item.
            // "KINGDOM HEARTS Collection [I~III]" → every owned game starting with the franchise name.
            var collectionRx = new System.Text.RegularExpressions.Regex(@"\b(the )?(collection|bundle|trilogy|anthology|complete saga)\b.*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            List<GameHistory> ResolveMany(string item)
            {
                var m = collectionRx.Match(item);
                if (m.Success && m.Index > 0)
                {
                    var franchise = LicenseParsing.Norm(item.Substring(0, m.Index));
                    if (franchise.Length >= 5)
                    {
                        var members = byName.Values.Where(x => LicenseParsing.Norm(x.Game.Name).StartsWith(franchise)).ToList();
                        if (members.Count > 0) return members;
                    }
                }
                var one = Resolve(item);
                return one == null ? new List<GameHistory>() : new List<GameHistory> { one };
            }

            GameHistory Resolve(string item)
            {
                if (itemToGame.TryGetValue(item, out var h)) return h;
                var n = LicenseParsing.Norm(item);
                return byName.Values.Where(x => LicenseParsing.Norm(x.Game.Name).Length >= 5 && n.StartsWith(LicenseParsing.Norm(x.Game.Name)))
                             .OrderByDescending(x => x.Game.Name.Length).FirstOrDefault();
            }

            foreach (var t in tx.Where(x => !x.IsWalletTopUp))
            {
                var sign = t.IsRefund ? -1 : 1;
                if (t.IsInGame)
                {
                    var g = Resolve(t.Items.FirstOrDefault() ?? "");
                    if (g != null) For(g).InGame += sign * t.Total; else idx.Unattributed += sign * t.Total;
                    continue;
                }
                var targets = t.Items.SelectMany(ResolveMany).Distinct().Cast<GameHistory>().ToList();
                if (targets.Count == 0) { idx.Unattributed += sign * t.Total; continue; }
                // Split bundles by current list price, else evenly.
                var weights = targets.Select(x => x == null ? 0m : (idx.Games.TryGetValue(x.Game.Id, out var gm) ? gm.ListPrice ?? 0 : 0)).ToList();
                var sum = weights.Sum();
                for (var i = 0; i < targets.Count; i++)
                {
                    if (targets[i] == null) continue;
                    var share = sum > 0 ? weights[i] / sum : 1m / targets.Count(x => x != null);
                    For(targets[i]).Paid += sign * t.Total * share;
                }
            }

            // Epic: order totals recorded by the Epic importer (Amount on each order item).
            foreach (var r in EpicAcquisitions.LoadOrders(ledgerDir).Where(o => o.Amount > 0))
            {
                var g = games.Where(x => x.Game.PluginId == EpicAcquisitions.EpicPluginId)
                             .FirstOrDefault(x => x.AcquiredVia != null && x.Acquired.HasValue && x.Acquired.Value.Date == r.Date.Date && LicenseParsing.Norm(r.Name).StartsWith(LicenseParsing.Norm(x.Game.Name)));
                if (g != null) For(g).Paid += r.Amount ?? 0;
            }
            return idx;
        }
    }
}
