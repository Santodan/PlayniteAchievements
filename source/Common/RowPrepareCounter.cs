using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Playnite.SDK;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Tracing only: counts the rows a grid prepares in one burst (one layout pass, as after a
    /// filter swaps the rows) and logs the count once the burst ends, as
    /// <c>[GridRows] grid= rows= spanMs=</c>. Rows are prepared inside the render pass, which no
    /// scope covers, so the count is what ties that pass's time to a grid. The span runs from
    /// the first to the last row and also holds any other work between them.
    /// </summary>
    public sealed class RowPrepareCounter
    {
        private readonly ILogger _logger;
        private readonly string _grid;
        private readonly Stopwatch _span = new Stopwatch();
        private int _rows;
        private long _lastMs;

        // The burst's first row, whose make-up the flush logs once it has been laid out.
        private WeakReference<DataGridRow> _sampleRow;

        // Rows are prepared and measured one after another, so the gap before each row is
        // roughly what the row before it cost.
        private readonly System.Text.StringBuilder _gaps = new System.Text.StringBuilder();

        public RowPrepareCounter(ILogger logger, string grid)
        {
            _logger = logger;
            _grid = grid;
        }

        public void RowPrepared(Dispatcher dispatcher, DataGridRow row = null)
        {
            if (!PerfScope.PerfTracingEnabled || dispatcher == null)
            {
                return;
            }

            if (_rows == 0)
            {
                _sampleRow = row == null ? null : new WeakReference<DataGridRow>(row);
                _span.Restart();
                // Loaded runs after the render pass that prepares the rows. Background would wait
                // until no input is pending, so a scrollbar drag merged many passes into one burst.
                dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(Flush));
            }

            var now = _span.ElapsedMilliseconds;
            if (_rows > 0)
            {
                _gaps.Append(_gaps.Length > 0 ? "," : string.Empty).Append(now - _lastMs);
            }

            _rows++;
            _lastMs = now;
        }

        // What one row is made of, per column, heaviest first: its visual elements, its images
        // (with the widest decoded bitmap, which shows a cover decoded far above its drawn size),
        // and its shader effects, which re-run on every redraw of the region they cover.
        private void LogCensus(DataGridRow row)
        {
            var columns = new List<(string Name, int Elements, int Images, int MaxPixelWidth, int Effects)>();
            var rowTotals = Count(row);
            foreach (var cell in Descendants(row).OfType<DataGridCell>())
            {
                if (cell.Visibility != Visibility.Visible || cell.Column?.Visibility != Visibility.Visible)
                {
                    continue;
                }

                var name = cell.Column.Header as string;
                if (string.IsNullOrWhiteSpace(name))
                {
                    name = cell.Column.SortMemberPath;
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    name = "#" + cell.Column.DisplayIndex;
                }

                var counts = Count(cell);
                columns.Add((name, counts.Elements, counts.Images, counts.MaxPixelWidth, counts.Effects));
            }

            var detail = string.Join(" | ", columns
                .OrderByDescending(column => column.Elements)
                .Select(column => $"{column.Name}: el={column.Elements} img={column.Images}/{column.MaxPixelWidth}px fx={column.Effects}"));
            _logger?.Debug(
                $"[GridRowCensus] grid={_grid} el={rowTotals.Elements} img={rowTotals.Images}/{rowTotals.MaxPixelWidth}px " +
                $"fx={rowTotals.Effects} height={row.ActualHeight:0} columns: {detail}");
        }

        private static (int Elements, int Images, int MaxPixelWidth, int Effects) Count(DependencyObject root)
        {
            int elements = 0, images = 0, maxPixelWidth = 0, effects = 0;
            foreach (var node in Descendants(root))
            {
                if (node is UIElement element)
                {
                    if (element.Visibility != Visibility.Visible)
                    {
                        continue;
                    }

                    elements++;
                    if (element.Effect != null)
                    {
                        effects++;
                    }
                }

                if (node is Image image)
                {
                    images++;
                    if (image.Source is BitmapSource bitmap)
                    {
                        maxPixelWidth = Math.Max(maxPixelWidth, bitmap.PixelWidth);
                    }
                }
            }

            return (elements, images, maxPixelWidth, effects);
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            var pending = new Stack<DependencyObject>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var node = pending.Pop();
                yield return node;
                if (!(node is Visual) && !(node is System.Windows.Media.Media3D.Visual3D))
                {
                    continue;
                }

                var count = VisualTreeHelper.GetChildrenCount(node);
                for (var i = 0; i < count; i++)
                {
                    pending.Push(VisualTreeHelper.GetChild(node, i));
                }
            }
        }

        private void Flush()
        {
            if (_rows > 0)
            {
                _logger?.Debug($"[GridRows] grid={_grid} rows={_rows} spanMs={_lastMs} gapsMs={_gaps}");
                if (_sampleRow != null && _sampleRow.TryGetTarget(out var sample) && sample.IsLoaded)
                {
                    LogCensus(sample);
                }
            }

            _sampleRow = null;
            _rows = 0;
            _gaps.Clear();
            _span.Reset();
        }
    }
}
