using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ledger
{
    /// <summary>
    /// acquisitions.json: one record per game Ledger has dated, with the Added value it replaced.
    /// Shared by every store importer, so Undo restores everything regardless of where it came from.
    /// </summary>
    internal class AcquisitionStore
    {
        public class Record
        {
            public Guid GameId { get; set; }
            public string GameName { get; set; }
            public string Store { get; set; }
            public string License { get; set; }
            public string Method { get; set; }
            public DateTime Acquired { get; set; }
            public DateTime? PreviousAdded { get; set; }
        }

        private readonly IPlayniteAPI api;
        private readonly string dataDir;
        public string RecordPath => Path.Combine(dataDir, "acquisitions.json");

        public AcquisitionStore(IPlayniteAPI api, string dataDir)
        {
            this.api = api;
            this.dataDir = dataDir;
        }

        public List<Record> Load()
        {
            if (!File.Exists(RecordPath)) return new List<Record>();
            try { return Serialization.FromJsonFile<List<Record>>(RecordPath) ?? new List<Record>(); }
            catch (Exception e)
            {
                LogManager.GetLogger().Warn($"Ledger: bad acquisitions.json: {e.Message}");
                return new List<Record>();
            }
        }

        private void Save(IEnumerable<Record> records)
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(RecordPath, Serialization.ToJson(records.OrderBy(r => r.Acquired).ToList(), true));
        }

        /// <summary>Set Game.Added from matches (game name → license); returns how many Added values changed.</summary>
        public int Apply(string store, List<Game> games, Dictionary<string, SteamLicense> matches)
        {
            var existing = Load().GroupBy(r => r.GameId).ToDictionary(g => g.Key, g => g.First());
            var changed = 0;
            using (api.Database.BufferedUpdate())
            {
                foreach (var g in games)
                {
                    if (!matches.TryGetValue(g.Name, out var lic)) continue;
                    // Keep the *original* pre-Ledger Added across repeated runs.
                    var prev = existing.TryGetValue(g.Id, out var old) ? old.PreviousAdded : g.Added;
                    existing[g.Id] = new Record
                    {
                        GameId = g.Id, GameName = g.Name, Store = store, License = lic.Name,
                        Method = lic.Method, Acquired = lic.Date, PreviousAdded = prev
                    };
                    if (g.Added?.Date != lic.Date.Date)
                    {
                        g.Added = lic.Date;
                        api.Database.Games.Update(g);
                        changed++;
                    }
                }
            }
            Save(existing.Values);
            return changed;
        }

        /// <summary>Restore every recorded game's previous Added. Returns count restored.</summary>
        public int Undo()
        {
            var records = Load();
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
            if (File.Exists(RecordPath))
            {
                File.Move(RecordPath, RecordPath + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".undone");
            }
            return restored;
        }
    }
}
