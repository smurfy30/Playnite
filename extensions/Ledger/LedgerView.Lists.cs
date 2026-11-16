using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Ledger
{
    internal partial class LedgerView
    {
        private ListStore Lists => new ListStore(plugin.GetPluginUserDataPath());

        // ---------------- context menus ----------------

        private MenuItem Item(string header, Action click)
        {
            var m = new MenuItem { Header = header };
            m.Click += (s, e) => click();
            return m;
        }

        private MenuItem AddToListMenu(Func<ListItem> make)
        {
            var root = new MenuItem { Header = "Add to list" };
            foreach (var l in Lists.Load())
            {
                var id = l.Id;
                root.Items.Add(Item($"{l.Icon}  {l.Name}", () => { Lists.Add(id, make()); Toast($"Added to {l.Name}"); }));
            }
            root.Items.Add(new Separator());
            root.Items.Add(Item("New list…", () =>
            {
                var r = api.Dialogs.SelectString("Name the new list:", "Ledger · New list", "");
                if (!r.Result || string.IsNullOrWhiteSpace(r.SelectedString)) return;
                var list = new GameList { Name = r.SelectedString.Trim() };
                list.Items.Add(make());
                Lists.Mutate(ls => ls.Add(list));
                Toast($"Created {list.Name}");
            }));
            return root;
        }

        internal ContextMenu MenuFor(GameHistory h)
        {
            var m = new ContextMenu();
            var id = h.Game.Id;
            m.Items.Add(Item("Details", () => OpenDrawer(id)));
            m.Items.Add(Item(h.Game.IsInstalled ? "▶  Play" : "⬇  Install", () => { if (h.Game.IsInstalled) api.StartGame(id); else api.InstallGame(id); }));
            m.Items.Add(Item("Show in library", () => { api.MainView.SwitchToLibraryView(); api.MainView.SelectGame(id); }));
            m.Items.Add(new Separator());
            m.Items.Add(AddToListMenu(() => new ListItem { GameId = id, Name = h.Game.Name }));
            return m;
        }

        internal ContextMenu MenuFor(Rec r)
        {
            var m = new ContextMenu();
            m.Items.Add(Item("Open on Steam", () => Open("https://store.steampowered.com/app/" + r.AppId)));
            m.Items.Add(AddToListMenu(() => new ListItem { SteamAppId = r.AppId, Name = r.Name }));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Not interested", () => { Lists.Dismiss(r.AppId); Toast($"Won't suggest {r.Name} again"); SafeShow(last); }));
            return m;
        }

        /// <summary>Lightweight confirmation: Playnite notification that clears itself next time.</summary>
        private void Toast(string text) =>
            api.Notifications.Add(new Playnite.SDK.NotificationMessage("ledger-toast", text, Playnite.SDK.NotificationType.Info));

        // ---------------- the Lists tab ----------------

        private void BuildLists(Data d)
        {
            var lists = Lists.Load();
            var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            bar.Children.Add(Btn("＋ New list", () =>
            {
                var r = api.Dialogs.SelectString("Name the new list:", "Ledger · New list", "");
                if (!r.Result || string.IsNullOrWhiteSpace(r.SelectedString)) return;
                Lists.Mutate(ls => ls.Add(new GameList { Name = r.SelectedString.Trim() }));
                RebuildCurrentTab();
            }));
            bar.Children.Add(Text("Right-click any game (here, Discover, tiles anywhere) → Add to list. Lists can hold games you don't own yet.", 11, dim: true));
            root.Children.Add(bar);

            var recs = plugin.Discover.LoadRecs().GroupBy(r => r.AppId).ToDictionary(g => g.Key, g => g.First());
            foreach (var l in lists)
            {
                var list = l;
                var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 18, 0, 2) };
                var title = Text($"{l.Icon}  {l.Name}  ·  {l.Items.Count}", 17, bold: true);
                DockPanel.SetDock(title, Dock.Left);
                head.Children.Add(title);
                var tools = new StackPanel { Orientation = Orientation.Horizontal };
                tools.Children.Add(Btn("Rename", () =>
                {
                    var r = api.Dialogs.SelectString("Rename list:", "Ledger", list.Name);
                    if (r.Result && !string.IsNullOrWhiteSpace(r.SelectedString)) { Lists.Mutate(ls => ls.First(x => x.Id == list.Id).Name = r.SelectedString.Trim()); RebuildCurrentTab(); }
                }));
                tools.Children.Add(Btn("Icon", () =>
                {
                    var r = api.Dialogs.SelectString("An emoji or symbol for this list:", "Ledger", list.Icon);
                    if (r.Result && !string.IsNullOrWhiteSpace(r.SelectedString)) { Lists.Mutate(ls => ls.First(x => x.Id == list.Id).Icon = r.SelectedString.Trim()); RebuildCurrentTab(); }
                }));
                tools.Children.Add(Btn("Delete", () =>
                {
                    if (api.Dialogs.ShowMessage($"Delete the list \"{list.Name}\"? (The games stay in your library.)", "Ledger", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                    Lists.Mutate(ls => ls.RemoveAll(x => x.Id == list.Id));
                    RebuildCurrentTab();
                }));
                DockPanel.SetDock(tools, Dock.Right);
                head.Children.Add(tools);
                root.Children.Add(head);
                if (!string.IsNullOrEmpty(l.Description)) root.Children.Add(Text(l.Description, 12, dim: true));
                if (l.Items.Count == 0) { root.Children.Add(Text("Empty. Right-click a game → Add to list.", 12, dim: true)); continue; }

                var tiles = new List<Charts.Tile>();
                for (var i = 0; i < l.Items.Count; i++)
                {
                    var it = l.Items[i];
                    var pos = i;
                    var menu = new ContextMenu();
                    if (pos > 0) menu.Items.Add(Item("Move up", () => { Lists.Move(list.Id, it.Key, -1); RebuildCurrentTab(); }));
                    if (pos < l.Items.Count - 1) menu.Items.Add(Item("Move down", () => { Lists.Move(list.Id, it.Key, +1); RebuildCurrentTab(); }));
                    if (pos > 0) menu.Items.Add(Item("Move to top", () => { Lists.Move(list.Id, it.Key, -pos); RebuildCurrentTab(); }));
                    menu.Items.Add(Item("Edit note…", () =>
                    {
                        var r = api.Dialogs.SelectString($"Note for {it.Name}:", "Ledger", it.Note ?? "");
                        if (r.Result) { Lists.Mutate(ls => ls.First(x => x.Id == list.Id).Items.First(x => x.Key == it.Key).Note = r.SelectedString?.Trim()); RebuildCurrentTab(); }
                    }));
                    menu.Items.Add(new Separator());
                    menu.Items.Add(Item($"Remove from {list.Name}", () => { Lists.Remove(list.Id, it.Key); RebuildCurrentTab(); }));

                    var h = it.GameId.HasValue ? d.Index.For(it.GameId.Value) : null;
                    if (h != null)
                    {
                        var t = TileFor(h, $"{i + 1}. " + (h.Hltb != null ? $"{h.Hltb.Typical:N0}h to beat" : h.Hours > 0 ? $"{h.Hours:N0}h played" : "unplayed"), it.Note ?? (h.Game.IsInstalled ? "installed" : h.Game.Source?.Name));
                        t.Menu = menu;
                        tiles.Add(t);
                    }
                    else
                    {
                        recs.TryGetValue(it.SteamAppId ?? "", out var rec);
                        tiles.Add(new Charts.Tile
                        {
                            Title = it.Name,
                            Line1 = $"{i + 1}. " + (rec != null ? $"{rec.ReviewPct}% · not owned" : "not owned"),
                            Line2 = it.Note,
                            CoverPath = it.SteamAppId != null ? (CachedCover(it.SteamAppId + ".jpg") ?? plugin.Discover.SteamCover(it.SteamAppId)) : null,
                            Tip = "Click to open on Steam. Right-click to reorder or remove.",
                            OnClick = () => Open("https://store.steampowered.com/app/" + it.SteamAppId),
                            Menu = menu
                        });
                    }
                }
                root.Children.Add(Charts.Tiles(tiles, 110));
            }
        }

        private void RebuildCurrentTab() => SafeShow(last);
    }
}
