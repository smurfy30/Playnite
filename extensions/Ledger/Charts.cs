using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Ledger
{
    internal class Slice
    {
        public string Label { get; set; }
        public double Value { get; set; }
        public string Detail { get; set; }          // tooltip / legend suffix
        public Action OnClick { get; set; }
    }

    /// <summary>
    /// Tiny native WPF charts (no chart library): donut, columns (optionally 2 series), histogram-ish
    /// columns, cover tiles. Colours derive from the theme's GlyphBrush so they follow the active theme.
    /// </summary>
    internal static class Charts
    {
        // Palette: theme accent first, then fixed, readable-on-dark hues.
        private static readonly Color[] Palette =
        {
            Color.FromRgb(0x4F, 0xC3, 0xF7), Color.FromRgb(0xFF, 0xB7, 0x4D), Color.FromRgb(0x81, 0xC7, 0x84),
            Color.FromRgb(0xE5, 0x73, 0x73), Color.FromRgb(0xBA, 0x68, 0xC8), Color.FromRgb(0x4D, 0xB6, 0xAC),
            Color.FromRgb(0xF0, 0x62, 0x92), Color.FromRgb(0xA1, 0x88, 0x7F), Color.FromRgb(0x90, 0xA4, 0xAE)
        };

        /// <summary>Catch-all slices ("Other", "Everything else") are neutral so real slices stand out.</summary>
        private static Brush SliceBrush(Slice s, int i) =>
            s.Label == "Other" || s.Label == "Everything else" ? new SolidColorBrush(Color.FromRgb(0x50, 0x5A, 0x6E)) : SeriesBrush(i);

        public static Brush SeriesBrush(int i) => new SolidColorBrush(Palette[i % Palette.Length]);

        private static TextBlock T(string s, double size, bool bold = false, bool dim = false) => Ui.Text(s, size, bold, dim);

        /// <summary>Donut with a centre label and a clickable legend. Max ~8 slices (rest merged into "Other").</summary>
        public static FrameworkElement Donut(string title, IEnumerable<Slice> input, string centre, string centreSub, double size = 170, bool sort = true, Func<double, string> fmt = null)
        {
            fmt = fmt ?? Fmt;
            var slices = input.Where(s => s.Value > 0).ToList();
            if (sort) slices = slices.OrderByDescending(s => s.Value).ToList();
            if (slices.Count > 8)
            {
                var rest = slices.Skip(7).ToList();
                slices = slices.Take(7).ToList();
                slices.Add(new Slice { Label = "Other", Value = rest.Sum(x => x.Value), Detail = string.Join(", ", rest.Select(x => x.Label)) });
            }
            var total = slices.Sum(s => s.Value);
            if (slices.Count < 2) return new StackPanel(); // one slice = nothing to compare (e.g. filtered to one store)
            var panel = new StackPanel { Margin = new Thickness(0, 0, 30, 10) };
            panel.Children.Add(T(title, 15, bold: true));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var canvas = new Grid { Width = size, Height = size };
            if (total <= 0) { panel.Children.Add(T("No data", 12, dim: true)); return panel; }

            double start = -90, r = size / 2, inner = r * 0.62;
            for (var i = 0; i < slices.Count; i++)
            {
                var s = slices[i];
                var sweep = 360 * s.Value / total;
                var path = Arc(r, inner, start, Math.Min(sweep, 359.99));
                path.Fill = SliceBrush(s, i);
                path.ToolTip = $"{s.Label}: {fmt(s.Value)} ({100 * s.Value / total:0}%)" + (s.Detail != null ? Environment.NewLine + s.Detail : "");
                Hover(path, s.OnClick);
                canvas.Children.Add(path);
                start += sweep;
            }
            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
            var c = T(centre, 20, bold: true); c.HorizontalAlignment = HorizontalAlignment.Center; c.TextWrapping = TextWrapping.NoWrap;
            var cs = T(centreSub, 11, dim: true); cs.HorizontalAlignment = HorizontalAlignment.Center; cs.TextAlignment = TextAlignment.Center; cs.MaxWidth = inner * 1.6;
            mid.Children.Add(c); mid.Children.Add(cs);
            canvas.Children.Add(mid);
            row.Children.Add(canvas);

            var legend = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            for (var i = 0; i < slices.Count; i++)
            {
                var s = slices[i];
                var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent };
                item.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = SliceBrush(s, i), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                var label = T($"{s.Label}", 12); label.TextWrapping = TextWrapping.NoWrap;
                item.Children.Add(label);
                var val = T($"  {fmt(s.Value)} · {100 * s.Value / total:0}%", 12, dim: true); val.TextWrapping = TextWrapping.NoWrap;
                item.Children.Add(val);
                item.ToolTip = s.Detail;
                Hover(item, s.OnClick);
                legend.Children.Add(item);
            }
            row.Children.Add(legend);
            panel.Children.Add(row);
            return panel;
        }

        private static Path Arc(double r, double inner, double startDeg, double sweepDeg)
        {
            Point P(double rad, double deg) => new Point(r + rad * Math.Cos(deg * Math.PI / 180), r + rad * Math.Sin(deg * Math.PI / 180));
            var large = sweepDeg > 180;
            var fig = new PathFigure { StartPoint = P(r, startDeg), IsClosed = true };
            fig.Segments.Add(new ArcSegment(P(r, startDeg + sweepDeg), new Size(r, r), 0, large, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(P(inner, startDeg + sweepDeg), true));
            fig.Segments.Add(new ArcSegment(P(inner, startDeg), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true));
            return new Path { Data = new PathGeometry(new[] { fig }) };
        }

        public class Column
        {
            public string Label { get; set; }
            public double A { get; set; }
            public double B { get; set; }          // optional second series
            public string Tip { get; set; }
            public Action OnClick { get; set; }
        }

        /// <summary>Vertical columns, optionally two series side by side, value labels on top.</summary>
        public static FrameworkElement Columns(string title, string subtitle, IList<Column> cols, string seriesA, string seriesB = null, Func<double, string> fmt = null, double height = 160)
        {
            fmt = fmt ?? Fmt;
            var panel = new StackPanel { Margin = new Thickness(0, 0, 30, 14) };
            panel.Children.Add(T(title, 15, bold: true));
            if (subtitle != null) panel.Children.Add(T(subtitle, 11, dim: true));
            if (cols.Count == 0) { panel.Children.Add(T("No data", 12, dim: true)); return panel; }
            var max = Math.Max(1e-9, cols.Max(c => Math.Max(c.A, c.B)));
            var two = seriesB != null;
            var chart = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            foreach (var c in cols)
            {
                var col = new StackPanel { Margin = new Thickness(4, 0, 4, 0), Background = Brushes.Transparent, ToolTip = c.Tip ?? $"{c.Label}: {fmt(c.A)}" + (two ? $" / {fmt(c.B)}" : "") };
                var bars = new StackPanel { Orientation = Orientation.Horizontal, Height = height, HorizontalAlignment = HorizontalAlignment.Center };
                foreach (var (v, i) in new[] { (c.A, 0) }.Concat(two ? new[] { (c.B, 1) } : new (double, int)[0]))
                {
                    var stack = new DockPanel { Margin = new Thickness(1, 0, 1, 0), LastChildFill = false, MinWidth = two ? 20 : 30 };
                    var bar = new Border { Width = two ? 18 : 30, Height = Math.Max(v > 0 ? 2 : 0, (height - 18) * v / max), Background = SeriesBrush(i), CornerRadius = new CornerRadius(3, 3, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
                    DockPanel.SetDock(bar, Dock.Bottom);
                    var lbl = T(v > 0 ? fmt(v) : "", 10, dim: true); lbl.HorizontalAlignment = HorizontalAlignment.Center; lbl.TextWrapping = TextWrapping.NoWrap;
                    DockPanel.SetDock(lbl, Dock.Bottom);
                    stack.Children.Add(bar);
                    stack.Children.Add(lbl);
                    bars.Children.Add(stack);
                }
                col.Children.Add(bars);
                var name = T(c.Label, 11, dim: true); name.HorizontalAlignment = HorizontalAlignment.Center; name.TextWrapping = TextWrapping.NoWrap;
                col.Children.Add(name);
                Hover(col, c.OnClick);
                chart.Children.Add(col);
            }
            panel.Children.Add(new ScrollViewer { Content = chart, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
            if (two)
            {
                var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 6, 0, 0) };
                foreach (var (name, i) in new[] { (seriesA, 0), (seriesB, 1) })
                {
                    legend.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Background = SeriesBrush(i), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                    var t = T(name, 11, dim: true); t.Margin = new Thickness(0, 0, 16, 0);
                    legend.Children.Add(t);
                }
                panel.Children.Add(legend);
            }
            return panel;
        }

        public class Tile
        {
            public string Title { get; set; }
            public string Line1 { get; set; }
            public string Line2 { get; set; }
            public string CoverPath { get; set; }
            public string Tip { get; set; }
            public Action OnClick { get; set; }
        }

        /// <summary>Cover-art tiles (2:3), title + two meta lines under each. Wraps to width.</summary>
        public static FrameworkElement Tiles(IEnumerable<Tile> tiles, double width = 120)
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
            foreach (var t in tiles)
            {
                var card = new StackPanel { Width = width, Margin = new Thickness(0, 0, 14, 14), Background = Brushes.Transparent, ToolTip = t.Tip };
                var art = new Border { Width = width, Height = width * 1.5, CornerRadius = new CornerRadius(6), ClipToBounds = true };
                art.SetResourceReference(Border.BackgroundProperty, "ControlBackgroundBrush");
                var img = Load(t.CoverPath, (int)width * 2);
                if (img != null) art.Child = new Image { Source = img, Stretch = Stretch.UniformToFill };
                else
                {
                    var ph = T(t.Title, 13, bold: true); ph.Margin = new Thickness(8); ph.VerticalAlignment = VerticalAlignment.Center; ph.TextAlignment = TextAlignment.Center;
                    art.Child = ph;
                }
                card.Children.Add(art);
                var title = T(t.Title, 12, bold: true); title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; title.Margin = new Thickness(0, 6, 0, 0);
                card.Children.Add(title);
                if (t.Line1 != null) { var l = T(t.Line1, 11, dim: true); l.TextWrapping = TextWrapping.NoWrap; l.TextTrimming = TextTrimming.CharacterEllipsis; card.Children.Add(l); }
                if (t.Line2 != null) { var l = T(t.Line2, 11, dim: true); l.TextWrapping = TextWrapping.NoWrap; l.TextTrimming = TextTrimming.CharacterEllipsis; card.Children.Add(l); }
                Hover(card, t.OnClick, scale: true);
                wrap.Children.Add(card);
            }
            return wrap;
        }

        private static BitmapImage Load(string path, int decodeWidth)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return null;
            try
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.UriSource = new Uri(path);
                b.DecodePixelWidth = decodeWidth;   // don't hold 4K covers in memory for a 120px tile
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.EndInit();
                b.Freeze();
                return b;
            }
            catch { return null; }
        }

        /// <summary>Hand cursor + slight fade/lift on hover for clickable things.</summary>
        public static void Hover(FrameworkElement e, Action onClick, bool scale = false)
        {
            if (onClick == null) return;
            e.Cursor = Cursors.Hand;
            e.MouseEnter += (s, a) => { e.Opacity = 0.8; if (scale) e.RenderTransform = new TranslateTransform(0, -3); };
            e.MouseLeave += (s, a) => { e.Opacity = 1; if (scale) e.RenderTransform = null; };
            e.MouseLeftButtonUp += (s, a) => { a.Handled = true; onClick(); };
        }

        public static string Fmt(double v) =>
            v >= 10000 ? (v / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k" : v >= 100 ? v.ToString("N0") : v >= 10 ? v.ToString("0") : v.ToString("0.#", CultureInfo.InvariantCulture);
    }
}
