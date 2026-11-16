using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

namespace Ledger
{
    internal class Transaction
    {
        public DateTime Date { get; set; }
        public List<string> Items { get; set; } = new List<string>();
        public string Type { get; set; }          // "Purchase", "In-Game Purchase", "Refund", "Gift Purchase", ...
        public decimal Total { get; set; }        // what was charged (positive), refunds positive too
        public decimal? ListPrice { get; set; }   // pre-discount, when Steam shows one
        public bool IsWalletTopUp { get; set; }
        public bool IsRefund => (Type ?? "").IndexOf("Refund", StringComparison.OrdinalIgnoreCase) >= 0;
        public bool IsInGame => (Type ?? "").IndexOf("In-Game", StringComparison.OrdinalIgnoreCase) >= 0;
        public string Store { get; set; } = "Steam";
    }

    internal class Price
    {
        public string AppId { get; set; }
        public decimal? List { get; set; }       // full price, null = free / not sold
        public decimal? Now { get; set; }        // current (maybe discounted)
        public string Currency { get; set; }
        public DateTime FetchedAt { get; set; }
    }

    /// <summary>
    /// Money: what you paid (Steam purchase history via the logged-in browser; Epic order totals),
    /// and what your library is worth now (Steam store appdetails, public, batched, cached a week).
    /// </summary>
    internal class Money
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly IPlayniteAPI api;
        private readonly string dataDir;
        public string SpendPath => Path.Combine(dataDir, "steam-spend.json");
        public string PricesPath => Path.Combine(dataDir, "steam-prices.json");

        public Money(IPlayniteAPI api, string dataDir)
        {
            this.api = api;
            this.dataDir = dataDir;
        }

        // ---------------- Steam purchase history ----------------

        public int FetchSpend(bool silent = false)
        {
            string html = null;
            api.Dialogs.ActivateGlobalProgress(p =>
            {
                using (var view = api.WebViews.CreateOffscreenView())
                {
                    view.NavigateAndWait("https://store.steampowered.com/account/history/");
                    // Click "Load more" until it disappears. Each click appends a batch via AJAX.
                    for (var i = 0; i < 200; i++)
                    {
                        p.Text = $"Ledger: loading Steam purchase history ({i + 1})…";
                        var r = view.EvaluateScriptAsync(
                            "(function(){var b=document.getElementById('load_more_button');" +
                            "if(b && b.style.display!='none' && b.offsetParent!==null){b.click();return 'more';}return 'done';})()").GetAwaiter().GetResult();
                        if (!(r?.Result is string s) || s != "more") break;
                        Thread.Sleep(1500);
                    }
                    html = view.GetPageSource();
                }
            }, new GlobalProgressOptions("Ledger: Steam purchase history…", false) { IsIndeterminate = true });

            if (html == null || html.IndexOf("wallet_table_row", StringComparison.Ordinal) < 0)
            {
                logger.Warn("Ledger: Steam purchase history not readable (not logged in?)");
                if (!silent) api.Dialogs.ShowErrorMessage("Couldn't read Steam purchase history. Log in via Extensions → Ledger → Import Steam acquisition dates first.", "Ledger");
                return 0;
            }
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(Path.Combine(dataDir, "steam-history.html"), html);
            var tx = ParseHistory(html);
            File.WriteAllText(SpendPath, Serialization.ToJson(tx, true));
            logger.Info($"Ledger: Steam purchase history: {tx.Count} transactions");
            return tx.Count;
        }

