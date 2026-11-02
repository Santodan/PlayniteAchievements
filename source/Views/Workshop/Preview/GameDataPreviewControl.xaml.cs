using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Summaries;
using PlayniteAchievements.Services.Workshop.Preview;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Helpers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using AchievementDataGridControl = PlayniteAchievements.Views.Controls.AchievementDataGridControl;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.Views.Workshop.Preview
{
    /// <summary>
    /// A previewed game data package (<see cref="GameCustomDataPreviewModel"/> as the DataContext):
    /// a summary of what it carries and the game it is compared against. Compared against a
    /// library game, the game's achievements show in the achievement grid as View Achievements
    /// shows them: one grid after the install and one as they are now, Before / After showing
    /// one of the two, changed rows only unless unchanged ones are asked for. Without a game, the
    /// package's own entries show in the same grid, with the columns none of them fills hidden.
    /// <see cref="MaxRows"/> caps either with an "and N more" line.
    /// </summary>
    public partial class GameDataPreviewControl : UserControl
    {
        /// <summary>The grid's height cap: none, so it takes the space it is given.</summary>
        public static readonly double UnboundedHeight = double.PositiveInfinity;

        /// <summary>Decode size of a grid icon, as the grid's icon cell requests it.</summary>
        internal const int GridIconDecodePixel = 128;

        // The live grids keep a column layout of their own (right-click a header to show or hide
        // columns), so hiding a column here leaves View Achievements alone. The published image
        // uses a fixed set of columns, so neither the sharer's layout nor their unlock dates
        // reach it. Both keys start with "WorkshopPreview", which drops the Captures column.
        private const string LiveColumnSettingsKey = "WorkshopPreviewGrid";
        private const string NeutralColumnSettingsKey = "WorkshopPreview";

        private static readonly HashSet<string> NeutralColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Status", "Icon", "Achievement", "CategoryKind", "CategoryType", "CategoryLabel", "Trophy", "Rarity", "Points"
        };

        // Shown in the published image whatever the rows hold; the rest of the fixed set is shown
        // only when some drawn row has a value for it.
        private static readonly HashSet<string> NeutralAlwaysShownColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Status", "Icon", "Achievement"
        };

        // Icon and badge columns of the published image, centered; every other column is text and
        // aligned left, header and cells, so the columns line up whatever the sharer's grid
        // alignment settings are.
        private static readonly HashSet<string> NeutralCenteredColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Status", "Icon", "Trophy"
        };

        /// <summary>The widest a fitted text column of the published image gets; longer text wraps.</summary>
        private const double NeutralFittedColumnMaxWidth = 220;

        public static readonly DependencyProperty MaxRowsProperty = DependencyProperty.Register(
            nameof(MaxRows), typeof(int), typeof(GameDataPreviewControl),
            new PropertyMetadata(0, (d, e) => ((GameDataPreviewControl)d).RefreshRows()));

        public static readonly DependencyProperty NeutralRenderProperty = DependencyProperty.Register(
            nameof(NeutralRender), typeof(bool), typeof(GameDataPreviewControl),
            new PropertyMetadata(false, (d, e) => ((GameDataPreviewControl)d).Rebuild()));

        // Columns a package's own entries can leave empty; the live grid hides each one none fills.
        private static readonly string[] PackageOptionalColumns =
        {
            "CategoryKind", "CategoryType", "CategoryLabel", "CategoryIcon", "Note", "Trophy", "Points", "Rarity", "RarityTier",
            "RarityPercent", "UnlockDate", "CollectionScore", "PrestigeScore"
        };

        private static readonly HashSet<string> CategoryColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Cover", "GameSummaryName", "TotalAchievements"
        };

        private GameCustomDataPreviewDiff _diff;

        // What the grid shows after the install: the game's data, or the package's own entries.
        private GameAchievementData _afterData;
        private HashSet<string> _changedApiNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private List<AchievementDisplayItem> _afterItems;
        private List<AchievementDisplayItem> _beforeItems;

        // The after-install category tree, every node expanded, built from all after rows.
        private List<GameSummaryItem> _categoryRows;
        private List<AchievementDisplayItem> _shownItems = new List<AchievementDisplayItem>();
        private Style _neutralHeaderStyle;
        private DataTemplate _defaultStatusTemplate;

        public GameDataPreviewControl()
        {
            InitializeComponent();
            var reveal = new RelayCommand(item => (item as AchievementDisplayItem)?.ToggleReveal());
            AfterGrid.RevealCommand = reveal;
            BeforeGrid.RevealCommand = reveal;

            // A package carries no progress, so the category tree shows what each category is:
            // its art, its name and how many achievements it holds.
            CategoryGrid.AllowedColumnKeys = CategoryColumns;
            CategoryGrid.ColumnSettingsKey = "WorkshopPreviewCategorySummaries";

            // The tree's order and its connector lanes are one thing: a column sort would scramble
            // the rows under lanes drawn for the tree order.
            CategoryGrid.InternalDataGrid.CanUserSortColumns = false;
            DataContextChanged += (sender, args) => Rebuild();
        }

        /// <summary>The most rows shown, 0 for all; the rest are counted in an "and N more" line.</summary>
        public int MaxRows
        {
            get => (int)GetValue(MaxRowsProperty);
            set => SetValue(MaxRowsProperty, value);
        }

        /// <summary>
        /// True for the published preview image: no controls, the rows the package touches after
        /// the install, default settings, a fixed column set with a status cell that shows only
        /// the capstone badge, and the note that addresses the viewing user
        /// ("not in your library") hidden, since it describes the sharer's machine.
        /// </summary>
        public bool NeutralRender
        {
            get => (bool)GetValue(NeutralRenderProperty);
            set => SetValue(NeutralRenderProperty, value);
        }

        /// <summary>The icon sources the visible grid's rows load, for a render that decodes them ahead.</summary>
        internal IReadOnlyList<string> DisplayedIconUris => _shownItems
            .Select(item => item.DisplayIcon)
            .Where(uri => !string.IsNullOrWhiteSpace(uri))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        /// <summary>
        /// Puts decoded icons into the grid's laid-out image elements, keyed by the source each
        /// image loads. An offscreen tree never starts its own loads, so a render calls this after
        /// layout with the images it decoded from <see cref="DisplayedIconUris"/>.
        /// </summary>
        internal void ApplyPreloadedIcons(IReadOnlyDictionary<string, ImageSource> images)
        {
            if (images == null || images.Count == 0)
            {
                return;
            }

            ApplyPreloadedIcons(VisibleGrid, images);
        }

        /// <summary>The grid Before / After shows: always the after-install one in a neutral render.</summary>
        private AchievementDataGridControl VisibleGrid => ShowsBefore ? BeforeGrid : AfterGrid;

        private bool ShowsBefore => !NeutralRender && _diff?.BeforeData != null && BeforeButton.IsChecked == true;

        private bool ShowsCategories => !NeutralRender && CategoriesButton.IsChecked == true;

        /// <summary>
        /// Gives the grid's star-sized columns fixed widths that share <paramref name="width"/>,
        /// the width the render gives this control. An offscreen DataGrid never loads, and its
        /// star columns stay at their minimum width or overflow without it, so a render calls this
        /// after the first layout pass and lays out again. The width is passed in because an
        /// element arranged narrower than it asked for reports the width it asked for.
        /// </summary>
        internal void FixStarColumnWidths(double width)
        {
            var dataGrid = VisibleGrid.AchievementsDataGrid;
            if (width <= 0)
            {
                return;
            }

            if (NeutralRender)
            {
                FitColumnsToContent(dataGrid);
            }

            var visible = dataGrid.Columns.Where(column => column.Visibility == Visibility.Visible).ToList();
            var stars = visible.Where(column => column.Width.IsStar).ToList();
            if (stars.Count == 0)
            {
                return;
            }

            // Declared widths: before the columns settle, ActualWidth can still be the minimum.
            var fixedWidth = visible
                .Where(column => !column.Width.IsStar)
                .Sum(column => column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth);
            var totalStars = stars.Sum(column => column.Width.Value);
            var remaining = Math.Max(0, width - fixedWidth - 2);
            foreach (var column in stars)
            {
                var share = Math.Floor(remaining * column.Width.Value / totalStars);
                column.Width = new DataGridLength(Math.Max(column.MinWidth, share), DataGridLengthUnitType.Pixel);
            }
        }

        /// <summary>
        /// Sizes each visible column of the published image other than Status, Icon and
        /// Achievement to the wider of its header and its widest laid-out cell, up to
        /// <see cref="NeutralFittedColumnMaxWidth"/>, so no header is clipped and no column is
        /// wider than what it shows. Achievement keeps its star width and takes what is left.
        /// </summary>
        private static void FitColumnsToContent(DataGrid dataGrid)
        {
            var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
            var headers = FindDescendants<DataGridColumnHeader>(dataGrid)
                .Where(header => header.Column != null)
                .ToList();
            foreach (var column in dataGrid.Columns)
            {
                var key = ColumnVisibilityHelper.GetColumnKey(column) ?? string.Empty;
                if (column.Visibility != Visibility.Visible || NeutralAlwaysShownColumns.Contains(key))
                {
                    continue;
                }

                var widest = 0.0;
                foreach (var header in headers.Where(header => ReferenceEquals(header.Column, column)))
                {
                    header.Measure(unbounded);
                    widest = Math.Max(widest, header.DesiredSize.Width);
                    header.InvalidateMeasure();
                }

                foreach (var item in dataGrid.Items)
                {
                    var cell = FindAncestor<DataGridCell>(column.GetCellContent(item));
                    if (cell == null)
                    {
                        continue;
                    }

                    cell.Measure(unbounded);
                    widest = Math.Max(widest, cell.DesiredSize.Width);
                    cell.InvalidateMeasure();
                }

                if (widest <= 0)
                {
                    continue;
                }

                var fitted = Math.Ceiling(Math.Min(NeutralFittedColumnMaxWidth, widest)) + 1;
                column.MaxWidth = Math.Max(column.MaxWidth, fitted);
                column.MinWidth = Math.Min(column.MinWidth, fitted);
                column.Width = new DataGridLength(fitted, DataGridLengthUnitType.Pixel);
            }
        }

        private static T FindAncestor<T>(DependencyObject element) where T : DependencyObject
        {
            var current = element;
            while (current != null && !(current is T))
            {
                current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current);
            }

            return current as T;
        }

        private static IEnumerable<T> FindDescendants<T>(DependencyObject parent) where T : DependencyObject
        {
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                {
                    yield return match;
                }

                foreach (var descendant in FindDescendants<T>(child))
                {
                    yield return descendant;
                }
            }
        }

        /// <summary>
        /// Collapses each column of the published image's fixed set, other than Status, Icon and
        /// Achievement, that no drawn row has a value for.
        /// </summary>
        private static void ApplyNeutralColumnVisibility(DataGrid dataGrid, IReadOnlyList<AchievementDisplayItem> items)
        {
            foreach (var column in dataGrid.Columns)
            {
                var key = ColumnVisibilityHelper.GetColumnKey(column) ?? string.Empty;
                if (!NeutralColumns.Contains(key))
                {
                    continue;
                }

                column.Visibility = NeutralAlwaysShownColumns.Contains(key) || items.Any(item => HasNeutralCellValue(key, item))
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        /// <summary>Whether <paramref name="item"/> shows anything in the column <paramref name="key"/>.</summary>
        private static bool HasNeutralCellValue(string key, AchievementDisplayItem item)
        {
            switch (key)
            {
                case "CategoryKind":
                    return !string.IsNullOrWhiteSpace(item.CategoryKindDisplay);
                case "CategoryType":
                    return !string.IsNullOrWhiteSpace(item.TagsDisplay);
                case "CategoryLabel":
                case "CategoryIcon":
                    return !string.IsNullOrWhiteSpace(item.CategoryLabelDisplay);
                case "Note":
                    return item.HasAchievementNote;
                case "Trophy":
                    return item.HasTrophyType;
                case "Points":
                    return !string.IsNullOrWhiteSpace(item.PointsTextResolved);
                case "Rarity":
                case "RarityTier":
                case "RarityPercent":
                    // An achievement without rarity data reads as Common; a grid of those alone
                    // says nothing.
                    return item.HasRarityPercent || item.Rarity != RarityTier.Common;
                case "UnlockDate":
                case "CollectionScore":
                case "PrestigeScore":
                    // A package carries no progress.
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>
        /// The package's own entries as achievement data for the grid. A package carries no
        /// progress, so the live dialog shows every entry locked, and the category tree counts
        /// none unlocked; the published image (<paramref name="neutral"/>) shows them unlocked,
        /// as its status cell never shows progress. The package's category order, art and
        /// summary category come with them, the art at its extracted paths.
        /// </summary>
        private static GameAchievementData BuildPackageData(
            GameCustomDataPreviewDiff diff,
            GameCustomDataPortableFile manifest,
            bool neutral)
        {
            var achievements = diff.Rows
                .Where(row => row?.After != null)
                .Select(row => new AchievementDetail
                {
                    ApiName = row.ApiName,
                    DisplayName = row.After.DisplayName,
                    Description = row.After.Description,
                    UnlockedIconPath = row.After.UnlockedIcon as string,
                    LockedIconPath = row.After.LockedIcon as string,
                    Category = row.After.Category,
                    Tags = row.After.Tags,
                    IsCapstone = row.After.IsCapstone,
                    IsGoal = row.After.IsGoal,
                    AchievementNote = row.After.Note,
                    IsCustom = row.After.IsCustom,
                    Unlocked = neutral
                })
                .ToList();
            return new GameAchievementData
            {
                Achievements = achievements,
                AchievementCategoryOrder = manifest?.AchievementCategoryOrder?.Count > 0
                    ? new List<string>(manifest.AchievementCategoryOrder)
                    : null,
                AchievementCategoryImageOverrides = manifest?.AchievementCategoryImageOverrides?.Count > 0
                    ? GameCustomDataFile.CloneCategoryImageOverrideMap(manifest.AchievementCategoryImageOverrides)
                    : null,
                GameSummaryCategory = manifest?.GameSummaryCategory
            };
        }

        private static void ApplyPreloadedIcons(DependencyObject parent, IReadOnlyDictionary<string, ImageSource> images)
        {
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is Image image &&
                    AsyncImage.GetUri(image) is string uri &&
                    images.TryGetValue(uri, out var source))
                {
                    image.Source = source;
                }

                ApplyPreloadedIcons(child, images);
            }
        }

        private void Rebuild()
        {
            _diff = (DataContext as GameCustomDataPreviewModel)?.Diff;
            _afterItems = null;
            _beforeItems = null;
            _categoryRows = null;
            var diff = _diff;
            if (diff == null)
            {
                _afterData = null;
                _shownItems = new List<AchievementDisplayItem>();
                AfterGrid.ItemsSource = null;
                BeforeGrid.ItemsSource = null;
                CategoryGrid.ItemsSource = null;
                CategoryGrid.Visibility = Visibility.Collapsed;
                SummaryText.Text = string.Empty;
                PackageOnlyText.Visibility = Visibility.Collapsed;
                GameText.Visibility = Visibility.Collapsed;
                KeptEditsText.Visibility = Visibility.Collapsed;
                ComparisonOptions.Visibility = Visibility.Collapsed;
                AfterGrid.Visibility = Visibility.Collapsed;
                BeforeGrid.Visibility = Visibility.Collapsed;
                MoreRowsText.Visibility = Visibility.Collapsed;
                return;
            }

            var culture = FormattingCulture.Current;
            SummaryText.Text = string.Format(
                culture,
                ResourceProvider.GetString("LOCPlayAch_Workshop_Preview_Summary"),
                diff.IconCount.ToString("N0", culture),
                diff.OverrideCount.ToString("N0", culture),
                diff.CategoryCount.ToString("N0", culture),
                diff.CapstoneCount.ToString("N0", culture),
                diff.CustomAchievementCount.ToString("N0", culture));

            PackageOnlyText.Visibility = diff.IsPackageOnly && !NeutralRender ? Visibility.Visible : Visibility.Collapsed;
            GameText.Visibility = diff.IsPackageOnly ? Visibility.Collapsed : Visibility.Visible;
            GameNameRun.Text = diff.ComparedAgainstGameName ?? string.Empty;

            KeptEditsText.Visibility = diff.KeptEditCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            KeptEditsText.Text = diff.KeptEditCount > 0
                ? string.Format(
                    culture,
                    ResourceProvider.GetString("LOCPlayAch_Workshop_Preview_KeptEdits"),
                    diff.KeptEditCount.ToString("N0", culture))
                : string.Empty;

            var compared = diff.AfterData != null;
            _afterData = compared
                ? diff.AfterData
                : BuildPackageData(diff, (DataContext as GameCustomDataPreviewModel)?.Package?.Manifest, NeutralRender);

            // Categories needs two or more categories to say anything; Before / After needs a
            // game to compare against. The row shows when either is on offer.
            var hasBefore = diff.BeforeData != null;
            var hasCategories = !NeutralRender && BuildCategoryRows().OfType<CategorySummaryItem>().Skip(1).Any();
            BeforeButton.Visibility = hasBefore ? Visibility.Visible : Visibility.Collapsed;
            AfterButton.Visibility = hasBefore || hasCategories ? Visibility.Visible : Visibility.Collapsed;
            CategoriesButton.Visibility = hasCategories ? Visibility.Visible : Visibility.Collapsed;
            if (!hasCategories && CategoriesButton.IsChecked == true)
            {
                AfterButton.IsChecked = true;
            }

            ComparisonOptions.Visibility = (compared || hasCategories) && !NeutralRender ? Visibility.Visible : Visibility.Collapsed;

            // The published image lists what the package touches in its after-install state;
            // shared from the game it was made on, the install itself changes nothing there.
            // Without a game, every row is one of the package's own entries.
            _changedApiNames = new HashSet<string>(
                NeutralRender && compared
                    ? diff.PackageTouchedApiNames
                    : diff.Rows.Select(row => row?.ApiName).Where(apiName => apiName != null),
                StringComparer.OrdinalIgnoreCase);
            ConfigureGrid(AfterGrid, packageOnly: !compared);
            if (!NeutralRender && diff.BeforeData != null)
            {
                ConfigureGrid(BeforeGrid, packageOnly: false);
            }

            RefreshRows();
        }

        /// <summary>
        /// Settings and columns of a grid for the live dialog or the published image. The live
        /// grid keeps View Achievements' status cell, which shows the package's own entries
        /// (<paramref name="packageOnly"/>) as locked.
        /// </summary>
        private void ConfigureGrid(AchievementDataGridControl grid, bool packageOnly)
        {
            if (NeutralRender)
            {
                grid.ColumnSettingsKey = NeutralColumnSettingsKey;
                grid.AllowLayoutPersistence = false;
                grid.AllowColumnVisibilityMenu = false;
                grid.ShowRarityGlow = false;
                // A fresh record: plain names and tags, whatever the sharer's grids use.
                grid.AppearanceOptions = new AchievementGridOptions { TagDisplay = AchievementTagDisplay.None };
                grid.ColorRarityColumnsByRarity = false;
                grid.FixedRowHeight = null;
                grid.ShowColumnHeaders = true;

                // The template's first-measure layout pass runs here, so the fixed column set
                // set below is what the render lays out.
                grid.ApplyTemplate();
                var dataGrid = grid.AchievementsDataGrid;

                // The sharer's grid alignment settings stay out of the image: the behavior that
                // applies them is switched off and every column gets a fixed alignment.
                DataGridAlignmentBehavior.SetIsEnabled(dataGrid, false);
                foreach (var column in dataGrid.Columns)
                {
                    var key = ColumnVisibilityHelper.GetColumnKey(column) ?? string.Empty;
                    column.Visibility = NeutralColumns.Contains(key)
                        ? Visibility.Visible
                        : Visibility.Collapsed;

                    var horizontal = NeutralCenteredColumns.Contains(key) ? HorizontalAlignment.Center : HorizontalAlignment.Left;
                    DataGridAlignmentBehavior.SetHeaderHorizontalAlignment(column, horizontal);
                    DataGridAlignmentBehavior.SetCellHorizontalAlignment(column, horizontal);
                    DataGridAlignmentBehavior.SetCellTextAlignment(
                        column,
                        horizontal == HorizontalAlignment.Center ? TextAlignment.Center : TextAlignment.Left);
                    DataGridAlignmentBehavior.SetCellVerticalAlignment(column, VerticalAlignment.Center);

                    // The status cell shows the capstone badge only: no check mark or padlock,
                    // so the image says nothing about the sharer's progress.
                    if (string.Equals(key, "Status", StringComparison.OrdinalIgnoreCase) &&
                        column is DataGridTemplateColumn statusColumn)
                    {
                        statusColumn.CellTemplate = (DataTemplate)FindResource("NeutralStatusTemplate");
                    }
                }

                // The table's outline: a border around the grid and a header row on a surface of
                // its own above the row separators the grid already draws.
                grid.BorderThickness = new Thickness(1);
                grid.SetResourceReference(BorderBrushProperty, "PlayAch.Brush.Border");
                if (_neutralHeaderStyle == null)
                {
                    _neutralHeaderStyle = new Style(typeof(DataGridColumnHeader), dataGrid.ColumnHeaderStyle);
                    _neutralHeaderStyle.Setters.Add(new Setter(
                        BackgroundProperty,
                        new DynamicResourceExtension("PlayAch.Brush.ControlSurface")));
                    _neutralHeaderStyle.Seal();
                }

                dataGrid.ColumnHeaderStyle = _neutralHeaderStyle;

                // Every row is realized for the render, and no scroll bar is drawn beside them.
                dataGrid.EnableRowVirtualization = false;
                VirtualizingPanel.SetIsVirtualizing(dataGrid, false);
                ScrollViewer.SetVerticalScrollBarVisibility(dataGrid, ScrollBarVisibility.Hidden);
                return;
            }

            var persisted = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted;
            grid.ColumnSettingsKey = LiveColumnSettingsKey;
            grid.ShowRarityGlow = persisted?.ViewAchievementsAchievementGridShowRarityGlow ?? true;
            grid.AppearanceOptions = persisted?.GridOptions.GetAchievement(GridOptionKeys.Achievement.SingleGame);
            grid.ColorRarityColumnsByRarity = persisted?.ViewAchievementsAchievementGridColorRarityColumnsByRarity ?? false;
            grid.FixedRowHeight = persisted?.SingleGameGridRowHeight;
            grid.ShowColumnHeaders = persisted?.ShowViewAchievementsAchievementGridColumnHeaders ?? true;

            grid.ApplyTemplate();
            var liveStatusColumn = grid.AchievementsDataGrid?.Columns
                .OfType<DataGridTemplateColumn>()
                .FirstOrDefault(column => string.Equals(ColumnVisibilityHelper.GetColumnKey(column), "Status", StringComparison.OrdinalIgnoreCase));
            if (liveStatusColumn != null)
            {
                if (_defaultStatusTemplate == null)
                {
                    _defaultStatusTemplate = liveStatusColumn.CellTemplate;
                }

                liveStatusColumn.CellTemplate = _defaultStatusTemplate;
            }

            if (!packageOnly)
            {
                grid.HiddenColumnKeys = null;
            }
        }

        private void RefreshRows()
        {
            var diff = _diff;
            if (diff == null)
            {
                MoreRowsText.Visibility = Visibility.Collapsed;
                return;
            }

            // Unchanged rows exist only against a game, and the category tree shows every row.
            ShowUnchangedCheckBox.Visibility = diff.AfterData != null && !ShowsCategories
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (ShowsCategories)
            {
                AfterGrid.Visibility = Visibility.Collapsed;
                BeforeGrid.Visibility = Visibility.Collapsed;
                CategoryGrid.ItemsSource = BuildCategoryRows();
                CategoryGrid.Visibility = Visibility.Visible;
                MoreRowsText.Visibility = Visibility.Collapsed;
                return;
            }

            CategoryGrid.Visibility = Visibility.Collapsed;
            var hidden = RefreshGrid(diff);
            MoreRowsText.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
            MoreRowsText.Text = hidden > 0
                ? string.Format(
                    FormattingCulture.Current,
                    ResourceProvider.GetString("LOCPlayAch_Workshop_Preview_MoreRows"),
                    hidden.ToString("N0", FormattingCulture.Current))
                : string.Empty;
        }

        /// <summary>
        /// Shows the grid Before / After picks, the other one hidden, with the changed rows, or
        /// every row with the changed ones highlighted, up to <see cref="MaxRows"/>. Returns how
        /// many are left out.
        /// </summary>
        private int RefreshGrid(GameCustomDataPreviewDiff diff)
        {
            var showBefore = ShowsBefore;
            var showUnchanged = !NeutralRender && ShowUnchangedCheckBox.IsChecked == true;
            var grid = showBefore ? BeforeGrid : AfterGrid;
            AfterGrid.Visibility = showBefore ? Visibility.Collapsed : Visibility.Visible;
            BeforeGrid.Visibility = showBefore ? Visibility.Visible : Visibility.Collapsed;
            var items = showBefore
                ? _beforeItems ?? (_beforeItems = BuildItems(diff.BeforeData))
                : _afterItems ?? (_afterItems = BuildItems(_afterData));

            var wanted = showUnchanged
                ? items
                : items.Where(item => _changedApiNames.Contains(item.ApiName ?? string.Empty)).ToList();
            if (NeutralRender && wanted.Count == 0)
            {
                // A package that touches no single achievement (an order or category order only)
                // shows the game's first rows in their after-install order.
                wanted = items;
            }

            var limit = MaxRows > 0 ? Math.Min(MaxRows, wanted.Count) : wanted.Count;
            _shownItems = wanted.Take(limit).ToList();

            // Every row is a changed one unless unchanged rows are shown too.
            grid.HighlightedItems = showUnchanged
                ? new HashSet<AchievementDisplayItem>(_shownItems.Where(item => _changedApiNames.Contains(item.ApiName ?? string.Empty)))
                : null;
            grid.ItemsSource = _shownItems;
            if (NeutralRender)
            {
                ApplyNeutralColumnVisibility(grid.AchievementsDataGrid, _shownItems);
            }
            else if (diff.AfterData == null)
            {
                // The package's own entries: hide what none of them fills, without touching the
                // layout the user keeps for this grid.
                grid.HiddenColumnKeys = PackageOptionalColumns
                    .Where(key => !items.Any(item => HasNeutralCellValue(key, item)))
                    .ToList();
            }

            return wanted.Count - limit;
        }

        /// <summary>
        /// The after-install category tree as View Achievements' category list shows it: one row
        /// per category with its art and leaf name, nested under the tree guide, every node
        /// expanded, from every after row whatever the Before / After filters show. The rows go in
        /// unsorted, as View Achievements passes them, since a category the category order does
        /// not list is placed where its first achievement comes.
        /// </summary>
        private List<GameSummaryItem> BuildCategoryRows()
        {
            if (_categoryRows != null)
            {
                return _categoryRows;
            }

            var items = BuildItems(_afterData, sorted: false);
            var badgeMode = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.CategoryCompletionBadgeMode
                ?? CategoryCompletionBadgeMode.All;
            _categoryRows = items.Count == 0
                ? new List<GameSummaryItem>()
                : CategorySummaryBuilder.BuildTree(items, badgeMode, useLeafNames: true);
            CategoryTreeShapeBuilder.Stamp(_categoryRows, enabled: true);
            return _categoryRows;
        }

        /// <summary>
        /// The display rows of <paramref name="data"/> as View Achievements builds them: custom
        /// order applied, then the configured default sort with goals first unless
        /// <paramref name="sorted"/> is false.
        /// </summary>
        private List<AchievementDisplayItem> BuildItems(GameAchievementData data, bool sorted = true)
        {
            if (data?.Achievements == null)
            {
                return new List<AchievementDisplayItem>();
            }

            var settings = NeutralRender ? null : PlayniteAchievementsPlugin.Instance?.Settings;
            IEnumerable<AchievementDetail> source = data.Achievements.Where(achievement => achievement != null);
            if (data.AchievementOrder != null && data.AchievementOrder.Count > 0)
            {
                source = AchievementOrderHelper.ApplyOrder(source, achievement => achievement.ApiName, data.AchievementOrder);
            }

            var items = source
                .Select(achievement => AchievementDisplayItem.Create(data, achievement, settings, playniteGameIdOverride: data.PlayniteGameId))
                .Where(item => item != null)
                .ToList();
            if (NeutralRender)
            {
                // The published image stamps every row unlocked so it says nothing about the
                // sharer's progress; without this, that stamp would also unmask the game's hidden
                // achievements for everyone browsing. The image uses the default hidden settings,
                // so a hidden row shows the fallback icon and the hidden title.
                foreach (var item in items)
                {
                    item.MaskAsLocked = true;
                }
            }

            if (!sorted)
            {
                return items;
            }

            AchievementSortHelper.OrderGameAchievementItems(
                items,
                columnSortPath: null,
                columnSortDirection: null,
                useSourceOrder: false,
                settings?.Persisted,
                AchievementSortSurface.SingleGame);
            return items;
        }

        private void Options_Changed(object sender, RoutedEventArgs e)
        {
            if (IsInitialized)
            {
                RefreshRows();
            }
        }
    }
}
