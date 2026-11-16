using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;

namespace Ledger
{
    /// <summary>
    /// Ledger: the history Playnite and the stores don't keep.
    ///  - Daily library snapshots (playtime per game, per day) as JSONL files.
    ///  - Real acquisition dates from Steam's licenses page, applied to Game.Added.
    /// </summary>
    public class Ledger : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string MenuSection = "@Ledger";

        private LedgerSettingsViewModel settings { get; set; }
        internal LedgerSettings Settings => settings.Settings;
        internal void SaveSettings() => SavePluginSettings(settings.Settings);
        private ActivityTracker tracker;
        internal Tracking Tracking { get; private set; }
        internal Snapshots Snapshots { get; }
        internal SteamAcquisitions SteamAcquisitions { get; }
        internal EpicAcquisitions EpicAcquisitions { get; }
        internal Money Money { get; }

        public override Guid Id { get; } = Guid.Parse("0ca0d44d-044a-4eb0-b6b8-863e05a6df89");

        public Ledger(IPlayniteAPI api) : base(api)
        {
            settings = new LedgerSettingsViewModel(this);
            Snapshots = new Snapshots(api, GetPluginUserDataPath());
            SteamAcquisitions = new SteamAcquisitions(api, GetPluginUserDataPath());
            EpicAcquisitions = new EpicAcquisitions(api, GetPluginUserDataPath());
            Money = new Money(api, GetPluginUserDataPath());
            Properties = new GenericPluginProperties { HasSettings = true };

            // playnite://ledger/<command>: lets scripts drive Ledger without clicking.
            //   render   - draw the Ledger page to <data>/render.png (visual check without a screen)
            //   snapshot - write today's snapshot now
            api.UriHandler.RegisterSource("ledger", a =>
            {
                var cmd = a.Arguments.Length > 0 ? a.Arguments[0] : "";
                if (cmd == "render") RenderToPng(a.Arguments.Length > 1 ? a.Arguments[1] : "Overview");
                else if (cmd == "snapshot") Snapshots.TryWriteToday(force: true);
                else if (cmd == "render-mods")
                {
                    var all = ModIndex.Build(PlayniteApi, GetPluginUserDataPath());
                    var gm = a.Arguments.Length > 1 ? all.FirstOrDefault(m => (m.Game?.Name ?? m.Label).IndexOf(Uri.UnescapeDataString(a.Arguments[1]), StringComparison.OrdinalIgnoreCase) >= 0) : all.FirstOrDefault();
                    if (gm != null)
                    {
                        ModsView.FetchTitlesNow(gm, GetPluginUserDataPath());
                        RenderElement(ModsView.BuildList(gm, out _), System.IO.Path.Combine(GetPluginUserDataPath(), "render-mods.png"), 900);
                    }
                }
                else if (cmd == "probe" && a.Arguments.Length > 1)
                {
                    // Dev aid: save what Playnite's logged-in browser sees at a URL (page text) to probe.txt.
                    var url = Uri.UnescapeDataString(a.Arguments[1]);
                    PlayniteApi.Dialogs.ActivateGlobalProgress(_ =>
                    {
                        using (var v = PlayniteApi.WebViews.CreateOffscreenView())
                        {
                            v.NavigateAndWait(url);
                            System.Threading.Thread.Sleep(1500);
                            System.IO.File.WriteAllText(System.IO.Path.Combine(GetPluginUserDataPath(), "probe.txt"),
                                v.GetCurrentAddress() + Environment.NewLine + Environment.NewLine + v.GetPageText());
                        }
                    }, new GlobalProgressOptions("Ledger: probing…", false) { IsIndeterminate = true });
                }
                else if (cmd == "tidy-names") new TidyNames(PlayniteApi, GetPluginUserDataPath()).Apply();
                else if (cmd == "hltb-fill") FillHltb(silent: true, all: a.Arguments.Length > 1 && a.Arguments[1] == "all");
                else if (cmd == "daily") System.Threading.Tasks.Task.Run(() => Tracking?.RunDailyIfDue(force: true));
                else if (cmd == "fetch-spend") Money.FetchSpend(silent: true);
                else if (cmd == "fetch-prices") FetchPrices();
                else if (cmd == "import-epic") EpicAcquisitions.Run(silent: true);
                else if (cmd == "import-steam") SteamAcquisitions.RunInteractive(silent: true);
            });
        }

