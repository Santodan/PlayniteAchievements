using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.Showcase
{
    /// <summary>
    /// What deleting one row or column would do, computed without touching the layout: which
    /// widget blocks confined to the track step into empty cells beside it, and which widgets
    /// have nowhere to go and would be deleted with it.
    /// </summary>
    public sealed class ShowcaseTrackDeletion
    {
        public static readonly ShowcaseTrackDeletion Refused = new ShowcaseTrackDeletion(
            false,
            Array.Empty<ShowcaseTrackShift>(),
            Array.Empty<ShowcaseBlockSettings>());

        public ShowcaseTrackDeletion(
            bool isAllowed,
            IReadOnlyList<ShowcaseTrackShift> shifts,
            IReadOnlyList<ShowcaseBlockSettings> lostBlocks)
        {
            IsAllowed = isAllowed;
            Shifts = shifts;
            LostBlockIds = lostBlocks.Select(block => block.BlockId).ToList();
            LostWidgetIds = lostBlocks.Select(block => block.WidgetInstanceId).ToList();
        }

        /// <summary>False when the page, track, or axis cannot lose a track (it is at the minimum).</summary>
        public bool IsAllowed { get; }

        /// <summary>Widget blocks that survive by stepping one track toward a neighbour.</summary>
        public IReadOnlyList<ShowcaseTrackShift> Shifts { get; }

        /// <summary>Blocks confined to the track whose widget cannot step aside.</summary>
        public IReadOnlyList<string> LostBlockIds { get; }

        /// <summary>The widgets of <see cref="LostBlockIds"/>, deleted with the track.</summary>
        public IReadOnlyList<string> LostWidgetIds { get; }
    }

    /// <summary>A block that moves one track: -1 toward the preceding track (up or left), +1 toward the following one.</summary>
    public sealed class ShowcaseTrackShift
    {
        public ShowcaseTrackShift(string blockId, int direction)
        {
            BlockId = blockId;
            Direction = direction;
        }

        public string BlockId { get; }

        public int Direction { get; }
    }

    // Row and column insertion and deletion. Every operation takes an axis (vertical = columns,
    // as in TrySplit and the track grippers) and works on any track, not just the edges.
    public static partial class ShowcaseLayoutService
    {
        /// <summary>
        /// Inserts an empty track before <paramref name="index"/> (0..count; count appends).
        /// Blocks past the line move over, blocks that straddle it grow by one track so their
        /// widget stays whole, and the new cells nothing covers become empty 1x1 blocks.
        /// </summary>
        public static bool TryInsertTrack(
            ShowcaseSettings settings,
            string pageId,
            bool vertical,
            int index)
        {
            Normalize(settings);
            var page = FindPage(settings, pageId);
            var axis = new TrackAxis(vertical);
            var count = page == null ? 0 : axis.Count(page);
            if (page == null || index < 0 || index > count || count >= MaxTrackCount)
            {
                return false;
            }

            foreach (var block in page.Blocks)
            {
                var start = axis.Start(block);
                if (start >= index)
                {
                    axis.SetStart(block, start + 1);
                }
                else if (index < start + axis.Span(block))
                {
                    axis.SetSpan(block, axis.Span(block) + 1);
                }
            }

            // The new track takes its neighbour's size, so it reads as one of them on a page
            // whose tracks have been resized.
            var weights = axis.Weights(page);
            weights?.Insert(index, weights[index > 0 ? index - 1 : 0]);
            axis.SetCount(page, count + 1);
            FillTrackGaps(page);
            return IsValidPartition(page.Blocks, page.RowCount, page.ColumnCount);
        }

        /// <summary>
        /// Non-mutating preview of <see cref="TryDeleteTrack"/>: never calls Normalize, never
        /// edits blocks or widgets, so hover and confirmation code can call it freely.
        /// </summary>
        public static ShowcaseTrackDeletion PreviewDeleteTrack(
            ShowcaseSettings settings,
            string pageId,
            bool vertical,
            int index)
        {
            return ComputeTrackDeletion(FindPage(settings, pageId), new TrackAxis(vertical), index);
        }

        /// <summary>
        /// Deletes one track. Blocks that span it shrink by one track; widget blocks confined to
        /// it step into the preceding track, else the following one, when every cell they land
        /// on is empty; the widgets of the rest are deleted (see <see cref="PreviewDeleteTrack"/>).
        /// </summary>
        public static bool TryDeleteTrack(
            ShowcaseSettings settings,
            string pageId,
            bool vertical,
            int index)
        {
            Normalize(settings);
            var page = FindPage(settings, pageId);
            var axis = new TrackAxis(vertical);
            var deletion = ComputeTrackDeletion(page, axis, index);
            if (!deletion.IsAllowed)
            {
                return false;
            }

            // Step the survivors aside first, clearing the empty blocks they land on; the cells
            // those blocks also covered refill as 1x1 empties below.
            foreach (var shift in deletion.Shifts)
            {
                var block = FindBlock(page, shift.BlockId);
                var target = index + shift.Direction;
                page.Blocks.RemoveAll(other =>
                    !ReferenceEquals(other, block) &&
                    IntersectsTrack(axis, other, target, block));
                axis.SetStart(block, target);
            }

            var removed = new List<ShowcaseBlockSettings>();
            foreach (var block in page.Blocks)
            {
                var start = axis.Start(block);
                var span = axis.Span(block);
                if (start > index)
                {
                    axis.SetStart(block, start - 1);
                }
                else if (index < start + span)
                {
                    if (span > 1)
                    {
                        axis.SetSpan(block, span - 1);
                    }
                    else
                    {
                        removed.Add(block);
                    }
                }
            }

            foreach (var block in removed)
            {
                page.Blocks.Remove(block);
            }

            foreach (var widgetId in deletion.LostWidgetIds)
            {
                var widget = FindWidget(settings, widgetId);
                if (widget != null)
                {
                    settings.WidgetInstances.Remove(widget);
                }
            }

            axis.Weights(page)?.RemoveAt(index);
            axis.SetCount(page, axis.Count(page) - 1);
            FillTrackGaps(page);
            return IsValidPartition(page.Blocks, page.RowCount, page.ColumnCount);
        }

        private static ShowcaseTrackDeletion ComputeTrackDeletion(
            ShowcasePageSettings page,
            TrackAxis axis,
            int index)
        {
            var count = page?.Blocks == null ? 0 : axis.Count(page);
            if (count <= MinTrackCount || index < 0 || index >= count)
            {
                return ShowcaseTrackDeletion.Refused;
            }

            var shifts = new List<ShowcaseTrackShift>();
            var lost = new List<ShowcaseBlockSettings>();
            foreach (var block in page.Blocks.Where(block =>
                         block != null &&
                         axis.Start(block) == index &&
                         axis.Span(block) == 1 &&
                         !string.IsNullOrWhiteSpace(block.WidgetInstanceId)))
            {
                var direction = new[] { -1, 1 }.FirstOrDefault(candidate =>
                    CanStepInto(page, axis, block, index + candidate, count));
                if (direction == 0)
                {
                    lost.Add(block);
                }
                else
                {
                    shifts.Add(new ShowcaseTrackShift(block.BlockId, direction));
                }
            }

            return new ShowcaseTrackDeletion(true, shifts, lost);
        }

        // The cells the block would cover in the target track all belong to empty blocks. Blocks
        // confined to the deleted track share no cells, so their targets never collide.
        private static bool CanStepInto(
            ShowcasePageSettings page,
            TrackAxis axis,
            ShowcaseBlockSettings block,
            int target,
            int count)
        {
            return target >= 0 &&
                   target < count &&
                   page.Blocks.All(other =>
                       other == null ||
                       ReferenceEquals(other, block) ||
                       !IntersectsTrack(axis, other, target, block) ||
                       string.IsNullOrWhiteSpace(other.WidgetInstanceId));
        }

        // Whether the block covers any cell of the given track within the reference block's
        // extent on the cross axis.
        private static bool IntersectsTrack(
            TrackAxis axis,
            ShowcaseBlockSettings block,
            int track,
            ShowcaseBlockSettings reference)
        {
            return ShowcaseGeometry.RangesOverlap(axis.Start(block), axis.Span(block), track, 1) &&
                   ShowcaseGeometry.RangesOverlap(
                       axis.CrossStart(block),
                       axis.CrossSpan(block),
                       axis.CrossStart(reference),
                       axis.CrossSpan(reference));
        }

        // Covers every cell no block holds with an empty 1x1 block, in row-major order.
        private static void FillTrackGaps(ShowcasePageSettings page)
        {
            page.Blocks = NormalizeBlocks(
                page.Blocks,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                page.RowCount,
                page.ColumnCount);
        }

        // Reads and writes a block's position along one axis, so each track operation is
        // written once for rows and columns.
        private readonly struct TrackAxis
        {
            private readonly bool _vertical;

            public TrackAxis(bool vertical)
            {
                _vertical = vertical;
            }

            public int Start(ShowcaseBlockSettings block) => _vertical ? block.Column : block.Row;

            public int Span(ShowcaseBlockSettings block) => _vertical ? block.ColumnSpan : block.RowSpan;

            public int CrossStart(ShowcaseBlockSettings block) => _vertical ? block.Row : block.Column;

            public int CrossSpan(ShowcaseBlockSettings block) => _vertical ? block.RowSpan : block.ColumnSpan;

            public void SetStart(ShowcaseBlockSettings block, int value)
            {
                if (_vertical)
                {
                    block.Column = value;
                }
                else
                {
                    block.Row = value;
                }
            }

            public void SetSpan(ShowcaseBlockSettings block, int value)
            {
                if (_vertical)
                {
                    block.ColumnSpan = value;
                }
                else
                {
                    block.RowSpan = value;
                }
            }

            public int Count(ShowcasePageSettings page) => _vertical ? page.ColumnCount : page.RowCount;

            public void SetCount(ShowcasePageSettings page, int value)
            {
                if (_vertical)
                {
                    page.ColumnCount = value;
                }
                else
                {
                    page.RowCount = value;
                }
            }

            public List<double> Weights(ShowcasePageSettings page) =>
                _vertical ? page.ColumnWeights : page.RowWeights;
        }
    }
}
