using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Showcase;

namespace PlayniteAchievements.Services.Library
{
    /// <summary>
    /// One showcase page as a library target. The projection holds the page's scalars
    /// (<c>Page</c>: name, row and column counts and weights), its grid (<c>Layout</c>, one
    /// value) and each widget on it by its id on this page (<c>Widgets</c>: kind, title, options
    /// as one value, each control bar's filters as one value under ControlBar, grid settings as
    /// one value, and its image - a profile background or an Image widget's picture - as a file
    /// hash under Background).
    /// What belongs to the user (the pin collection, control bars' Game selections) and every
    /// part of a profile card but its background stay out, as they stay out of a page package. The
    /// link maps the package's widget ids to this page's, so a merge pairs each widget with the
    /// one it became; a widget the user added or removed stays as they left it.
    /// </summary>
    public sealed class ShowcaseLibraryAdapter : ISettingsLibraryAdapter, ILibraryIdMapAdapter<PersistedSettings>
    {
        public const string PageKey = "Page";
        public const string LayoutKey = "Layout";
        public const string WidgetsKey = "Widgets";

        private const string KindKey = "Kind";
        private const string TitleKey = "Title";
        private const string OptionsKey = "Options";
        private const string ControlBarKey = "ControlBar";
        private const string GridKey = "Grid";
        // An achievement grid widget's category list (category mode) keeps its own record.
        private const string CategoryGridKey = "CategoryGrid";
        private const string BackgroundKey = "Background";

        private static readonly string[] AtomicPaths =
        {
            WidgetsKey + "." + JsonThreeWayMerge.AnySegment + "." + OptionsKey,
            WidgetsKey + "." + JsonThreeWayMerge.AnySegment + "." + GridKey,
            WidgetsKey + "." + JsonThreeWayMerge.AnySegment + "." + CategoryGridKey,
            WidgetsKey + "." + JsonThreeWayMerge.AnySegment + "." + BackgroundKey
        };

