using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Ledger
{
    internal class Session
    {
        public Guid GameId { get; set; }
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public double Seconds => (End - Start).TotalSeconds;
        public string Source { get; set; }   // "watcher" | "playnite"
    }

    /// <summary>
    /// Lightweight play tracker. Every minute, matches running processes' exe paths against installed
    /// games' install folders. Catches games launched outside Playnite (Steam, shortcuts) that
    /// Playnite/GameActivity never see. Sessions are appended to sessions.jsonl when they end.
    /// Cost: one process list + QueryFullProcessImageName per process per minute (~ms).
    /// </summary>
    internal class ActivityTracker : IDisposable
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly IPlayniteAPI api;
        private readonly string path;
        private Timer timer;
        private List<KeyValuePair<string, Guid>> dirs = new List<KeyValuePair<string, Guid>>();
        private DateTime dirsBuilt = DateTime.MinValue;
        private readonly Dictionary<Guid, DateTime> open = new Dictionary<Guid, DateTime>();
        private readonly Dictionary<Guid, DateTime> lastSeen = new Dictionary<Guid, DateTime>();
        private static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);

        public ActivityTracker(IPlayniteAPI api, string dataDir)
        {
            this.api = api;
            path = Path.Combine(dataDir, "sessions.jsonl");
        }

        public void Start() => timer = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(20), Tick);

        public void Dispose()
        {
            timer?.Dispose();
            // Close anything still open so a Playnite exit doesn't lose the session.
            lock (open) { foreach (var id in open.Keys.ToList()) Close(id, lastSeen.TryGetValue(id, out var t) ? t : DateTime.Now); }
        }

        /// <summary>Playnite's own start/stop events (exact times), recorded through the same file.</summary>
        public void Record(Guid gameId, DateTime start, DateTime end, string source) =>
            Append(new Session { GameId = gameId, Start = start, End = end, Source = source });

        private void RebuildDirs()
        {
            // Longest folders first so "Steam\common\Foo 2" beats "Steam\common\Foo".
            dirs = api.Database.Games
                .Where(g => g.IsInstalled && !string.IsNullOrEmpty(g.InstallDirectory) && g.InstallDirectory.Length > 8)
                .Select(g => new KeyValuePair<string, Guid>(Path.GetFullPath(g.InstallDirectory).TrimEnd('\\') + "\\", g.Id))
                .Where(kv => !IsLauncherRoot(kv.Key))
                .OrderByDescending(kv => kv.Key.Length).ToList();
            if (dirsBuilt == DateTime.MinValue) logger.Info($"Ledger tracker watching {dirs.Count} install folders");
            dirsBuilt = DateTime.Now;
        }

        // Folders that contain whole launchers/libraries rather than one game.
        private static bool IsLauncherRoot(string dir) =>
            dir.EndsWith("\\steamapps\\common\\", StringComparison.OrdinalIgnoreCase) || dir.EndsWith("\\Epic Games\\", StringComparison.OrdinalIgnoreCase);

        private void Poll()
        {
            try
            {
                if (DateTime.Now - dirsBuilt > TimeSpan.FromMinutes(10)) RebuildDirs();
                var now = DateTime.Now;
                var running = new HashSet<Guid>();
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        var exe = ImagePath(p);
                        if (exe == null) continue;
                        foreach (var kv in dirs)
                        {
                            if (exe.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase)) { running.Add(kv.Value); break; }
                        }
                    }
                    catch { }
                    finally { p.Dispose(); }
                }

                lock (open)
                {
                    foreach (var id in running)
                    {
                        // Playnite-launched games are recorded exactly via OnGameStopped; don't double count.
                        if (api.Database.Games.Get(id)?.IsRunning == true) continue;
                        if (!open.ContainsKey(id)) open[id] = now;
                        lastSeen[id] = now;
                    }
                    foreach (var id in open.Keys.Where(k => !running.Contains(k)).ToList())
                    {
                        Close(id, lastSeen.TryGetValue(id, out var t) ? t : now);
                    }
                }
            }
            catch (Exception e)
            {
                logger.Warn($"Ledger tracker poll failed: {e.Message}");
            }
        }

        private void Close(Guid id, DateTime end)
        {
            var start = open[id];
            open.Remove(id);
            // Sessions under a minute are probably launcher/updater blips.
            if ((end - start).TotalSeconds >= 60) Append(new Session { GameId = id, Start = start, End = end, Source = "watcher" });
        }

        private readonly object fileLock = new object();
        private void Append(Session s)
        {
            lock (fileLock)
            {
                try { File.AppendAllText(path, Serialization.ToJson(s, false) + Environment.NewLine, new UTF8Encoding(false)); }
                catch (Exception e) { logger.Warn($"Ledger: couldn't write session: {e.Message}"); }
            }
        }

        public static List<Session> Load(string dataDir)
        {
            var p = Path.Combine(dataDir, "sessions.jsonl");
            var list = new List<Session>();
            if (!File.Exists(p)) return list;
            foreach (var line in File.ReadAllLines(p))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { list.Add(Serialization.FromJson<Session>(line)); } catch { }
            }
            return list;
        }

        // QueryFullProcessImageName works for most processes without elevation (unlike MainModule).
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(int access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        private static string ImagePath(Process p)
        {
            var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                var size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally { CloseHandle(h); }
        }
    }

    /// <summary>
    /// Hours per game per day from every source we have, without double counting:
    /// day hours = max(sessions that day [Ledger + GameActivity], playtime increase between snapshots).
    /// Snapshot deltas catch play we didn't see (another PC, Steam Deck); sessions give exact days.
    /// </summary>
    internal class ActivityIndex
    {
        public Dictionary<Guid, Dictionary<DateTime, double>> Daily { get; } = new Dictionary<Guid, Dictionary<DateTime, double>>();
        public DateTime? TrackedSince { get; private set; }

        public static ActivityIndex Build(IPlayniteAPI api, string ledgerDir)
        {
            var idx = new ActivityIndex();
            var sessions = new Dictionary<Guid, Dictionary<DateTime, double>>();
            void AddSession(Guid id, DateTime day, double hours)
            {
                if (!sessions.TryGetValue(id, out var d)) sessions[id] = d = new Dictionary<DateTime, double>();
                d[day.Date] = (d.TryGetValue(day.Date, out var v) ? v : 0) + hours;
            }

            foreach (var s in ActivityTracker.Load(ledgerDir)) AddSession(s.GameId, s.Start, s.Seconds / 3600);
            foreach (var kv in GameActivitySessions(api)) foreach (var s in kv.Value) AddSession(kv.Key, s.Key, s.Value);

            // Snapshot deltas.
            var deltas = new Dictionary<Guid, Dictionary<DateTime, double>>();
            var snapDir = Path.Combine(ledgerDir, "snapshots");
            if (Directory.Exists(snapDir))
            {
                Dictionary<Guid, ulong> prev = null;
                foreach (var f in Snapshots.Ordered(snapDir))
                {
                    if (!DateTime.TryParse(Path.GetFileName(f).Substring(0, 10), out var day)) continue;
                    idx.TrackedSince = idx.TrackedSince ?? day;
                    // Delta files only hold changed rows: start from yesterday's state.
                    var isDelta = f.EndsWith(".d.jsonl");
                    var cur = isDelta && prev != null ? new Dictionary<Guid, ulong>(prev) : new Dictionary<Guid, ulong>();
                    foreach (var line in File.ReadLines(f))
                    {
                        var id = Field(line, "\"id\":\"", "\"");
                        var pt = Field(line, "\"playtimeSec\":", ",");
                        if (Guid.TryParse(id, out var gid) && ulong.TryParse(pt, out var sec)) cur[gid] = sec;
                    }
                    if (prev != null)
                    {
                        foreach (var kv in cur)
                        {
                            var before = prev.TryGetValue(kv.Key, out var b) ? b : 0;
                            if (kv.Value > before)
                            {
                                if (!deltas.TryGetValue(kv.Key, out var d)) deltas[kv.Key] = d = new Dictionary<DateTime, double>();
                                // Playtime that appeared by this snapshot was mostly played the day before
                                // (stores sync on next library update), so book it there.
                                d[day.AddDays(-1)] = (kv.Value - before) / 3600.0;
                            }
                        }
                    }
                    prev = cur;
                }
            }

            foreach (var id in sessions.Keys.Union(deltas.Keys))
            {
                var days = new Dictionary<DateTime, double>();
                sessions.TryGetValue(id, out var s);
                deltas.TryGetValue(id, out var dl);
                foreach (var day in (s?.Keys ?? Enumerable.Empty<DateTime>()).Union(dl?.Keys ?? Enumerable.Empty<DateTime>()))
                {
                    var a = s != null && s.TryGetValue(day, out var x) ? x : 0;
                    var b = dl != null && dl.TryGetValue(day, out var y) ? y : 0;
                    days[day] = Math.Max(a, b);
                }
                idx.Daily[id] = days;
            }
            var firstSession = sessions.Values.SelectMany(d => d.Keys).DefaultIfEmpty(DateTime.MaxValue).Min();
            if (firstSession < (idx.TrackedSince ?? DateTime.MaxValue)) idx.TrackedSince = firstSession;
            return idx;
        }

        public double HoursIn(Guid id, DateTime from, DateTime to) =>
            Daily.TryGetValue(id, out var d) ? d.Where(kv => kv.Key >= from.Date && kv.Key < to).Sum(kv => kv.Value) : 0;

        public Dictionary<DateTime, double> TotalByDay() =>
            Daily.Values.SelectMany(d => d).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Sum(x => x.Value));

        private static string Field(string line, string start, string end)
        {
            var i = line.IndexOf(start, StringComparison.Ordinal);
            if (i < 0) return null;
            i += start.Length;
            var j = line.IndexOf(end, i, StringComparison.Ordinal);
            return j < 0 ? null : line.Substring(i, j - i);
        }

        private class GaItem { public DateTime DateSession { get; set; } public ulong ElapsedSeconds { get; set; } }
        private class GaFile { public List<GaItem> Items { get; set; } }

        private static Dictionary<Guid, Dictionary<DateTime, double>> GameActivitySessions(IPlayniteAPI api)
        {
            var result = new Dictionary<Guid, Dictionary<DateTime, double>>();
            var dir = Path.Combine(api.Paths.ExtensionsDataPath, "afbb1a0d-04a1-4d0c-9afa-c6e42ca855b4", "GameActivity");
            if (!Directory.Exists(dir)) return result;
            foreach (var f in Directory.GetFiles(dir, "*.json"))
            {
                if (!Guid.TryParse(Path.GetFileNameWithoutExtension(f), out var id)) continue;
                try
                {
                    var items = Serialization.FromJsonFile<GaFile>(f)?.Items;
                    if (items == null || items.Count == 0) continue;
                    result[id] = items.GroupBy(i => i.DateSession.ToLocalTime().Date).ToDictionary(g => g.Key, g => g.Sum(i => (double)i.ElapsedSeconds) / 3600.0);
                }
                catch { }
            }
            return result;
        }
    }
}
