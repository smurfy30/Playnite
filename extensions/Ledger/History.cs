using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ledger
{
    /// <summary>Everything Ledger knows about one game's timeline.</summary>
    internal class GameHistory
    {
        public Game Game { get; set; }
        public DateTime? Acquired { get; set; }
        public string AcquiredVia { get; set; }
        /// <summary>Earliest achievement unlock: a lower bound on when play actually started.</summary>
        public DateTime? FirstPlayed { get; set; }
        public int Unlocks { get; set; }
        public DateTime? LastPlayed => Game.LastActivity;
        public HltbTimes Hltb { get; set; }
        /// <summary>"D:" etc. from the install directory, or null.</summary>
        public string Drive => string.IsNullOrEmpty(Game.InstallDirectory) ? null
            : System.IO.Path.GetPathRoot(Game.InstallDirectory)?.TrimEnd('\\', '/').ToUpperInvariant();
        public bool Played => Game.Playtime > 0 || FirstPlayed.HasValue;
        /// <summary>Servers, SDKs, mod tools, soundtracks, playtests: things that aren't games to play.</summary>
        public bool IsTool => ToolRx.IsMatch(Game.Name ?? "");
        private static readonly System.Text.RegularExpressions.Regex ToolRx = new System.Text.RegularExpressions.Regex(
            @"\b(dedicated server|server|mod tools|modding tools|sdk|soundtrack|ost|editor|benchmark|playtest|test server|public test|pts|demo|prologue)\b|\(demo\)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        public double Hours => Game.Playtime / 3600.0;
        /// <summary>Days between getting it and first playing it (null if either is unknown).</summary>
        public int? DaysToFirstPlay => Acquired.HasValue && FirstPlayed.HasValue
            ? (int?)Math.Max(0, (FirstPlayed.Value.Date - Acquired.Value.Date).Days) : null;
    }

    /// <summary>
    /// Builds GameHistory for the whole library from Ledger's own data plus the Steam local cache.
    /// Cheap enough to rebuild on every view open (a few hundred small files); nothing is cached.
    /// </summary>
    internal class HistoryIndex
    {
        public static readonly Guid SteamPluginId = Guid.Parse("cb91dfc9-b977-43bf-8e70-55f46e410fab");

        public List<GameHistory> Games { get; } = new List<GameHistory>();
        public DateTime BuiltAt { get; } = DateTime.Now;

        public static HistoryIndex Build(IPlayniteAPI api, string ledgerDataDir)
        {
            var idx = new HistoryIndex();
            var acq = LoadAcquisitions(ledgerDataDir);
            var unlocks = SteamLocalStats.Load();
            var hltb = global::Ledger.Hltb.ReadAll(api);

            foreach (var g in api.Database.Games)
            {
                var h = new GameHistory { Game = g, Hltb = hltb.TryGetValue(g.Id, out var t) ? t : null };
                if (acq.TryGetValue(g.Id, out var a))
                {
                    h.Acquired = a.Acquired;
                    h.AcquiredVia = a.Method;
                }
                if (g.PluginId == SteamPluginId && g.GameId != null && unlocks.TryGetValue(g.GameId, out var u))
                {
                    h.FirstPlayed = u.First;
                    h.Unlocks = u.Count;
                }
                idx.Games.Add(h);
            }
            return idx;
        }

        public GameHistory For(Guid gameId) => Games.FirstOrDefault(h => h.Game.Id == gameId);

        private static Dictionary<Guid, AcquisitionStore.Record> LoadAcquisitions(string dir)
        {
            var path = Path.Combine(dir, "acquisitions.json");
            if (!File.Exists(path)) return new Dictionary<Guid, AcquisitionStore.Record>();
            try
            {
                return (Serialization.FromJsonFile<List<AcquisitionStore.Record>>(path) ?? new List<AcquisitionStore.Record>())
                    .GroupBy(r => r.GameId).ToDictionary(g => g.Key, g => g.First());
            }
            catch (Exception e)
            {
                LogManager.GetLogger().Warn($"Ledger: bad acquisitions.json: {e.Message}");
                return new Dictionary<Guid, AcquisitionStore.Record>();
            }
        }
    }
}
