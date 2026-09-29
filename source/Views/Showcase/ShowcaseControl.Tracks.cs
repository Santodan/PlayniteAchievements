using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PlayniteAchievements.Services.Showcase;
using static PlayniteAchievements.Views.Showcase.ShowcaseUiText;

namespace PlayniteAchievements.Views.Showcase
{
    // Row and column insertion and deletion in edit mode. Every track has a strip on both of its
    // outer edges, in the stretch between the grippers: hovering one highlights the track, and a
    // right-click opens its insert/delete menu. The track's size label sits on its strip and
    // answers the same way.
    public partial class ShowcaseControl
    {
        private readonly List<FrameworkElement> _trackStrips = new List<FrameworkElement>();

        // While a track menu is open its track stays highlighted, though the pointer has left the strip.
        private bool _trackMenuOpen;

        private void AddTrackStrips()
        {
            foreach (var vertical in new[] { true, false })
            {
                for (var index = 0; index < PageTrackCount(vertical); index++)
                {
                    AddOverlay(CreateTrackStrip(vertical, index, nearEdge: true));
                    AddOverlay(CreateTrackStrip(vertical, index, nearEdge: false));
                }
            }
        }

        // Wholly inside the overhang outside the grid, so it never takes a click meant for the
        // edge of a block. Column strips run along the top (near) and bottom (far) edges, row
        // strips along the left and right.
        private FrameworkElement CreateTrackStrip(bool vertical, int index, bool nearEdge)
        {
            var strip = new Border { Background = Brushes.Transparent };
            var lastCell = PageTrackCount(!vertical) - 1;
            if (vertical)
            {
                strip.Height = TrackRulerOverhang;
                strip.HorizontalAlignment = HorizontalAlignment.Stretch;
                strip.VerticalAlignment = nearEdge ? VerticalAlignment.Top : VerticalAlignment.Bottom;
                strip.Margin = nearEdge
                    ? new Thickness(0, -TrackRulerOverhang, 0, 0)
                    : new Thickness(0, 0, 0, -TrackRulerOverhang);
                Grid.SetColumn(strip, index);
                Grid.SetRow(strip, nearEdge ? 0 : lastCell);
            }
            else
            {
                strip.Width = TrackRulerOverhang;
                strip.VerticalAlignment = VerticalAlignment.Stretch;
                strip.HorizontalAlignment = nearEdge ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                strip.Margin = nearEdge
                    ? new Thickness(-TrackRulerOverhang, 0, 0, 0)
                    : new Thickness(0, 0, -TrackRulerOverhang, 0);
                Grid.SetRow(strip, index);
                Grid.SetColumn(strip, nearEdge ? 0 : lastCell);
            }

            // Below the grippers (40) and rulers (41): where a gripper overlaps the strip's end,
            // the drag keeps the hit.
            Panel.SetZIndex(strip, 39);
            AttachTrackMenu(strip, vertical, index);
            _trackStrips.Add(strip);
            return strip;
        }

        private void AttachTrackMenu(FrameworkElement element, bool vertical, int index)
        {
            element.MouseEnter += (_, __) =>
            {
                if (!_trackMenuOpen)
                {
                    ShowTrackHighlight(vertical, index);
                }
            };
            element.MouseLeave += (_, __) =>
            {
                if (!_trackMenuOpen)
                {
                    ClearLayoutPreview();
                }
            };
            // Preview, so a ruler's text box never sees the click; handled, so no ancestor's
            // context menu opens behind this one.
            element.PreviewMouseRightButtonUp += (_, args) =>
            {
                args.Handled = true;
                OpenTrackMenu(element, vertical, index);
            };
        }

        private void ShowTrackHighlight(bool vertical, int index)
        {
            ShowLayoutPreview(new[] { TrackCell(vertical, index) }, null);
        }

        private (int Row, int Column, int RowSpan, int ColumnSpan, Thickness Margin) TrackCell(
            bool vertical,
            int index)
        {
            return vertical
                ? (0, index, PageRowCount, 1, BlockInset)
                : (index, 0, 1, PageColumnCount, BlockInset);
        }

