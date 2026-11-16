using System;
using System.Collections.Generic;
using System.Linq;

namespace Ledger
{
    /// <summary>
    /// Everything the Ledger page is filtered by. One serializable object so it can be persisted,
    /// saved as a named view, compared, and rendered as chips. Empty/null = "no constraint".
    /// </summary>
    public class FilterState
    {
        public string Period { get; set; } = "All time";
        public List<string> Stores { get; set; } = new List<string>();
        public List<string> Genres { get; set; } = new List<string>();
        public string Installed { get; set; }          // null | "Installed" | "Not installed"
        public string Played { get; set; }             // null | "Played" | "Unplayed"
        public string Search { get; set; }
        public double? MinHours { get; set; }
        public double? MaxHours { get; set; }
        public double? MaxHltb { get; set; }           // "short games": HowLongToBeat main story ≤ N
        public int? MinYear { get; set; }              // release year
        public int? MaxYear { get; set; }
        public bool? Paid { get; set; }                // true = bought, false = free/key
        public bool IncludeFamilySharing { get; set; }

        public FilterState Clone() => Playnite.SDK.Data.Serialization.GetClone(this);

        public bool IsEmpty =>
            Period == "All time" && Stores.Count == 0 && Genres.Count == 0 && Installed == null && Played == null &&
            string.IsNullOrWhiteSpace(Search) && MinHours == null && MaxHours == null && MaxHltb == null &&
            MinYear == null && MaxYear == null && Paid == null;

        internal bool Matches(GameHistory h, Func<GameHistory, bool> isPaid)
        {
            var g = h.Game;
            if (!IncludeFamilySharing && g.Source?.Name == "Steam Family Sharing") return false;
            if (Stores.Count > 0 && !Stores.Contains(h.Origin)) return false;
            if (Genres.Count > 0 && !(g.Genres?.Any(x => Genres.Contains(x.Name)) ?? false)) return false;
            if (Installed != null && (Installed == "Installed") != g.IsInstalled) return false;
            if (Played != null && (Played == "Played") != h.Played) return false;
            if (!string.IsNullOrWhiteSpace(Search) && g.Name.IndexOf(Search.Trim(), StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (MinHours.HasValue && h.Hours < MinHours) return false;
            if (MaxHours.HasValue && h.Hours > MaxHours) return false;
            if (MaxHltb.HasValue && (h.Hltb == null || h.Hltb.Typical > MaxHltb)) return false;
            if (MinYear.HasValue && (g.ReleaseYear ?? 0) < MinYear) return false;
            if (MaxYear.HasValue && (g.ReleaseYear ?? 9999) > MaxYear) return false;
            if (Paid.HasValue && isPaid(h) != Paid.Value) return false;
            return true;
        }

        /// <summary>Human-readable chips; each comes with the edit that removes it.</summary>
        internal IEnumerable<KeyValuePair<string, Action<FilterState>>> Chips()
        {
            if (Period != "All time") yield return Chip(Period, f => f.Period = "All time");
            foreach (var s in Stores.ToList()) yield return Chip(s, f => f.Stores.Remove(s));
            foreach (var g in Genres.ToList()) yield return Chip(g, f => f.Genres.Remove(g));
            if (Installed != null) yield return Chip(Installed, f => f.Installed = null);
            if (Played != null) yield return Chip(Played, f => f.Played = null);
            if (!string.IsNullOrWhiteSpace(Search)) yield return Chip($"\"{Search.Trim()}\"", f => f.Search = null);
            if (MinHours.HasValue || MaxHours.HasValue) yield return Chip(Range("played", MinHours, MaxHours, "h"), f => { f.MinHours = null; f.MaxHours = null; });
            if (MaxHltb.HasValue) yield return Chip($"≤ {MaxHltb}h to beat", f => f.MaxHltb = null);
            if (MinYear.HasValue || MaxYear.HasValue) yield return Chip(Range("released", MinYear, MaxYear, ""), f => { f.MinYear = null; f.MaxYear = null; });
            if (Paid.HasValue) yield return Chip(Paid.Value ? "Paid for" : "Free / key", f => f.Paid = null);
        }

        private static KeyValuePair<string, Action<FilterState>> Chip(string label, Action<FilterState> remove) =>
            new KeyValuePair<string, Action<FilterState>>(label, remove);

        private static string Range(string what, double? lo, double? hi, string unit) =>
            lo.HasValue && hi.HasValue ? $"{what} {lo}{unit}–{hi}{unit}" : lo.HasValue ? $"{what} ≥ {lo}{unit}" : $"{what} ≤ {hi}{unit}";
    }

    public class SavedView
    {
        public string Name { get; set; }
        public string Tab { get; set; }
        public FilterState Filter { get; set; }
    }

    internal static class BuiltInViews
    {
        public static List<SavedView> All => new List<SavedView>
        {
            new SavedView { Name = "Short unplayed, installed", Tab = "Backlog", Filter = new FilterState { Played = "Unplayed", Installed = "Installed", MaxHltb = 10 } },
            new SavedView { Name = "Paid for, never played", Tab = "Backlog", Filter = new FilterState { Played = "Unplayed", Paid = true } },
            new SavedView { Name = "Free games I actually played", Tab = "Stats", Filter = new FilterState { Played = "Played", Paid = false } },
            new SavedView { Name = "Big time sinks (100h+)", Tab = "Timeline", Filter = new FilterState { MinHours = 100 } },
            new SavedView { Name = "Installed, untouched", Tab = "Installs", Filter = new FilterState { Installed = "Installed", Played = "Unplayed" } },
            new SavedView { Name = "Last 12 months", Tab = "Overview", Filter = new FilterState { Period = "Last 12 months" } },
        };
    }
}
