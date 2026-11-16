using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace Ledger
{
    /// <summary>
    /// Reads https://store.steampowered.com/account/licenses (needs a Steam login in Playnite's
    /// browser), matches licenses to Steam games and sets Game.Added to the acquisition date.
    /// Every change is recorded in acquisitions.json with the previous Added value, so Undo
    /// restores exactly what was there.
    /// </summary>
    internal class SteamAcquisitions
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly Guid SteamPluginId = Guid.Parse("cb91dfc9-b977-43bf-8e70-55f46e410fab");
        private const string LicensesUrl = "https://store.steampowered.com/account/licenses/";
        private const string LoginUrl = "https://store.steampowered.com/login/?redir=account%2Flicenses%2F";

        private readonly IPlayniteAPI api;
        private readonly string dataDir;
        private string RecordPath => Path.Combine(dataDir, "acquisitions.json");
        private string RawPath => Path.Combine(dataDir, "steam-licenses.json");

        public class Record
        {
            public Guid GameId { get; set; }
            public string GameName { get; set; }
            public string License { get; set; }
            public string Method { get; set; }
            public DateTime Acquired { get; set; }
            public DateTime? PreviousAdded { get; set; }
        }

        public SteamAcquisitions(IPlayniteAPI api, string dataDir)
        {
            this.api = api;
            this.dataDir = dataDir;
        }

        private string FetchLicensesHtml()
        {
            string html = null;
            api.Dialogs.ActivateGlobalProgress(_ =>
            {
                using (var view = api.WebViews.CreateOffscreenView())
                {
                    view.NavigateAndWait(LicensesUrl);
                    html = view.GetPageSource();
                }
            }, new GlobalProgressOptions("Ledger: reading Steam licenses…", false) { IsIndeterminate = true });
            return html;
        }

        private void Login()
        {
            using (var view = api.WebViews.CreateView(1000, 800))
            {
                view.LoadingChanged += (s, e) =>
                {
                    if (!e.IsLoading && view.GetCurrentAddress().Contains("/account/licenses"))
                    {
                        view.Close();
                    }
                };
                view.Navigate(LoginUrl);
                view.OpenDialog();
            }
        }

        public void RunInteractive()
        {
            try
            {
                var html = FetchLicensesHtml();
                if (!LicenseParsing.LooksLikeLicensesPage(html))
                {
                    var r = api.Dialogs.ShowMessage(
                        "Playnite's browser isn't logged in to the Steam store.\n\nLog in now? The window closes by itself once you're in.",
                        "Ledger", MessageBoxButton.YesNo);
                    if (r != MessageBoxResult.Yes) return;
                    Login();
                    html = FetchLicensesHtml();
                    if (!LicenseParsing.LooksLikeLicensesPage(html))
                    {
                        api.Dialogs.ShowErrorMessage("Still couldn't read the licenses page after login.", "Ledger");
                        return;
                    }
                }

                var licenses = LicenseParsing.Parse(html);
                Directory.CreateDirectory(dataDir);
                File.WriteAllText(RawPath, Serialization.ToJson(licenses, true));

                var steamGames = api.Database.Games.Where(g => g.PluginId == SteamPluginId).ToList();
                var matches = LicenseParsing.Match(steamGames.Select(g => g.Name), licenses);

                var existing = LoadRecords().ToDictionary(r => r.GameId);
                var changed = 0;
                using (api.Database.BufferedUpdate())
                {
                    foreach (var g in steamGames)
                    {
                        if (!matches.TryGetValue(g.Name, out var lic)) continue;
                        // Keep the *original* pre-Ledger Added across repeated runs.
                        var prev = existing.TryGetValue(g.Id, out var old) ? old.PreviousAdded : g.Added;
                        existing[g.Id] = new Record { GameId = g.Id, GameName = g.Name, License = lic.Name, Method = lic.Method, Acquired = lic.Date, PreviousAdded = prev };
                        if (g.Added?.Date != lic.Date.Date)
                        {
                            g.Added = lic.Date;
                            api.Database.Games.Update(g);
                            changed++;
                        }
                    }
                }
                SaveRecords(existing.Values);

                var unmatched = steamGames.Count - steamGames.Count(g => matches.ContainsKey(g.Name));
                api.Dialogs.ShowMessage(
                    $"Steam licenses read: {licenses.Count}\nSteam games matched: {matches.Count} of {steamGames.Count}\n" +
                    $"Added dates changed: {changed}\nUnmatched: {unmatched} (kept as-is)\n\n" +
                    "Sort or group by \"Date added\" to use it. Ledger → Undo restores the old dates.",
                    "Ledger");
            }
            catch (Exception e)
            {
                logger.Error(e, "Steam acquisition import failed");
                api.Dialogs.ShowErrorMessage($"Import failed: {e.Message}", "Ledger");
            }
        }

        public void UndoInteractive()
        {
            var records = LoadRecords();
            if (records.Count == 0)
            {
                api.Dialogs.ShowMessage("Nothing to undo.", "Ledger");
                return;
            }

            var restored = 0;
            using (api.Database.BufferedUpdate())
            {
                foreach (var r in records)
                {
                    var g = api.Database.Games.Get(r.GameId);
                    if (g == null) continue;
                    g.Added = r.PreviousAdded;
                    api.Database.Games.Update(g);
                    restored++;
                }
            }
            File.Move(RecordPath, RecordPath + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".undone");
            api.Dialogs.ShowMessage($"Restored previous Added date on {restored} games.", "Ledger");
        }

        private List<Record> LoadRecords() =>
            File.Exists(RecordPath) ? Serialization.FromJsonFile<List<Record>>(RecordPath) ?? new List<Record>() : new List<Record>();

        private void SaveRecords(IEnumerable<Record> records) =>
            File.WriteAllText(RecordPath, Serialization.ToJson(records.OrderBy(r => r.Acquired).ToList(), true));
    }
}