        private static readonly Regex RowRx = new Regex(@"<tr[^>]*class=""wallet_table_row[^""]*""[^>]*>(?<row>.*?)</tr>", RegexOptions.Singleline);
        private static string Cell(string row, string cls)
        {
            // Attributes may come before class (data-tooltip-text=...), and Steam emits junk like "=""".
            var m = Regex.Match(row, $@"<td[^>]*?class=""{cls}[ ""][^>]*>(?<v>.*?)</td>", RegexOptions.Singleline);
            return m.Success ? m.Groups["v"].Value : "";
        }
        private static string Strip(string s) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(s ?? "", "<.*?>", " ")), @"\s+", " ").Trim();
        private static readonly Regex MoneyRx = new Regex(@"[£$€]\s?(?<n>[\d.,]+)|(?<n>[\d.,]+)\s?(?:€|zł|kr)");

        internal static decimal? ParseMoney(string s)
        {
            var all = MoneyRx.Matches(s ?? "").Cast<Match>().ToList();
            if (all.Count == 0) return null;
            var n = all.Last().Groups["n"].Value;
            // "1.234,56" vs "1,234.56": whichever separator comes last is the decimal point.
            if (n.LastIndexOf(',') > n.LastIndexOf('.')) n = n.Replace(".", "").Replace(',', '.');
            else n = n.Replace(",", "");
            return decimal.TryParse(n, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : (decimal?)null;
        }

        internal static List<Transaction> ParseHistory(string html)
        {
            var list = new List<Transaction>();
            foreach (Match m in RowRx.Matches(html ?? ""))
            {
                var row = m.Groups["row"].Value;
                var dateText = Strip(Cell(row, "wht_date"));
                if (!DateTime.TryParseExact(dateText, new[] { "d MMM, yyyy", "MMM d, yyyy", "d MMM yyyy" }, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AllowWhiteSpaces, out var date)) continue;

                var itemsCell = Cell(row, "wht_items");
                var items = Regex.Matches(itemsCell, @"<div[^>]*>(?<t>.*?)</div>", RegexOptions.Singleline).Cast<Match>()
                                 .Select(x => Strip(x.Groups["t"].Value)).Where(x => x.Length > 0).ToList();
                if (items.Count == 0) items.Add(Strip(itemsCell));

                var typeCell = Cell(row, "wht_type");
                var type = Strip(Regex.Match(typeCell, @"<div[^>]*>(?<t>.*?)</div>", RegexOptions.Singleline).Groups["t"].Value);
                if (type.Length == 0) type = Strip(typeCell).Split(' ').FirstOrDefault() ?? "";

                var totalCell = Cell(row, "wht_total");
                var baseMatch = Regex.Match(Cell(row, "wht_base_price"), @"wht_original_price[^>]*>(?<v>.*?)<", RegexOptions.Singleline);
                list.Add(new Transaction
                {
                    Date = date,
                    Items = items,
                    Type = type,
                    Total = ParseMoney(Strip(totalCell)) ?? 0,
                    ListPrice = baseMatch.Success ? ParseMoney(Strip(baseMatch.Groups["v"].Value)) : null,
                    IsWalletTopUp = Strip(itemsCell).IndexOf("Wallet Credit", StringComparison.OrdinalIgnoreCase) >= 0
                });
            }
            return list;
        }

        public List<Transaction> LoadSpend()
        {
            try { return File.Exists(SpendPath) ? Serialization.FromJsonFile<List<Transaction>>(SpendPath) ?? new List<Transaction>() : new List<Transaction>(); }
            catch { return new List<Transaction>(); }
        }

        // ---------------- Steam store prices ----------------

        public Dictionary<string, Price> LoadPrices()
        {
            try { return File.Exists(PricesPath) ? (Serialization.FromJsonFile<List<Price>>(PricesPath) ?? new List<Price>()).ToDictionary(p => p.AppId) : new Dictionary<string, Price>(); }
            catch { return new Dictionary<string, Price>(); }
        }

        public class AppDetails { public bool success { get; set; } public object data { get; set; } }

        /// <summary>Fetch list prices for Steam app ids not fetched in the last 7 days. Batched (price_overview filter allows many ids).</summary>
        public int FetchPrices(IEnumerable<string> appIds, string cc = "gb")
        {
            var cache = LoadPrices();
            var todo = appIds.Where(id => !cache.TryGetValue(id, out var p) || p.FetchedAt < DateTime.Now.AddDays(-7)).Distinct().ToList();
            var fetched = 0;
            for (var i = 0; i < todo.Count; i += 50)
            {
                var batch = todo.Skip(i).Take(50).ToList();
                try
                {
                    using (var wc = new WebClient())
                    {
                        wc.Encoding = System.Text.Encoding.UTF8;
                        var json = wc.DownloadString($"https://store.steampowered.com/api/appdetails?appids={string.Join(",", batch)}&filters=price_overview&cc={cc}");
                        var parsed = Serialization.FromJson<Dictionary<string, Dictionary<string, object>>>(json) ?? new Dictionary<string, Dictionary<string, object>>();
                        foreach (var id in batch)
                        {
                            var price = new Price { AppId = id, FetchedAt = DateTime.Now };
                            if (parsed.TryGetValue(id, out var entry) && entry.TryGetValue("data", out var data))
                            {
                                var po = Regex.Match(Serialization.ToJson(data), "\"initial\":(?<i>\\d+),\"final\":(?<f>\\d+)");
                                var cur = Regex.Match(Serialization.ToJson(data), "\"currency\":\"(?<c>[A-Z]+)\"");
                                if (po.Success)
                                {
                                    price.List = int.Parse(po.Groups["i"].Value) / 100m;
                                    price.Now = int.Parse(po.Groups["f"].Value) / 100m;
                                    price.Currency = cur.Success ? cur.Groups["c"].Value : null;
                                }
                            }
                            cache[id] = price;
                            fetched++;
                        }
                    }
                }
                catch (Exception e) { logger.Warn($"Ledger: price batch failed: {e.Message}"); }
                Thread.Sleep(1500); // store API rate limit is ~200 requests / 5 min; we're far under
            }
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(PricesPath, Serialization.ToJson(cache.Values.ToList(), true));
            return fetched;
        }
    }
}
