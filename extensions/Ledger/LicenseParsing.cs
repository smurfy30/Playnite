using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace Ledger
{
    internal class SteamLicense
    {
        public DateTime Date { get; set; }
        public string Name { get; set; }
        public string Method { get; set; }
    }

    /// <summary>Pure parsing/matching logic, no Playnite dependencies, so it can be tested offline.</summary>
    internal static class LicenseParsing
    {
        private static readonly Regex RowRx = new Regex(
            @"<td class=""license_date_col"">(?<date>.*?)</td>\s*<td>(?<name>.*?)</td>\s*<td class=""license_acquisition_col"">(?<method>.*?)</td>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private static readonly Regex RemoveLinkRx = new Regex(@"<div class=""free_license_remove_link"">.*?</div>", RegexOptions.Singleline);
        private static readonly Regex TagRx = new Regex("<.*?>", RegexOptions.Singleline);

        // Steam renders dates per account language; cover the English variants.
        private static readonly string[] DateFormats = { "d MMM, yyyy", "MMM d, yyyy", "d MMM yyyy", "MMM d yyyy", "yyyy-MM-dd" };

        private static readonly Regex NextRx = new Regex(@"class=""license_paginator_next""\s+href=""(?<href>[^""]+)""", RegexOptions.IgnoreCase);

        /// <summary>Absolute URL of the next licenses page, or null on the last page.</summary>
        public static string NextPageUrl(string html, string baseUrl)
        {
            var m = NextRx.Match(html ?? "");
            if (!m.Success) return null;
            var href = WebUtility.HtmlDecode(m.Groups["href"].Value);
            return new Uri(new Uri(baseUrl), href).ToString();
        }

        public static bool LooksLikeLicensesPage(string html) =>
            html != null && html.IndexOf("license_date_col", StringComparison.OrdinalIgnoreCase) >= 0;

        public static List<SteamLicense> Parse(string html)
        {
            var result = new List<SteamLicense>();
            foreach (Match m in RowRx.Matches(html ?? ""))
            {
                var dateText = Clean(m.Groups["date"].Value);
                if (!DateTime.TryParseExact(dateText, DateFormats, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AllowWhiteSpaces, out var date))
                {
                    continue;
                }

                var name = Clean(RemoveLinkRx.Replace(m.Groups["name"].Value, ""));
                if (name.Length == 0) continue;
                result.Add(new SteamLicense { Date = date, Name = name, Method = Clean(m.Groups["method"].Value) });
            }
            return result;
        }

        private static string Clean(string s) =>
            Regex.Replace(WebUtility.HtmlDecode(TagRx.Replace(s, " ")), @"\s+", " ").Trim();

        /// <summary>Lowercase alphanumerics only: "Gunfire Reborn™ - Deluxe" → "gunfirereborndeluxe".</summary>
        public static string Norm(string s) =>
            new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        // Suffixes Steam appends to package names that the game name doesn't have.
        private static readonly string[] PackageNoise = { "limitedfreepromotionalpackage", "freeweekend", "earlyaccess", "commercial", "retail", "steamstore", "complimentary", "freeonsteam", "beta", "playtest", "forstoresignup", "playtestforstoresignup" };
        // Words that mark a multi-game package; the rest of the name is the franchise prefix.
        private static readonly Regex CollectionRx = new Regex(@"\b(the )?(collection|bundle|trilogy|anthology|complete|ultimate|saga|pack)\b.*$", RegexOptions.IgnoreCase);

        /// <summary>"fallout" vs "fallout76": a digit right after the shared prefix means a different game.</summary>
        private static bool NextIsDigit(string longer, int at) => at < longer.Length && char.IsDigit(longer[at]);

        private static string StripNoise(string n)
        {
            foreach (var noise in PackageNoise)
            {
                if (n.EndsWith(noise) && n.Length > noise.Length) n = n.Substring(0, n.Length - noise.Length);
            }
            return n;
        }

        /// <summary>
        /// Earliest license per game name, in passes from most to least certain. Each game takes
        /// the first pass that finds anything:
        ///  1. exact normalized name (raw or with Steam's package noise stripped)
        ///  2. license starts with game   ("X Deluxe Edition" → X), longest games first so
        ///     "Portal 2" claims its packages before "Portal" can
        ///  3. game starts with license   ("Fallout 3 - GOTY Edition" ← "Fallout 3 - GOTY")
        ///  4. collection franchise       ("BioShock The Collection" → BioShock 2, Infinite…)
        /// Names shorter than 6 chars only ever match exactly.
        /// </summary>
        public static Dictionary<string, SteamLicense> Match(IEnumerable<string> gameNames, IList<SteamLicense> licenses)
        {
            var byNorm = new Dictionary<string, SteamLicense>();
            void Add(string key, SteamLicense l)
            {
                if (key.Length == 0) return;
                if (!byNorm.TryGetValue(key, out var cur) || l.Date < cur.Date) byNorm[key] = l;
            }
            var franchises = new Dictionary<string, SteamLicense>();
            foreach (var l in licenses)
            {
                var raw = Norm(l.Name);
                Add(raw, l);
                Add(StripNoise(raw), l);
                var m = CollectionRx.Match(l.Name);
                if (m.Success && m.Index > 0)
                {
                    var f = Norm(l.Name.Substring(0, m.Index));
                    if (f.Length >= 6 && (!franchises.TryGetValue(f, out var cur) || l.Date < cur.Date)) franchises[f] = l;
                }
            }

            var games = gameNames.Distinct().Select(g => new { name = g, norm = Norm(g) }).Where(g => g.norm.Length >= 3)
                                 .OrderByDescending(g => g.norm.Length).ToList();
            var result = new Dictionary<string, SteamLicense>();
            var claimed = new HashSet<string>();
            foreach (var g in games)
            {
                if (byNorm.TryGetValue(g.norm, out var exact)) { result[g.name] = exact; claimed.Add(g.norm); }
            }

            foreach (var g in games.Where(x => !result.ContainsKey(x.name) && x.norm.Length >= 6))
            {
                // 2. license starts with game
                var c2 = byNorm.Where(kv => kv.Key.StartsWith(g.norm) && !claimed.Contains(kv.Key) && !NextIsDigit(kv.Key, g.norm.Length)).ToList();
                if (c2.Count > 0)
                {
                    result[g.name] = c2.OrderBy(kv => kv.Value.Date).First().Value;
                    foreach (var c in c2) claimed.Add(c.Key);
                    continue;
                }
                // 3. game starts with license (longest license wins: most specific)
                var c3 = byNorm.Where(kv => kv.Key.Length >= 8 && g.norm.StartsWith(kv.Key) && !NextIsDigit(g.norm, kv.Key.Length)).OrderByDescending(kv => kv.Key.Length).FirstOrDefault();
                if (c3.Value != null) { result[g.name] = c3.Value; continue; }
                // 4. collection franchise
                var c4 = franchises.Where(kv => g.norm.StartsWith(kv.Key)).OrderByDescending(kv => kv.Key.Length).FirstOrDefault();
                if (c4.Value != null) result[g.name] = c4.Value;
            }
            return result;
        }
    }
}