        private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            Converters = { new StringEnumConverter() }
        });

        private readonly string _pageId;
        private readonly Func<string, string> _storeImage;
        private readonly Action<PersistedSettings> _afterWrite;

        /// <param name="pageId">The page this adapter writes.</param>
        /// <param name="storeImage">Stores an extracted widget image and returns its stored path.</param>
        /// <param name="afterWrite">Runs after the page changed, with the settings it changed in.</param>
        public ShowcaseLibraryAdapter(string pageId, Func<string, string> storeImage, Action<PersistedSettings> afterWrite = null)
        {
            if (string.IsNullOrWhiteSpace(pageId))
            {
                throw new ArgumentException("A page id is required.", nameof(pageId));
            }

            _pageId = pageId.Trim();
            _storeImage = storeImage;
            _afterWrite = afterWrite;
        }

        public LibraryItemKind Kind => LibraryItemKind.ShowcasePage;

        public string TargetKey => LibraryTargetKeys.Showcase(_pageId);

        public string PageId => _pageId;

        public JObject Project(PersistedSettings target, IEnumerable<string> ownedKeys = null)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            var layout = target.Showcase;
            var page = FindPage(layout, _pageId);
            if (page == null)
            {
                return new JObject();
            }

            var grid = target.GridOptions;
            return ProjectPage(
                page,
                id => FindWidget(layout, id),
                id => id,
                widget => GridOf(grid, widget.Kind, widget.InstanceId),
                widget => CategoryGridOf(grid, widget.Kind, widget.InstanceId),
                widget => ShowcaseWidgetImages.GetFilePath(widget));
        }

        public IReadOnlyCollection<string> OwnedKeys(string packagePath) => null;

        public void ApplyReplace(string packagePath, PersistedSettings target)
        {
            ApplyReplace(packagePath, target, null);
        }

        public void ApplyMerged(string packagePath, PersistedSettings target, JToken baseline, out int keptEdits)
        {
            ApplyMerged(packagePath, target, baseline, null, out keptEdits);
        }

        public IReadOnlyDictionary<string, string> ApplyReplace(string packagePath, PersistedSettings target, IReadOnlyDictionary<string, string> idMap)
        {
            return ApplyCore(packagePath, target, null, idMap, replace: true, out _);
        }

        public IReadOnlyDictionary<string, string> ApplyMerged(
            string packagePath,
            PersistedSettings target,
            JToken baseline,
            IReadOnlyDictionary<string, string> idMap,
            out int keptEdits)
        {
            return ApplyCore(packagePath, target, baseline, idMap, replace: false, out keptEdits);
        }

        public IReadOnlyDictionary<string, string> IdentityMap(string packagePath)
        {
            var portable = ShowcasePagePortableStore.Read(packagePath);
            try
            {
                return PackageWidgetIds(portable).ToDictionary(id => id, id => id, StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                ShowcasePagePortableStore.DeleteExtractedImages(portable);
            }
        }

        // ---- apply ------------------------------------------------------------------------------

        private IReadOnlyDictionary<string, string> ApplyCore(
            string packagePath,
            PersistedSettings target,
            JToken baseline,
            IReadOnlyDictionary<string, string> idMap,
            bool replace,
            out int keptEdits)
        {
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            keptEdits = 0;
            var layout = target.Showcase ?? throw new InvalidOperationException("The showcase is not available.");
            ShowcaseLayoutService.Normalize(layout);
            var page = FindPage(layout, _pageId) ?? throw new InvalidOperationException("The showcase page is gone.");
            var grid = target.GridOptions;

            var portable = ShowcasePagePortableStore.Read(packagePath);
            try
            {
                var map = PairWidgets(portable, idMap ?? PairByPlacement(portable.Page, portable, page, layout), page);
                var incoming = ProjectPackage(portable, map, out var incomingBackgrounds);
                var current = Project(target);
                var merged = replace ? incoming : MergeProjections(baseline, current, incoming, out keptEdits);
                if (replace || baseline == null)
                {
                    KeepCurrentControlBars(merged, current);
                }

                WritePage(layout, grid, page, merged, current, incoming, incomingBackgrounds);
                ShowcaseLayoutService.Normalize(layout);
                ShowcaseLayoutService.PruneOrphanedWidgets(layout);
                ShowcaseGridSurfaces.PruneOrphaned(grid, layout);
                _afterWrite?.Invoke(target);
                return map;
            }
            finally
            {
                ShowcasePagePortableStore.DeleteExtractedImages(portable);
            }
        }

        /// <summary>
        /// The package's widget ids mapped to this page's: a widget the link already pairs with one
        /// still on the page keeps it; every other gets a new id.
        /// </summary>
        private static Dictionary<string, string> PairWidgets(
            ShowcasePagePortableFile portable,
            IReadOnlyDictionary<string, string> previous,
            ShowcasePageSettings page)
        {
            var onPage = new HashSet<string>(
                page.Blocks.Select(block => block.WidgetInstanceId).Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.OrdinalIgnoreCase);
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in PackageWidgetIds(portable))
            {
                if (previous != null
                    && previous.TryGetValue(id, out var local)
                    && !string.IsNullOrWhiteSpace(local)
                    && onPage.Contains(local)
                    && taken.Add(local))
                {
                    map[id] = local;
                }
                else
                {
                    map[id] = Guid.NewGuid().ToString("N");
                }
            }

            return map;
        }

        /// <summary>
        /// For a page whose link has no id map (made before links kept one), the package's widgets
        /// paired with the page's widgets of the same kind in the same grid cell, so the widgets the
        /// page already has keep their ids and with them the user's own options.
        /// </summary>
        private static Dictionary<string, string> PairByPlacement(
            ShowcasePageSettings packagePage,
            ShowcasePagePortableFile portable,
            ShowcasePageSettings page,
            ShowcaseSettings layout)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var packageKinds = (portable.Widgets ?? new List<ShowcaseWidgetInstanceSettings>())
                .Where(widget => widget != null && !string.IsNullOrWhiteSpace(widget.InstanceId))
                .GroupBy(widget => widget.InstanceId.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Kind, StringComparer.OrdinalIgnoreCase);
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var block in packagePage?.Blocks ?? new List<ShowcaseBlockSettings>())
            {
                var exported = block?.WidgetInstanceId?.Trim();
                if (string.IsNullOrEmpty(exported) || map.ContainsKey(exported) || !packageKinds.TryGetValue(exported, out var kind))
                {
                    continue;
                }

                var local = page.Blocks
                    .Where(candidate => candidate.Row == block.Row && candidate.Column == block.Column)
                    .Select(candidate => FindWidget(layout, candidate.WidgetInstanceId))
                    .FirstOrDefault(widget => widget != null && widget.Kind == kind && !taken.Contains(widget.InstanceId));
                if (local != null)
                {
                    taken.Add(local.InstanceId);
                    map[exported] = local.InstanceId;
                }
            }

            return map;
        }

        /// <summary>The package's page projected under this page's widget ids, with the extracted background of each widget.</summary>
        private static JObject ProjectPackage(
            ShowcasePagePortableFile portable,
            IReadOnlyDictionary<string, string> map,
            out Dictionary<string, string> backgrounds)
        {
            // Normalized the way the page will be once written, so an untouched value compares equal.
            var normalized = new ShowcaseSettings
            {
                Pages = new List<ShowcasePageSettings> { portable.Page.Clone() },
                WidgetInstances = (portable.Widgets ?? new List<ShowcaseWidgetInstanceSettings>())
                    .Where(widget => widget != null)
                    .Select(widget => widget.Clone())
                    .ToList()
            };
            ShowcaseLayoutService.Normalize(normalized);

            var extracted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var widget in normalized.WidgetInstances)
            {
                var entry = ShowcaseWidgetImages.GetFilePath(widget)?.Trim();
                if (!string.IsNullOrEmpty(entry)
                    && map.TryGetValue(widget.InstanceId ?? string.Empty, out var local)
                    && portable.BundledImages != null
                    && portable.BundledImages.TryGetValue(entry, out var file))
                {
                    extracted[local] = file;
                }
            }

            backgrounds = extracted;
            return ProjectPage(
                normalized.Pages[0],
                id => FindWidget(normalized, id),
                id => map.TryGetValue(id, out var local) ? local : null,
                widget => PackageGridOf(portable, widget.Kind, widget.InstanceId),
                widget => PackageCategoryGridOf(portable, widget.Kind, widget.InstanceId),
                widget => map.TryGetValue(widget.InstanceId ?? string.Empty, out var local) && extracted.TryGetValue(local, out var file) ? file : null);
        }

        /// <summary>
        /// Three-way merge of the projections. Whether a widget is on the page is the user's choice
        /// when they changed it, so such a widget stays as they left it; a widget the new version
        /// drops goes too, unless the user edited it. Everything else merges leaf by leaf, with each
        /// widget's options, grid settings and background as single values.
        /// </summary>
        private static JObject MergeProjections(JToken baseline, JObject current, JObject incoming, out int keptEdits)
        {
            var baseObject = (baseline as JObject)?.DeepClone() as JObject ?? new JObject();
            var currentObject = (JObject)current.DeepClone();
            var incomingObject = (JObject)incoming.DeepClone();
            var baseWidgets = baseObject[WidgetsKey] as JObject ?? new JObject();
            var currentWidgets = currentObject[WidgetsKey] as JObject ?? new JObject();
            var incomingWidgets = incomingObject[WidgetsKey] as JObject ?? new JObject();
            baseObject[WidgetsKey] = baseWidgets;
            currentObject[WidgetsKey] = currentWidgets;
            incomingObject[WidgetsKey] = incomingWidgets;

            var held = new List<KeyValuePair<string, JToken>>();
            var heldEdits = 0;
            var ids = baseWidgets.Properties()
                .Concat(currentWidgets.Properties())
                .Concat(incomingWidgets.Properties())
                .Select(property => property.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var id in ids)
            {
                var b = baseWidgets.GetValue(id, StringComparison.OrdinalIgnoreCase);
                var c = currentWidgets.GetValue(id, StringComparison.OrdinalIgnoreCase);
                var i = incomingWidgets.GetValue(id, StringComparison.OrdinalIgnoreCase);
                var inBase = !JsonThreeWayMerge.SameValue(b, null);
                var inCurrent = !JsonThreeWayMerge.SameValue(c, null);
                var inIncoming = !JsonThreeWayMerge.SameValue(i, null);

                JToken value;
                if (inBase != inCurrent)
                {
                    value = c;
                }
                else if (inCurrent && !inIncoming)
                {
                    value = JsonThreeWayMerge.SameValue(c, b) ? null : c;
                }
                else
                {
                    continue;
                }

                if (!JsonThreeWayMerge.SameValue(value, i))
                {
                    heldEdits++;
                }

                held.Add(new KeyValuePair<string, JToken>(id, value?.DeepClone() ?? JValue.CreateNull()));
                baseWidgets.Remove(id);
                currentWidgets.Remove(id);
                incomingWidgets.Remove(id);
            }

            var merged = JsonThreeWayMerge.Merge(baseObject, currentObject, incomingObject, AtomicPaths, out keptEdits) as JObject
                         ?? new JObject();
            if (!(merged[WidgetsKey] is JObject mergedWidgets))
            {
                mergedWidgets = new JObject();
                merged[WidgetsKey] = mergedWidgets;
            }

            foreach (var pair in held)
            {
                mergedWidgets[pair.Key] = pair.Value;
            }

            keptEdits += heldEdits;
            return merged;
        }

        // A replace (a reset, or an update without a baseline, where nothing tells the user's
        // filters apart from ones they never changed) leaves each widget the page already has
        // with its control bars' filters; a widget it adds takes the package's.
        private static void KeepCurrentControlBars(JObject merged, JObject current)
        {
            var currentWidgets = current[WidgetsKey] as JObject;
            foreach (var property in (merged[WidgetsKey] as JObject ?? new JObject()).Properties())
            {
                if (property.Value is JObject widget &&
                    currentWidgets?.GetValue(property.Name, StringComparison.OrdinalIgnoreCase) is JObject existing)
                {
                    widget[ControlBarKey] = existing[ControlBarKey]?.DeepClone() ?? new JObject();
                }
            }
        }

        private void WritePage(
            ShowcaseSettings layout,
            GridOptionsCatalog grid,
            ShowcasePageSettings page,
            JObject merged,
            JObject current,
            JObject incoming,
            IReadOnlyDictionary<string, string> incomingBackgrounds)
        {
            if (merged[PageKey] is JObject scalars)
            {
                var name = (string)scalars["Name"];
                if (!string.IsNullOrWhiteSpace(name) && !string.Equals(name, page.Name, StringComparison.Ordinal))
                {
                    ShowcaseLayoutService.RenamePage(layout, page.PageId, name);
                }

                page.RowCount = (int?)scalars["RowCount"] ?? page.RowCount;
                page.ColumnCount = (int?)scalars["ColumnCount"] ?? page.ColumnCount;
                page.RowWeights = Weights(scalars["RowWeights"]);
                page.ColumnWeights = Weights(scalars["ColumnWeights"]);
            }

            var mergedLayout = merged[LayoutKey];
            if (!JsonThreeWayMerge.SameValue(mergedLayout, current[LayoutKey]))
            {
                page.Blocks = (mergedLayout as JArray ?? new JArray())
                    .OfType<JObject>()
                    .Select(block => new ShowcaseBlockSettings
                    {
                        BlockId = Guid.NewGuid().ToString("N"),
                        Row = (int?)block["Row"] ?? 0,
                        Column = (int?)block["Column"] ?? 0,
                        RowSpan = (int?)block["RowSpan"] ?? 1,
                        ColumnSpan = (int?)block["ColumnSpan"] ?? 1,
                        WidgetInstanceId = (string)block["Widget"]
                    })
                    .ToList();
            }

            var currentWidgets = current[WidgetsKey] as JObject;
            var incomingWidgets = incoming[WidgetsKey] as JObject;
            foreach (var property in (merged[WidgetsKey] as JObject ?? new JObject()).Properties())
            {
                if (!(property.Value is JObject value) || JsonThreeWayMerge.SameValue(value, null))
                {
                    continue;
                }

                var id = property.Name;
                var currentValue = currentWidgets?.GetValue(id, StringComparison.OrdinalIgnoreCase) as JObject;
                var incomingValue = incomingWidgets?.GetValue(id, StringComparison.OrdinalIgnoreCase) as JObject;
                if (!Enum.TryParse((string)value[KindKey], ignoreCase: true, out ShowcaseWidgetKind kind)
                    || !Enum.IsDefined(typeof(ShowcaseWidgetKind), kind))
                {
                    continue;
                }

                var widget = FindWidget(layout, id);
                var isNew = widget == null;
                if (isNew)
                {
                    widget = new ShowcaseWidgetInstanceSettings { InstanceId = id, Kind = kind };
                    layout.WidgetInstances.Add(widget);
                }
                else
                {
                    widget.Kind = kind;
                }

                widget.CustomTitle = (string)value[TitleKey];

                // Read before the options are rebuilt: an Image widget keeps its file there.
                var currentImage = ShowcaseWidgetImages.GetFilePath(widget);

                // The user's pin collection stays.
                var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var localBars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in widget.Options ?? new Dictionary<string, string>())
                {
                    if (ShowcaseWidgetOptions.IsControlBarState(pair.Key))
                    {
                        localBars[pair.Key] = pair.Value;
                    }
                    else if (ShowcasePagePortableStore.IsUserOption(pair.Key))
                    {
                        options[pair.Key] = pair.Value;
                    }
                }

                // Each bar takes its merged filters, with this library's Game selections kept.
                var mergedBars = value[ControlBarKey] as JObject ?? new JObject();
                foreach (var key in localBars.Keys
                             .Concat(mergedBars.Properties().Select(property => property.Name))
                             .Where(ShowcaseWidgetOptions.IsControlBarState)
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .ToList())
                {
                    localBars.TryGetValue(key, out var local);
                    var state = ShowcaseWidgetOptions.WithLocalControlBarGames(
                        (string)mergedBars.GetValue(key, StringComparison.OrdinalIgnoreCase),
                        local);
                    if (state != null)
                    {
                        options[key] = state;
                    }
                }

                if (value[OptionsKey] is JObject mergedOptions)
                {
                    foreach (var option in mergedOptions.Properties())
                    {
                        if (!ShowcasePagePortableStore.IsUserOption(option.Name) && !IsFileOption(option.Name))
                        {
                            options[option.Name] = option.Value.Type == JTokenType.Null ? null : option.Value.ToString();
                        }
                    }
                }

                widget.Options = options;
                if (isNew)
                {
                    ShowcaseLayoutService.SeedPinCollectionSelection(layout, widget);
                }

                incomingBackgrounds.TryGetValue(id, out var incomingFile);
                var image = ResolveBackground(
                    value[BackgroundKey],
                    currentValue?[BackgroundKey],
                    incomingValue?[BackgroundKey],
                    currentImage,
                    incomingFile);
                ShowcaseWidgetImages.SetFilePath(widget, image);

                WriteGrid(grid, widget, value[GridKey], currentValue?[GridKey]);
                WriteCategoryGrid(grid, widget, value[CategoryGridKey], currentValue?[CategoryGridKey]);
            }
        }

        // An Image widget's file is projected as a hash under Background; its machine-specific
        // path never merges as an option.
        private static bool IsFileOption(string key) =>
            string.Equals(key, ShowcaseWidgetOptions.ImagePathKey, StringComparison.OrdinalIgnoreCase);

        private string ResolveBackground(JToken merged, JToken current, JToken incoming, string currentPath, string incomingFile)
        {
            if (JsonThreeWayMerge.SameValue(merged, null))
            {
                return null;
            }

            if (JsonThreeWayMerge.SameValue(merged, current))
            {
                return currentPath;
            }

            if (JsonThreeWayMerge.SameValue(merged, incoming) && !string.IsNullOrWhiteSpace(incomingFile))
            {
                return _storeImage?.Invoke(incomingFile);
            }

            return currentPath;
        }

        private static void WriteGrid(GridOptionsCatalog grid, ShowcaseWidgetInstanceSettings widget, JToken merged, JToken current)
        {
            if (grid == null || !(merged is JObject value) || JsonThreeWayMerge.SameValue(merged, current))
            {
                return;
            }

            var key = ShowcaseGridSurfaces.ResolveWidgetSurface(widget.Kind, widget.InstanceId);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (ShowcaseGridSurfaces.IsAchievementSurface(key))
            {
                grid.SetAchievement(key, value.ToObject<AchievementGridOptions>(Serializer));
            }
            else if (ShowcaseGridSurfaces.IsGameSurface(key))
            {
                grid.SetGameSummaries(key, value.ToObject<GameSummaryGridOptions>(Serializer));
            }
        }

        private static void WriteCategoryGrid(GridOptionsCatalog grid, ShowcaseWidgetInstanceSettings widget, JToken merged, JToken current)
        {
            if (grid == null || !(merged is JObject value) || JsonThreeWayMerge.SameValue(merged, current))
            {
                return;
            }

            var key = ShowcaseGridSurfaces.ResolveCategorySurface(
                ShowcaseGridSurfaces.ResolveWidgetSurface(widget.Kind, widget.InstanceId));
            if (key != null)
            {
                grid.SetCategorySummaries(key, value.ToObject<CategorySummaryGridOptions>(Serializer));
            }
        }

        // ---- projection -------------------------------------------------------------------------

        private static JObject ProjectPage(
            ShowcasePageSettings page,
            Func<string, ShowcaseWidgetInstanceSettings> widgetOf,
            Func<string, string> idOf,
            Func<ShowcaseWidgetInstanceSettings, JToken> gridOf,
            Func<ShowcaseWidgetInstanceSettings, JToken> categoryGridOf,
            Func<ShowcaseWidgetInstanceSettings, string> backgroundOf)
        {
            var scalars = new JObject
            {
                ["Name"] = page.Name,
                ["RowCount"] = page.RowCount,
                ["ColumnCount"] = page.ColumnCount,
                ["RowWeights"] = page.RowWeights == null ? null : new JArray(page.RowWeights),
                ["ColumnWeights"] = page.ColumnWeights == null ? null : new JArray(page.ColumnWeights)
            };

            // Every cell, implicit empty ones included, so a projection matches the ones saved as
            // merge baselines whatever the page stores.
            var blocks = ShowcaseLayoutService.DenseBlocks(page);
            var layout = new JArray();
            foreach (var block in blocks)
            {
                var widget = string.IsNullOrWhiteSpace(block.WidgetInstanceId) ? null : widgetOf(block.WidgetInstanceId);
                layout.Add(new JObject
                {
                    ["Row"] = block.Row,
                    ["Column"] = block.Column,
                    ["RowSpan"] = block.RowSpan,
                    ["ColumnSpan"] = block.ColumnSpan,
                    ["Widget"] = widget == null ? null : idOf(widget.InstanceId)
                });
            }

            var widgets = new SortedDictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var block in blocks)
            {
                var widget = string.IsNullOrWhiteSpace(block.WidgetInstanceId) ? null : widgetOf(block.WidgetInstanceId);
                var id = widget == null ? null : idOf(widget.InstanceId);
                if (id == null || widgets.ContainsKey(id))
                {
                    continue;
                }

                var options = new JObject();
                foreach (var pair in (widget.Options ?? new Dictionary<string, string>())
                         .Where(pair => !ShowcasePagePortableStore.IsUserOption(pair.Key) && !IsFileOption(pair.Key))
                         .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    options[pair.Key] = pair.Value;
                }

                // Each bar's filters are one value of their own, so a filter the user changed
                // keeps their whole bar without holding back the widget's other options.
                var controlBar = new JObject();
                foreach (var pair in (widget.Options ?? new Dictionary<string, string>())
                         .Where(pair => ShowcaseWidgetOptions.IsControlBarState(pair.Key))
                         .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var state = ShowcaseWidgetOptions.PortableControlBarState(pair.Value);
                    if (state != null)
                    {
                        controlBar[pair.Key] = state;
                    }
                }

                widgets[id] = new JObject
                {
                    [KindKey] = widget.Kind.ToString(),
                    [TitleKey] = widget.CustomTitle,
                    [OptionsKey] = options,
                    [ControlBarKey] = controlBar,
                    [GridKey] = gridOf(widget),
                    [CategoryGridKey] = categoryGridOf(widget),
                    [BackgroundKey] = LibraryFileTokens.FileValue(backgroundOf(widget))
                };
            }

            var widgetObject = new JObject();
            foreach (var pair in widgets)
            {
                widgetObject[pair.Key] = pair.Value;
            }

            return new JObject
            {
                [PageKey] = scalars,
                [LayoutKey] = layout,
                [WidgetsKey] = widgetObject
            };
        }

        private static JToken GridOf(GridOptionsCatalog grid, ShowcaseWidgetKind kind, string instanceId)
        {
            var key = ShowcaseGridSurfaces.ResolveWidgetSurface(kind, instanceId);
            if (grid == null || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            if (ShowcaseGridSurfaces.IsAchievementSurface(key) && grid.Achievement.TryGetValue(key, out var achievement) && achievement != null)
            {
                return JObject.FromObject(achievement, Serializer);
            }

            if (ShowcaseGridSurfaces.IsGameSurface(key) && grid.GameSummaries.TryGetValue(key, out var games) && games != null)
            {
                return JObject.FromObject(games, Serializer);
            }

            return null;
        }

        private static JToken PackageGridOf(ShowcasePagePortableFile portable, ShowcaseWidgetKind kind, string exportedId)
        {
            var key = ShowcaseGridSurfaces.ResolveWidgetSurface(kind, exportedId);
            var id = exportedId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            if (ShowcaseGridSurfaces.IsAchievementSurface(key)
                && portable.AchievementGridSurfaces != null
                && portable.AchievementGridSurfaces.TryGetValue(id, out var achievement)
                && achievement != null)
            {
                return JObject.FromObject(achievement, Serializer);
            }

            if (ShowcaseGridSurfaces.IsGameSurface(key)
                && portable.GameGridSurfaces != null
                && portable.GameGridSurfaces.TryGetValue(id, out var games)
                && games != null)
            {
                return JObject.FromObject(games, Serializer);
            }

            return null;
        }

        private static JToken CategoryGridOf(GridOptionsCatalog grid, ShowcaseWidgetKind kind, string instanceId)
        {
            var key = ShowcaseGridSurfaces.ResolveCategorySurface(
                ShowcaseGridSurfaces.ResolveWidgetSurface(kind, instanceId));
            return grid != null && key != null &&
                   grid.CategorySummaries.TryGetValue(key, out var categories)
                ? CategoryGridToken(categories)
                : null;
        }

        private static JToken PackageCategoryGridOf(ShowcasePagePortableFile portable, ShowcaseWidgetKind kind, string exportedId)
        {
            var id = exportedId?.Trim() ?? string.Empty;
            return ShowcaseGridSurfaces.IsAchievementSurface(ShowcaseGridSurfaces.ResolveWidgetSurface(kind, exportedId)) &&
                   portable.CategoryGridSurfaces != null &&
                   portable.CategoryGridSurfaces.TryGetValue(id, out var categories)
                ? CategoryGridToken(categories)
                : null;
        }

        // A record still at its defaults reads as none. Showing the widget creates one, and a
        // created record must not count as the user's edit against a package that had none.
        private static JToken CategoryGridToken(CategorySummaryGridOptions categories)
        {
            if (categories == null)
            {
                return null;
            }

            var token = JObject.FromObject(categories, Serializer);
            var defaults = JObject.FromObject(
                new GridOptionsCatalog().GetCategorySummaries(ShowcaseGridSurfaces.AchievementCategories),
                Serializer);
            return JToken.DeepEquals(token, defaults) ? null : token;
        }

        private static IEnumerable<string> PackageWidgetIds(ShowcasePagePortableFile portable)
        {
            return (portable?.Widgets ?? new List<ShowcaseWidgetInstanceSettings>())
                .Select(widget => widget?.InstanceId?.Trim())
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<double> Weights(JToken token)
        {
            return token is JArray array && array.Count > 0
                ? array.Select(value => (double)value).ToList()
                : null;
        }

        private static ShowcasePageSettings FindPage(ShowcaseSettings layout, string pageId)
        {
            return layout?.Pages?.FirstOrDefault(page => string.Equals(page?.PageId, pageId, StringComparison.OrdinalIgnoreCase));
        }

        private static ShowcaseWidgetInstanceSettings FindWidget(ShowcaseSettings layout, string id)
        {
            return string.IsNullOrWhiteSpace(id)
                ? null
                : layout?.WidgetInstances?.FirstOrDefault(widget => string.Equals(widget?.InstanceId, id.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>The showcase pages as library targets, one adapter per page that still exists.</summary>
    public sealed class ShowcaseLibraryTargets : ILibraryTargetResolver
    {
        private readonly Func<PersistedSettings> _live;
        private readonly Func<string, string> _storeImage;
        private readonly Action<PersistedSettings> _afterWrite;

        public ShowcaseLibraryTargets(Func<PersistedSettings> live, Func<string, string> storeImage, Action<PersistedSettings> afterWrite = null)
        {
            _live = live ?? throw new ArgumentNullException(nameof(live));
            _storeImage = storeImage;
            _afterWrite = afterWrite;
        }

        /// <summary>The adapter of one page, whether or not it exists yet.</summary>
        public ShowcaseLibraryAdapter AdapterFor(string pageId) => new ShowcaseLibraryAdapter(pageId, _storeImage, _afterWrite);

        public ISettingsLibraryAdapter SettingsAdapter(string targetKey)
        {
            if (!LibraryTargetKeys.TryGetShowcasePageId(targetKey, out var pageId))
            {
                return null;
            }

            var exists = _live()?.Showcase?.Pages?.Any(page => string.Equals(page?.PageId, pageId, StringComparison.OrdinalIgnoreCase)) == true;
            return exists ? AdapterFor(pageId) : null;
        }

        public ILibraryGameTarget GameTarget(string targetKey) => null;
    }
}
