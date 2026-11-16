using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
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

        public override ISettings GetSettings(bool firstRunSettings) => settings;

        public override UserControl GetSettingsView(bool firstRunSettings) => new LedgerSettingsView();
    }
}
