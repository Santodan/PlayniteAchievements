using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.ViewModels.Tags;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Controls
{
    // The Tags column's icon size: one size for every row, as large as the column width and row
    // height let the row with the most crowded tags fit.
    public partial class AchievementDataGridControl
    {
        // AchievementCellStyle pads cells 10,6; the row style's MinHeight is 88.
        private const double TagCellHorizontalPadding = 20;
        private const double TagCellVerticalPadding = 12;
        private const double AutoRowMinHeight = 88;

        private static readonly DependencyPropertyKey TagIconSizePropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(TagIconSize), typeof(double),
                typeof(AchievementDataGridControl), new PropertyMetadata(16.0));

        /// <summary>Identifies the TagIconSize dependency property: the Tags column's icon size.</summary>
        public static readonly DependencyProperty TagIconSizeProperty = TagIconSizePropertyKey.DependencyProperty;

        public double TagIconSize => (double)GetValue(TagIconSizeProperty);

        private static readonly DependencyProperty TagsColumnWidthProperty =
            DependencyProperty.Register("TagsColumnWidth", typeof(double),
                typeof(AchievementDataGridControl), new PropertyMetadata(0.0, OnTagIconLayoutChanged));

        private static readonly DependencyProperty TagsColumnVisibilityProperty =
            DependencyProperty.Register("TagsColumnVisibility", typeof(Visibility),
                typeof(AchievementDataGridControl), new PropertyMetadata(Visibility.Visible, OnTagIconLayoutChanged));

        private bool _tagIconFitQueued;

        private static void OnTagIconLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
            ((AchievementDataGridControl)d).RequestTagIconFit();

        private void ObserveTagIconLayout()
        {
            SetBinding(TagsColumnWidthProperty, new Binding(nameof(DataGridColumn.ActualWidth)) { Source = TagsColumn });
            SetBinding(TagsColumnVisibilityProperty, new Binding(nameof(DataGridColumn.Visibility)) { Source = TagsColumn });
            ((INotifyCollectionChanged)AchievementsDataGrid.Items).CollectionChanged += (s, e) => RequestTagIconFit();
        }

        // Column resizes and collection batches arrive many at a time; one pass after them all.
        private void RequestTagIconFit()
        {
            if (_tagIconFitQueued)
            {
                return;
            }

            _tagIconFitQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _tagIconFitQueued = false;
                UpdateTagIconSize();
            }), DispatcherPriority.Background);
        }

        private void UpdateTagIconSize()
        {
            var display = TagDisplay;
            if ((display & AchievementTagDisplay.Icons) == 0 ||
                TagsColumn.Visibility != Visibility.Visible ||
                TagsColumn.ActualWidth <= 0)
            {
                return;
            }

            var family = TryFindResource("PlayAch.FontFamily.Body") as FontFamily ?? SystemFonts.MessageFontFamily;
            var fontSize = TryFindResource("PlayAch.FontSize.Body") is double size && size > 0 ? size : SystemFonts.MessageFontSize;
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var textHeight = Math.Ceiling(family.LineSpacing * fontSize);

            // One pixel of slack on each axis absorbs layout rounding.
            var width = TagsColumn.ActualWidth - TagCellHorizontalPadding - 1;
            var height = (ResolveFixedRowHeight() ?? AutoRowMinHeight) - TagCellVerticalPadding - 1;
            var best = Math.Floor(Math.Min(width, height));

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var signature = new StringBuilder();
            var boxes = new List<TagChipBox>();
            foreach (var item in AchievementsDataGrid.Items.OfType<AchievementDisplayItem>())
            {
                var chips = item.TagChips;
                if (chips == null || chips.Count == 0)
                {
                    continue;
                }

                signature.Clear();
                boxes.Clear();
                for (var i = 0; i < chips.Count; i++)
                {
                    if (TagChipsPanel.IsIcon(chips[i], display))
                    {
                        signature.Append("\u0001I");
                        boxes.Add(TagChipBox.Icon());
                    }
                    else
                    {
                        var text = TagChipsPanel.TextOf(chips, i, display);
                        signature.Append("\u0001T").Append(text);
                        boxes.Add(TagChipBox.Text(MeasureTagText(text, typeface, fontSize, pixelsPerDip)));
                    }
                }

                if (!seen.Add(signature.ToString()))
                {
                    continue;
                }

                var fit = TagChipLayout.LargestIconSize(boxes, textHeight, width, height, fontSize);
                if (fit.HasValue)
                {
                    best = Math.Min(best, fit.Value);
                }
            }

            SetValue(TagIconSizePropertyKey, Math.Max(Math.Ceiling(fontSize), best));
        }

        private static double MeasureTagText(string text, Typeface typeface, double fontSize, double pixelsPerDip)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, fontSize, Brushes.Black, pixelsPerDip);
            return Math.Ceiling(formatted.WidthIncludingTrailingWhitespace) + 1;
        }
    }
}
