using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiveCharts;
using LiveCharts.Wpf;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>One tooltip row: the series swatch, its title, and the hovered value.</summary>
    public sealed class CartesianChartTooltipRow
    {
        public CartesianChartTooltipRow(Brush swatch, string title, string value)
        {
            Swatch = swatch;
            Title = title;
            Value = value;
            HasTitle = !string.IsNullOrWhiteSpace(title);
        }

        public Brush Swatch { get; }

        public string Title { get; }

        public bool HasTitle { get; }

        public string Value { get; }
    }

    /// <summary>
    /// Styled tooltip for the column and line charts, matching <see cref="PieChartTooltip"/>'s
    /// popup look: the hovered X label as a header over one row per series.
    /// <see cref="SurfaceBrush"/>, <see cref="OutlineBrush"/>, and Foreground default to the
    /// plugin popup brushes and can be overridden per chart, so theme-facing charts can pass
    /// Playnite's own resource keys.
    /// </summary>
    public partial class CartesianChartTooltip : UserControl, IChartTooltip
    {
        public static readonly DependencyProperty SurfaceBrushProperty =
            DependencyProperty.Register(
                nameof(SurfaceBrush),
                typeof(Brush),
                typeof(CartesianChartTooltip),
                new PropertyMetadata(null));

        public static readonly DependencyProperty OutlineBrushProperty =
            DependencyProperty.Register(
                nameof(OutlineBrush),
                typeof(Brush),
                typeof(CartesianChartTooltip),
                new PropertyMetadata(null));

        /// <summary>
        /// Optional per-column header texts indexed by the hovered X value. A label axis that
        /// blanks the slots between ticks still gets a full date or range in the tooltip.
        /// </summary>
        public static readonly DependencyProperty HeaderLabelsProperty =
            DependencyProperty.Register(
                nameof(HeaderLabels),
                typeof(IList<string>),
                typeof(CartesianChartTooltip),
                new PropertyMetadata(null));

        private TooltipData _data;
        private string _header;
        private IReadOnlyList<CartesianChartTooltipRow> _rows = Array.Empty<CartesianChartTooltipRow>();

        public CartesianChartTooltip()
        {
            InitializeComponent();
            Focusable = false;
            IsHitTestVisible = false;
            IsTabStop = false;

            // Resource references are local values, so a brush set on the chart's usage wins.
            SetResourceReference(SurfaceBrushProperty, "PlayAch.Brush.PopupSurface");
            SetResourceReference(OutlineBrushProperty, "PlayAch.Brush.PopupBorder");
            SetResourceReference(ForegroundProperty, "PlayAch.Brush.Text");
        }

        public Brush SurfaceBrush
        {
            get => (Brush)GetValue(SurfaceBrushProperty);
            set => SetValue(SurfaceBrushProperty, value);
        }

        public Brush OutlineBrush
        {
            get => (Brush)GetValue(OutlineBrushProperty);
            set => SetValue(OutlineBrushProperty, value);
        }

        public IList<string> HeaderLabels
        {
            get => (IList<string>)GetValue(HeaderLabelsProperty);
            set => SetValue(HeaderLabelsProperty, value);
        }

        public TooltipSelectionMode? SelectionMode { get; set; } = TooltipSelectionMode.SharedXValues;

        public TooltipData Data
        {
            get => _data;
            set
            {
                _data = value;
                OnPropertyChanged();
                Header = BuildHeader(value);
                Rows = BuildRows(value);
            }
        }

        public string Header
        {
            get => _header;
            private set
            {
                _header = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasHeader));
            }
        }

        public bool HasHeader => !string.IsNullOrWhiteSpace(Header);

        public IReadOnlyList<CartesianChartTooltipRow> Rows
        {
            get => _rows;
            private set
            {
                _rows = value ?? Array.Empty<CartesianChartTooltipRow>();
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // HeaderLabels win when the host supplies them; otherwise the X formatter is the axis's
        // own (its Labels when it has them), so the header reads like the axis tick under the
        // hovered column. A label axis returns an empty string past the end of its list.
        private string BuildHeader(TooltipData data)
        {
            var point = data?.Points?.FirstOrDefault()?.ChartPoint;
            var x = data?.SharedValue ?? point?.X;
            if (!x.HasValue)
            {
                return null;
            }

            var headers = HeaderLabels;
            if (headers != null)
            {
                var index = (int)Math.Round(x.Value);
                if (index >= 0 && index < headers.Count && !string.IsNullOrWhiteSpace(headers[index]))
                {
                    return headers[index];
                }
            }

            return data.XFormatter?.Invoke(x.Value);
        }

        private static IReadOnlyList<CartesianChartTooltipRow> BuildRows(TooltipData data)
        {
            if (data?.Points == null)
            {
                return Array.Empty<CartesianChartTooltipRow>();
            }

            return data.Points
                .Where(point => point?.ChartPoint != null)
                .Select(point => new CartesianChartTooltipRow(
                    point.Series?.Stroke ?? point.Series?.Fill,
                    point.Series?.Title,
                    FormatValue(data, point.ChartPoint.Y)))
                .ToList();
        }

        private static string FormatValue(TooltipData data, double value)
        {
            if (data?.YFormatter != null)
            {
                return data.YFormatter(value);
            }

            return value.ToString("N0", FormattingCulture.Current);
        }
    }
}
