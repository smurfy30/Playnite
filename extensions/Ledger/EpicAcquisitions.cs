using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ledger
{
    /// <summary>
    /// Epic order history (incl. free weekly claims) → Game.Added for Epic games. Uses Playnite's
    /// browser session (logged in by the Epic library add-on); 10 orders per page, paged by
    /// nextPageToken. Same record/undo store as Steam.
    /// </summary>
    internal class EpicAcquisitions
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        public static readonly Guid EpicPluginId = Guid.Parse("00000002-dbd1-46c6-b5d0-b1ba559d10e4");
        private const string OrdersUrl = "https://www.epicgames.com/account/v2/payment/ajaxGetOrderHistory?sortDir=DESC&sortBy=DATE&locale=en-US";

        private readonly IPlayniteAPI api;
        private readonly string dataDir;
        private readonly AcquisitionStore store;

        public class Item { public string description { get; set; } }
        public class Total { public int amount { get; set; } }
        public class Order { public string orderType { get; set; } public long createdAtMillis { get; set; } public List<Item> items { get; set; } public Total total { get; set; } }
        public class Page { public List<Order> orders { get; set; } public string nextPageToken { get; set; } }

        public EpicAcquisitions(IPlayniteAPI api, string dataDir)
        {
            this.api = api;
            this.dataDir = dataDir;
            store = new AcquisitionStore(api, dataDir);
        }

        private List<SteamLicense> Fetch(out bool loggedIn)
        {
            var result = new List<SteamLicense>();
            var ok = true;
            api.Dialogs.ActivateGlobalProgress(p =>
            {
                using (var view = api.WebViews.CreateOffscreenView())
                {
                    string token = null;
                    for (var page = 1; page <= 200; page++)
                    {
                        p.Text = $"Ledger: reading Epic orders, page {page}…";
                        var url = OrdersUrl + (token == null ? "" : "&nextPageToken=" + Uri.EscapeDataString(token));
                        view.NavigateAndWait(url);
                        var text = view.GetPageText();
                        Page parsed;
                        try { parsed = Serialization.FromJson<Page>(text); }
                        catch { ok = page > 1; break; } // first page not JSON → not logged in
                        if (parsed?.orders == null) { ok = page > 1; break; }
                        foreach (var o in parsed.orders)
                        {
                            foreach (var it in o.items ?? new List<Item>())
                            {
                                if (string.IsNullOrWhiteSpace(it.description)) continue;
                                result.Add(new SteamLicense
                                {
                                    Name = it.description.Trim(),
                                    Date = DateTimeOffset.FromUnixTimeMilliseconds(o.createdAtMillis).LocalDateTime,
                                    Method = (o.total?.amount ?? 0) == 0 ? "Free" : "Purchase",
                                    // Order total split evenly across its items (Epic orders are almost always one item).
                                    Amount = (o.total?.amount ?? 0) / 100m / Math.Max(1, o.items.Count)
                                });
                            }
                        }
                        if (string.IsNullOrEmpty(parsed.nextPageToken) || parsed.orders.Count == 0) break;
                        token = parsed.nextPageToken;
                    }
                }
            }, new GlobalProgressOptions("Ledger: reading Epic orders…", false) { IsIndeterminate = true });
            loggedIn = ok;
            return result;
        }

        public static List<SteamLicense> LoadOrders(string dir)
        {
            var p = Path.Combine(dir, "epic-orders.json");
            try { return File.Exists(p) ? Serialization.FromJsonFile<List<SteamLicense>>(p) ?? new List<SteamLicense>() : new List<SteamLicense>(); }
            catch { return new List<SteamLicense>(); }
        }

        public void Run(bool silent = false)
        {
            try
            {
                var orders = Fetch(out var loggedIn);
                if (!loggedIn)
                {
                    var msg = "Couldn't read Epic order history. Re-authenticate the Epic library add-on (Add-ons → Libraries → Epic).";
                    logger.Warn("Ledger: " + msg);
                    if (!silent) api.Dialogs.ShowErrorMessage(msg, "Ledger");
                    return;
                }
                Directory.CreateDirectory(dataDir);
                File.WriteAllText(Path.Combine(dataDir, "epic-orders.json"), Serialization.ToJson(orders, true));

                var epicGames = api.Database.Games.Where(g => g.PluginId == EpicPluginId).ToList();
                var matches = LicenseParsing.Match(epicGames.Select(g => g.Name), orders);
                var changed = store.Apply("Epic", epicGames, matches);
                var summary = $"Epic order items read: {orders.Count}" + Environment.NewLine +
                              $"Epic games matched: {matches.Count} of {epicGames.Count}" + Environment.NewLine +
                              $"Added dates changed: {changed}";
                logger.Info("Ledger import (Epic): " + summary.Replace(Environment.NewLine, "; "));
                if (!silent) api.Dialogs.ShowMessage(summary, "Ledger");
            }
            catch (Exception e)
            {
                logger.Error(e, "Epic acquisition import failed");
                if (!silent) api.Dialogs.ShowErrorMessage($"Import failed: {e.Message}", "Ledger");
            }
        }
    }
}
