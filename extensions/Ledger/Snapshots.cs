using Playnite.SDK;
using Playnite.SDK.Data;
using System;
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
                var path = Path.Combine(Dir, DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl");
                if (File.Exists(path) && !force)
                {
                    return null;
                }

                var sb = new StringBuilder();
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
                        completion = g.CompletionStatus?.Name
                    };
                    sb.AppendLine(Serialization.ToJson(row, false));
                }

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
