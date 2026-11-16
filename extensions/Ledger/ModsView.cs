using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace Ledger
{
    /// <summary>Per-game mod list window: source, name (links to the mod page), size, updated, enabled.</summary>
    internal static class ModsView
    {
        public static void Show(IPlayniteAPI api, GameMods gm, string ledgerDir)
        {
            var window = api.Dialogs.CreateWindow(new WindowCreationOptions { ShowMinimizeButton = false });
            window.Title = $"Mods · {gm.Game?.Name ?? gm.Label} · {gm.Mods.Count}";
            window.Width = 900;
            window.Height = 700;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.Owner = api.Dialogs.GetCurrentAppWindow();

            var list = BuildList(gm, out var Render);
            window.Content = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            FetchTitlesThenRender(gm, ledgerDir, () => window.Dispatcher.Invoke(Render));
            window.ShowDialog();
        }

        /// <summary>Fetch missing Workshop titles synchronously (used for headless renders).</summary>
        public static void FetchTitlesNow(GameMods gm, string ledgerDir)
        {
            var untitled = gm.Mods.Where(m => m.Source == "Workshop" && m.Name.StartsWith("Workshop item ")).Select(m => m.Id).ToList();
            if (untitled.Count == 0) return;
            WorkshopTitles.FetchMissing(ledgerDir, untitled);
            var titles = WorkshopTitles.Load(ledgerDir);
            foreach (var m in gm.Mods.Where(x => x.Source == "Workshop" && titles.ContainsKey(x.Id))) m.Name = titles[m.Id];
        }

        private static void FetchTitlesThenRender(GameMods gm, string ledgerDir, Action rerender)
        {
            if (!gm.Mods.Any(m => m.Source == "Workshop" && m.Name.StartsWith("Workshop item "))) return;
            Task.Run(() =>
            {
                try { FetchTitlesNow(gm, ledgerDir); rerender(); }
                catch (Exception e) { LogManager.GetLogger().Warn($"Ledger: workshop titles fetch failed: {e.Message}"); }
            });
        }

        public static StackPanel BuildList(GameMods gm, out Action render)
        {
            var list = new StackPanel { Margin = new Thickness(20) };
            void Render()
            {
                list.Children.Clear();
                var bySource = gm.Mods.GroupBy(m => m.Source).OrderByDescending(g => g.Count());
                foreach (var group in bySource)
                {
                    var header = Ui.Text($"{group.Key} · {group.Count()}" + (group.Any(m => m.Size.HasValue) ? $" · {Ui.Size(group.Sum(m => (double)(m.Size ?? 0)))}" : ""), 16, bold: true);
                    header.Margin = new Thickness(0, 12, 0, 6);
                    list.Children.Add(header);
                    foreach (var m in group.OrderByDescending(x => x.Updated ?? DateTime.MinValue))
                    {
                        var row = Ui.Row(new GridLength(1, GridUnitType.Star), new GridLength(90), new GridLength(110));
                        var name = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                        name.SetResourceReference(TextBlock.ForegroundProperty, m.Enabled == false ? "TextBrushDarker" : "TextBrush");
                        if (m.Url != null)
                        {
                            var link = new Hyperlink(new Run(m.Name)) { TextDecorations = null, Cursor = Cursors.Hand };
                            link.SetResourceReference(TextElement.ForegroundProperty, "TextBrush");
                            var url = m.Url;
                            link.Click += (s, e) => System.Diagnostics.Process.Start(url);
                            name.Inlines.Add(link);
                        }
                        else
                        {
                            name.Inlines.Add(new Run(m.Name));
                        }
                        if (m.Enabled == false) name.Inlines.Add(new Run("  [disabled]"));
                        Ui.Place(row, name, 0);
                        Ui.Place(row, Ui.Text(m.Size.HasValue ? Ui.Size(m.Size.Value) : "", 12, dim: true), 1);
                        Ui.Place(row, Ui.Text(m.Updated?.ToString("yyyy-MM-dd") ?? "", 12, dim: true), 2);
                        list.Children.Add(row);
                    }
                }
            }
            Render();
            render = Render;
            return list;
        }
    }

    /// <summary>Small shared UI helpers (theme-aware text, rows, sizes).</summary>
    internal static class Ui
    {
        public static TextBlock Text(string s, double size, bool bold = false, bool dim = false)
        {
            var tb = new TextBlock
            {
                Text = s,
                FontSize = size,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, dim ? "TextBrushDarker" : "TextBrush");
            return tb;
        }

        public static Grid Row(params GridLength[] cols)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            foreach (var c in cols) g.ColumnDefinitions.Add(new ColumnDefinition { Width = c });
            return g;
        }

        public static void Place(Grid g, UIElement e, int col)
        {
            Grid.SetColumn(e, col);
            g.Children.Add(e);
        }

        public static string Size(double bytes)
        {
            if (bytes <= 0) return "?";
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            var i = 0;
            while (bytes >= 1024 && i < u.Length - 1) { bytes /= 1024; i++; }
            return bytes.ToString(i >= 3 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + u[i];
        }
    }
}
