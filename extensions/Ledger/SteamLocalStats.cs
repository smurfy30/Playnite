using Microsoft.Win32;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ledger
{
    internal class UnlockSpan
    {
        public DateTime First { get; set; }
        public DateTime Last { get; set; }
        public int Count { get; set; }
    }

    /// <summary>
    /// Achievement unlock times from the Steam client's own cache:
    /// appcache/stats/UserGameStats_{accountId}_{appId}.bin (binary KeyValues). No key, no network.
    /// The earliest unlock is a good "first played" lower bound for games with achievements.
    /// </summary>
    internal static class SteamLocalStats
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const long SteamId64Base = 76561197960265728;

        public static string SteamDir()
        {
            var path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            return string.IsNullOrEmpty(path) ? @"C:\Program Files (x86)\Steam" : path.Replace('/', '\\');
        }

        /// <summary>Account ids of users who logged in on this PC, most recent first.</summary>
        public static List<long> AccountIds(string steamDir)
        {
            var result = new List<long>();
            var file = Path.Combine(steamDir, "config", "loginusers.vdf");
            if (!File.Exists(file)) return result;
            var text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, "\"(7656\\d{13})\"\\s*\\{(?<body>[^}]*)\\}"))
            {
                var id = long.Parse(m.Groups[1].Value) - SteamId64Base;
                if (Regex.IsMatch(m.Groups["body"].Value, "\"MostRecent\"\\s*\"1\"")) result.Insert(0, id);
                else result.Add(id);
            }
            return result;
        }

        /// <summary>appId → unlock span, for every game with at least one timed unlock.</summary>
        public static Dictionary<string, UnlockSpan> Load()
        {
            var result = new Dictionary<string, UnlockSpan>();
            try
            {
                var dir = SteamDir();
                var stats = Path.Combine(dir, "appcache", "stats");
                if (!Directory.Exists(stats)) return result;
                foreach (var account in AccountIds(dir))
                {
                    var prefix = $"UserGameStats_{account}_";
                    foreach (var file in Directory.EnumerateFiles(stats, prefix + "*.bin"))
                    {
                        var appId = Path.GetFileNameWithoutExtension(file).Substring(prefix.Length);
                        if (result.ContainsKey(appId)) continue; // most recent account wins
                        try
                        {
                            var times = new List<int>();
                            Collect(ReadKv(File.ReadAllBytes(file)), times);
                            if (times.Count == 0) continue;
                            result[appId] = new UnlockSpan
                            {
                                First = FromUnix(times.Min()),
                                Last = FromUnix(times.Max()),
                                Count = times.Count
                            };
                        }
                        catch (Exception e)
                        {
                            logger.Warn($"Ledger: couldn't parse {file}: {e.Message}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Ledger: reading Steam local stats failed");
            }
            return result;
        }

        private static DateTime FromUnix(int t) => DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime;

        private static void Collect(Dictionary<string, object> node, List<int> times)
        {
            foreach (var kv in node)
            {
                if (kv.Value is Dictionary<string, object> child)
                {
                    if (kv.Key == "AchievementTimes")
                    {
                        times.AddRange(child.Values.OfType<int>().Where(t => t > 1000000000));
                    }
                    else
                    {
                        Collect(child, times);
                    }
                }
            }
        }

        internal static Dictionary<string, object> ReadKv(byte[] buf)
        {
            var i = 0;
            return ReadObject(buf, ref i);
        }

        private static Dictionary<string, object> ReadObject(byte[] buf, ref int i)
        {
            var obj = new Dictionary<string, object>();
            while (i < buf.Length)
            {
                var type = buf[i++];
                if (type == 8) return obj;
                var key = ReadCString(buf, ref i);
                switch (type)
                {
                    case 0: obj[key] = ReadObject(buf, ref i); break;
                    case 1: obj[key] = ReadCString(buf, ref i); break;
                    case 2: case 4: case 6: obj[key] = BitConverter.ToInt32(buf, i); i += 4; break;
                    case 3: obj[key] = BitConverter.ToSingle(buf, i); i += 4; break;
                    case 7: case 10: obj[key] = BitConverter.ToInt64(buf, i); i += 8; break;
                    default: throw new InvalidDataException($"kv type {type} at {i}");
                }
            }
            return obj;
        }

        private static string ReadCString(byte[] buf, ref int i)
        {
            var end = Array.IndexOf(buf, (byte)0, i);
            var s = Encoding.UTF8.GetString(buf, i, end - i);
            i = end + 1;
            return s;
        }
    }
}
