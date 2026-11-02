using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Workshop.Preview;
using PlayniteAchievements.Views.Showcase;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Workshop.Preview
{
    /// <summary>
    /// A previewed showcase page (<see cref="ShowcasePagePreviewModel"/> as the DataContext): its
    /// name, its grid on a 16:9 canvas with each block at its row, column and span and named by
    /// the widget it holds, and thumbnails of the images it bundles.
    /// </summary>
    public partial class ShowcasePreviewControl : UserControl
    {
        public ShowcasePreviewControl()
        {
            InitializeComponent();
            DataContextChanged += (sender, args) => Rebuild();
        }

        private void Rebuild()
        {
            var model = DataContext as ShowcasePagePreviewModel;
            BuildGrid(model);
            ImageList.ItemsSource = model?.Thumbnails ?? Array.Empty<object>();
        }

        private void BuildGrid(ShowcasePagePreviewModel model)
        {
            LayoutGrid.Children.Clear();
            LayoutGrid.RowDefinitions.Clear();
            LayoutGrid.ColumnDefinitions.Clear();
            if (model == null)
            {
                return;
            }

            // Star tracks by the page's weights, as ShowcaseControl.BuildDashboard lays the page out.
            foreach (var weight in Tracks(model.RowWeights))
            {
                LayoutGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(weight, GridUnitType.Star) });
            }

            foreach (var weight in Tracks(model.ColumnWeights))
            {
                LayoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weight, GridUnitType.Star) });
            }

            var rows = LayoutGrid.RowDefinitions.Count;
            var columns = LayoutGrid.ColumnDefinitions.Count;
            var covered = new bool[rows, columns];
            foreach (var block in model.Blocks)
            {
                var row = Clamp(block.Row, 0, rows - 1);
                var column = Clamp(block.Column, 0, columns - 1);
                var rowSpan = Clamp(block.RowSpan, 1, rows - row);
                var columnSpan = Clamp(block.ColumnSpan, 1, columns - column);
                // An empty single cell is drawn with the uncovered ones below.
                if (!block.WidgetKind.HasValue && rowSpan == 1 && columnSpan == 1)
                {
                    continue;
                }

                for (var r = row; r < row + rowSpan; r++)
                {
                    for (var c = column; c < column + columnSpan; c++)
                    {
                        covered[r, c] = true;
                    }
                }

                var container = CreateBlock(block);
                Grid.SetRow(container, row);
                Grid.SetColumn(container, column);
                Grid.SetRowSpan(container, rowSpan);
                Grid.SetColumnSpan(container, columnSpan);
                LayoutGrid.Children.Add(container);
            }

            // A file leaves empty single cells out, as the page fills them back in on load; they
            // are drawn as one layer, as the Showcase page draws them.
            var cells = new List<(int Row, int Column)>();
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    if (!covered[row, column])
                    {
                        cells.Add((row, column));
                    }
                }
            }

            var layer = new ShowcaseCellLayer();
            Grid.SetRowSpan(layer, rows);
            Grid.SetColumnSpan(layer, columns);
            Panel.SetZIndex(layer, -1);
            LayoutGrid.Children.Add(layer);
            layer.Update(cells, editing: false);
        }

        private static IReadOnlyList<double> Tracks(IReadOnlyList<double> weights)
        {
            return weights != null && weights.Count > 0 ? weights : new[] { 1d };
        }

        private static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        /// <summary>A block surface as ShowcaseControl.CreateBlockContainer draws it, labeled with its widget.</summary>
        private static FrameworkElement CreateBlock(ShowcaseBlockPreview block)
        {
            var border = new Border { Margin = new Thickness(4), Padding = new Thickness(8, 6, 8, 6) };
            border.SetResourceReference(Border.CornerRadiusProperty, "PlayAch.Radius.Section");
            border.SetResourceReference(Border.BackgroundProperty, "PlayAch.Brush.GridSurface");
            if (!block.WidgetKind.HasValue)
            {
                return border;
            }

            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var kind = new TextBlock
            {
                Text = KindLabel(block.WidgetKind.Value),
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            kind.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
            label.Children.Add(kind);

            if (!string.IsNullOrWhiteSpace(block.Title))
            {
                var title = new TextBlock
                {
                    Text = block.Title,
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                title.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
                title.SetResourceReference(UIElement.OpacityProperty, "PlayAch.Opacity.Muted");
                label.Children.Add(title);
            }

            border.Child = label;
            return border;
        }

        private static string KindLabel(ShowcaseWidgetKind kind)
        {
            var definition = ShowcaseWidgetCatalog.Definitions.FirstOrDefault(candidate => candidate.Kind == kind);
            return definition == null ? kind.ToString() : ResourceProvider.GetString(definition.NameKey);
        }
    }
}
