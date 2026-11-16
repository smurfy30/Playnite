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
            bar.Children.Add(Btn("⚡ Smart list from current filters", () =>
            {
                if (filter.IsEmpty) { api.Dialogs.ShowMessage("Set some filters first (Filters ▾ or click a chart). A smart list keeps whatever matches them, live.", "Ledger"); return; }
                var r = api.Dialogs.SelectString("Name the smart list:", "Ledger · Smart list", string.Join(", ", filter.Chips().Select(c => c.Key)));
                if (!r.Result || string.IsNullOrWhiteSpace(r.SelectedString)) return;
                var smart = filter.Clone();
                Lists.Mutate(ls => ls.Add(new GameList { Name = r.SelectedString.Trim(), Icon = "⚡", Smart = smart }));
                RebuildCurrentTab();
            }, tip: "Saves the current filters as a list that updates itself"));
            bar.Children.Add(Text("Right-click any game → Add to list. Drag tiles to reorder. Lists can hold games you don't own yet.", 11, dim: true));
            root.Children.Add(bar);

            var recs = plugin.Discover.LoadRecs().GroupBy(r => r.AppId).ToDictionary(g => g.Key, g => g.First());
            foreach (var l in lists)
            {
                var list = l;
                var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 18, 0, 2) };
                var title = Text($"{l.Icon}  {l.Name}  ·  {(l.IsSmart ? SmartMembers(l, d).Count : l.Items.Count)}", 17, bold: true);
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
                var exportBtn = Btn("Export ▾", () => { });
                exportBtn.ContextMenu = new ContextMenu();
                exportBtn.ContextMenu.Items.Add(Item("Copy as Markdown", () => { Clipboard.SetText(ExportMarkdown(list, d)); Toast($"Copied {list.Name} as Markdown"); }));
                exportBtn.ContextMenu.Items.Add(Item("Save as HTML page…", () => ExportHtml(list, d)));
                exportBtn.Click += (o, e) => { exportBtn.ContextMenu.PlacementTarget = exportBtn; exportBtn.ContextMenu.IsOpen = true; };
                tools.Children.Add(exportBtn);
                if (list.IsSmart)
                {
                    var sortBtn = Btn("Sort: " + list.Sort + " ▾", () => { });
                    sortBtn.ContextMenu = new ContextMenu();
                    foreach (var opt in SmartSorts.Keys)
                    {
                        var o2 = opt;
                        sortBtn.ContextMenu.Items.Add(Item(opt, () => { Lists.Mutate(ls => ls.First(x => x.Id == list.Id).Sort = o2); RebuildCurrentTab(); }));
                    }
                    sortBtn.Click += (o, e) => { sortBtn.ContextMenu.PlacementTarget = sortBtn; sortBtn.ContextMenu.IsOpen = true; };
                    tools.Children.Insert(0, sortBtn);
                    tools.Children.Insert(1, Btn("Open as filter", () => Edit(f => { var keep = f.IncludeFamilySharing; var c = list.Smart.Clone(); c.IncludeFamilySharing = keep; filter = c; }), tip: "Apply this list's filters to the whole page"));
                }
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
                if (l.IsSmart)
                {
                    var members = SmartMembers(l, d);
                    root.Children.Add(Text("⚡ " + string.Join(" · ", l.Smart.Chips().Select(c => c.Key)) + $"  ·  {members.Count} match" + (members.Count > l.Limit ? $", showing {l.Limit}" : ""), 12, dim: true));
                    root.Children.Add(Charts.Tiles(members.Take(l.Limit).Select(h => TileFor(h,
                        h.Hours > 0 ? $"{h.Hours:N0}h played" : h.Hltb != null ? $"{h.Hltb.Typical:N0}h to beat" : "unplayed", h.Game.IsInstalled ? "installed" : h.Game.Source?.Name)), 105));
                    continue;
                }
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
                        t.DragKey = it.Key;
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
                            Menu = menu,
                            DragKey = it.Key
                        });
                    }
                }
                root.Children.Add(Charts.Tiles(tiles, 110, (from, to) =>
                {
                    Lists.Mutate(ls =>
                    {
                        var target = ls.First(x => x.Id == list.Id).Items;
                        var a = target.FindIndex(x => x.Key == from);
                        var b = target.FindIndex(x => x.Key == to);
                        if (a < 0 || b < 0) return;
                        var moved = target[a];
                        target.RemoveAt(a);
                        target.Insert(b, moved);
                    });
                    RebuildCurrentTab();
                }));
            }
        }

        private static readonly Dictionary<string, Func<GameHistory, IComparable>> SmartSorts = new Dictionary<string, Func<GameHistory, IComparable>>
        {
            ["Best match"] = h => -(h.Game.CommunityScore ?? h.Game.CriticScore ?? 60) - (h.Game.IsInstalled ? 20 : 0),
            ["Shortest first"] = h => h.Hltb?.Typical ?? 9999,
            ["Most played"] = h => -h.Hours,
            ["Recently played"] = h => -(h.LastPlayed?.Ticks ?? 0),
            ["Oldest owned"] = h => h.Acquired?.Ticks ?? long.MaxValue,
            ["Best rated"] = h => -(h.Game.UserScore ?? h.Game.CommunityScore ?? h.Game.CriticScore ?? 0),
            ["Name"] = h => h.Game.Name,
        };

        private List<GameHistory> SmartMembers(GameList l, Data d)
        {
            var sort = SmartSorts.TryGetValue(l.Sort ?? "", out var f) ? f : SmartSorts["Best match"];
            return d.Visible.Where(h => !h.IsTool && l.Smart.Matches(h, IsPaid)).OrderBy(sort).ToList();
        }

        private List<KeyValuePair<string, string>> ListLines(GameList l, Data d)
        {
            // (name, detail) per entry, for export
            if (l.IsSmart) return SmartMembers(l, d).Take(l.Limit).Select(h => new KeyValuePair<string, string>(h.Game.Name, h.Hltb != null ? $"~{h.Hltb.Typical:N0}h" : h.Game.Source?.Name)).ToList();
            return l.Items.Select(it => new KeyValuePair<string, string>(it.Name, it.Note)).ToList();
        }

        private string ExportMarkdown(GameList l, Data d)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"## {l.Icon} {l.Name}");
            if (!string.IsNullOrEmpty(l.Description)) sb.AppendLine().AppendLine(l.Description);
            sb.AppendLine();
            var i = 1;
            foreach (var line in ListLines(l, d)) sb.AppendLine($"{i++}. **{line.Key}**" + (string.IsNullOrWhiteSpace(line.Value) ? "" : $": {line.Value}"));
            return sb.ToString();
        }

        /// <summary>Standalone HTML page with embedded covers, to send to a friend or keep.</summary>
        private void ExportHtml(GameList l, Data d)
        {
            string Enc(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");
            string Img(string path)
            {
                if (path == null || !System.IO.File.Exists(path)) return "";
                var bmp = Charts.LoadImage(path, 200);
                if (bmp == null) return "";
                var enc = new System.Windows.Media.Imaging.JpegBitmapEncoder { QualityLevel = 80 };
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var ms = new System.IO.MemoryStream()) { enc.Save(ms); return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray()); }
            }
            var cards = new List<string>();
            if (l.IsSmart)
                foreach (var h in SmartMembers(l, d).Take(l.Limit)) cards.Add($"<div class=c><img src='{Img(Cover(h))}'><b>{Enc(h.Game.Name)}</b><span>{Enc(h.Hltb != null ? $"~{h.Hltb.Typical:N0}h to beat" : h.Game.Source?.Name)}</span></div>");
            else
                foreach (var it in l.Items)
                {
                    var h = it.GameId.HasValue ? d.Index.For(it.GameId.Value) : null;
                    var cover = h != null ? Cover(h) : it.SteamAppId != null ? plugin.Discover.SteamCover(it.SteamAppId) : null;
                    var link = it.SteamAppId != null ? $"https://store.steampowered.com/app/{it.SteamAppId}" : null;
                    var title = link != null ? $"<a href='{link}'>{Enc(it.Name)}</a>" : Enc(it.Name);
                    cards.Add($"<div class=c><img src='{Img(cover)}'><b>{title}</b><span>{Enc(it.Note)}</span></div>");
                }
            var html = "<!doctype html><meta charset=utf-8><title>" + Enc(l.Name) + "</title><style>body{background:#0f1420;color:#e6e9ef;font-family:Segoe UI,sans-serif;margin:40px}" +
                       ".g{display:flex;flex-wrap:wrap;gap:18px}.c{width:150px}.c img{width:150px;height:225px;object-fit:cover;border-radius:8px;background:#1d2433}" +
                       ".c b{display:block;margin-top:6px;font-size:14px}.c span{color:#9aa3b2;font-size:12px}a{color:#4fc3f7;text-decoration:none}</style>" +
                       $"<h1>{Enc(l.Icon)} {Enc(l.Name)}</h1><p style='color:#9aa3b2'>{Enc(l.Description)} · exported {DateTime.Now:d MMM yyyy} from Ledger</p><div class=g>{string.Join("", cards)}</div>";
            var dir = System.IO.Path.Combine(plugin.GetPluginUserDataPath(), "exports");
            System.IO.Directory.CreateDirectory(dir);
            var safe = string.Concat(l.Name.Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
            var outPath = System.IO.Path.Combine(dir, safe + ".html");
            System.IO.File.WriteAllText(outPath, html);
            System.Diagnostics.Process.Start(outPath);
        }

        private void RebuildCurrentTab() => SafeShow(last);
    }
}