        /// <summary>HowLongToBeat times for the backlog (never-played, visible, not tools) via the HLTB add-on.</summary>
        internal void FillHltb(bool silent = false, bool all = false)
        {
            var idx = HistoryIndex.Build(PlayniteApi, GetPluginUserDataPath());
            var games = idx.Games.Where(h => !h.Game.Hidden && !h.IsTool && (all || h.Game.Playtime == 0)).Select(h => h.Game).ToList();
            var tried = 0;
            PlayniteApi.Dialogs.ActivateGlobalProgress(p =>
            {
                tried = Hltb.Fill(PlayniteApi, games, (i, n) => { p.Text = $"Ledger: HowLongToBeat {i}/{n}…"; }, p.CancelToken);
            }, new GlobalProgressOptions("Ledger: HowLongToBeat…", true) { IsIndeterminate = true });
            var have = Hltb.ReadAll(PlayniteApi).Count;
            if (!silent) PlayniteApi.Dialogs.ShowMessage($"Asked HowLongToBeat about {tried} backlog games. Times known now: {have}.", "Ledger");
        }

        /// <summary>Render one Ledger tab (by name or index) to render-&lt;tab&gt;.png in the data dir.</summary>
        /// <summary>List prices for every Steam game in the library (owned + family), cached a week.</summary>
        internal int FetchPrices()
        {
            var ids = PlayniteApi.Database.Games.Where(g => g.PluginId == HistoryIndex.SteamPluginId && g.GameId != null).Select(g => g.GameId).ToList();
            var n = 0;
            PlayniteApi.Dialogs.ActivateGlobalProgress(_ => n = Money.FetchPrices(ids, settings.Settings.PriceRegion ?? "gb"),
                new GlobalProgressOptions("Ledger: Steam store prices…", false) { IsIndeterminate = true });
            logger.Info($"Ledger: fetched {n} Steam prices");
            return n;
        }

        internal string RenderToPng(string tab = "Overview", int width = 1400)
        {
            if (tab.Equals("full", StringComparison.OrdinalIgnoreCase))
            {
                // Whole control as the user sees it (header + tab strip), fixed window-ish size.
                var full = new LedgerView(this, PlayniteApi, sync: true) { Height = 1000 };
                var fp = System.IO.Path.Combine(GetPluginUserDataPath(), "render-full.png");
                RenderElement(full, fp, width);
                return fp;
            }
            var i = Array.FindIndex(LedgerView.Tabs, t => t.Equals(tab, StringComparison.OrdinalIgnoreCase));
            if (i < 0 && !int.TryParse(tab, out i)) i = 0;
            var view = new LedgerView(this, PlayniteApi, sync: true);
            var page = view.DetachPage(i);
            var path = System.IO.Path.Combine(GetPluginUserDataPath(), $"render-{LedgerView.Tabs[Math.Min(i, LedgerView.Tabs.Length - 1)].ToLowerInvariant()}.png");
            RenderElement(page, path, width);
            logger.Info($"Ledger render written: {path}");
            return path;
        }

