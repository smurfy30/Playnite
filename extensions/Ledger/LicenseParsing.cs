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
        private static readonly string[] PackageNoise = { "limitedfreepromotionalpackage", "freeweekend", "earlyaccess", "commercial", "retail", "steamstore", "complimentary", "freeonsteam", "demo", "beta", "playtest" };

        /// <summary>
        /// Earliest license per game name. Exact normalized match first; else the license whose
        /// normalized name starts with the game's (editions, bundles: "X Deluxe Edition"), taking
        /// the longest game name so "Portal 2" doesn't get claimed by "Portal".
        /// </summary>
        public static Dictionary<string, SteamLicense> Match(IEnumerable<string> gameNames, IList<SteamLicense> licenses)
        {
            var byNorm = new Dictionary<string, SteamLicense>();
            foreach (var l in licenses)
            {
                var n = Norm(l.Name);
                foreach (var noise in PackageNoise)
                {
                    if (n.EndsWith(noise) && n.Length > noise.Length) n = n.Substring(0, n.Length - noise.Length);
                }
                if (!byNorm.TryGetValue(n, out var cur) || l.Date < cur.Date) byNorm[n] = l;
            }

            var games = gameNames.Distinct().Select(g => new { name = g, norm = Norm(g) }).Where(g => g.norm.Length >= 3)
                                 .OrderByDescending(g => g.norm.Length).ToList();
            var result = new Dictionary<string, SteamLicense>();
            var claimedPrefix = new HashSet<string>();
            foreach (var g in games)
            {
                if (byNorm.TryGetValue(g.norm, out var exact))
                {
                    result[g.name] = exact;
                    continue;
                }

                // Prefix match only for names long enough to be distinctive.
                if (g.norm.Length < 6) continue;
                var candidates = byNorm.Where(kv => kv.Key.StartsWith(g.norm) && !claimedPrefix.Contains(kv.Key)).ToList();
                if (candidates.Count == 0) continue;
                var best = candidates.OrderBy(kv => kv.Value.Date).First();
                result[g.name] = best.Value;
                foreach (var c in candidates) claimedPrefix.Add(c.Key);
            }
            return result;
        }
    }
}
