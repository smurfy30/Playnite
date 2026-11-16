using System;
using System.Collections.Generic;
using System.Linq;

namespace Ledger
{
    internal class Suggestion
    {
        public GameHistory H { get; set; }
        public double Score { get; set; }
        public List<string> MatchedGenres { get; set; } = new List<string>();
        public int? Rating { get; set; }
    }

    /// <summary>
    /// "What should I play next?" from your own history, no external service:
    ///   taste  = how much of your playtime went into each of the game's genres (0..1)
    ///   rating = community score, else critic score (0..100), else neutral 60
    ///   score  = 0.55·taste + 0.35·rating + 0.10·installed
    /// Only never-played, non-hidden, non-tool games are candidates.
    /// </summary>
    internal static class UpNext
    {
        public static List<Suggestion> Rank(IEnumerable<GameHistory> all)
        {
            var games = all.Where(h => !h.Game.Hidden && !h.IsTool).ToList();
            var played = games.Where(h => h.Game.Playtime > 3600).ToList(); // >1h counts as "played"

            // Genre → share of total playtime.
            var genreHours = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in played)
            {
                foreach (var g in h.Game.Genres ?? Enumerable.Empty<Playnite.SDK.Models.Genre>())
                {
                    genreHours[g.Name] = (genreHours.TryGetValue(g.Name, out var v) ? v : 0) + h.Hours;
                }
            }
            var top = genreHours.Values.DefaultIfEmpty(1).Max();

            var result = new List<Suggestion>();
            foreach (var h in games.Where(x => x.Game.Playtime == 0 && !x.FirstPlayed.HasValue))
            {
                var genres = (h.Game.Genres ?? new List<Playnite.SDK.Models.Genre>()).Select(g => g.Name).ToList();
                if (genres.Count == 0) continue; // no metadata → can't judge taste
                // Average of the game's two strongest genre matches: one niche genre tag shouldn't sink it.
                var weights = genres.Select(g => genreHours.TryGetValue(g, out var v) ? v / top : 0).OrderByDescending(w => w).Take(2).ToList();
                var taste = weights.Average();
                var rating = h.Game.CommunityScore ?? h.Game.CriticScore;
                var score = 0.55 * taste + 0.35 * ((rating ?? 60) / 100.0) + (h.Game.IsInstalled ? 0.10 : 0);
                result.Add(new Suggestion
                {
                    H = h,
                    Score = score,
                    Rating = rating,
                    MatchedGenres = genres.Where(g => genreHours.ContainsKey(g)).OrderByDescending(g => genreHours[g]).Take(3).ToList()
                });
            }
            return result.OrderByDescending(s => s.Score).ToList();
        }

        /// <summary>Weighted random pick from the top N, so it isn't always the same game.</summary>
        public static Suggestion Pick(List<Suggestion> ranked, int topN = 15, Random rng = null)
        {
            var pool = ranked.Take(topN).ToList();
            if (pool.Count == 0) return null;
            rng = rng ?? new Random();
            var total = pool.Sum(s => s.Score);
            var roll = rng.NextDouble() * total;
            foreach (var s in pool)
            {
                roll -= s.Score;
                if (roll <= 0) return s;
            }
            return pool.Last();
        }
    }
}
