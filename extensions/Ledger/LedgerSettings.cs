using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ledger
{
    public class LedgerSettings : ObservableObject
    {
        private bool dailySnapshots = true;
        private bool trackActivity = true;
        private int staleDays = 180;
        private bool includeFamilySharing = false;
        private string priceRegion = "gb";
        private string period = "All time";
        private string store = "All stores";
        private bool askForVerdict = true;
        private string genre = "All genres";
        private string installState = "Any";
        private string playState = "Any";
        private string search = "";

        /// <summary>Write snapshots/yyyy-MM-dd.jsonl on startup and after each library update.</summary>
        public bool DailySnapshots { get => dailySnapshots; set => SetValue(ref dailySnapshots, value); }
        /// <summary>Background process watcher: records play sessions for games launched outside Playnite.</summary>
        public bool TrackActivity { get => trackActivity; set => SetValue(ref trackActivity, value); }
        /// <summary>"Stale install" threshold.</summary>
        public int StaleDays { get => staleDays; set => SetValue(ref staleDays, Math.Max(7, value)); }
        /// <summary>Count Steam Family Sharing games in stats (they aren't yours).</summary>
        public bool IncludeFamilySharing { get => includeFamilySharing; set => SetValue(ref includeFamilySharing, value); }
        /// <summary>Steam store country code for prices (gb, us, de...).</summary>
        public string PriceRegion { get => priceRegion; set => SetValue(ref priceRegion, value); }
        /// <summary>Last-used filters on the Ledger page (remembered between sessions).</summary>
        public string Period { get => period; set => SetValue(ref period, value); }
        public string Store { get => store; set => SetValue(ref store, value); }
        /// <summary>Ask for a 1–10 rating + one-line note when a game is marked Beaten/Completed/Abandoned.</summary>
        public bool AskForVerdict { get => askForVerdict; set => SetValue(ref askForVerdict, value); }
        public string Genre { get => genre; set => SetValue(ref genre, value); }
        public string InstallState { get => installState; set => SetValue(ref installState, value); }
        public string PlayState { get => playState; set => SetValue(ref playState, value); }
        public string Search { get => search; set => SetValue(ref search, value); }
    }

    public class LedgerSettingsViewModel : ObservableObject, ISettings
    {
        private readonly Ledger plugin;
        private LedgerSettings editingClone { get; set; }

        private LedgerSettings settings;
        public LedgerSettings Settings
        {
            get => settings;
            set
            {
                settings = value;
                OnPropertyChanged();
            }
        }

        public LedgerSettingsViewModel(Ledger plugin)
        {
            // Injecting your plugin instance is required for Save/Load method because Playnite saves data to a location based on what plugin requested the operation.
            this.plugin = plugin;

            // Load saved settings.
            var savedSettings = plugin.LoadPluginSettings<LedgerSettings>();

            // LoadPluginSettings returns null if no saved data is available.
            if (savedSettings != null)
            {
                Settings = savedSettings;
            }
            else
            {
                Settings = new LedgerSettings();
            }
        }

        public void BeginEdit()
        {
            // Code executed when settings view is opened and user starts editing values.
            editingClone = Serialization.GetClone(Settings);
        }

        public void CancelEdit()
        {
            // Code executed when user decides to cancel any changes made since BeginEdit was called.
            // This method should revert any changes made to Option1 and Option2.
            Settings = editingClone;
        }

        public void EndEdit()
        {
            // Code executed when user decides to confirm changes made since BeginEdit was called.
            // This method should save settings made to Option1 and Option2.
            plugin.SavePluginSettings(Settings);
        }

        public bool VerifySettings(out List<string> errors)
        {
            // Code execute when user decides to confirm changes made since BeginEdit was called.
            // Executed before EndEdit is called and EndEdit is not called if false is returned.
            // List of errors is presented to user if verification fails.
            errors = new List<string>();
            return true;
        }
    }
}