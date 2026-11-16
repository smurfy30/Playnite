using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ledger
{
    public class ListItem
    {
        public Guid? GameId { get; set; }       // a game in the Playnite library
        public string SteamAppId { get; set; }  // or a Steam game you don't own (from Discover)
        public string Name { get; set; }
        public string Note { get; set; }
        public DateTime Added { get; set; } = DateTime.Now;
        public string Key => GameId?.ToString() ?? "steam:" + SteamAppId;
    }

    public class GameList
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; }
        public string Icon { get; set; } = "📋";
        public string Description { get; set; }
        public List<ListItem> Items { get; set; } = new List<ListItem>();
        /// <summary>Smart list: membership is this filter, evaluated live (Items unused).</summary>
        public FilterState Smart { get; set; }
        public string Sort { get; set; } = "Best match";
        public int Limit { get; set; } = 24;
        public bool IsSmart => Smart != null;
    }

    /// <summary>A yearly target, measured from what Ledger records (never typed-in progress).</summary>
    public class Goal
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Kind { get; set; }      // beat | start | shrink | spend | hours
        public double Target { get; set; }
        public int Year { get; set; } = DateTime.Now.Year;

        public static readonly Dictionary<string, string> Kinds = new Dictionary<string, string>
        {
            ["beat"] = "Finish games (mark Beaten/Completed)",
            ["start"] = "Start games I own but never played",
            ["shrink"] = "Shrink the never-played pile by",
            ["spend"] = "Spend at most (on games + in-game)",
            ["hours"] = "Play at most (hours, tracked)",
        };
    }

    /// <summary>
    /// User-made lists (lists.json) + dismissed recommendations (dismissed.json). Small enough to load
    /// and save whole on every change.
    /// </summary>
    internal class ListStore
    {
        private readonly string dir;
        private string ListsPath => Path.Combine(dir, "lists.json");
        private string DismissedPath => Path.Combine(dir, "dismissed.json");

        public ListStore(string dataDir) { dir = dataDir; }

        public List<GameList> Load()
        {
            List<GameList> lists = null;
            try { if (File.Exists(ListsPath)) lists = Serialization.FromJsonFile<List<GameList>>(ListsPath); } catch { }
            if (lists == null || lists.Count == 0)
            {
                lists = new List<GameList>
                {
                    new GameList { Name = "Play next", Icon = "▶", Description = "Your queue. Top of the list is what you're playing next." },
                    new GameList { Name = "Want to buy", Icon = "🛒", Description = "Picks from Discover you're considering." },
                };
                Save(lists);
            }
            return lists;
        }

        public void Save(List<GameList> lists)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(ListsPath, Serialization.ToJson(lists, true));
        }

        public void Mutate(Action<List<GameList>> change)
        {
            var lists = Load();
            change(lists);
            Save(lists);
        }

        public void Add(Guid listId, ListItem item) => Mutate(ls =>
        {
            var l = ls.FirstOrDefault(x => x.Id == listId);
            if (l != null && !l.Items.Any(i => i.Key == item.Key)) l.Items.Add(item);
        });

        public void Remove(Guid listId, string key) => Mutate(ls => ls.FirstOrDefault(x => x.Id == listId)?.Items.RemoveAll(i => i.Key == key));

        public void Move(Guid listId, string key, int delta) => Mutate(ls =>
        {
            var l = ls.FirstOrDefault(x => x.Id == listId);
            var i = l?.Items.FindIndex(x => x.Key == key) ?? -1;
            var j = i + delta;
            if (l == null || i < 0 || j < 0 || j >= l.Items.Count) return;
            var it = l.Items[i];
            l.Items.RemoveAt(i);
            l.Items.Insert(j, it);
        });

        private string GoalsPath => Path.Combine(dir, "goals.json");

        public List<Goal> Goals()
        {
            try { return File.Exists(GoalsPath) ? Serialization.FromJsonFile<List<Goal>>(GoalsPath) ?? new List<Goal>() : new List<Goal>(); }
            catch { return new List<Goal>(); }
        }

        public void SaveGoals(List<Goal> goals)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(GoalsPath, Serialization.ToJson(goals, true));
        }

        public HashSet<string> Dismissed()
        {
            try { return File.Exists(DismissedPath) ? new HashSet<string>(Serialization.FromJsonFile<List<string>>(DismissedPath) ?? new List<string>()) : new HashSet<string>(); }
            catch { return new HashSet<string>(); }
        }

        public void Dismiss(string steamAppId)
        {
            var d = Dismissed();
            d.Add(steamAppId);
            Directory.CreateDirectory(dir);
            File.WriteAllText(DismissedPath, Serialization.ToJson(d.ToList(), true));
        }
    }
}
