using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.Showcase
{
    public static partial class ShowcaseLayoutService
    {
        /// <summary>The fewest rows or columns a page can have.</summary>
        public const int MinTrackCount = 1;

        /// <summary>
        /// The most rows or columns a stored page can have. Editing is limited by
        /// <see cref="MinTrackPixels"/> instead, which stops well short of this on any screen;
        /// this only bounds what a settings file or an imported page can ask for.
        /// </summary>
        public const int MaxStoredTrackCount = 384;

        /// <summary>
        /// The narrowest a row or column can be made by inserting tracks or resizing them. A
        /// smaller window can still show tracks below it.
        /// </summary>
        public const double MinTrackPixels = 12;

        /// <summary>Rows and columns of a new page, and of a persisted page that records neither.</summary>
        public const int DefaultTrackCount = 5;

        /// <summary>Clamps a persisted row or column count into [MinTrackCount, MaxStoredTrackCount].</summary>
        public static int NormalizeTrackCount(int count)
        {
            return Math.Max(MinTrackCount, Math.Min(MaxStoredTrackCount, count));
        }

        /// <summary>
        /// The id an implicit empty cell gets. Pages store only blocks that hold a widget or span
        /// more than one cell; every other cell is an empty 1x1 block made on request under this
        /// id, so the same cell always answers to the same id.
        /// </summary>
        public static string CellBlockId(string pageId, int row, int column) =>
            $"{pageId}-{row}-{column}";

        private static bool TryParseCellId(string pageId, string blockId, out int row, out int column)
        {
            row = -1;
            column = -1;
            var prefix = pageId + "-";
            if (pageId == null ||
                blockId == null ||
                !blockId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var parts = blockId.Substring(prefix.Length).Split('-');
            return parts.Length == 2 &&
                   int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out row) &&
                   int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out column);
        }

        private static ShowcaseBlockSettings ImplicitCell(ShowcasePageSettings page, int row, int column) =>
            new ShowcaseBlockSettings
            {
                BlockId = CellBlockId(page.PageId, row, column),
                Row = row,
                Column = column
            };

        private static bool Covers(ShowcaseBlockSettings block, int row, int column) =>
            block != null &&
            row >= block.Row && row < block.Row + block.RowSpan &&
            column >= block.Column && column < block.Column + block.ColumnSpan;

        /// <summary>
        /// The block covering a cell: the stored block there, or the cell's implicit empty block
        /// (a new object each call). Null outside the grid.
        /// </summary>
        public static ShowcaseBlockSettings ResolveCell(ShowcasePageSettings page, int row, int column)
        {
            if (page == null ||
                row < 0 || column < 0 ||
                row >= NormalizeTrackCount(page.RowCount) ||
                column >= NormalizeTrackCount(page.ColumnCount))
            {
                return null;
            }

            return page.Blocks?.FirstOrDefault(block => Covers(block, row, column)) ??
                   ImplicitCell(page, row, column);
        }

        /// <summary>
        /// The block with this id: a stored block, or the implicit empty block of the cell the id
        /// names while no stored block covers that cell.
        /// </summary>
        public static ShowcaseBlockSettings ResolveBlock(ShowcasePageSettings page, string blockId)
        {
            var stored = page?.Blocks?.FirstOrDefault(block =>
                string.Equals(block?.BlockId, blockId, StringComparison.OrdinalIgnoreCase));
            if (stored != null || page == null || !TryParseCellId(page.PageId, blockId, out var row, out var column))
            {
                return stored;
            }

            var cell = ResolveCell(page, row, column);
            return cell != null && cell.IsImplicit && cell.Row == row && cell.Column == column &&
                   string.Equals(cell.BlockId, blockId, StringComparison.OrdinalIgnoreCase)
                ? cell
                : null;
        }

        /// <summary>
        /// For each cell, the stored block covering it, or null where the cell is implicit.
        /// </summary>
        public static ShowcaseBlockSettings[,] CoverageOf(ShowcasePageSettings page)
        {
            var rows = NormalizeTrackCount(page?.RowCount ?? 0);
            var columns = NormalizeTrackCount(page?.ColumnCount ?? 0);
            var coverage = new ShowcaseBlockSettings[rows, columns];
            foreach (var block in page?.Blocks ?? new List<ShowcaseBlockSettings>())
            {
                if (block == null || !IsValidBlock(block, rows, columns))
                {
                    continue;
                }

                for (var row = block.Row; row < block.Row + block.RowSpan; row++)
                {
                    for (var column = block.Column; column < block.Column + block.ColumnSpan; column++)
                    {
                        coverage[row, column] = coverage[row, column] ?? block;
                    }
                }
            }

            return coverage;
        }

        /// <summary>
        /// Every block of the page in reading order, implicit empty cells included as new
        /// objects. For callers that need the whole grid at once, such as a file projection.
        /// </summary>
        public static List<ShowcaseBlockSettings> DenseBlocks(ShowcasePageSettings page)
        {
            var coverage = CoverageOf(page);
            var result = new List<ShowcaseBlockSettings>();
            for (var row = 0; row < coverage.GetLength(0); row++)
            {
                for (var column = 0; column < coverage.GetLength(1); column++)
                {
                    var block = coverage[row, column];
                    if (block == null)
                    {
                        result.Add(ImplicitCell(page, row, column));
                    }
                    else if (block.Row == row && block.Column == column)
                    {
                        result.Add(block);
                    }
                }
            }

            return result;
        }

        // Makes a resolved block part of the page, so a change to it is kept: an implicit cell
        // that takes a widget or a span becomes a stored block under its cell id.
        private static void Attach(ShowcasePageSettings page, ShowcaseBlockSettings block)
        {
            if (block != null && !page.Blocks.Contains(block))
            {
                page.Blocks.Add(block);
            }
        }

        /// <summary>
        /// Whether inserting a track at <paramref name="index"/> keeps every track on that axis
        /// at least <see cref="MinTrackPixels"/> across <paramref name="extentPixels"/>, the
        /// grid's current width (columns) or height (rows). An unknown extent (0) allows it.
        /// </summary>
        public static bool CanInsertTrack(
            ShowcasePageSettings page,
            bool vertical,
            int index,
            double extentPixels)
        {
            if (page == null)
            {
                return false;
            }

            var count = NormalizeTrackCount(vertical ? page.ColumnCount : page.RowCount);
            if (index < 0 || index > count || count >= MaxStoredTrackCount)
            {
                return false;
            }

            if (extentPixels <= 0 || double.IsNaN(extentPixels))
            {
                return true;
            }

            var weights = NormalizeTrackWeights(vertical ? page.ColumnWeights : page.RowWeights, count).ToList();
            weights.Insert(index, weights[index > 0 ? index - 1 : 0]);
            return extentPixels * weights.Min() / weights.Sum() >= MinTrackPixels;
        }

        /// <summary>
        /// Bounds on stored track weights. Star weights are relative, so the effective limit is
        /// the ratio between them: no track can be more than MaxTrackWeight / MinTrackWeight
        /// (60) times another.
        /// </summary>
        public const double MinTrackWeight = 0.05;
        public const double MaxTrackWeight = 3.0;

        /// <summary>
        /// Moves <paramref name="deltaStars"/> of weight from the track after
        /// <paramref name="boundary"/> to the track before it, keeping the pair's total. The pair
        /// is clamped so every track stays within the 60:1 ratio of every other; if a track then
        /// exceeds <see cref="MaxTrackWeight"/>, all weights are scaled down together, which keeps
        /// every track's pixel size. With <paramref name="minWeight"/>, neither track of the pair
        /// can shrink below it (one already smaller only cannot shrink further), which is how
        /// <see cref="MinTrackPixels"/> reaches a drag.
        /// </summary>
        public static double[] TransferTrackWeight(
            IReadOnlyList<double> weights,
            int boundary,
            double deltaStars,
            double minWeight = 0)
        {
            var result = weights.ToArray();
            if (boundary < 0 || boundary + 1 >= result.Length)
            {
                return result;
            }

            const double ratio = MaxTrackWeight / MinTrackWeight;
            var pairSum = result[boundary] + result[boundary + 1];
            var lower = Math.Max(pairSum / (ratio + 1), Math.Min(minWeight, result[boundary]));
            var upper = Math.Min(
                ratio * pairSum / (ratio + 1),
                pairSum - Math.Min(minWeight, result[boundary + 1]));
            var others = result.Where((_, index) => index != boundary && index != boundary + 1).ToArray();
            if (others.Length > 0)
            {
                var othersMax = others.Max();
                var othersMin = others.Min();
                lower = Math.Max(lower, Math.Max(othersMax / ratio, pairSum - ratio * othersMin));
                upper = Math.Min(upper, Math.Min(ratio * othersMin, pairSum - othersMax / ratio));
            }

            if (lower > upper)
            {
                return result;
            }

            var first = Math.Min(upper, Math.Max(lower, result[boundary] + deltaStars));
            result[boundary] = first;
            result[boundary + 1] = pairSum - first;

            var max = result.Max();
            if (max > MaxTrackWeight)
            {
                var scale = MaxTrackWeight / max;
                for (var index = 0; index < result.Length; index++)
                {
                    result[index] = Math.Max(MinTrackWeight, result[index] * scale);
                }
            }

            return result;
        }

        /// <summary>
        /// Resolves a page's persisted row or column star weights to exactly one value per
        /// track, clamped to [<see cref="MinTrackWeight"/>, <see cref="MaxTrackWeight"/>].
        /// Null, missing, or invalid entries fall back to 1.
        /// </summary>
        public static double[] NormalizeTrackWeights(
            IReadOnlyList<double> weights,
            int count)
        {
            count = NormalizeTrackCount(count);
            var result = new double[count];
            for (var index = 0; index < count; index++)
            {
                var value = weights != null && index < weights.Count ? weights[index] : 1d;
                if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                {
                    value = 1d;
                }

                result[index] = Math.Min(MaxTrackWeight, Math.Max(MinTrackWeight, value));
            }

            return result;
        }

        /// <summary>Normalized copy of a persisted weight list; null stays null (equal shares).</summary>
        private static List<double> NormalizeTrackWeightList(List<double> weights, int count)
        {
            return weights == null ? null : new List<double>(NormalizeTrackWeights(weights, count));
        }

        public static ShowcaseSettings CreateDefault(
            string scoreCard1 = null,
            string scoreCard2 = null)
        {
            scoreCard1 ??= ScoreCardSlots.Collection;
            scoreCard2 ??= ScoreCardSlots.Prestige;
            var settings = new ShowcaseSettings();
            var page = CreatePage(
                ShowcasePageTemplate.Showcase,
                settings,
                "Showcase",
                scoreCard1,
                scoreCard2);
            settings.Pages.Add(page);
            settings.LastSelectedPageId = page.PageId;
            return settings;
        }

        public static ShowcasePageSettings AddPage(
            ShowcaseSettings settings,
            ShowcasePageTemplate template,
            string name = null)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            settings.Pages = settings.Pages ?? new List<ShowcasePageSettings>();
            settings.WidgetInstances = settings.WidgetInstances ??
                new List<ShowcaseWidgetInstanceSettings>();
            var page = CreatePage(
                template,
                settings,
                name,
                scoreCard1: ScoreCardSlots.Collection,
                scoreCard2: ScoreCardSlots.Prestige);
            settings.Pages.Add(page);
            settings.LastSelectedPageId = page.PageId;
            return page;
        }

        public static bool RenamePage(
            ShowcaseSettings settings,
            string pageId,
            string name)
        {
            Normalize(settings);
            var page = FindPage(settings, pageId);
            var normalizedName = name?.Trim();
            if (page == null || string.IsNullOrWhiteSpace(normalizedName))
            {
                return false;
            }

            page.Name = MakeUniquePageName(settings, normalizedName, page.PageId);
            return true;
        }

        public static bool ResetPage(
            ShowcaseSettings settings,
            string pageId,
            ShowcasePageTemplate template = ShowcasePageTemplate.Showcase)
        {
            Normalize(settings);
            var page = FindPage(settings, pageId);
            if (page == null)
            {
                return false;
            }

            var pageIndex = settings.Pages.IndexOf(page);
            var replacement = CreatePage(
                template,
                settings,
                page.Name,
                scoreCard1: ScoreCardSlots.Collection,
                scoreCard2: ScoreCardSlots.Prestige);
            replacement.PageId = page.PageId;
            replacement.Name = page.Name;
            settings.Pages[pageIndex] = replacement;
            settings.LastSelectedPageId = replacement.PageId;
            PruneOrphanedWidgets(settings);
            return true;
        }

        /// <summary>
        /// Inserts a copy of the page after it. Widgets are cloned under new instance ids and
        /// keep their pin collection; with <paramref name="gridOptions"/>, grid widgets also keep
        /// their column settings.
        /// </summary>
        public static ShowcasePageSettings DuplicatePage(
            ShowcaseSettings settings,
            string pageId,
            string copySuffix = null,
            GridOptionsCatalog gridOptions = null)
        {
            Normalize(settings);
            var source = FindPage(settings, pageId);
            if (source == null)
            {
                return null;
            }

            return InsertPageCopy(
                settings,
                source,
                $"{source.Name} {(string.IsNullOrWhiteSpace(copySuffix) ? "Copy" : copySuffix.Trim())}",
                widgetInstanceId => FindWidget(settings, widgetInstanceId),
                settings.Pages.IndexOf(source) + 1,
                (sourceWidget, copy) => ShowcaseGridSurfaces.CopySurface(gridOptions, sourceWidget, copy));
        }

        /// <summary>
        /// Inserts a page that came from outside this layout (an imported file) after
        /// <paramref name="insertAfterPageId"/>, or last when that page is not found. Every id is
        /// regenerated so the page never collides with existing ones, widgets of unknown kinds
        /// are dropped (their blocks stay empty), and pin-capable widgets point at this layout's
        /// default collections. <paramref name="onWidgetImported"/> receives each source widget
        /// with its imported copy so callers can carry per-instance data across the id change.
        /// </summary>
        public static ShowcasePageSettings ImportPage(
            ShowcaseSettings settings,
            ShowcasePageSettings page,
            IEnumerable<ShowcaseWidgetInstanceSettings> widgets,
            string insertAfterPageId = null,
            Action<ShowcaseWidgetInstanceSettings, ShowcaseWidgetInstanceSettings> onWidgetImported = null)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (page == null)
            {
                return null;
            }

            Normalize(settings);
            var sourceWidgets = new Dictionary<string, ShowcaseWidgetInstanceSettings>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var widget in widgets ?? Array.Empty<ShowcaseWidgetInstanceSettings>())
            {
                if (widget != null &&
                    !string.IsNullOrWhiteSpace(widget.InstanceId) &&
                    Enum.IsDefined(typeof(ShowcaseWidgetKind), widget.Kind) &&
                    !sourceWidgets.ContainsKey(widget.InstanceId.Trim()))
                {
                    sourceWidgets[widget.InstanceId.Trim()] = widget;
                }
            }

            var anchor = FindPage(settings, insertAfterPageId);
            var imported = InsertPageCopy(
                settings,
                page,
                page.Name,
                widgetInstanceId => sourceWidgets.TryGetValue(widgetInstanceId.Trim(), out var widget)
                    ? widget
                    : null,
                anchor == null ? settings.Pages.Count : settings.Pages.IndexOf(anchor) + 1,
                (sourceWidget, copy) =>
                {
                    ShowcaseWidgetOptions.SetPinCollectionId(copy, null);
                    SeedPinCollectionSelection(settings, copy);
                    onWidgetImported?.Invoke(sourceWidget, copy);
                });
            // A file leaves out empty cells; fill them in before anyone reads the page.
            Normalize(settings);
            return imported;
        }

        // Clones the page and every widget its blocks reference under fresh ids. A widget that
        // fills several blocks is cloned once; blocks whose widget cannot be resolved are left
        // empty.
        private static ShowcasePageSettings InsertPageCopy(
            ShowcaseSettings settings,
            ShowcasePageSettings source,
            string preferredName,
            Func<string, ShowcaseWidgetInstanceSettings> resolveWidget,
            int insertIndex,
            Action<ShowcaseWidgetInstanceSettings, ShowcaseWidgetInstanceSettings> onWidgetCopied)
        {
            var copy = source.Clone();
            copy.PageId = NewId();
            copy.Name = MakeUniquePageName(settings, preferredName);

            var widgetIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var block in copy.Blocks)
            {
                block.BlockId = NewId();
                if (string.IsNullOrWhiteSpace(block.WidgetInstanceId))
                {
                    continue;
                }

                if (!widgetIdMap.TryGetValue(block.WidgetInstanceId, out var newWidgetId))
                {
                    var sourceWidget = resolveWidget(block.WidgetInstanceId);
                    if (sourceWidget == null)
                    {
                        block.WidgetInstanceId = null;
                        continue;
                    }

                    var widgetCopy = sourceWidget.Clone();
                    widgetCopy.InstanceId = newWidgetId = NewId();
                    settings.WidgetInstances.Add(widgetCopy);
                    widgetIdMap[block.WidgetInstanceId] = newWidgetId;
                    onWidgetCopied?.Invoke(sourceWidget, widgetCopy);
                }

                block.WidgetInstanceId = newWidgetId;
            }

            settings.Pages.Insert(Math.Max(0, Math.Min(settings.Pages.Count, insertIndex)), copy);
            settings.LastSelectedPageId = copy.PageId;
            return copy;
        }

        public static bool DeletePage(ShowcaseSettings settings, string pageId)
        {
            Normalize(settings);
            if (settings.Pages.Count <= 1)
            {
                return false;
            }

            var page = FindPage(settings, pageId);
            if (page == null)
            {
                return false;
            }

            var oldIndex = settings.Pages.IndexOf(page);
            settings.Pages.Remove(page);
            PruneOrphanedWidgets(settings);
            if (string.Equals(settings.LastSelectedPageId, pageId, StringComparison.OrdinalIgnoreCase))
            {
                settings.LastSelectedPageId =
                    settings.Pages[Math.Min(oldIndex, settings.Pages.Count - 1)].PageId;
            }

            return true;
        }

        public static bool MovePage(ShowcaseSettings settings, string pageId, int direction)
        {
            Normalize(settings);
            var page = FindPage(settings, pageId);
            if (page == null || direction == 0)
            {
                return false;
            }

            var current = settings.Pages.IndexOf(page);
            var target = Math.Max(0, Math.Min(settings.Pages.Count - 1, current + Math.Sign(direction)));
            if (target == current)
            {
                return false;
            }

            settings.Pages.RemoveAt(current);
            settings.Pages.Insert(target, page);
            return true;
        }

        public static bool TrySplit(
            ShowcaseSettings settings,
            string pageId,
            string blockId,
            bool vertical,
            int gridLine)
        {
            Normalize(settings);
            var page = FindPage(settings, pageId);
            var block = FindBlock(page, blockId);
            if (block == null)
            {
                return false;
            }

            if (vertical)
            {
                if (gridLine <= block.Column || gridLine >= block.Column + block.ColumnSpan)
                {
                    return false;
                }

                var right = block.Clone();
                right.BlockId = NewId();
                right.Column = gridLine;
                right.ColumnSpan = block.Column + block.ColumnSpan - gridLine;
                right.WidgetInstanceId = null;
                block.ColumnSpan = gridLine - block.Column;
                if (right.ColumnSpan > block.ColumnSpan)
                {
                    right.WidgetInstanceId = block.WidgetInstanceId;
                    block.WidgetInstanceId = null;
                }

                page.Blocks.Add(right);
            }
            else
            {
                if (gridLine <= block.Row || gridLine >= block.Row + block.RowSpan)
                {
                    return false;
                }

                var bottom = block.Clone();
                bottom.BlockId = NewId();
                bottom.Row = gridLine;
                bottom.RowSpan = block.Row + block.RowSpan - gridLine;
                bottom.WidgetInstanceId = null;
                block.RowSpan = gridLine - block.Row;
                if (bottom.RowSpan > block.RowSpan)
                {
                    bottom.WidgetInstanceId = block.WidgetInstanceId;
                    block.WidgetInstanceId = null;
                }

                page.Blocks.Add(bottom);
            }

            SortBlocks(page);
            return true;
        }

        public static bool TryMerge(
            ShowcaseSettings settings,
            string pageId,
            string firstBlockId,
            string secondBlockId)
        {
            return TryMerge(
                settings,
                pageId,
                firstBlockId,
                secondBlockId,
                preferredWidgetInstanceId: null);
        }

        public static bool TryMerge(
            ShowcaseSettings settings,
            string pageId,
            string firstBlockId,
            string secondBlockId,
            string preferredWidgetInstanceId)
        {
            Normalize(settings);
            var page = FindPage(settings, pageId);
            var first = FindBlock(page, firstBlockId);
            var second = FindBlock(page, secondBlockId);
            var closure = GetMergeClosureCore(page, first, second);
            return closure.Count == 2 && TryMergeClosure(
                settings,
                page,
                first,
                closure,
                preferredWidgetInstanceId);
        }

        public static IReadOnlyList<ShowcaseBlockSettings> GetMergeClosure(
            ShowcaseSettings settings,
            string pageId,
            string firstBlockId,
            string secondBlockId)
        {
            Normalize(settings);
            var page = FindPage(settings, pageId);
            var first = FindBlock(page, firstBlockId);
            var second = FindBlock(page, secondBlockId);
            return GetMergeClosureCore(page, first, second);
        }

        /// <summary>
        /// Non-mutating merge preview: never calls Normalize, never edits blocks or widgets.
        /// Legality is purely geometric (a rectangular closure of shared-edge neighbors);
        /// multi-widget closures are still legal because MergeSelectedWith resolves the
        /// surviving widget behind a confirmation. Used by hover/affordance code that must
        /// not disturb the layout.
        /// </summary>
        public static bool TryGetMergePreview(
            ShowcaseSettings settings,
            string pageId,
            string firstBlockId,
            string secondBlockId,
            out IReadOnlyList<ShowcaseBlockSettings> closure)
        {
            closure = Array.Empty<ShowcaseBlockSettings>();
            var page = FindPage(settings, pageId);
            var first = FindBlock(page, firstBlockId);
            var second = FindBlock(page, secondBlockId);
            var core = GetMergeClosureCore(page, first, second);
            if (core.Count < 2)
            {
                return false;
            }

            // Mirrors TryMergeClosure's rectangularity gate without any of its mutation.
            var row = core.Min(block => block.Row);
            var column = core.Min(block => block.Column);
            var rowEnd = core.Max(block => block.Row + block.RowSpan);
            var columnEnd = core.Max(block => block.Column + block.ColumnSpan);
            if (core.Sum(block => block.RowSpan * block.ColumnSpan) !=
                (rowEnd - row) * (columnEnd - column))
            {
                return false;
            }

            closure = core;
            return true;
        }

        public static bool TryMergeWithFallback(
            ShowcaseSettings settings,
            string pageId,
            string firstBlockId,
            string secondBlockId,
            string preferredWidgetInstanceId = null)
        {
            Normalize(settings);
            var page = FindPage(settings, pageId);
            var first = FindBlock(page, firstBlockId);
            var second = FindBlock(page, secondBlockId);
            var closure = GetMergeClosureCore(page, first, second);
            if (closure.Count < 2)
            {
                return false;
            }

            return TryMergeClosure(
                settings,
                page,
                first,
                closure,
                preferredWidgetInstanceId);
        }

        private static bool TryMergeClosure(
            ShowcaseSettings settings,
            ShowcasePageSettings page,
            ShowcaseBlockSettings survivor,
            IReadOnlyList<ShowcaseBlockSettings> closure,
            string preferredWidgetInstanceId)
        {
            if (survivor == null || closure == null || closure.Count < 2)
            {
                return false;
            }

            var occupiedWidgetIds = closure
                .Where(block => !string.IsNullOrWhiteSpace(block.WidgetInstanceId))
                .Select(block => block.WidgetInstanceId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (occupiedWidgetIds.Count > 1 &&
                !occupiedWidgetIds.Contains(
                    preferredWidgetInstanceId,
                    StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            var survivorWidgetId = occupiedWidgetIds.Count == 0
                ? null
                : occupiedWidgetIds.Count == 1
                    ? occupiedWidgetIds[0]
                    : preferredWidgetInstanceId;
            var row = closure.Min(block => block.Row);
            var column = closure.Min(block => block.Column);
            var rowEnd = closure.Max(block => block.Row + block.RowSpan);
            var columnEnd = closure.Max(block => block.Column + block.ColumnSpan);
            var cellCount = closure.Sum(block => block.RowSpan * block.ColumnSpan);
            if (cellCount != (rowEnd - row) * (columnEnd - column))
            {
                return false;
            }

            Attach(page, survivor);
            survivor.Row = row;
            survivor.Column = column;
            survivor.RowSpan = rowEnd - row;
            survivor.ColumnSpan = columnEnd - column;
            survivor.WidgetInstanceId = survivorWidgetId;
            foreach (var block in closure.Where(block => !ReferenceEquals(block, survivor)).ToList())
            {
                page.Blocks.Remove(block);
            }

            foreach (var removedWidgetId in occupiedWidgetIds.Where(id =>
                         !string.Equals(
                             id,
                             survivorWidgetId,
                             StringComparison.OrdinalIgnoreCase)))
            {
                var removed = FindWidget(settings, removedWidgetId);
                if (removed != null)
                {
                    settings.WidgetInstances.Remove(removed);
                }
            }

            SortBlocks(page);
            return IsValidPartition(page.Blocks, page.RowCount, page.ColumnCount);
        }

        public static bool PlaceWidget(
            ShowcaseSettings settings,
            string pageId,
            string blockId,
            string widgetInstanceId)
        {
            Normalize(settings);
            var placement = ResolvePlacement(settings, pageId, blockId, widgetInstanceId);
            if (placement == null)
            {
                return false;
            }

            var displaced = placement.Target.WidgetInstanceId;
            Attach(placement.TargetPage, placement.Target);
            placement.Target.WidgetInstanceId = placement.Widget.InstanceId;
            if (placement.Source != null &&
                !ReferenceEquals(placement.Source, placement.Target))
            {
                placement.Source.WidgetInstanceId = displaced;
            }

            return true;
        }

        public static bool CanPlaceWidget(
            ShowcaseSettings settings,
            string pageId,
            string blockId,
            string widgetInstanceId)
        {
            return ResolvePlacement(settings, pageId, blockId, widgetInstanceId) != null;
        }

        private static WidgetPlacement ResolvePlacement(
            ShowcaseSettings settings,
            string pageId,
            string blockId,
            string widgetInstanceId)
        {
            var targetPage = FindPage(settings, pageId);
            var target = FindBlock(targetPage, blockId);
            var widget = FindWidget(settings, widgetInstanceId);
            if (target == null || widget == null)
            {
                return null;
            }

            var placement = new WidgetPlacement
            {
                TargetPage = targetPage,
                Target = target,
                Widget = widget
            };
            var resolvedWidgetId = widget.InstanceId;
            foreach (var candidatePage in settings?.Pages ?? new List<ShowcasePageSettings>())
            {
                var source = candidatePage?.Blocks?.FirstOrDefault(block =>
                    string.Equals(block.WidgetInstanceId, resolvedWidgetId, StringComparison.OrdinalIgnoreCase));
                if (source != null)
                {
                    placement.Source = source;
                    break;
                }
            }

            return placement;
        }

        public static ShowcaseWidgetInstanceSettings CreateWidget(
            ShowcaseSettings settings,
            ShowcaseWidgetKind kind)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            var widget = NewWidget(settings, kind);
            settings.WidgetInstances.Add(widget);
            return widget;
        }

        private static void ClearWidgetAssignments(ShowcaseSettings settings, string widgetInstanceId)
        {
            if (settings?.Pages == null || string.IsNullOrWhiteSpace(widgetInstanceId))
            {
                return;
            }

            foreach (var block in settings.Pages
                .Where(page => page?.Blocks != null)
                .SelectMany(page => page.Blocks)
                .Where(block => block != null))
            {
                if (string.Equals(block.WidgetInstanceId, widgetInstanceId, StringComparison.OrdinalIgnoreCase))
                {
                    block.WidgetInstanceId = null;
                }
            }
        }

        public static bool DeleteWidget(ShowcaseSettings settings, string widgetInstanceId)
        {
            if (settings?.WidgetInstances == null || string.IsNullOrWhiteSpace(widgetInstanceId))
            {
                return false;
            }

            ClearWidgetAssignments(settings, widgetInstanceId);
            var widget = FindWidget(settings, widgetInstanceId);
            return widget != null && settings.WidgetInstances.Remove(widget);
        }

        public static int PruneOrphanedWidgets(ShowcaseSettings settings)
        {
            if (settings?.WidgetInstances == null)
            {
                return 0;
            }

            var placedIds = new HashSet<string>(
                (settings.Pages ?? new List<ShowcasePageSettings>())
                    .Where(page => page?.Blocks != null)
                    .SelectMany(page => page.Blocks)
                    .Where(block => !string.IsNullOrWhiteSpace(block?.WidgetInstanceId))
                    .Select(block => block.WidgetInstanceId),
                StringComparer.OrdinalIgnoreCase);
            var removed = settings.WidgetInstances.RemoveAll(widget =>
                widget == null || !placedIds.Contains(widget.InstanceId));
            return removed;
        }

        public static void Normalize(ShowcaseSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            var loadedLayoutVersion = settings.LayoutVersion;
            settings.LayoutVersion = ShowcaseSettings.CurrentLayoutVersion;
            MigrateLegacyProfile(settings);
            settings.WidgetInstances = NormalizeWidgets(settings.WidgetInstances);
            var widgetIds = new HashSet<string>(
                settings.WidgetInstances.Select(widget => widget.InstanceId),
                StringComparer.OrdinalIgnoreCase);

            settings.Pages = settings.Pages ?? new List<ShowcasePageSettings>();
            if (settings.Pages.Count == 0)
            {
                var seeded = CreateDefault();
                settings.Pages = seeded.Pages;
                settings.WidgetInstances = seeded.WidgetInstances;
                widgetIds = new HashSet<string>(
                    settings.WidgetInstances.Select(widget => widget.InstanceId),
                    StringComparer.OrdinalIgnoreCase);
            }

            var pageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var blockIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var placedWidgetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var pageIndex = 0; pageIndex < settings.Pages.Count; pageIndex++)
            {
                var page = settings.Pages[pageIndex];
                if (page == null)
                {
                    page = new ShowcasePageSettings();
                    settings.Pages[pageIndex] = page;
                }
                page.PageId = NormalizeUniqueId(page.PageId, pageIds);
                page.Name = string.IsNullOrWhiteSpace(page.Name)
                    ? $"Page {pageIndex + 1}"
                    : page.Name.Trim();
                NormalizeTrackCounts(page);
                page.Blocks = NormalizeBlocks(page.Blocks, blockIds, page.PageId, page.RowCount, page.ColumnCount);
                page.RowWeights = NormalizeTrackWeightList(page.RowWeights, page.RowCount);
                page.ColumnWeights = NormalizeTrackWeightList(page.ColumnWeights, page.ColumnCount);

                foreach (var block in page.Blocks)
                {
                    if (string.IsNullOrWhiteSpace(block.WidgetInstanceId) ||
                        !widgetIds.Contains(block.WidgetInstanceId) ||
                        placedWidgetIds.Contains(block.WidgetInstanceId))
                    {
                        block.WidgetInstanceId = null;
                        continue;
                    }

                    var widget = FindWidget(settings, block.WidgetInstanceId);
                    if (widget == null)
                    {
                        block.WidgetInstanceId = null;
                        continue;
                    }

                    placedWidgetIds.Add(widget.InstanceId);
                }
            }

            // After the blocks are normalized, so a split halves a block of the final partition.
            if (loadedLayoutVersion < ShowcaseSettings.OneCardScoresLayoutVersion)
            {
                MigrateToOneCardScores(settings);
            }

            if (!settings.Pages.Any(page =>
                string.Equals(page.PageId, settings.LastSelectedPageId, StringComparison.OrdinalIgnoreCase)))
            {
                settings.LastSelectedPageId = settings.Pages[0].PageId;
            }

            settings.DefaultAchievementPinCollectionId = NormalizeCollectionId(
                settings.DefaultAchievementPinCollectionId,
                ShowcaseSettings.BuiltInAchievementCollectionId);
            settings.DefaultGamePinCollectionId = NormalizeCollectionId(
                settings.DefaultGamePinCollectionId,
                ShowcaseSettings.BuiltInGameCollectionId);
            settings.AchievementPinCollections = NormalizeAchievementCollections(
                settings.AchievementPinCollections,
                settings.DefaultAchievementPinCollectionId);
            settings.GamePinCollections = NormalizeGameCollections(
                settings.GamePinCollections,
                settings.DefaultGamePinCollectionId);
            settings.StartPageInstances = NormalizeStartPageInstances(settings.StartPageInstances);
        }

        // Profile data used to live once on the layout; it now lives on each profile widget.
        // A layout saved by an older version still carries the shared object, which seeds every
        // profile widget that has none of its own, then goes away so it can never reseed a card
        // the user has since edited.
        private static void MigrateLegacyProfile(ShowcaseSettings settings)
        {
            var legacy = settings.Profile;
            if (legacy == null)
            {
                return;
            }

            var widgets = (settings.WidgetInstances ?? new List<ShowcaseWidgetInstanceSettings>())
                .Concat(settings.StartPageInstances?.Values ?? Enumerable.Empty<ShowcaseWidgetInstanceSettings>());
            foreach (var widget in widgets)
            {
                if (widget != null && widget.Kind == ShowcaseWidgetKind.Profile && widget.Profile == null)
                {
                    widget.Profile = legacy.Clone();
                }
            }

            settings.Profile = null;
        }

        // Pages used to be square, recording one GridSize for both axes. An unset count takes
        // that legacy value (or the default when the page records neither), and the legacy
        // field then goes away so it can never override a count the user has since changed.
        private static void NormalizeTrackCounts(ShowcasePageSettings page)
        {
            var legacy = page.GridSize ?? DefaultTrackCount;
            page.RowCount = NormalizeTrackCount(page.RowCount > 0 ? page.RowCount : legacy);
            page.ColumnCount = NormalizeTrackCount(page.ColumnCount > 0 ? page.ColumnCount : legacy);
            page.GridSize = null;
        }

        // A profile widget always carries a profile object; no other kind carries one.
        private static void NormalizeProfile(ShowcaseWidgetInstanceSettings widget)
        {
            widget.Profile = widget.Kind == ShowcaseWidgetKind.Profile
                ? widget.Profile ?? new ShowcaseProfileSettings()
                : null;
        }

        /// <summary>Whether every block lies inside the grid and no two overlap.</summary>
        public static bool IsValidPartition(
            IEnumerable<ShowcaseBlockSettings> blocks,
            int rows,
            int columns)
        {
            rows = NormalizeTrackCount(rows);
            columns = NormalizeTrackCount(columns);
            var cells = new bool[rows, columns];
            if (blocks == null)
            {
                return false;
            }

            foreach (var block in blocks)
            {
                if (!IsValidBlock(block, rows, columns))
                {
                    return false;
                }

                for (var row = block.Row; row < block.Row + block.RowSpan; row++)
                {
                    for (var column = block.Column; column < block.Column + block.ColumnSpan; column++)
                    {
                        if (cells[row, column])
                        {
                            return false;
                        }

                        cells[row, column] = true;
                    }
                }
            }

            // Cells no block covers are implicit empty blocks, so coverage is not required.
            return true;
        }

        private static ShowcasePageSettings CreatePage(
            ShowcasePageTemplate template,
            ShowcaseSettings settings,
            string name,
            string scoreCard1,
            string scoreCard2)
        {
            var page = new ShowcasePageSettings
            {
                Name = MakeUniquePageName(
                    settings,
                    string.IsNullOrWhiteSpace(name) ? GetDefaultPageName(template) : name.Trim()),
                RowCount = DefaultTrackCount,
                ColumnCount = DefaultTrackCount
            };

            // Templates are authored directly on the 5x5 lattice with equal tracks (null
            // weights); users refine from there with cuts, merges, and the track grippers.
            switch (template)
            {
                case ShowcasePageTemplate.Showcase:
                    AddBlock(settings, page, 0, 0, 2, 3, ShowcaseWidgetKind.Profile);
                    AddScoreBlock(settings, page, 0, 3, 2, 2, scoreCard1, scoreCard2);
                    AddBlock(
                        settings,
                        page,
                        2,
                        0,
                        3,
                        3,
                        ShowcaseWidgetKind.RecentAchievements,
                        widget => ShowcaseWidgetOptions.SetCollectionType(
                            widget,
                            ShowcaseCollectionType.Achievements));
                    AddBlock(settings, page, 2, 3, 2, 2, ShowcaseWidgetKind.Statistics);
                    AddBlock(
                        settings,
                        page,
                        4,
                        3,
                        1,
                        2,
                        ShowcaseWidgetKind.GameSummaries,
                        widget => ShowcaseWidgetOptions.SetCollectionType(
                            widget,
                            ShowcaseCollectionType.Games));
                    break;
                case ShowcasePageTemplate.Analytics:
                    AddScoreBlock(settings, page, 0, 0, 2, 5, ScoreCardSlots.Collection, ScoreCardSlots.Prestige);
                    AddBlock(settings, page, 2, 0, 3, 3, ShowcaseWidgetKind.ActivityCalendar);
                    AddBlock(settings, page, 2, 3, 1, 2, ShowcaseWidgetKind.Statistics);
                    AddBlock(settings, page, 3, 3, 2, 2, ShowcaseWidgetKind.Pie);
                    break;
                // The add-page presets below draw only on library data, never on pin
                // collections, so a new page is full on its first open.
                case ShowcasePageTemplate.Collection:
                    AddBlock(
                        settings,
                        page,
                        0,
                        0,
                        3,
                        3,
                        ShowcaseWidgetKind.RecentAchievements,
                        widget => ShowcaseWidgetOptions.SetAchievementGridSource(
                            widget,
                            ShowcaseAchievementGridSource.All));
                    AddBlock(
                        settings,
                        page,
                        0,
                        3,
                        3,
                        2,
                        ShowcaseWidgetKind.GameSummaries,
                        widget => ShowcaseWidgetOptions.SetGameGridSource(
                            widget,
                            ShowcaseGameGridSource.Library));
                    AddBlock(
                        settings,
                        page,
                        3,
                        0,
                        2,
                        5,
                        ShowcaseWidgetKind.IconMosaic,
                        widget => ConfigureGameMosaic(widget, ShowcaseGameMosaicSource.All));
                    break;
                case ShowcasePageTemplate.UpNext:
                    AddBlock(
                        settings,
                        page,
                        0,
                        0,
                        3,
                        3,
                        ShowcaseWidgetKind.RecentAchievements,
                        widget => ShowcaseWidgetOptions.SetAchievementGridSource(
                            widget,
                            ShowcaseAchievementGridSource.UnlockNext));
                    AddBlock(
                        settings,
                        page,
                        0,
                        3,
                        3,
                        2,
                        ShowcaseWidgetKind.GameSummaries,
                        widget => ShowcaseWidgetOptions.SetGameGridSource(
                            widget,
                            ShowcaseGameGridSource.FinishNext));
                    AddBlock(
                        settings,
                        page,
                        3,
                        0,
                        2,
                        3,
                        ShowcaseWidgetKind.IconMosaic,
                        widget => ConfigureAchievementMosaic(widget, ShowcaseMosaicSource.UnlockNext));
                    AddBlock(settings, page, 3, 3, 2, 2, ShowcaseWidgetKind.Pie);
                    break;
                case ShowcasePageTemplate.TrophyCase:
                    AddBlock(settings, page, 0, 0, 2, 3, ShowcaseWidgetKind.Profile);
                    AddScoreBlock(settings, page, 0, 3, 2, 2, ScoreCardSlots.Collection, ScoreCardSlots.Prestige);
                    AddBlock(
                        settings,
                        page,
                        2,
                        0,
                        3,
                        3,
                        ShowcaseWidgetKind.IconMosaic,
                        widget =>
                        {
                            ConfigureAchievementMosaic(widget, ShowcaseMosaicSource.All);
                            ShowcaseWidgetOptions.SetMosaicSort(widget, CompactListSortMode.Rarity);
                            ShowcaseWidgetOptions.SetMosaicSortDescending(widget, false);
                        });
                    AddBlock(
                        settings,
                        page,
                        2,
                        3,
                        3,
                        2,
                        ShowcaseWidgetKind.Pie,
                        widget => ShowcaseWidgetOptions.SetPieMode(widget, ShowcasePieMode.Rarity));
                    break;
                case ShowcasePageTemplate.Library:
                    AddBlock(settings, page, 0, 0, 3, 3, ShowcaseWidgetKind.GameSummaries);
                    AddBlock(
                        settings,
                        page,
                        0,
                        3,
                        3,
                        2,
                        ShowcaseWidgetKind.IconMosaic,
                        widget => ConfigureGameMosaic(widget, ShowcaseGameMosaicSource.All));
                    AddBlock(
                        settings,
                        page,
                        3,
                        0,
                        2,
                        2,
                        ShowcaseWidgetKind.Pie,
                        widget => ShowcaseWidgetOptions.SetPieMode(widget, ShowcasePieMode.Provider));
                    AddBlock(settings, page, 3, 2, 2, 3, ShowcaseWidgetKind.Statistics);
                    break;
                default:
                    // A blank page stores nothing: every cell is an implicit empty block.
                    break;
            }

            SortBlocks(page);
            return page;
        }

        // A preset's mosaics fill their block: the default count of 24 leaves most of a large
        // block empty, and extra tiles past the block's area only scroll.
        private const int PresetMosaicCount = 120;

        private static void ConfigureAchievementMosaic(
            ShowcaseWidgetInstanceSettings widget,
            ShowcaseMosaicSource source)
        {
            ShowcaseWidgetOptions.SetMosaicContent(widget, ShowcaseMosaicContent.Achievements);
            ShowcaseWidgetOptions.SetMosaicSource(widget, source);
            ShowcaseWidgetOptions.SetMosaicCount(widget, PresetMosaicCount);
        }

        private static void ConfigureGameMosaic(
            ShowcaseWidgetInstanceSettings widget,
            ShowcaseGameMosaicSource source)
        {
            ShowcaseWidgetOptions.SetMosaicContent(widget, ShowcaseMosaicContent.Games);
            ShowcaseWidgetOptions.SetGameMosaicSource(widget, source);
            ShowcaseWidgetOptions.SetGameMosaicCount(widget, PresetMosaicCount);
        }

        /// <summary>
        /// The template's score area: one Scores widget per requested card, split side by side
        /// as a legacy Both widget is (see <see cref="SplitBlockForSecondCard"/>), or an empty
        /// block when neither card is requested.
        /// </summary>
        private static void AddScoreBlock(
            ShowcaseSettings settings,
            ShowcasePageSettings page,
            int row,
            int column,
            int rowSpan,
            int columnSpan,
            string scoreCard1,
            string scoreCard2)
        {
            var cards = new List<ScoreCardType>();
            foreach (var slot in new[] { scoreCard1, scoreCard2 })
            {
                if (ScoreCardSlots.TryGetCardType(slot, out var type))
                {
                    cards.Add(type);
                }
            }

            var block = NewBlock(row, column, rowSpan, columnSpan, null);
            page.Blocks.Add(block);
            if (cards.Count == 0)
            {
                return;
            }

            var first = NewWidget(settings, ShowcaseWidgetKind.Scores);
            ShowcaseWidgetOptions.SetScoreCardType(first, cards[0]);
            settings.WidgetInstances.Add(first);
            block.WidgetInstanceId = first.InstanceId;
            if (cards.Count < 2)
            {
                return;
            }

            var secondBlock = SplitBlockForSecondCard(block);
            if (secondBlock != null)
            {
                var second = NewWidget(settings, ShowcaseWidgetKind.Scores);
                ShowcaseWidgetOptions.SetScoreCardType(second, cards[1]);
                settings.WidgetInstances.Add(second);
                secondBlock.WidgetInstanceId = second.InstanceId;
                page.Blocks.Add(secondBlock);
            }
        }

        /// <summary>
        /// Halves <paramref name="block"/> to make room for a second card: across its columns when
        /// it spans two or more (the first card keeps the left part), else across its rows when it
        /// spans two or more (the first card keeps the top part). A 1x1 block cannot be halved and
        /// returns null. No track is ever added.
        /// </summary>
        private static ShowcaseBlockSettings SplitBlockForSecondCard(ShowcaseBlockSettings block)
        {
            if (block.ColumnSpan >= 2)
            {
                var left = Math.Max(1, block.ColumnSpan / 2);
                var right = NewBlock(block.Row, block.Column + left, block.RowSpan, block.ColumnSpan - left, null);
                block.ColumnSpan = left;
                return right;
            }

            if (block.RowSpan >= 2)
            {
                var top = Math.Max(1, block.RowSpan / 2);
                var bottom = NewBlock(block.Row + top, block.Column, block.RowSpan - top, block.ColumnSpan, null);
                block.RowSpan = top;
                return bottom;
            }

            return null;
        }

        /// <summary>
        /// Splits every legacy Both Scores widget placed on <paramref name="page"/> into a
        /// Collection widget (in the original block) and a Prestige widget beside or below it.
        /// When the block is 1x1 only the Collection card is kept.
        /// </summary>
        public static void SplitLegacyDualScoreWidgets(ShowcaseSettings settings, ShowcasePageSettings page)
        {
            if (settings == null || page?.Blocks == null)
            {
                return;
            }

            settings.WidgetInstances ??= new List<ShowcaseWidgetInstanceSettings>();
            var split = false;
            foreach (var block in page.Blocks.ToList())
            {
                var widget = FindWidget(settings, block?.WidgetInstanceId);
                if (!ShowcaseWidgetOptions.IsLegacyDualScores(widget))
                {
                    continue;
                }

                var prestige = ShowcaseWidgetOptions.SplitLegacyDualScores(widget);
                var prestigeBlock = SplitBlockForSecondCard(block);
                if (prestige != null && prestigeBlock != null)
                {
                    prestigeBlock.WidgetInstanceId = prestige.InstanceId;
                    settings.WidgetInstances.Add(prestige);
                    page.Blocks.Add(prestigeBlock);
                    split = true;
                }
            }

            if (split)
            {
                SortBlocks(page);
            }
        }

        /// <summary>
        /// One-time move to one card per Scores widget, for layouts saved before
        /// <see cref="ShowcaseSettings.OneCardScoresLayoutVersion"/>: split placed Both widgets,
        /// then write every remaining Scores widget's legacy options (unplaced ones and start
        /// page instances, where a Both widget becomes its Collection card) as one-card options.
        /// </summary>
        private static void MigrateToOneCardScores(ShowcaseSettings settings)
        {
            foreach (var page in settings.Pages ?? new List<ShowcasePageSettings>())
            {
                SplitLegacyDualScoreWidgets(settings, page);
            }

            var widgets = (settings.WidgetInstances ?? new List<ShowcaseWidgetInstanceSettings>())
                .Concat(settings.StartPageInstances?.Values ?? Enumerable.Empty<ShowcaseWidgetInstanceSettings>());
            foreach (var widget in widgets)
            {
                ShowcaseWidgetOptions.MigrateLegacyScoreOptions(widget);
            }
        }
        private static void AddBlock(
            ShowcaseSettings settings,
            ShowcasePageSettings page,
            int row,
            int column,
            int rowSpan,
            int columnSpan,
            ShowcaseWidgetKind kind,
            Action<ShowcaseWidgetInstanceSettings> configure = null)
        {
            var widget = NewWidget(settings, kind);
            configure?.Invoke(widget);
            settings.WidgetInstances.Add(widget);
            page.Blocks.Add(NewBlock(row, column, rowSpan, columnSpan, widget.InstanceId));
        }

        private static ShowcaseWidgetInstanceSettings NewWidget(
            ShowcaseSettings settings,
            ShowcaseWidgetKind kind)
        {
            var widget = ShowcaseWidgetSettingsFactory.CreateDefault(kind);
            SeedPinCollectionSelection(settings, widget);
            return widget;
        }

        /// <summary>Points a pin-capable widget at the layout's default pin collection.</summary>
        internal static void SeedPinCollectionSelection(
            ShowcaseSettings settings,
            ShowcaseWidgetInstanceSettings widget)
        {
            if (settings == null || widget == null)
            {
                return;
            }

            if (widget.Kind == ShowcaseWidgetKind.IconMosaic ||
                widget.Kind == ShowcaseWidgetKind.RecentAchievements)
            {
                ShowcaseWidgetOptions.SetPinCollectionId(
                    widget,
                    settings.DefaultAchievementPinCollectionId);
            }
            else if (widget.Kind == ShowcaseWidgetKind.GameSummaries)
            {
                ShowcaseWidgetOptions.SetPinCollectionId(
                    widget,
                    settings.DefaultGamePinCollectionId);
            }
        }

        private static ShowcaseBlockSettings NewBlock(
            int row,
            int column,
            int rowSpan,
            int columnSpan,
            string widgetInstanceId)
        {
            return new ShowcaseBlockSettings
            {
                Row = row,
                Column = column,
                RowSpan = rowSpan,
                ColumnSpan = columnSpan,
                WidgetInstanceId = widgetInstanceId
            };
        }

        private static List<ShowcaseWidgetInstanceSettings> NormalizeWidgets(
            IEnumerable<ShowcaseWidgetInstanceSettings> widgets)
        {
            var result = new List<ShowcaseWidgetInstanceSettings>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var widget in widgets ?? Array.Empty<ShowcaseWidgetInstanceSettings>())
            {
                if (widget == null || !Enum.IsDefined(typeof(ShowcaseWidgetKind), widget.Kind))
                {
                    continue;
                }

                widget.InstanceId = NormalizeUniqueId(widget.InstanceId, ids);
                widget.Options = widget.Options != null
                    ? new Dictionary<string, string>(widget.Options, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                NormalizeProfile(widget);
                ShowcaseWidgetOptions.MigrateCompletedGameMosaic(widget);
                ShowcaseWidgetOptions.MigrateSlideshowVariant(widget);
                ShowcaseWidgetOptions.MigrateAchievementMosaicSource(widget);
                ShowcaseWidgetOptions.MigrateGameGridScope(widget);
                result.Add(widget);
            }

            return result;
        }

        // Keeps the blocks a page stores, in reading order: those holding a widget or spanning
        // more than one cell, inside the grid and not overlapping an earlier one. Empty 1x1
        // blocks are dropped (ResolveCell recreates them on request), and a stored block that
        // carries the cell id of another cell (it was an implicit cell, then moved) gets a fresh
        // id so that cell's own id stays free.
        private static List<ShowcaseBlockSettings> NormalizeBlocks(
            IEnumerable<ShowcaseBlockSettings> blocks,
            HashSet<string> blockIds,
            string pageId,
            int rows,
            int columns)
        {
            rows = NormalizeTrackCount(rows);
            columns = NormalizeTrackCount(columns);
            var stored = new List<ShowcaseBlockSettings>();
            var occupied = new bool[rows, columns];
            foreach (var block in blocks ?? Array.Empty<ShowcaseBlockSettings>())
            {
                if (block == null ||
                    block.IsImplicit ||
                    !IsValidBlock(block, rows, columns) ||
                    Overlaps(occupied, block))
                {
                    continue;
                }

                if (TryParseCellId(pageId, block.BlockId, out var row, out var column) &&
                    (row != block.Row || column != block.Column))
                {
                    block.BlockId = NewId();
                }

                block.BlockId = NormalizeUniqueId(block.BlockId, blockIds);
                Mark(occupied, block);
                stored.Add(block);
            }

            return stored
                .OrderBy(block => block.Row)
                .ThenBy(block => block.Column)
                .ToList();
        }

        private static List<PinnedAchievementReference> NormalizeAchievementPins(
            IEnumerable<PinnedAchievementReference> pins)
        {
            var result = new List<PinnedAchievementReference>();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pin in pins ?? Array.Empty<PinnedAchievementReference>())
            {
                if (pin == null || pin.GameId == Guid.Empty || string.IsNullOrWhiteSpace(pin.ApiName))
                {
                    continue;
                }

                pin.ApiName = pin.ApiName.Trim();
                if (keys.Add(pin.Key))
                {
                    result.Add(pin);
                }
            }

            return result;
        }

        private static List<PinnedAchievementCollection> NormalizeAchievementCollections(
            IEnumerable<PinnedAchievementCollection> collections,
            string defaultCollectionId)
        {
            var result = new List<PinnedAchievementCollection>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in collections ?? Array.Empty<PinnedAchievementCollection>())
            {
                if (source == null)
                {
                    continue;
                }

                var id = string.IsNullOrWhiteSpace(source.CollectionId)
                    ? NewId()
                    : source.CollectionId.Trim();
                if (!ids.Add(id))
                {
                    id = NormalizeUniqueId(null, ids);
                }

                var fallbackName = string.Equals(id, defaultCollectionId, StringComparison.OrdinalIgnoreCase)
                    ? "Default"
                    : "Collection";
                result.Add(new PinnedAchievementCollection
                {
                    CollectionId = id,
                    Name = MakeUniqueCollectionName(source.Name, fallbackName, names),
                    Pins = NormalizeAchievementPins(source.Pins)
                });
            }

            var defaultCollection = result.FirstOrDefault(collection =>
                string.Equals(collection.CollectionId, defaultCollectionId, StringComparison.OrdinalIgnoreCase));
            if (defaultCollection == null)
            {
                defaultCollection = new PinnedAchievementCollection
                {
                    CollectionId = defaultCollectionId,
                    Name = MakeUniqueCollectionName("Default", "Default", names)
                };
                result.Insert(0, defaultCollection);
            }
            else
            {
                result.Remove(defaultCollection);
                result.Insert(0, defaultCollection);
            }

            return result;
        }

        private static List<PinnedGameCollection> NormalizeGameCollections(
            IEnumerable<PinnedGameCollection> collections,
            string defaultCollectionId)
        {
            var result = new List<PinnedGameCollection>();
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in collections ?? Array.Empty<PinnedGameCollection>())
            {
                if (source == null)
                {
                    continue;
                }

                var id = string.IsNullOrWhiteSpace(source.CollectionId)
                    ? NewId()
                    : source.CollectionId.Trim();
                if (!ids.Add(id))
                {
                    id = NormalizeUniqueId(null, ids);
                }

                var fallbackName = string.Equals(id, defaultCollectionId, StringComparison.OrdinalIgnoreCase)
                    ? "Default"
                    : "Collection";
                result.Add(new PinnedGameCollection
                {
                    CollectionId = id,
                    Name = MakeUniqueCollectionName(source.Name, fallbackName, names),
                    GameIds = (source.GameIds ?? new List<Guid>())
                        .Where(idValue => idValue != Guid.Empty)
                        .Distinct()
                        .ToList()
                });
            }

            var defaultCollection = result.FirstOrDefault(collection =>
                string.Equals(collection.CollectionId, defaultCollectionId, StringComparison.OrdinalIgnoreCase));
            if (defaultCollection == null)
            {
                defaultCollection = new PinnedGameCollection
                {
                    CollectionId = defaultCollectionId,
                    Name = MakeUniqueCollectionName("Default", "Default", names)
                };
                result.Insert(0, defaultCollection);
            }
            else
            {
                result.Remove(defaultCollection);
                result.Insert(0, defaultCollection);
            }

            return result;
        }

        private static string NormalizeCollectionId(string value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        private static string MakeUniqueCollectionName(
            string value,
            string fallback,
            HashSet<string> used)
        {
            var root = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            var candidate = root;
            var suffix = 2;
            while (!used.Add(candidate))
            {
                candidate = $"{root} ({suffix++})";
            }

            return candidate;
        }

        private static Dictionary<string, ShowcaseWidgetInstanceSettings> NormalizeStartPageInstances(
            IDictionary<string, ShowcaseWidgetInstanceSettings> source)
        {
            var result = new Dictionary<string, ShowcaseWidgetInstanceSettings>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in source ??
                new Dictionary<string, ShowcaseWidgetInstanceSettings>(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(pair.Key) ||
                    pair.Value == null ||
                    !Enum.IsDefined(typeof(ShowcaseWidgetKind), pair.Value.Kind))
                {
                    continue;
                }

                pair.Value.InstanceId = string.IsNullOrWhiteSpace(pair.Value.InstanceId)
                    ? NewId()
                    : pair.Value.InstanceId.Trim();
                pair.Value.Options = pair.Value.Options != null
                    ? new Dictionary<string, string>(pair.Value.Options, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                NormalizeProfile(pair.Value);
                ShowcaseWidgetOptions.MigrateCompletedGameMosaic(pair.Value);
                ShowcaseWidgetOptions.MigrateSlideshowVariant(pair.Value);
                ShowcaseWidgetOptions.MigrateAchievementMosaicSource(pair.Value);
                ShowcaseWidgetOptions.MigrateGameGridScope(pair.Value);
                result[pair.Key.Trim()] = pair.Value;
            }

            return result;
        }

        private static bool IsValidBlock(ShowcaseBlockSettings block, int rows, int columns)
        {
            return block != null &&
                   block.Row >= 0 &&
                   block.Column >= 0 &&
                   block.RowSpan > 0 &&
                   block.ColumnSpan > 0 &&
                   block.Row + block.RowSpan <= rows &&
                   block.Column + block.ColumnSpan <= columns;
        }

        private static IReadOnlyList<ShowcaseBlockSettings> GetMergeClosureCore(
            ShowcasePageSettings page,
            ShowcaseBlockSettings first,
            ShowcaseBlockSettings second)
        {
            if (page?.Blocks == null ||
                first == null ||
                second == null ||
                ReferenceEquals(first, second) ||
                !SharesEdge(first, second))
            {
                return Array.Empty<ShowcaseBlockSettings>();
            }

            var row = Math.Min(first.Row, second.Row);
            var column = Math.Min(first.Column, second.Column);
            var rowEnd = Math.Max(
                first.Row + first.RowSpan,
                second.Row + second.RowSpan);
            var columnEnd = Math.Max(
                first.Column + first.ColumnSpan,
                second.Column + second.ColumnSpan);
            var expanded = true;
            while (expanded)
            {
                expanded = false;
                foreach (var block in page.Blocks.Where(block =>
                             Intersects(block, row, column, rowEnd, columnEnd)))
                {
                    var nextRow = Math.Min(row, block.Row);
                    var nextColumn = Math.Min(column, block.Column);
                    var nextRowEnd = Math.Max(rowEnd, block.Row + block.RowSpan);
                    var nextColumnEnd = Math.Max(columnEnd, block.Column + block.ColumnSpan);
                    if (nextRow != row ||
                        nextColumn != column ||
                        nextRowEnd != rowEnd ||
                        nextColumnEnd != columnEnd)
                    {
                        row = nextRow;
                        column = nextColumn;
                        rowEnd = nextRowEnd;
                        columnEnd = nextColumnEnd;
                        expanded = true;
                    }
                }
            }

            // Implicit cells are 1x1 and never widen the rectangle, so only stored blocks took
            // part above; the cells inside it that none of them covers join as implicit blocks,
            // reusing the two given blocks' objects where they are such cells.
            var closure = page.Blocks
                .Where(block => Intersects(block, row, column, rowEnd, columnEnd))
                .ToList();
            var covered = new bool[rowEnd - row, columnEnd - column];
            foreach (var block in closure)
            {
                for (var r = Math.Max(row, block.Row); r < Math.Min(rowEnd, block.Row + block.RowSpan); r++)
                {
                    for (var c = Math.Max(column, block.Column); c < Math.Min(columnEnd, block.Column + block.ColumnSpan); c++)
                    {
                        covered[r - row, c - column] = true;
                    }
                }
            }

            for (var r = row; r < rowEnd; r++)
            {
                for (var c = column; c < columnEnd; c++)
                {
                    if (covered[r - row, c - column])
                    {
                        continue;
                    }

                    closure.Add(Covers(first, r, c) ? first
                        : Covers(second, r, c) ? second
                        : ImplicitCell(page, r, c));
                }
            }

            return closure
                .OrderBy(block => block.Row)
                .ThenBy(block => block.Column)
                .ToList();
        }

        private static bool SharesEdge(
            ShowcaseBlockSettings first,
            ShowcaseBlockSettings second)
        {
            var sharesVerticalEdge =
                (first.Column + first.ColumnSpan == second.Column ||
                 second.Column + second.ColumnSpan == first.Column) &&
                ShowcaseGeometry.RangesOverlap(first.Row, first.RowSpan, second.Row, second.RowSpan);
            var sharesHorizontalEdge =
                (first.Row + first.RowSpan == second.Row ||
                 second.Row + second.RowSpan == first.Row) &&
                ShowcaseGeometry.RangesOverlap(first.Column, first.ColumnSpan, second.Column, second.ColumnSpan);
            return sharesVerticalEdge || sharesHorizontalEdge;
        }

        private static bool Intersects(
            ShowcaseBlockSettings block,
            int row,
            int column,
            int rowEnd,
            int columnEnd)
        {
            return block != null &&
                   block.Row < rowEnd &&
                   row < block.Row + block.RowSpan &&
                   block.Column < columnEnd &&
                   column < block.Column + block.ColumnSpan;
        }

        private static bool Overlaps(bool[,] occupied, ShowcaseBlockSettings block)
        {
            for (var row = block.Row; row < block.Row + block.RowSpan; row++)
            {
                for (var column = block.Column; column < block.Column + block.ColumnSpan; column++)
                {
                    if (occupied[row, column])
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static void Mark(bool[,] occupied, ShowcaseBlockSettings block)
        {
            for (var row = block.Row; row < block.Row + block.RowSpan; row++)
            {
                for (var column = block.Column; column < block.Column + block.ColumnSpan; column++)
                {
                    occupied[row, column] = true;
                }
            }
        }

        private static ShowcasePageSettings FindPage(ShowcaseSettings settings, string pageId)
        {
            return settings?.Pages?.FirstOrDefault(page =>
                string.Equals(page?.PageId, pageId, StringComparison.OrdinalIgnoreCase));
        }

        private static ShowcaseBlockSettings FindBlock(ShowcasePageSettings page, string blockId) =>
            ResolveBlock(page, blockId);

        private static ShowcaseWidgetInstanceSettings FindWidget(
            ShowcaseSettings settings,
            string widgetInstanceId)
        {
            return settings?.WidgetInstances?.FirstOrDefault(widget =>
                string.Equals(widget?.InstanceId, widgetInstanceId, StringComparison.OrdinalIgnoreCase));
        }

        private static void SortBlocks(ShowcasePageSettings page)
        {
            page.Blocks = page.Blocks
                .OrderBy(block => block.Row)
                .ThenBy(block => block.Column)
                .ToList();
        }

        private static string NormalizeUniqueId(string value, HashSet<string> used)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized) || !used.Add(normalized))
            {
                do
                {
                    normalized = NewId();
                }
                while (!used.Add(normalized));
            }

            return normalized;
        }

        private static string MakeUniquePageName(
            ShowcaseSettings settings,
            string preferred,
            string excludedPageId = null)
        {
            var baseName = string.IsNullOrWhiteSpace(preferred) ? "Showcase" : preferred.Trim();
            var names = new HashSet<string>(
                (settings?.Pages ?? new List<ShowcasePageSettings>())
                    .Where(page =>
                        page != null &&
                        !string.Equals(
                            page.PageId,
                            excludedPageId,
                            StringComparison.OrdinalIgnoreCase))
                    .Select(page => page.Name ?? string.Empty),
                StringComparer.CurrentCultureIgnoreCase);
            if (!names.Contains(baseName))
            {
                return baseName;
            }

            var suffix = 2;
            while (names.Contains($"{baseName} {suffix}"))
            {
                suffix++;
            }

            return $"{baseName} {suffix}";
        }

        private static string GetDefaultPageName(ShowcasePageTemplate template)
        {
            switch (template)
            {
                case ShowcasePageTemplate.Analytics:
                    return "Analytics";
                case ShowcasePageTemplate.Collection:
                    return "Collection";
                case ShowcasePageTemplate.UpNext:
                    return "Up Next";
                case ShowcasePageTemplate.TrophyCase:
                    return "Trophy Case";
                case ShowcasePageTemplate.Library:
                    return "Library";
                case ShowcasePageTemplate.Blank:
                    return "New Page";
                default:
                    return "Showcase";
            }
        }

        private sealed class WidgetPlacement
        {
            public ShowcasePageSettings TargetPage { get; set; }

            public ShowcaseBlockSettings Target { get; set; }

            public ShowcaseBlockSettings Source { get; set; }

            public ShowcaseWidgetInstanceSettings Widget { get; set; }
        }

        private static string NewId() => Guid.NewGuid().ToString("N");
    }
}
