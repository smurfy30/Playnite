using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Ledger
{
    /// <summary>
    /// One JSONL file per day: snapshots/yyyy-MM-dd.jsonl, one line per game.
    /// Totals and per-day deltas are computed from the files when read; nothing
    /// aggregated is ever stored ("a count is a query").
    /// </summary>
    internal class Snapshots
    {
        /// <summary>Row without its takenAt, for change detection.</summary>
        private static string Body(string line)
        {
            var i = line.IndexOf(",\"id\":", StringComparison.Ordinal);
            return i < 0 ? line : line.Substring(i);
        }

        /// <summary>
        /// Full state as of the newest snapshot (excluding a given day): last full file, then each
        /// later delta overlaid in date order. id → raw JSON line. Null if there are no snapshots.
        /// </summary>
        public static Dictionary<string, string> ReconstructLatest(string dir, string exclude = null)
        {
            Dictionary<string, string> state = null;
            foreach (var f in Ordered(dir))
            {
                var name = Path.GetFileName(f);
                if (exclude != null && name.StartsWith(exclude)) continue;
                if (!name.EndsWith(".d.jsonl")) state = new Dictionary<string, string>();
                if (state == null) continue; // delta before any full: ignore
                foreach (var line in File.ReadLines(f))
                {
                    var i = line.IndexOf("\"id\":\"", StringComparison.Ordinal);
                    if (i < 0) continue;
                    var id = line.Substring(i + 6, 36);
                    state[id] = line;
                }
            }
            return state;
        }

        /// <summary>Snapshot files in date order; a full and a delta never share a day.</summary>
        public static IEnumerable<string> Ordered(string dir) =>
            Directory.Exists(dir) ? Directory.GetFiles(dir, "*.jsonl").OrderBy(f => Path.GetFileName(f).Substring(0, 10)) : Enumerable.Empty<string>();

        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly IPlayniteAPI api;
        public string Dir { get; }

        public Snapshots(IPlayniteAPI api, string pluginDataDir)
        {
            this.api = api;
            Dir = Path.Combine(pluginDataDir, "snapshots");
        }

        /// <returns>Path written, or null if skipped (already exists and !force) or failed.</returns>
        public string TryWriteToday(bool force)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var day = DateTime.Now.ToString("yyyy-MM-dd");
                var fullPath = Path.Combine(Dir, day + ".jsonl");
                var deltaPath = Path.Combine(Dir, day + ".d.jsonl");
                if ((File.Exists(fullPath) || File.Exists(deltaPath)) && !force)
                {
                    return null;
                }

                var rows = new List<KeyValuePair<string, string>>();
                var takenAt = DateTime.Now.ToString("o");
                foreach (var g in api.Database.Games.OrderBy(g => g.Name))
                {
                    var row = new
                    {
                        takenAt,
                        id = g.Id,
                        name = g.Name,
                        source = g.Source?.Name,
                        pluginId = g.PluginId,
                        gameId = g.GameId,
                        playtimeSec = g.Playtime,
                        playCount = g.PlayCount,
                        lastActivity = g.LastActivity,
                        added = g.Added,
                        installed = g.IsInstalled,
                        hidden = g.Hidden,
                        completion = g.CompletionStatus?.Name,
                        // metadata coverage, so "how many games lack covers" is a query too
                        hasCover = !string.IsNullOrEmpty(g.CoverImage),
                        hasBackground = !string.IsNullOrEmpty(g.BackgroundImage),
                        hasIcon = !string.IsNullOrEmpty(g.Icon),
                        hasDescription = !string.IsNullOrEmpty(g.Description),
                        genres = g.Genres?.Select(x => x.Name).ToList(),
                        releaseYear = g.ReleaseYear,
                        installSize = g.InstallSize
                    };
                    rows.Add(new KeyValuePair<string, string>(g.Id.ToString(), Serialization.ToJson(row, false)));
                }

                // Compaction: full snapshot on Mondays or if none in the last 7 days; otherwise only
                // rows that changed since the reconstructed previous state (readers overlay deltas).
                var prev = ReconstructLatest(Dir, exclude: day);
                var lastFull = Directory.GetFiles(Dir, "????-??-??.jsonl").Select(f => Path.GetFileName(f).Substring(0, 10)).Where(d => string.CompareOrdinal(d, day) < 0).OrderBy(d => d).LastOrDefault();
                var needFull = prev == null || DateTime.Now.DayOfWeek == DayOfWeek.Monday || lastFull == null ||
                               (DateTime.Now - DateTime.Parse(lastFull)).TotalDays >= 7;
                var sb = new StringBuilder();
                foreach (var r in rows)
                {
                    if (needFull || !prev.TryGetValue(r.Key, out var old) || Body(old) != Body(r.Value)) sb.AppendLine(r.Value);
                }
                var path = needFull ? fullPath : deltaPath;
                foreach (var stale in new[] { fullPath, deltaPath }.Where(x => x != path && File.Exists(x))) File.Delete(stale);

                // Write-then-move so a crash never leaves a half file that looks complete.
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                logger.Info($"Ledger snapshot written: {path}");
                return path;
            }
            catch (Exception e)
            {
                logger.Error(e, "Ledger snapshot failed");
                return null;
            }
        }
    }
}