        internal void RenderElement(System.Windows.UIElement content, string path, int width)
        {
            try
            {
                var host = new System.Windows.Controls.Border { Child = content, Width = width };
                host.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "WindowBackgourndBrush");
                host.Measure(new System.Windows.Size(width, double.PositiveInfinity));
                host.Arrange(new System.Windows.Rect(host.DesiredSize));
                host.UpdateLayout();
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(host);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var fs = System.IO.File.Create(path)) enc.Save(fs);
            }
            catch (Exception e) { logger.Error(e, "Ledger render failed"); }
        }


        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            Tracking = new Tracking(this, PlayniteApi, GetPluginUserDataPath());
            Tracking.Start();
            if (settings.Settings.TrackActivity)
            {
                tracker = new ActivityTracker(PlayniteApi, GetPluginUserDataPath());
                tracker.Start();
            }
            if (settings.Settings.DailySnapshots)
            {
                Snapshots.TryWriteToday(force: false);
            }
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            tracker?.Dispose();
            Tracking?.Dispose();
        }

        public override void OnGameInstalled(OnGameInstalledEventArgs args) => Tracking?.Installed(args.Game);
        public override void OnGameUninstalled(OnGameUninstalledEventArgs args) => Tracking?.Uninstalled(args.Game);

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            // Exact session for Playnite-launched games (the watcher skips games Playnite says are running).
            if (args.ElapsedSeconds >= 60)
            {
                var end = DateTime.Now;
                (tracker ?? new ActivityTracker(PlayniteApi, GetPluginUserDataPath()))
                    .Record(args.Game.Id, end.AddSeconds(-(double)args.ElapsedSeconds), end, "playnite");
            }
        }

        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args)
        {
            // A library update pulls fresh playtime from the stores; snapshot after it so
            // today's file reflects store data, not just what was cached at startup.
            if (settings.Settings.DailySnapshots)
            {
                Snapshots.TryWriteToday(force: true);
            }
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                MenuSection = MenuSection,
                Description = "Import Steam acquisition dates…",
                Action = _ => SteamAcquisitions.RunInteractive()
            };
            yield return new MainMenuItem
            {
                MenuSection = MenuSection,
                Description = "Import Epic acquisition dates",
                Action = _ => EpicAcquisitions.Run()
            };
            yield return new MainMenuItem
            {
                MenuSection = MenuSection,
                Description = "Undo acquisition dates (all stores)",
                Action = _ => SteamAcquisitions.UndoInteractive()
            };
            yield return new MainMenuItem
            {
                MenuSection = MenuSection,
                Description = "Fetch HowLongToBeat times for the backlog",
                Action = _ => FillHltb()
            };
            yield return new MainMenuItem
            {
                MenuSection = MenuSection,
                Description = "Tidy game names (fix ™/® mangled to ?)",
                Action = _ =>
                {
                    var done = new TidyNames(PlayniteApi, GetPluginUserDataPath()).Apply();
                    PlayniteApi.Dialogs.ShowMessage(done.Count == 0 ? "Nothing to tidy." :
                        string.Join(Environment.NewLine, done.Select(r => $"{r.From}  →  {r.To}")), "Ledger · Tidy names");
                }
            };
            yield return new MainMenuItem
            {
                MenuSection = MenuSection,
                Description = "Write library snapshot now",
                Action = _ =>
                {
                    var path = Snapshots.TryWriteToday(force: true);
                    PlayniteApi.Dialogs.ShowMessage(path != null ? $"Snapshot written:\n{path}" : "Snapshot failed, see playnite.log.", "Ledger");
                }
            };
            yield return new MainMenuItem
            {
                MenuSection = MenuSection,
                Description = "Open Ledger data folder",
                Action = _ => System.Diagnostics.Process.Start(GetPluginUserDataPath())
            };
        }

        public override IEnumerable<SidebarItem> GetSidebarItems()
        {
            yield return new SidebarItem
            {
                Title = "Ledger",
                Type = SiderbarItemType.View,
                Icon = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(GetType().Assembly.Location), "icon.png"),
                Opened = () => new LedgerView(this, PlayniteApi)
            };
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            if (args.Games.Count != 1) yield break;
            var game = args.Games[0];
            yield return new GameMenuItem
            {
                MenuSection = "Ledger",
                Description = "History…",
                Action = _ =>
                {
                    var h = HistoryIndex.Build(PlayniteApi, GetPluginUserDataPath()).For(game.Id);
                    PlayniteApi.Dialogs.ShowMessage(h == null ? "No data." : LedgerView.Tooltip(h), "Ledger");
                }
            };
            yield return new GameMenuItem
            {
                MenuSection = "Ledger",
                Description = "Mods…",
                Action = _ =>
                {
                    var gm = ModIndex.Build(PlayniteApi, GetPluginUserDataPath()).FirstOrDefault(m => m.Game?.Id == game.Id);
                    if (gm == null) PlayniteApi.Dialogs.ShowMessage($"No mods found for {game.Name} (Workshop, Vortex, r2modman).", "Ledger");
                    else ModsView.Show(PlayniteApi, gm, GetPluginUserDataPath());
                }
            };
        }

        public override ISettings GetSettings(bool firstRunSettings) => settings;

        public override UserControl GetSettingsView(bool firstRunSettings) => new LedgerSettingsView();
    }
}