        private void OpenTrackMenu(FrameworkElement target, bool vertical, int index)
        {
            if (_disposed || EditLayoutButton.IsChecked != true)
            {
                return;
            }

            var canInsert = PageTrackCount(vertical) < ShowcaseLayoutService.MaxTrackCount;
            var deletion = ShowcaseLayoutService.PreviewDeleteTrack(
                Layout,
                CurrentPage.PageId,
                vertical,
                index);
            var menu = new ContextMenu
            {
                PlacementTarget = target,
                Placement = PlacementMode.MousePoint
            };
            var insertBefore = MenuItem(
                Localize(vertical ? "LOCPlayAch_Showcase_InsertColumnLeft" : "LOCPlayAch_Showcase_InsertRowAbove"),
                () => InsertTrack(vertical, index));
            insertBefore.IsEnabled = canInsert;
            var insertAfter = MenuItem(
                Localize(vertical ? "LOCPlayAch_Showcase_InsertColumnRight" : "LOCPlayAch_Showcase_InsertRowBelow"),
                () => InsertTrack(vertical, index + 1));
            insertAfter.IsEnabled = canInsert;
            var delete = MenuItem(
                Localize(vertical ? "LOCPlayAch_Showcase_DeleteColumn" : "LOCPlayAch_Showcase_DeleteRow"),
                () => DeleteTrack(vertical, index));
            delete.IsEnabled = deletion.IsAllowed;
            // Over Delete, the widgets that would go with the track are marked as well.
            delete.MouseEnter += (_, __) => ShowTrackDeletionPreview(vertical, index, deletion);
            delete.MouseLeave += (_, __) => ShowTrackHighlight(vertical, index);
            menu.Items.Add(insertBefore);
            menu.Items.Add(insertAfter);
            menu.Items.Add(new Separator());
            menu.Items.Add(delete);
            menu.Closed += (_, __) =>
            {
                _trackMenuOpen = false;
                ClearLayoutPreview();
            };

            _trackMenuOpen = true;
            ShowTrackHighlight(vertical, index);
            menu.IsOpen = true;
        }

        private void ShowTrackDeletionPreview(bool vertical, int index, ShowcaseTrackDeletion deletion)
        {
            ShowTrackHighlight(vertical, index);
            foreach (var blockId in deletion.LostBlockIds)
            {
                var block = CurrentPage.Blocks.FirstOrDefault(candidate => string.Equals(
                    candidate.BlockId,
                    blockId,
                    StringComparison.OrdinalIgnoreCase));
                if (block != null)
                {
                    AddLayoutPreviewGhost(
                        (block.Row, block.Column, block.RowSpan, block.ColumnSpan, BlockInset),
                        "PlayAch.Brush.ErrorText");
                }
            }
        }

        private void InsertTrack(bool vertical, int index)
        {
            if (ShowcaseLayoutService.TryInsertTrack(Layout, CurrentPage.PageId, vertical, index))
            {
                SaveAndApplyBlocks();
            }
        }

        // Asks first only when a widget would be deleted; widgets that can step aside just move.
        private void DeleteTrack(bool vertical, int index)
        {
            var deletion = ShowcaseLayoutService.PreviewDeleteTrack(
                Layout,
                CurrentPage.PageId,
                vertical,
                index);
            if (!deletion.IsAllowed)
            {
                return;
            }

            if (deletion.LostWidgetIds.Count > 0 &&
                !ConfirmMessage(string.Format(
                    Localize("LOCPlayAch_Showcase_DeleteTrackConfirm"),
                    string.Join("\n", deletion.LostWidgetIds.Select(id => GetWidgetName(FindWidget(id)))))))
            {
                return;
            }

            if (ShowcaseLayoutService.TryDeleteTrack(Layout, CurrentPage.PageId, vertical, index))
            {
                SaveAndApplyBlocks();
            }
        }
    }
}
