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
        internal Snapshots Snapshots { get; }
        internal SteamAcquisitions SteamAcquisitions { get; }

        public override Guid Id { get; } = Guid.Parse("0ca0d44d-044a-4eb0-b6b8-863e05a6df89");

        public Ledger(IPlayniteAPI api) : base(api)
        {
            settings = new LedgerSettingsViewModel(this);
            Snapshots = new Snapshots(api, GetPluginUserDataPath());
            SteamAcquisitions = new SteamAcquisitions(api, GetPluginUserDataPath());
            Properties = new GenericPluginProperties { HasSettings = true };

            // playnite://ledger/<command>: lets scripts drive Ledger without clicking.
            //   render   - draw the Ledger page to <data>/render.png (visual check without a screen)
            //   snapshot - write today's snapshot now
            api.UriHandler.RegisterSource("ledger", a =>
            {
                var cmd = a.Arguments.Length > 0 ? a.Arguments[0] : "";
                if (cmd == "render") RenderToPng();
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
                else if (cmd == "import-steam") SteamAcquisitions.RunInteractive(silent: true);
            });
        }

        internal string RenderToPng(int width = 1400)
        {
            var path = System.IO.Path.Combine(GetPluginUserDataPath(), "render.png");
            try
            {
                var view = new LedgerView(this, PlayniteApi);
                var host = new System.Windows.Controls.Border { Child = view, Width = width };
                host.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "WindowBackgourndBrush");
                // Unconstrained height so the whole page renders, not just one screenful.
                view.Content = ((System.Windows.Controls.ScrollViewer)view.Content).Content is System.Windows.UIElement inner
                    ? DetachFromScroll(view) : view.Content;
                host.Measure(new System.Windows.Size(width, double.PositiveInfinity));
                host.Arrange(new System.Windows.Rect(host.DesiredSize));
                host.UpdateLayout();
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)host.ActualWidth, (int)host.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(host);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var fs = System.IO.File.Create(path)) enc.Save(fs);
                logger.Info($"Ledger render written: {path}");
                return path;
            }
            catch (Exception e)
            {
                logger.Error(e, "Ledger render failed");
                return null;
            }
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

        private static object DetachFromScroll(LedgerView view)
        {
            var sv = (System.Windows.Controls.ScrollViewer)view.Content;
            var inner = sv.Content;
            sv.Content = null;
            return inner;
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            if (settings.Settings.DailySnapshots)
            {
                Snapshots.TryWriteToday(force: false);
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
                Description = "Undo Steam acquisition dates",
                Action = _ => SteamAcquisitions.UndoInteractive()
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
