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
        private string RawPath => Path.Combine(dataDir, "steam-licenses.json");

        private readonly AcquisitionStore store;

        public SteamAcquisitions(IPlayniteAPI api, string dataDir)
        {
            this.api = api;
            this.dataDir = dataDir;
            store = new AcquisitionStore(api, dataDir);
        }

        /// <summary>
        /// All license pages concatenated. Steam paginates (100 per page) with a
        /// "license_paginator_next" link carrying a continuation token; follow it to the end.
        /// </summary>
        private string FetchLicensesHtml()
        {
            var all = new System.Text.StringBuilder();
            api.Dialogs.ActivateGlobalProgress(p =>
            {
                using (var view = api.WebViews.CreateOffscreenView())
                {
                    var url = LicensesUrl;
                    for (var page = 1; page <= 50 && url != null; page++)
                    {
                        p.Text = $"Ledger: reading Steam licenses, page {page}…";
                        view.NavigateAndWait(url);
                        var html = WaitForStableSource(view);
                        all.AppendLine(html);
                        url = LicenseParsing.NextPageUrl(html, LicensesUrl);
                    }
                }
            }, new GlobalProgressOptions("Ledger: reading Steam licenses…", false) { IsIndeterminate = true });
            return all.ToString();
        }

        private static string WaitForStableSource(IWebView view)
        {
            // NavigateAndWait can return before the table is complete; take the source once it stops growing.
            var html = view.GetPageSource() ?? "";
            for (var i = 0; i < 10; i++)
            {
                System.Threading.Thread.Sleep(700);
                var again = view.GetPageSource() ?? "";
                if (again.Length <= html.Length) break;
                html = again;
            }
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

        public void RunInteractive(bool silent = false)
        {
            try
            {
                var html = FetchLicensesHtml();
                if (!LicenseParsing.LooksLikeLicensesPage(html) && silent)
                {
                    logger.Warn("Ledger: Steam licenses page not available (not logged in?)");
                    return;
                }
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

                Directory.CreateDirectory(dataDir);
                File.WriteAllText(Path.Combine(dataDir, "steam-licenses.html"), html);
                var licenses = LicenseParsing.Parse(html);
                File.WriteAllText(RawPath, Serialization.ToJson(licenses, true));

                var steamGames = api.Database.Games.Where(g => g.PluginId == SteamPluginId).ToList();
                var matches = LicenseParsing.Match(steamGames.Select(g => g.Name), licenses);

                var changed = store.Apply("Steam", steamGames, matches);
                var unmatched = steamGames.Count - steamGames.Count(g => matches.ContainsKey(g.Name));
                var summary = $"Steam licenses read: {licenses.Count}" + Environment.NewLine +
                              $"Steam games matched: {matches.Count} of {steamGames.Count}" + Environment.NewLine +
                              $"Added dates changed: {changed}" + Environment.NewLine +
                              $"Unmatched: {unmatched} (kept as-is)";
                logger.Info("Ledger import (Steam): " + summary.Replace(Environment.NewLine, "; "));
                if (!silent)
                {
                    api.Dialogs.ShowMessage(summary + Environment.NewLine + Environment.NewLine +
                        "Sort or group by \"Date added\" to use it. Ledger → Undo restores the old dates.", "Ledger");
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Steam acquisition import failed");
                api.Dialogs.ShowErrorMessage($"Import failed: {e.Message}", "Ledger");
            }
        }

        public void UndoInteractive()
        {
            var n = store.Undo();
            api.Dialogs.ShowMessage(n == 0 ? "Nothing to undo." : $"Restored previous Added date on {n} games (all stores).", "Ledger");
        }
    }
}
