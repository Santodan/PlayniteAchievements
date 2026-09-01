using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Summaries;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.ViewModels.ManageAchievements;
using PlayniteAchievements.Views.Helpers;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Reusable DataGrid control for displaying achievements with sorting,
    /// column visibility, and width persistence.
    /// </summary>
    public partial class AchievementDataGridControl : UserControl, IDisposable
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private DataGridColumnLayoutService _columnPersistence;
        private bool _isAttached;
        private PersistedSettingsSubscription _persistedSubscription;
        private List<AchievementDisplayItem> _preSortItems;
        private const double DefaultStatusColumnWidth = 40;
        private const double DefaultIconColumnWidth = 72;
        private const double DefaultGameImageColumnWidth = 96;
        private const double DefaultFriendAvatarColumnWidth = 44;
        private const double DefaultFriendColumnWidth = 140;
        private const double DefaultTrophyIconColumnWidth = 72;
        private const double DefaultCapturesColumnWidth = 56;
        private const double MinimumStatusColumnWidth = 28;
        private const double MinimumGameImageColumnWidth = 32;
        private const double MinimumFriendAvatarColumnWidth = 32;
        private const double MinimumFriendColumnWidth = 64;
        private const double MaximumStatusColumnWidth = 96;
        private const double MaximumGameImageColumnWidth = 600;
        private const double MaximumFriendAvatarColumnWidth = 96;
        private const double MaximumFriendColumnWidth = 280;
        private const string StatusColumnKey = "Status";
        private const string GameColumnKey = "Game";
        private const string FriendAvatarColumnKey = "Avatar";
        private const string FriendColumnKey = "Friend";

        private static readonly IReadOnlyDictionary<string, double> DefaultImageColumnWidthSeeds =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["Status"] = DefaultStatusColumnWidth,
                ["Icon"] = DefaultIconColumnWidth,
                ["Game"] = DefaultGameImageColumnWidth,
                ["Avatar"] = DefaultFriendAvatarColumnWidth,
                ["CategoryIcon"] = DefaultGameImageColumnWidth,
                ["Trophy"] = DefaultTrophyIconColumnWidth,
                ["RarityTier"] = DefaultTrophyIconColumnWidth,
                ["Captures"] = DefaultCapturesColumnWidth
            };

        private static readonly IReadOnlyDictionary<string, double> LegacyImageColumnRuntimeDefaults =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["Status"] = 36,
                ["Icon"] = 64,
                ["Game"] = 64
            };

        // Defaults are applied only when a saved layout is missing a key.
        private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, bool>> DefaultVisibilityByColumnSettingsKey =
            new Dictionary<string, IReadOnlyDictionary<string, bool>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Default"] = CreateAchievementVisibility(captures: true),
                ["SingleGame"] = CreateAchievementVisibility(captures: true),
                ["DesktopTheme"] = CreateAchievementVisibility(captures: true),
                ["OverviewSelectedGameAchievements"] = CreateAchievementVisibility(captures: true),
                ["OverviewGame"] = CreateAchievementVisibility(captures: true),
                ["OverviewRecentAchievements"] = CreateAchievementVisibility(status: false, game: true, captures: true),
                ["FriendsOverviewRecentAchievements"] = CreateAchievementVisibility(
                    status: false,
                    game: true,
                    friendAvatar: true,
                    friend: true,
                    unlockDate: true),
                ["FriendsOverviewSelectedFriendAchievements"] = CreateAchievementVisibility(
                    status: false,
                    game: true,
                    friendAvatar: false,
                    friend: false,
                    unlockDate: true),
                ["FriendsOverviewSelectedGameAchievements"] = CreateAchievementVisibility(
                    status: false,
                    game: false,
                    friendAvatar: true,
                    friend: true,
                    unlockDate: true),
                ["FriendsOverviewSelectedFriendGameAchievements"] = CreateAchievementVisibility(
                    status: true,
                    game: false,
                    friendAvatar: false,
                    friend: false,
                    unlockDate: true),
                ["ViewFriendsAchievements"] = CreateAchievementVisibility(
                    status: true,
                    game: false,
                    friendAvatar: true,
                    friend: true,
                    unlockDate: true),
                ["ViewFriendsAchievementsSelectedFriendAchievements"] = CreateAchievementVisibility(
                    status: true,
                    game: false,
                    friendAvatar: false,
                    friend: false,
                    unlockDate: true),
                ["Overview"] = CreateAchievementVisibility(status: false, game: true, captures: true),
                ["StartPageAchievements"] = CreateAchievementVisibility(
                    status: false,
                    game: false,
                    unlockDate: false,
                    categoryType: false,
                    categoryLabel: false,
                    trophy: false,
                    rarity: false,
                    rarityTier: true,
                    collectionScore: false,
                    prestigeScore: false,
                    points: false),
                ["StartPageFriendAchievements"] = CreateAchievementVisibility(
                    status: false,
                    game: true,
                    friendAvatar: true,
                    friend: true,
                    unlockDate: true)
            };

        private static IReadOnlyDictionary<string, bool> CreateAchievementVisibility(
            bool status = true,
            bool icon = true,
            bool achievement = true,
            bool title = false,
            bool note = false,
            bool game = false,
            bool friendAvatar = false,
            bool friend = false,
            bool unlockDate = true,
            bool categoryType = false,
            bool categoryLabel = false,
            bool categoryIcon = false,
            bool trophy = false,
            bool rarity = true,
            bool rarityTier = false,
            bool rarityPercent = false,
            bool collectionScore = false,
            bool prestigeScore = false,
            bool points = false,
            bool captures = false)
        {
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["Status"] = status,
                ["Icon"] = icon,
                ["Achievement"] = achievement,
                ["Title"] = title,
                ["Note"] = note,
                ["Game"] = game,
                ["Avatar"] = friendAvatar,
                ["Friend"] = friend,
                ["UnlockDate"] = unlockDate,
                ["CategoryType"] = categoryType,
                ["CategoryLabel"] = categoryLabel,
                ["CategoryIcon"] = categoryIcon,
                ["Trophy"] = trophy,
                ["Rarity"] = rarity,
                ["RarityTier"] = rarityTier,
                ["RarityPercent"] = rarityPercent,
                ["CollectionScore"] = collectionScore,
                ["PrestigeScore"] = prestigeScore,
                ["Points"] = points,
                ["Captures"] = captures
            };
        }

        private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> DefaultOrderByColumnSettingsKey =
            new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.OrdinalIgnoreCase)
            {
                ["FriendsOverviewRecentAchievements"] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Avatar"] = 0,
                    ["Friend"] = 1
                },
                ["FriendsOverviewSelectedGameAchievements"] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Avatar"] = 0,
                    ["Friend"] = 1
                },
                ["FriendsOverviewSelectedFriendGameAchievements"] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Status"] = 0,
                },
                ["ViewFriendsAchievements"] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Avatar"] = 0,
                    ["Friend"] = 1
                },
                ["ViewFriendsAchievementsSelectedFriendAchievements"] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Status"] = 0
                },
                ["StartPageFriendAchievements"] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Avatar"] = 0,
                    ["Friend"] = 1
                }
            };

        /// <summary>
        /// Identifies the ItemsSource dependency property.
        /// </summary>
        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable<AchievementDisplayItem>),
                typeof(AchievementDataGridControl), new PropertyMetadata(null, OnItemsSourceChanged));

        /// <summary>
        /// Gets or sets the achievement items to display.
        /// </summary>
        public IEnumerable<AchievementDisplayItem> ItemsSource
        {
            get => (IEnumerable<AchievementDisplayItem>)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        private static readonly DependencyPropertyKey HasAnyFavoritesPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(HasAnyFavorites), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(false));

        /// <summary>
        /// True when any row belongs to a favorited friend. The Friend column's favorite-star gutter
        /// collapses when this is false (also always false for self-achievement grids).
        /// </summary>
        public static readonly DependencyProperty HasAnyFavoritesProperty = HasAnyFavoritesPropertyKey.DependencyProperty;

        public bool HasAnyFavorites => (bool)GetValue(HasAnyFavoritesProperty);

        private void RecomputeHasAnyFavorites()
        {
            var hasAny = ItemsSource?.Any(item => item?.FriendIsFavorite == true) ?? false;
            SetValue(HasAnyFavoritesPropertyKey, hasAny);
        }

        private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AchievementDataGridControl control)
            {
                control._preSortItems = null;
                if (!control.UseExternalSorting)
                {
                    // Externally sorted surfaces own their header indicators via SetSortIndicator
                    // and re-apply them only when their sort state changes; clearing here would
                    // drop the arrow on a plain source swap while the external sort still applies.
                    DataGridSortingHelper.ClearSortIndicators(control.AchievementsDataGrid);
                }

                control.ObserveItemsSourceCollection();
                control.OnItemsSourceContentChanged();
            }
        }

        /// <summary>
        /// Identifies the RevealCommand dependency property.
        /// </summary>
        public static readonly DependencyProperty RevealCommandProperty =
            DependencyProperty.Register(nameof(RevealCommand), typeof(System.Windows.Input.ICommand),
                typeof(AchievementDataGridControl), new PropertyMetadata(null));

        /// <summary>
        /// Gets or sets the command to execute when revealing a hidden achievement.
        /// The command parameter will be the AchievementDisplayItem.
        /// </summary>
        public System.Windows.Input.ICommand RevealCommand
        {
            get => (System.Windows.Input.ICommand)GetValue(RevealCommandProperty);
            set => SetValue(RevealCommandProperty, value);
        }

        /// <summary>
        /// Identifies the ColumnSettingsKey dependency property.
        /// Used to separate persisted column settings for different contexts.
        /// </summary>
        public static readonly DependencyProperty ColumnSettingsKeyProperty =
            DependencyProperty.Register(nameof(ColumnSettingsKey), typeof(string),
                typeof(AchievementDataGridControl), new PropertyMetadata("Default", OnColumnSettingsKeyChanged));

        /// <summary>
        /// Gets or sets the key used to persist column settings separately per control instance.
        /// </summary>
        public string ColumnSettingsKey
        {
            get => (string)GetValue(ColumnSettingsKeyProperty);
            set => SetValue(ColumnSettingsKeyProperty, value);
        }

        /// <summary>
        /// Identifies the UnlockDateMode dependency property.
        /// </summary>
        public static readonly DependencyProperty UnlockDateModeProperty =
            DependencyProperty.Register(nameof(UnlockDateMode), typeof(DateDisplayMode),
                typeof(AchievementDataGridControl), new PropertyMetadata(DateDisplayMode.DateAndTime));

        /// <summary>
        /// Resolved per-surface display mode for the "Unlock Date" column; bound by the cell template.
        /// </summary>
        public DateDisplayMode UnlockDateMode
        {
            get => (DateDisplayMode)GetValue(UnlockDateModeProperty);
            private set => SetValue(UnlockDateModeProperty, value);
        }

        /// <summary>
        /// Identifies the UseExternalSorting dependency property.
        /// When true, the control raises the Sorting event but does not perform in-memory sorting.
        /// </summary>
        public static readonly DependencyProperty UseExternalSortingProperty =
            DependencyProperty.Register(nameof(UseExternalSorting), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(false));

        /// <summary>
        /// Gets or sets whether sorting should be handled externally.
        /// When true, the Sorting event is raised but in-memory sorting is skipped.
        /// </summary>
        public bool UseExternalSorting
        {
            get => (bool)GetValue(UseExternalSortingProperty);
            set => SetValue(UseExternalSortingProperty, value);
        }

        public static readonly DependencyProperty SortScopeProperty =
            DependencyProperty.Register(nameof(SortScope), typeof(AchievementSortScope),
                typeof(AchievementDataGridControl), new PropertyMetadata(AchievementSortScope.GameAchievements));

        public AchievementSortScope SortScope
        {
            get => (AchievementSortScope)GetValue(SortScopeProperty);
            set => SetValue(SortScopeProperty, value);
        }

        /// <summary>
        /// Identifies the ShowGameColumn dependency property.
        /// When true, displays the Game column showing the associated game's icon/cover.
        /// </summary>
        public static readonly DependencyProperty ShowGameColumnProperty =
            DependencyProperty.Register(nameof(ShowGameColumn), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(false, OnColumnVisibilityChanged));

        /// <summary>
        /// Gets or sets whether to show the Game column.
        /// </summary>
        public bool ShowGameColumn
        {
            get => (bool)GetValue(ShowGameColumnProperty);
            set => SetValue(ShowGameColumnProperty, value);
        }

        /// <summary>
        /// Identifies the ShowFriendColumn dependency property.
        /// When true, displays the friend identity column.
        /// </summary>
        public static readonly DependencyProperty ShowFriendColumnProperty =
            DependencyProperty.Register(nameof(ShowFriendColumn), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(false, OnColumnVisibilityChanged));

        /// <summary>
        /// Gets or sets whether to show the Friend column.
        /// </summary>
        public bool ShowFriendColumn
        {
            get => (bool)GetValue(ShowFriendColumnProperty);
            set => SetValue(ShowFriendColumnProperty, value);
        }

        /// <summary>
        /// Identifies the HideStatusColumn dependency property.
        /// When true, hides the Status column (checkmark/padlock).
        /// </summary>
        public static readonly DependencyProperty HideStatusColumnProperty =
            DependencyProperty.Register(nameof(HideStatusColumn), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(false, OnColumnVisibilityChanged));

        /// <summary>
        /// Gets or sets whether to hide the Status column.
        /// </summary>
        public bool HideStatusColumn
        {
            get => (bool)GetValue(HideStatusColumnProperty);
            set => SetValue(HideStatusColumnProperty, value);
        }

        /// <summary>
        /// Identifies the UseCoverImages dependency property.
        /// When true and ShowGameColumn is true, displays cover images instead of icons in the Game column.
        /// </summary>
        public static readonly DependencyProperty UseCoverImagesProperty =
            DependencyProperty.Register(nameof(UseCoverImages), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(false));

        /// <summary>
        /// Gets or sets whether to use cover images (instead of icons) in the Game column.
        /// </summary>
        public bool UseCoverImages
        {
            get => (bool)GetValue(UseCoverImagesProperty);
            set => SetValue(UseCoverImagesProperty, value);
        }

        /// <summary>
        /// Identifies the ShowRarityGlow dependency property.
        /// When true, unlocked achievement icons in this grid display rarity-based glow effects.
        /// </summary>
        public static readonly DependencyProperty ShowRarityGlowProperty =
            DependencyProperty.Register(nameof(ShowRarityGlow), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(true));

        /// <summary>
        /// Gets or sets whether unlocked achievement icons in this grid display rarity glow.
        /// </summary>
        public bool ShowRarityGlow
        {
            get => (bool)GetValue(ShowRarityGlowProperty);
            set => SetValue(ShowRarityGlowProperty, value);
        }

        /// <summary>
        /// Identifies the AnimateRarityGlows dependency property. When true, rarity glows in this
        /// grid gently fade in and out. Self-bound to the global setting in the constructor.
        /// </summary>
        public static readonly DependencyProperty AnimateRarityGlowsProperty =
            DependencyProperty.Register(nameof(AnimateRarityGlows), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(true));

        /// <summary>
        /// Gets or sets whether unlocked achievement icons in this grid pulse their rarity glow.
        /// </summary>
        public bool AnimateRarityGlows
        {
            get => (bool)GetValue(AnimateRarityGlowsProperty);
            set => SetValue(AnimateRarityGlowsProperty, value);
        }

        /// <summary>
        /// Identifies the SoftGlowTiers dependency property: which rarity tiers show the soft halo in
        /// this grid. Self-bound to the global setting in the constructor, so changing the selection
        /// re-evaluates the cells' glow bindings immediately.
        /// </summary>
        public static readonly DependencyProperty SoftGlowTiersProperty =
            DependencyProperty.Register(nameof(SoftGlowTiers), typeof(RaritySelection),
                typeof(AchievementDataGridControl), new PropertyMetadata(RaritySelection.All));

        /// <summary>
        /// Gets or sets which rarity tiers show the soft halo in this grid.
        /// </summary>
        public RaritySelection SoftGlowTiers
        {
            get => (RaritySelection)GetValue(SoftGlowTiersProperty);
            set => SetValue(SoftGlowTiersProperty, value);
        }

        /// <summary>
        /// Identifies the RayGlowTiers dependency property: which rarity tiers show the rays in this
        /// grid. The ray layer itself self-binds this, but the edge that comes with it is an effect on
        /// a cell layer, which needs the selection reachable from the template.
        /// </summary>
        public static readonly DependencyProperty RayGlowTiersProperty =
            DependencyProperty.Register(nameof(RayGlowTiers), typeof(RaritySelection),
                typeof(AchievementDataGridControl), new PropertyMetadata(RaritySelection.None));

        /// <summary>
        /// Gets or sets which rarity tiers show the rays in this grid.
        /// </summary>
        public RaritySelection RayGlowTiers
        {
            get => (RaritySelection)GetValue(RayGlowTiersProperty);
            set => SetValue(RayGlowTiersProperty, value);
        }

        /// <summary>
        /// Identifies the ShowHardcoreBorder dependency property: whether Hardcore unlocks take the
        /// crisp metallic border in place of a glow. Self-bound to the global setting.
        /// </summary>
        public static readonly DependencyProperty ShowHardcoreBorderProperty =
            DependencyProperty.Register(nameof(ShowHardcoreBorder), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(true));

        public bool ShowHardcoreBorder
        {
            get => (bool)GetValue(ShowHardcoreBorderProperty);
            set => SetValue(ShowHardcoreBorderProperty, value);
        }

        /// <summary>
        /// Identifies the ColorNamesByRarity dependency property.
        /// When true, achievement name text in this grid is colored by rarity tier (capstone
        /// achievements use the completed color) instead of the default text color.
        /// </summary>
        public static readonly DependencyProperty ColorNamesByRarityProperty =
            DependencyProperty.Register(nameof(ColorNamesByRarity), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(false));

        /// <summary>
        /// Gets or sets whether achievement name text in this grid is colored by rarity.
        /// </summary>
        public bool ColorNamesByRarity
        {
            get => (bool)GetValue(ColorNamesByRarityProperty);
            set => SetValue(ColorNamesByRarityProperty, value);
        }

        /// <summary>
        /// Identifies the ColorRarityColumnsByRarity dependency property.
        /// When true, rarity and rarity percent text in this grid are colored by rarity tier
        /// instead of the default text color.
        /// </summary>
        public static readonly DependencyProperty ColorRarityColumnsByRarityProperty =
            DependencyProperty.Register(nameof(ColorRarityColumnsByRarity), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(false));

        /// <summary>
        /// Gets or sets whether rarity column text in this grid is colored by rarity.
        /// </summary>
        public bool ColorRarityColumnsByRarity
        {
            get => (bool)GetValue(ColorRarityColumnsByRarityProperty);
            set => SetValue(ColorRarityColumnsByRarityProperty, value);
        }

        /// <summary>
        /// Identifies the DataGridMaxHeight dependency property.
        /// When set, limits the maximum height of the internal DataGrid.
        /// </summary>
        public static readonly DependencyProperty DataGridMaxHeightProperty =
            DependencyProperty.Register(nameof(DataGridMaxHeight), typeof(double),
                typeof(AchievementDataGridControl), new PropertyMetadata(PersistedSettings.DefaultAchievementDataGridMaxHeight));

        /// <summary>
        /// Gets or sets the maximum height of the internal DataGrid.
        /// Default is PersistedSettings.DefaultAchievementDataGridMaxHeight.
        /// </summary>
        public double DataGridMaxHeight
        {
            get => (double)GetValue(DataGridMaxHeightProperty);
            set => SetValue(DataGridMaxHeightProperty, value);
        }

        /// <summary>
        /// Identifies the FixedRowHeight dependency property.
        /// Null or NaN keeps the existing automatic row sizing.
        /// </summary>
        public static readonly DependencyProperty FixedRowHeightProperty =
            DependencyProperty.Register(nameof(FixedRowHeight), typeof(double?),
                typeof(AchievementDataGridControl), new PropertyMetadata(null, OnRowSizingChanged));

        /// <summary>
        /// Gets or sets a fixed DataGrid row height. Null keeps automatic sizing.
        /// </summary>
        public double? FixedRowHeight
        {
            get => (double?)GetValue(FixedRowHeightProperty);
            set => SetValue(FixedRowHeightProperty, value);
        }

        /// <summary>
        /// Identifies the AllowLayoutPersistence dependency property.
        /// When false, the control reads persisted layout state but never writes changes back.
        /// </summary>
        public static readonly DependencyProperty AllowLayoutPersistenceProperty =
            DependencyProperty.Register(nameof(AllowLayoutPersistence), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(true));

        /// <summary>
        /// Gets or sets whether column widths and visibility changes can be persisted.
        /// </summary>
        public bool AllowLayoutPersistence
        {
            get => (bool)GetValue(AllowLayoutPersistenceProperty);
            set => SetValue(AllowLayoutPersistenceProperty, value);
        }

        /// <summary>
        /// Identifies the AllowColumnVisibilityMenu dependency property.
        /// When false, the right-click column visibility menu is suppressed.
        /// </summary>
        public static readonly DependencyProperty AllowColumnVisibilityMenuProperty =
            DependencyProperty.Register(nameof(AllowColumnVisibilityMenu), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(true));

        /// <summary>
        /// Gets or sets whether the right-click column visibility menu is enabled.
        /// </summary>
        public bool AllowColumnVisibilityMenu
        {
            get => (bool)GetValue(AllowColumnVisibilityMenuProperty);
            set => SetValue(AllowColumnVisibilityMenuProperty, value);
        }

        public static readonly DependencyProperty DelayInitialRenderUntilNormalizedProperty =
            DependencyProperty.Register(nameof(DelayInitialRenderUntilNormalized), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(false, OnDelayInitialRenderUntilNormalizedChanged));

        public bool DelayInitialRenderUntilNormalized
        {
            get => (bool)GetValue(DelayInitialRenderUntilNormalizedProperty);
            set => SetValue(DelayInitialRenderUntilNormalizedProperty, value);
        }

        public static readonly DependencyProperty ShowColumnHeadersProperty =
            DependencyProperty.Register(nameof(ShowColumnHeaders), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(true, OnShowColumnHeadersChanged));

        public bool ShowColumnHeaders
        {
            get => (bool)GetValue(ShowColumnHeadersProperty);
            set => SetValue(ShowColumnHeadersProperty, value);
        }

        public static readonly DependencyProperty ControlBarProperty =
            DependencyProperty.Register(nameof(ControlBar), typeof(GridControlBarViewModel),
                typeof(AchievementDataGridControl), new PropertyMetadata(null, OnControlBarChanged));

        private static void OnControlBarChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AchievementDataGridControl control)
            {
                control.SyncModeToggle();
            }
        }

        public GridControlBarViewModel ControlBar
        {
            get => (GridControlBarViewModel)GetValue(ControlBarProperty);
            set => SetValue(ControlBarProperty, value);
        }

        public static readonly DependencyProperty ShowControlBarProperty =
            DependencyProperty.Register(nameof(ShowControlBar), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(true));

        public bool ShowControlBar
        {
            get => (bool)GetValue(ShowControlBarProperty);
            set => SetValue(ShowControlBarProperty, value);
        }

        // ---- Category-summaries mode -------------------------------------------------------
        // When EnableCategoryMode is set, a toggle is injected into the control bar (right of the
        // category dropdowns). Toggling it swaps the flat achievement grid for a summary grid
        // holding every category in the tree, indented by depth; clicking any row, at any depth,
        // replaces that list with the achievements of its whole subtree, and Back returns. One hop
        // each way. All state is self-contained here so any surface hosting this control opts in
        // with a single attribute.

        private bool _isCategoryMode;
        // Segments rather than the joined path: rendering a breadcrumb and jumping to an ancestor
        // are the two things this state exists for, and both are trivial on segments.
        private readonly List<string> _drillPath = new List<string>();

        // Scroll position of the category list at the moment of drilling in, so stepping back out
        // returns to the same place instead of the top.
        private double _categoryListScrollOffset;

        private bool IsDrilled => _drillPath.Count > 0;

        // What the drilled achievement grid covers: the node's whole subtree, or only its direct
        // achievements. Recorded at drill time from the clicked row's collapse state - a collapsed
        // row's numbers absorbed its subtree, an expanded row's numbers were its own, and the drill
        // opens exactly what the numbers described. Lives beside the path rather than in it: the
        // path alone cannot carry scope.
        private enum DrillScope
        {
            Subtree = 0,
            Own = 1
        }

        private DrillScope _drillScope = DrillScope.Subtree;

        private string DrilledPath => _drillPath.Count == 0 ? null : CategoryPathHelper.Join(_drillPath);

        private void SetDrillPath(string path, DrillScope scope = DrillScope.Subtree)
        {
            _drillPath.Clear();
            _drillScope = DrillScope.Subtree;
            if (!string.IsNullOrWhiteSpace(path))
            {
                _drillPath.AddRange(CategoryPathHelper.Split(path));
                _drillScope = scope;
            }
        }

        private void ClearDrillSelection()
        {
            _drillPath.Clear();
            _drillScope = DrillScope.Subtree;
            SelectedCategorySummaryItems = null;
            if (CategoryListGrid != null)
            {
                CategoryListGrid.SelectedItem = null;
            }
        }
        private GridModeToggle _modeToggle;
        private GridMultiSelectFilter _connectedCategoryFilter;
        private GridControlBarViewModel _controlBarWithToggle;
        private INotifyCollectionChanged _observedItemsSource;
        private INotifyCollectionChanged _observedCategorySummarySource;
        private BulkObservableCollection<AchievementDisplayItem> _drillItems;
        private List<GameSummaryItem> _allCategorySummaries;
        private GridSearchControl _categorySearch;
        private GridSearchControl _originalSearch;
        private string _categorySearchText = string.Empty;
        private string _categorySortPath;
        private ListSortDirection? _categorySortDirection;
        private bool _startInCategoryModeApplied;
        private DataGridRow _pendingCategoryRightClickRow;

        // Collapsed subtrees, keyed by CategoryPath. View-local for the control's lifetime and
        // never cleared on drill, mode, or data changes: a key whose path is gone is inert, and
        // keeping the rest means a drill round-trip or data delta preserves the user's collapses.
        private readonly HashSet<string> _collapsedCategoryPaths =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Whether the full (unfiltered) tree has any nesting; gates the Expand/Collapse All buttons.
        private bool _categoryTreeHasNesting;
        private GridActionButton _expandAllButton;
        private GridActionButton _collapseAllButton;

        // The published category rows, kept as one live collection so a collapse/expand applies as
        // row removals and insertions instead of an ItemsSource swap - the swap threw away every
        // realized row container, which showed as the whole grid flickering on each toggle.
        private BulkObservableCollection<GameSummaryItem> _visibleCategoryRows;
        private List<GameSummaryItem> _visibleRowsMaster;
        private bool _visibleRowsInMasterOrder;

        public static readonly DependencyProperty EnableCategoryModeProperty =
            DependencyProperty.Register(nameof(EnableCategoryMode), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(false, OnEnableCategoryModeChanged));

        public bool EnableCategoryMode
        {
            get => (bool)GetValue(EnableCategoryModeProperty);
            set => SetValue(EnableCategoryModeProperty, value);
        }

        private static void OnEnableCategoryModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AchievementDataGridControl control)
            {
                control.SyncModeToggle();
            }
        }

        public static readonly DependencyProperty HideCategorySummaryRowProperty =
            DependencyProperty.Register(nameof(HideCategorySummaryRow), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(false, OnHideCategorySummaryRowChanged));

        // When true, the in-grid category summary row (DrillHeaderVisible) stays hidden once a
        // category is selected, giving the achievement list the full vertical space.
        public bool HideCategorySummaryRow
        {
            get => (bool)GetValue(HideCategorySummaryRowProperty);
            set => SetValue(HideCategorySummaryRowProperty, value);
        }

        private static void OnHideCategorySummaryRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AchievementDataGridControl control)
            {
                control.ApplyCategoryViewState();
            }
        }

        public static readonly DependencyProperty CategoryColumnSettingsKeyProperty =
            DependencyProperty.Register(nameof(CategoryColumnSettingsKey), typeof(string),
                typeof(AchievementDataGridControl), new PropertyMetadata(null));

        // Per-surface column-settings key for the embedded category grids (list + drill header),
        // kept distinct from the achievement grid's ColumnSettingsKey so category columns persist
        // independently. Falls back to "<ColumnSettingsKey>CategorySummaries" when unset.
        public string CategoryColumnSettingsKey
        {
            get => (string)GetValue(CategoryColumnSettingsKeyProperty);
            set => SetValue(CategoryColumnSettingsKeyProperty, value);
        }

        public static readonly DependencyProperty CategoryUseCoverImagesProperty =
            DependencyProperty.Register(nameof(CategoryUseCoverImages), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(false));

        // Independent cover-images toggle for the embedded category grids (list + drill header),
        // kept distinct from the achievement grid's own UseCoverImages.
        public bool CategoryUseCoverImages
        {
            get => (bool)GetValue(CategoryUseCoverImagesProperty);
            set => SetValue(CategoryUseCoverImagesProperty, value);
        }

        public static readonly DependencyProperty CategoryShowCompletionGlowProperty =
            DependencyProperty.Register(nameof(CategoryShowCompletionGlow), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(true));

        // Completion glow toggle for the embedded category grids (list + drill header),
        // kept distinct from any game-summaries grid on the same surface.
        public bool CategoryShowCompletionGlow
        {
            get => (bool)GetValue(CategoryShowCompletionGlowProperty);
            set => SetValue(CategoryShowCompletionGlowProperty, value);
        }

        public static readonly DependencyProperty CategoryShowColumnHeadersProperty =
            DependencyProperty.Register(nameof(CategoryShowColumnHeaders), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(true));

        public bool CategoryShowColumnHeaders
        {
            get => (bool)GetValue(CategoryShowColumnHeadersProperty);
            set => SetValue(CategoryShowColumnHeadersProperty, value);
        }

        public static readonly DependencyProperty CategoryFixedRowHeightProperty =
            DependencyProperty.Register(nameof(CategoryFixedRowHeight), typeof(double?),
                typeof(AchievementDataGridControl), new PropertyMetadata(null));

        public double? CategoryFixedRowHeight
        {
            get => (double?)GetValue(CategoryFixedRowHeightProperty);
            set => SetValue(CategoryFixedRowHeightProperty, value);
        }

        public static readonly DependencyProperty CategorySummariesProperty =
            DependencyProperty.Register(nameof(CategorySummaries), typeof(IEnumerable<GameSummaryItem>),
                typeof(AchievementDataGridControl), new PropertyMetadata(null));

        public IEnumerable<GameSummaryItem> CategorySummaries
        {
            get => (IEnumerable<GameSummaryItem>)GetValue(CategorySummariesProperty);
            set => SetValue(CategorySummariesProperty, value);
        }

        public static readonly DependencyProperty SelectedCategorySummaryItemsProperty =
            DependencyProperty.Register(nameof(SelectedCategorySummaryItems), typeof(IEnumerable<GameSummaryItem>),
                typeof(AchievementDataGridControl), new PropertyMetadata(null));

        public IEnumerable<GameSummaryItem> SelectedCategorySummaryItems
        {
            get => (IEnumerable<GameSummaryItem>)GetValue(SelectedCategorySummaryItemsProperty);
            set => SetValue(SelectedCategorySummaryItemsProperty, value);
        }

        public static readonly DependencyProperty EffectiveAchievementsProperty =
            DependencyProperty.Register(nameof(EffectiveAchievements), typeof(IEnumerable<AchievementDisplayItem>),
                typeof(AchievementDataGridControl), new PropertyMetadata(null));

        // The collection actually bound to the achievement DataGrid: the full ItemsSource in flat
        // and drill-list modes, or the category-filtered subset while drilled in.
        public IEnumerable<AchievementDisplayItem> EffectiveAchievements
        {
            get => (IEnumerable<AchievementDisplayItem>)GetValue(EffectiveAchievementsProperty);
            set => SetValue(EffectiveAchievementsProperty, value);
        }

        public static readonly DependencyProperty CategorySummarySourceProperty =
            DependencyProperty.Register(nameof(CategorySummarySource), typeof(IEnumerable<AchievementDisplayItem>),
                typeof(AchievementDataGridControl), new PropertyMetadata(null, OnCategorySummarySourceChanged));

        // Optional unfiltered achievement source for building category rollups. When set, the category
        // list and drill-header summaries are computed from this full set so achievement filters
        // (Unlocked/Locked/Hidden) applied while drilled never shift other categories' totals. Falls
        // back to ItemsSource when unset.
        public IEnumerable<AchievementDisplayItem> CategorySummarySource
        {
            get => (IEnumerable<AchievementDisplayItem>)GetValue(CategorySummarySourceProperty);
            set => SetValue(CategorySummarySourceProperty, value);
        }

        private static void OnCategorySummarySourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AchievementDataGridControl control)
            {
                control.ObserveCategorySummarySourceCollection();
                if (control._isCategoryMode)
                {
                    control.OnItemsSourceContentChanged();
                }
            }
        }

        public static readonly DependencyProperty DrilledCategoryProperty =
            DependencyProperty.Register(nameof(DrilledCategory), typeof(string),
                typeof(AchievementDataGridControl), new PropertyMetadata(null));

        // Reports the currently drilled-into category in display form - "DLC > Season Pass", never
        // the storage separator - so a host can title its header with it (null when not drilled).
        // Written by the control; bind OneWayToSource. Not an identity: it is text for a person.
        public string DrilledCategory
        {
            get => (string)GetValue(DrilledCategoryProperty);
            set => SetValue(DrilledCategoryProperty, value);
        }

        public static readonly DependencyProperty DrilledCategorySegmentsProperty =
            DependencyProperty.Register(nameof(DrilledCategorySegments), typeof(IReadOnlyList<CategoryPathSegment>),
                typeof(AchievementDataGridControl), new PropertyMetadata(null));

        // The same path as DrilledCategory, but as clickable hops so a host header can offer the
        // ancestors instead of one dead string. Empty at the root. Written by the control.
        public IReadOnlyList<CategoryPathSegment> DrilledCategorySegments
        {
            get => (IReadOnlyList<CategoryPathSegment>)GetValue(DrilledCategorySegmentsProperty);
            set => SetValue(DrilledCategorySegmentsProperty, value);
        }

        public static readonly DependencyProperty DrilledCategoryPathProperty =
            DependencyProperty.Register(nameof(DrilledCategoryPath), typeof(string),
                typeof(AchievementDataGridControl), new PropertyMetadata(null));

        // The drilled category in storage form ("DLC::Season Pass"), for a host that has to match
        // it against achievement labels. DrilledCategory is the display form and is text for a
        // person - comparing labels against that silently matches nothing.
        public string DrilledCategoryPath
        {
            get => (string)GetValue(DrilledCategoryPathProperty);
            set => SetValue(DrilledCategoryPathProperty, value);
        }

        public static readonly DependencyProperty AchievementGridVisibleProperty =
            DependencyProperty.Register(nameof(AchievementGridVisible), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(true));

        public bool AchievementGridVisible
        {
            get => (bool)GetValue(AchievementGridVisibleProperty);
            set => SetValue(AchievementGridVisibleProperty, value);
        }

        public static readonly DependencyProperty CategoryListVisibleProperty =
            DependencyProperty.Register(nameof(CategoryListVisible), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(false));

        public bool CategoryListVisible
        {
            get => (bool)GetValue(CategoryListVisibleProperty);
            set => SetValue(CategoryListVisibleProperty, value);
        }

        public static readonly DependencyProperty DrillHeaderVisibleProperty =
            DependencyProperty.Register(nameof(DrillHeaderVisible), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(false));

        public bool DrillHeaderVisible
        {
            get => (bool)GetValue(DrillHeaderVisibleProperty);
            set => SetValue(DrillHeaderVisibleProperty, value);
        }

        public string ResolvedCategoryColumnSettingsKey
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(CategoryColumnSettingsKey))
                {
                    return CategoryColumnSettingsKey;
                }

                var baseKey = string.IsNullOrWhiteSpace(ColumnSettingsKey) ? "Default" : ColumnSettingsKey;
                return baseKey + "CategorySummaries";
            }
        }

        // Category grouping is a per-game concept, so the toggle is only offered when the current
        // source stays within a single game. Returns false only when two or more distinct games are
        // positively found; a null/empty source is treated as single-game so the toggle is never
        // hidden mid-load before data arrives.
        private bool IsCategorySourceSingleGame()
        {
            var items = ItemsSource;
            if (items == null)
            {
                return true;
            }

            string firstKey = null;
            var haveFirst = false;
            foreach (var item in items)
            {
                if (item == null)
                {
                    continue;
                }

                var key = item.PlayniteGameId?.ToString() ?? item.GameName ?? string.Empty;
                if (!haveFirst)
                {
                    firstKey = key;
                    haveFirst = true;
                }
                else if (!string.Equals(firstKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        // Mirrors the category dropdowns' auto-hide rule: the mode toggle is only meaningful when
        // there is something to group into. More than one root qualifies, and so does a single root
        // that has depth - a game whose only labels are "DLC::A" and "DLC::B" has one root but is
        // exactly the case nesting exists for.
        private bool HasMultipleCategories()
        {
            var items = CategorySummarySource ?? ItemsSource;
            if (items == null)
            {
                return false;
            }

            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (item == null)
                {
                    continue;
                }

                var path = CategoryPathHelper.NormalizePath(item.CategoryLabel);
                if (CategoryPathHelper.GetDepth(path) > 1)
                {
                    return true;
                }

                roots.Add(path);
                if (roots.Count > 1)
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsCategoryGroupingEffective()
        {
            return _isCategoryMode && HasMultipleCategories();
        }

        // Category summaries roll up the full achievement set, so the mode toggle is only offered when
        // nothing is filtered out. Inspecting the bar's own toggle items keeps this decoupled from the
        // adapter; IsChecked (not EffectiveIsVisible) is used so a toggle auto-hidden because the game
        // has no items of that kind stays "on" and does not spuriously block category mode.
        private bool CanEnterCategoryMode()
        {
            return HasMultipleCategories() && AllAchievementFiltersOn();
        }

        private bool AllAchievementFiltersOn()
        {
            return ControlBar?.Items.OfType<GridToggleFilter>().All(t => t.IsChecked) ?? true;
        }

        // Invoked by AchievementHotkeyService when the category-mode hotkey is pressed while this
        // control's hosting window is active. Drives the same GridModeToggle the control bar
        // renders, so the hotkey acts exactly when that toggle is currently shown and clickable,
        // and never otherwise. Returns true when the mode was flipped.
        public bool TryFlipCategoryModeFromHotkey()
        {
            // Mirror the toggle's own gating: this control must be on screen, the control bar
            // shown, the toggle injected into the currently attached bar, and effectively visible
            // (it auto-hides when category mode is unavailable, e.g. multi-game sources or active
            // filters).
            var toggle = _modeToggle;
            if (toggle == null ||
                !IsVisible ||
                !ShowControlBar ||
                _controlBarWithToggle == null ||
                !ReferenceEquals(_controlBarWithToggle, ControlBar) ||
                !toggle.EffectiveIsVisible)
            {
                return false;
            }

            // Same path as clicking the control-bar ToggleButton (bound to GridModeToggle.IsChecked).
            toggle.IsChecked = !toggle.IsChecked;
            return true;
        }

        // Injects the category-mode toggle and Back button into the surface-owned control bar and
        // reconciles which items are shown for the current mode (flat / category list / drill).
        private void SyncModeToggle()
        {
            // Detach from a control bar we no longer own, restoring anything we changed.
            if (_controlBarWithToggle != null && !ReferenceEquals(_controlBarWithToggle, ControlBar))
            {
                RestoreControlBar(_controlBarWithToggle);
                _controlBarWithToggle = null;
            }

            if (!EnableCategoryMode || ControlBar == null || !IsCategorySourceSingleGame())
            {
                // Disabled, detached, or a multi-game source: leave category mode and strip the
                // injected toggle/Back button from the current bar if we previously added them.
                if (_isCategoryMode)
                {
                    SetCategoryMode(false);
                }

                if (_controlBarWithToggle != null)
                {
                    RestoreControlBar(_controlBarWithToggle);
                    _controlBarWithToggle = null;
                }

                return;
            }

            if (_categorySearch == null)
            {
                _categorySearch = new GridSearchControl(
                    null,
                    null,
                    () => _categorySearchText,
                    value =>
                    {
                        _categorySearchText = value ?? string.Empty;
                        ApplyCategoryNameFilter();
                    },
                    CategoryModeText("LOCPlayAch_CategorySummaries_FilterPlaceholder", "Filter categories..."),
                    () =>
                    {
                        _categorySearchText = string.Empty;
                        ApplyCategoryNameFilter();
                    });
            }

            if (_modeToggle == null)
            {
                _modeToggle = new GridModeToggle(
                    null,
                    null,
                    CategoryModeText("LOCPlayAch_ManageAchievements_Tab_Category", "Categories"),
                    () => _isCategoryMode,
                    SetCategoryMode,
                    CategoryModeText("LOCPlayAch_CategorySummaries_ToggleToolTip", "Group by category"),
                    CanEnterCategoryMode,
                    HasMultipleCategories);
            }

            if (_expandAllButton == null)
            {
                _expandAllButton = new GridActionButton(
                    CategoryModeText("LOCPlayAch_CategorySummaries_ExpandAll", "Expand All"),
                    ExpandAllCategories,
                    CategoryModeText("LOCPlayAch_CategorySummaries_ExpandAllToolTip", "Expand all categories"));
                _collapseAllButton = new GridActionButton(
                    CategoryModeText("LOCPlayAch_CategorySummaries_CollapseAll", "Collapse All"),
                    CollapseAllCategories,
                    CategoryModeText("LOCPlayAch_CategorySummaries_CollapseAllToolTip", "Collapse all categories"));
            }

            if (!ReferenceEquals(_controlBarWithToggle, ControlBar))
            {
                // The category-label dropdown is the last category-scoped filter (Type is added
                // first); achievement-scoped dropdowns like Compare leave IsCategoryFilter false.
                // It becomes the left half of the segmented unit in flat mode.
                _connectedCategoryFilter = ControlBar.Items
                    .OfType<GridMultiSelectFilter>()
                    .LastOrDefault(filter => filter.IsCategoryFilter);
                _controlBarWithToggle = ControlBar;
            }

            // The leading zone is empty in the category list, so the pair reads beside the search
            // box there; visibility is managed by UpdateCollapseControlBarButtons.
            if (!_controlBarWithToggle.LeadingItems.Contains(_expandAllButton))
            {
                _controlBarWithToggle.LeadingItems.Add(_expandAllButton);
                _controlBarWithToggle.LeadingItems.Add(_collapseAllButton);
            }

            // Recompute the toggle's auto-hide; ApplyControlBarModeState positions Back/toggle.
            _modeToggle?.Refresh();
            ApplyStartInCategoryModeIfNeeded();
            ApplyControlBarModeState();
        }

        private void ApplyStartInCategoryModeIfNeeded()
        {
            if (_startInCategoryModeApplied ||
                !EnableCategoryMode ||
                ControlBar == null ||
                !IsCategorySourceSingleGame())
            {
                return;
            }

            _startInCategoryModeApplied = true;
            if (GetStartInCategoryMode())
            {
                SetCategoryMode(true);
            }
        }

        private bool GetStartInCategoryMode()
        {
            var persisted = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted;
            if (persisted == null)
            {
                return false;
            }

            return persisted.GridOptions.GetAchievement(ResolveStartInCategoryModeOptionsId()).StartInCategoryMode;
        }

        // The Start in Category Mode setting is edited once per view (on the parent surface's grid
        // options), while the friends grids swap ColumnSettingsKey per selection state.
        private string ResolveStartInCategoryModeOptionsId()
        {
            switch (ColumnSettingsKey)
            {
                case "FriendsOverviewSelectedFriendAchievements":
                case "FriendsOverviewSelectedGameAchievements":
                case "FriendsOverviewSelectedFriendGameAchievements":
                    return GridOptionKeys.Achievement.FriendsOverviewRecent;
                case "ViewFriendsAchievementsSelectedFriendAchievements":
                    return GridOptionKeys.Achievement.ViewFriendsAchievements;
                default:
                    return GridOptionsCatalog.ResolveAchievementId(ColumnSettingsKey);
            }
        }

        // Positions the category-mode Back and toggle controls. The toggle stays in the trailing
        // (right-side) items but changes slot with the mode: directly after the category dropdown
        // while the flat grid shows it, forming the segmented unit, and directly ahead of the
        // unlock-state toggles once grouping hides the category dropdowns, so the visible row reads
        // [Compare] [Mode] [Unlocked] [Locked] [Hidden]. Only the Back button moves to the leading
        // zone, left of the search box, while in category mode.
        private void UpdateModeControlPlacement(bool grouping)
        {
            var bar = _controlBarWithToggle;
            if (bar == null || _modeToggle == null)
            {
                return;
            }

            PositionModeToggle(bar, grouping);
        }

        // Slots the toggle for the current mode. Moves rather than removes and re-inserts, so the
        // generated ToggleButton survives the reflow instead of being rebuilt on every state pass.
        private void PositionModeToggle(GridControlBarViewModel bar, bool grouping)
        {
            // Indices are resolved against the bar without the toggle, which is what both
            // ObservableCollection.Move's target index and a fresh Insert expect.
            var others = bar.Items.Where(item => !ReferenceEquals(item, _modeToggle)).ToList();

            int target;
            if (grouping)
            {
                // The category dropdowns are hidden, so the toggle sits just ahead of the
                // unlock-state toggles, leaving Compare on its left.
                var firstToggleFilter = others.FindIndex(item => item is GridToggleFilter);
                target = firstToggleFilter >= 0 ? firstToggleFilter : others.Count;
            }
            else
            {
                var anchor = _connectedCategoryFilter == null ? -1 : others.IndexOf(_connectedCategoryFilter);
                target = anchor >= 0 ? anchor + 1 : others.Count;
            }

            var current = bar.Items.IndexOf(_modeToggle);
            if (current < 0)
            {
                bar.Items.Insert(Math.Min(target, bar.Items.Count), _modeToggle);
            }
            else if (current != target)
            {
                bar.Items.Move(current, target);
            }
        }

        // Flips both halves of the category dropdown / mode toggle segment together, adopting the
        // segmented styling only while both are actually shown so a flat edge is never left facing
        // empty space. Runs after the per-item visibility pass, which is what it keys off.
        private void SyncSegmentedUnit()
        {
            var connected =
                _modeToggle?.EffectiveIsVisible == true &&
                _connectedCategoryFilter?.EffectiveIsVisible == true;

            if (_connectedCategoryFilter != null)
            {
                _connectedCategoryFilter.ConnectedRight = connected;
            }

            if (_modeToggle != null)
            {
                _modeToggle.Connected = connected;
            }
        }

        // Restores the control bar to its plain (non-category) state.
        private void RestoreControlBar(GridControlBarViewModel bar)
        {
            if (bar == null)
            {
                return;
            }

            bar.Items.Remove(_modeToggle);
            bar.LeadingItems.Remove(_modeToggle);
            bar.LeadingItems.Remove(_expandAllButton);
            bar.LeadingItems.Remove(_collapseAllButton);
            foreach (var item in bar.Items)
            {
                if (item is GridMultiSelectFilter filter)
                {
                    filter.IsVisible = true;
                    filter.ConnectedRight = false;
                }
                else if (item is GridToggleFilter toggle)
                {
                    toggle.IsVisible = true;
                }
            }

            _connectedCategoryFilter = null;
            if (_modeToggle != null)
            {
                _modeToggle.Connected = false;
            }

            if (_originalSearch != null && ReferenceEquals(bar.Search, _categorySearch))
            {
                bar.Search = _originalSearch;
            }
        }

        // Shows/hides the injected items and swaps the search box to match the active nested grid:
        // category dropdowns are hidden while grouping is in effect, Back shows only when drilled,
        // and the search box filters category names in the list but achievements once drilled in.
        private void ApplyControlBarModeState()
        {
            var bar = _controlBarWithToggle;
            if (bar == null)
            {
                return;
            }

            var grouping = IsCategoryGroupingEffective();
            var drilled = grouping && IsDrilled;
            var list = grouping && !drilled;

            // Reslot the mode toggle and move Back into or out of the leading zone for this mode.
            UpdateModeControlPlacement(grouping);

            foreach (var item in bar.Items)
            {
                if (item is GridToggleFilter)
                {
                    // The Unlocked/Locked/Hidden toggles filter achievements, so hide them in the
                    // category list (rows are categories) but restore them flat and when drilled in.
                    item.IsVisible = !list;
                }
                else if (item is GridMultiSelectFilter filter)
                {
                    // The category dropdowns are redundant while grouping is in effect;
                    // achievement-scoped dropdowns like Compare follow the toggles instead. Both
                    // stay shown when grouping is not effective (e.g. a single-category game
                    // falling back to the flat grid).
                    filter.IsVisible = filter.IsCategoryFilter ? !grouping : !list;
                }
            }

            SyncSegmentedUnit();
            UpdateCollapseControlBarButtons();

            if (list)
            {
                if (!ReferenceEquals(bar.Search, _categorySearch))
                {
                    _originalSearch = bar.Search;
                    bar.Search = _categorySearch;
                }
            }
            else if (_originalSearch != null && ReferenceEquals(bar.Search, _categorySearch))
            {
                bar.Search = _originalSearch;
            }
        }

        private void SetCategoryMode(bool enabled)
        {
            _startInCategoryModeApplied = true;
            if (_isCategoryMode == enabled)
            {
                return;
            }

            _isCategoryMode = enabled;
            _drillPath.Clear();
            _drillScope = DrillScope.Subtree;
            SelectedCategorySummaryItems = null;
            if (CategoryListGrid != null)
            {
                CategoryListGrid.SelectedItem = null;
            }

            if (enabled)
            {
                // Clear any active achievement search so the category rollups reflect all achievements;
                // the list's search box then filters category names instead.
                if (ControlBar?.Search != null && !ReferenceEquals(ControlBar.Search, _categorySearch))
                {
                    _originalSearch = ControlBar.Search;
                    _originalSearch.Clear();
                }

                _categorySearchText = string.Empty;
                _categorySortPath = null;
                _categorySortDirection = null;
                RebuildCategorySummaries();
            }

            ApplyCategoryViewState();
            ApplyControlBarModeState();
            _modeToggle?.Refresh();

            if (!enabled)
            {
                // Leaving category mode returns to a clean, unfiltered flat grid.
                ResetAchievementFilters();
            }
        }

        private void RebuildCategorySummaries()
        {
            // Build from the unfiltered source (when provided) so achievement filters applied while
            // drilled never change the category rollups; fall back to ItemsSource otherwise.
            var items = (CategorySummarySource ?? ItemsSource)?.ToList();
            // Every node of the tree at once rather than one level per click: the list is the whole
            // map, so any category is one click away and one click back.
            //
            // Rows are titled with their leaf name and placed by the connector guide the leftmost
            // column draws (stamped in ApplyCategoryNameFilter). The name column sorts on what it
            // shows, and retitling to full paths made sorting swap the whole column's text for long
            // shared prefixes; a nested row's path stays on hover.
            using (PerfScope.Start(Logger, "CategoryGrid.BuildTree", thresholdMs: 10,
                context: $"items={items?.Count ?? 0}"))
            {
                _allCategorySummaries = items == null || items.Count == 0
                    ? null
                    : CategorySummaryBuilder.BuildTree(
                        items,
                        ResolveCategoryCompletionBadgeMode(),
                        useLeafNames: true);
            }

            // From the full tree, not the visible rows: collapsing everything must not read as the
            // tree having gone flat, or the buttons that undo it would hide themselves.
            _categoryTreeHasNesting = _allCategorySummaries != null &&
                _allCategorySummaries
                    .OfType<CategorySummaryItem>()
                    .Any(c => c.HasChildCategories);

            ApplyCategoryNameFilter();
        }

        /// <summary>
        /// Reads the global category completion badge mode. Sourced from the plugin singleton the
        /// same way <see cref="UpdateUnlockDateMode"/> is; no dependency property is needed because
        /// the decision is applied in code onto the row item rather than bound from XAML.
        /// </summary>
        private static CategoryCompletionBadgeMode ResolveCategoryCompletionBadgeMode()
        {
            return PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.CategoryCompletionBadgeMode
                ?? CategoryCompletionBadgeMode.All;
        }


        private void ApplyCategoryNameFilter()
        {
            var all = _allCategorySummaries;
            if (all == null)
            {
                _visibleCategoryRows = null;
                _visibleRowsMaster = null;
                CategorySummaries = null;
                return;
            }

            using (PerfScope.Start(Logger, "CategoryGrid.VisiblePass", thresholdMs: 10,
                context: $"rows={all.Count}"))
            {
                ApplyCategoryNameFilterCore(all);
            }
        }

        private void ApplyCategoryNameFilterCore(List<GameSummaryItem> all)
        {

            var searchActive = !string.IsNullOrWhiteSpace(_categorySearchText);
            var sortActive = _categorySortDirection.HasValue && !string.IsNullOrWhiteSpace(_categorySortPath);

            var visible = all;
            if (searchActive)
            {
                var needle = _categorySearchText.Trim();
                visible = all
                    .Where(c => (c.GameName ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
            }

            // Collapsing is suspended while a name search is active - a matching row must never be
            // hidden by a collapsed ancestor - and while a column sort has destroyed the tree. The
            // set itself is untouched either way, so clearing the search or sort restores the view.
            var collapseActive = !searchActive && !sortActive && _collapsedCategoryPaths.Count > 0;
            var removedAny = false;
            if (collapseActive)
            {
                visible = CategoryCollapseFilter.Apply(visible, _collapsedCategoryPaths, out removedAny);
            }

            // Stamped fresh on every pass: rebuilds replace the row objects, and the flag must also
            // clear whenever search or sort suspends collapsing so the glyphs revert to plain beads.
            // The stat swap rides the same pass: a collapsed row absorbs its hidden subtree's
            // numbers, an expanded one returns to its own. ApplyStats is a no-op when the row
            // already holds the right snapshot, so a single toggle costs one real swap; rows hidden
            // under a collapsed ancestor keep a stale scope harmlessly and are re-stamped here the
            // moment they reappear.
            foreach (var row in visible)
            {
                var category = row as CategorySummaryItem;
                var collapsed = collapseActive &&
                    category != null &&
                    _collapsedCategoryPaths.Contains(category.CategoryPath ?? string.Empty);
                row.IsCollapsed = collapsed;
                category?.ApplyStats(collapsed ? CategoryStatsScope.Subtree : CategoryStatsScope.Own);
            }

            // A manual column sort overlays the builder's default order (custom category order,
            // then provider order); sorting a copy keeps _allCategorySummaries as the reset target.
            if (sortActive)
            {
                var sortPath = string.Empty;
                var sortDirection = ListSortDirection.Ascending;
                var sorted = new List<GameSummaryItem>(visible);
                if (GameSummariesSortHelper.TrySortItems(
                        sorted,
                        _categorySortPath,
                        _categorySortDirection.Value,
                        ref sortPath,
                        ref sortDirection))
                {
                    visible = sorted;
                }
            }

            // Tree connectors describe the pre-order run the builder emitted, so they survive the
            // name filter above (the surviving rows keep their order) but not a manual column sort,
            // which reorders rows into something the lanes would misdescribe. A collapse that
            // removed rows can leave only roots visible, so the stamp is told nesting exists - the
            // "+" toggles that re-expand them live on the shapes it produces.
            CategoryTreeShapeBuilder.Stamp(
                visible,
                enabled: !_categorySortDirection.HasValue,
                assumeNesting: removedAny);

            PublishCategorySummaries(visible, orderedByMaster: !sortActive);
            CategoryListGrid?.SetSortIndicator(_categorySortPath, _categorySortDirection);

            if (CategoryListGrid != null)
            {
                CategoryListGrid.ShowCategoryCollapseToggles = !searchActive && !sortActive;
            }

            UpdateCollapseControlBarButtons();
        }

        /// <summary>
        /// Collapses or re-expands the clicked row's subtree. The toggle's routed event bubbles up
        /// from the tree guide in the name cell; the guide already swallowed the mouse event, so no
        /// row selection - and therefore no drill - accompanies it.
        /// </summary>
        private void OnCategoryCollapseToggleClicked(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (!((e.OriginalSource as FrameworkElement)?.DataContext is CategorySummaryItem item) ||
                string.IsNullOrEmpty(item.CategoryPath))
            {
                return;
            }

            if (!_collapsedCategoryPaths.Remove(item.CategoryPath))
            {
                _collapsedCategoryPaths.Add(item.CategoryPath);
            }

            // Re-runs the visible-row pass without rebuilding the tree. The publish is
            // incremental (same master, master order), so untouched rows keep their containers
            // and the list neither flickers nor loses its scroll position.
            ApplyCategoryNameFilter();
        }

        /// <summary>
        /// Hands the visible rows to the list. Toggling a collapse must not swap the ItemsSource -
        /// the swap discards every realized row container, which reads as the whole grid
        /// flickering - so when the rows still come from the same build in the builder's order,
        /// the published collection is edited in place and only the rows that actually appeared or
        /// disappeared raise changes. A rebuild (new row instances) or a column sort (new order)
        /// falls back to wholesale replacement, which is what those paths always did.
        /// </summary>
        private void PublishCategorySummaries(List<GameSummaryItem> visible, bool orderedByMaster)
        {
            var master = _allCategorySummaries;
            var incremental = _visibleCategoryRows != null &&
                ReferenceEquals(_visibleRowsMaster, master) &&
                _visibleRowsInMasterOrder &&
                orderedByMaster;

            if (incremental)
            {
                SyncVisibleRows(_visibleCategoryRows, visible, master);
            }
            else
            {
                if (_visibleCategoryRows == null)
                {
                    _visibleCategoryRows = new BulkObservableCollection<GameSummaryItem>();
                }

                _visibleCategoryRows.ReplaceAll(visible);
            }

            _visibleRowsMaster = master;
            _visibleRowsInMasterOrder = orderedByMaster;

            if (!ReferenceEquals(CategorySummaries, _visibleCategoryRows))
            {
                CategorySummaries = _visibleCategoryRows;
            }
        }

        /// <summary>
        /// Edits <paramref name="rows"/> in place until it equals <paramref name="target"/>. Both
        /// are subsequences of the same master run, so a two-pointer merge over the master's order
        /// suffices: an existing row that sorts before the next wanted row was collapsed away, a
        /// wanted row not at the cursor was just expanded back in.
        /// </summary>
        private static void SyncVisibleRows(
            BulkObservableCollection<GameSummaryItem> rows,
            List<GameSummaryItem> target,
            List<GameSummaryItem> master)
        {
            var order = new Dictionary<GameSummaryItem, int>(master.Count);
            for (var i = 0; i < master.Count; i++)
            {
                order[master[i]] = i;
            }

            var index = 0;
            foreach (var item in target)
            {
                if (!order.TryGetValue(item, out var targetOrder))
                {
                    // Defensive: a row from outside the master cannot be ordered, so just insert.
                    rows.Insert(index++, item);
                    continue;
                }

                while (index < rows.Count &&
                       (!order.TryGetValue(rows[index], out var existingOrder) || existingOrder < targetOrder))
                {
                    rows.RemoveAt(index);
                }

                if (index < rows.Count && ReferenceEquals(rows[index], item))
                {
                    index++;
                }
                else
                {
                    rows.Insert(index++, item);
                }
            }

            while (rows.Count > index)
            {
                rows.RemoveAt(index);
            }
        }

        /// <summary>Collapses every category that has child categories, from the full tree.</summary>
        private void CollapseAllCategories()
        {
            _collapsedCategoryPaths.Clear();
            foreach (var category in
                (_allCategorySummaries ?? Enumerable.Empty<GameSummaryItem>()).OfType<CategorySummaryItem>())
            {
                // Every parent, not just the roots: expanding a root later shows its children
                // still collapsed, so each node's state reads consistently on its own.
                if (category.HasChildCategories &&
                    !string.IsNullOrEmpty(category.CategoryPath))
                {
                    _collapsedCategoryPaths.Add(category.CategoryPath);
                }
            }

            ApplyCategoryNameFilter();
        }

        private void ExpandAllCategories()
        {
            _collapsedCategoryPaths.Clear();
            ApplyCategoryNameFilter();
        }

        /// <summary>
        /// Expand/Collapse All accompany the category list only: not the flat grid, not a drilled
        /// category, not a searched or column-sorted list (both suspend collapsing), and not a tree
        /// with nothing to collapse.
        /// </summary>
        private void UpdateCollapseControlBarButtons()
        {
            var show = IsCategoryGroupingEffective() && !IsDrilled &&
                string.IsNullOrWhiteSpace(_categorySearchText) &&
                !_categorySortDirection.HasValue &&
                _categoryTreeHasNesting;

            if (_expandAllButton != null)
            {
                _expandAllButton.IsVisible = show;
            }

            if (_collapseAllButton != null)
            {
                _collapseAllButton.IsVisible = show;
            }
        }

        private void DrillIntoCategory(CategorySummaryItem item)
        {
            if (item == null)
            {
                return;
            }

            // Remember where the list was before it is replaced, so coming back does not dump the
            // user at the top of a long tree.
            _categoryListScrollOffset = CategoryListGrid?.VerticalScrollOffset ?? 0d;

            // Summary rows carry a fully qualified path, so set the drill rather than appending:
            // the click is then idempotent however the row was reached. The scope matches the
            // numbers displayed at click time: a collapsed row absorbed its subtree, an expanded
            // one showed only its own - except an expanded pure container, whose own view would be
            // empty and useless, so it opens the subtree.
            var scope = item.IsCollapsed || item.DirectAchievementCount == 0
                ? DrillScope.Subtree
                : DrillScope.Own;
            SetDrillPath(item.CategoryLabel, scope);
            RefreshDrillState(rebuildSummaries: false);
            ApplyCategoryViewState();
            ApplyControlBarModeState();
        }

        /// <summary>
        /// Returns to the category list, which holds every node: one click in, one click back, at
        /// any depth. Ancestors remain individually reachable through the breadcrumb. Distinct from
        /// <see cref="ExitDrilledCategory"/>, which host headers use to leave category mode.
        /// </summary>
        public void PopDrilledCategory()
        {
            if (!IsDrilled)
            {
                return;
            }

            DrillToPath(null);
        }

        /// <summary>
        /// Puts the category list back where the user left it when they drilled in, so stepping in
        /// and out of a category does not lose their place in a long list.
        /// </summary>
        private void RestoreCategoryListScroll()
        {
            if (CategoryListGrid == null || _categoryListScrollOffset <= 0)
            {
                return;
            }

            var offset = _categoryListScrollOffset;

            // After the visibility flip the list has not laid out its rows yet.
            CategoryListGrid.Dispatcher.BeginInvoke(
                new Action(() => CategoryListGrid?.ScrollToVerticalOffset(offset)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Path navigation: returns to the category list rather than drilling into the hop. The
        /// list is the map and holds every node, so it is one click from there to anywhere - and
        /// coming back lands on the same scroll position the user drilled in from.
        /// </summary>
        private void NavigateToAncestorInList(int depth)
        {
            DrillToPath(null);
        }

        /// <summary>Navigates to an ancestor of the current path, or to the root when null.</summary>
        public void DrillToPath(string path)
        {
            SetDrillPath(path, ResolveDrillScope(path));
            if (CategoryListGrid != null)
            {
                CategoryListGrid.SelectedItem = null;
            }

            RefreshDrillState(rebuildSummaries: false);
            ResetAchievementFilters();
            ApplyCategoryViewState();
            ApplyControlBarModeState();
        }

        private void ApplyCategoryViewState()
        {
            var grouping = IsCategoryGroupingEffective();
            if (!grouping && IsDrilled)
            {
                ClearDrillSelection();
            }

            var drill = grouping && IsDrilled;

            // The two panes replace each other rather than sharing the area: the category list is
            // the whole tree, so once a row is picked its subtree of achievements is all there is
            // left to show, and it gets the full height.
            var hasCategoryRows = grouping && CategorySummaries != null && CategorySummaries.Any();

            CategoryListVisible = hasCategoryRows && !drill;
            AchievementGridVisible = !hasCategoryRows || drill;
            DrillHeaderVisible = drill && !HideCategorySummaryRow;
            // Display form: hosts bind this straight into a header TextBlock, and the storage
            // separator is internal - never shown to a user.
            DrilledCategory = drill ? CategoryPathHelper.ToDisplayPath(DrilledPath) : null;
            DrilledCategoryPath = drill ? DrilledPath : null;
            DrilledCategorySegments = drill
                ? CategoryPathSegment.Build(_drillPath, NavigateToAncestorInList)
                : null;

            ApplyCategoryPaneLayout();

            if (!drill && CategoryListVisible)
            {
                RestoreCategoryListScroll();
            }

            RecomputeEffectiveAchievements();
        }

        /// <summary>
        /// Gives the whole pane area to whichever grid is showing. The two are mutually exclusive,
        /// so neither is ever capped to make room for the other.
        /// </summary>
        private void ApplyCategoryPaneLayout()
        {
            if (CategoryPaneRow == null || AchievementPaneRow == null)
            {
                return;
            }

            CategoryPaneRow.Height = CategoryListVisible
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);
            AchievementPaneRow.Height = AchievementGridVisible
                ? new GridLength(1, GridUnitType.Star)
                : new GridLength(0);

            if (CategoryListGrid != null)
            {
                CategoryListGrid.MaxHeight = double.PositiveInfinity;
            }
        }

        private void RecomputeEffectiveAchievements()
        {
            if (IsCategoryGroupingEffective() && IsDrilled)
            {
                // The scope of the row that was clicked, so the grid always agrees with the numbers
                // that led here: a collapsed row's numbers absorbed its whole subtree and the drill
                // opens it; an expanded row's numbers were its own and the drill opens those.
                var drilled = DrilledPath;
                var selfOnly = _drillScope == DrillScope.Own;
                var filtered = (ItemsSource ?? Enumerable.Empty<AchievementDisplayItem>())
                    .Where(i => i != null && (selfOnly
                        ? CategoryPathHelper.IsSame(i.CategoryLabel, drilled)
                        : CategoryPathHelper.IsSelfOrDescendantOf(i.CategoryLabel, drilled)))
                    .ToList();

                // Mutate a stable collection in place rather than reassigning a new list, so the grid
                // keeps its view (and column sort) instead of rebuilding it on every refresh.
                if (_drillItems == null)
                {
                    _drillItems = new BulkObservableCollection<AchievementDisplayItem>();
                }

                _drillItems.ReplaceAll(filtered);
                if (!ReferenceEquals(EffectiveAchievements, _drillItems))
                {
                    EffectiveAchievements = _drillItems;
                }
            }
            else if (!ReferenceEquals(EffectiveAchievements, ItemsSource))
            {
                EffectiveAchievements = ItemsSource;
            }
        }

        private void ObserveItemsSourceCollection()
        {
            if (_observedItemsSource != null)
            {
                _observedItemsSource.CollectionChanged -= OnItemsSourceCollectionChanged;
                _observedItemsSource = null;
            }

            if (ItemsSource is INotifyCollectionChanged incc)
            {
                _observedItemsSource = incc;
                incc.CollectionChanged += OnItemsSourceCollectionChanged;
            }
        }

        private void OnItemsSourceCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            RequestItemsSourceContentRefresh();
        }

        // Hosts feed CategorySummarySource from a stable collection mutated in place (ReplaceAll),
        // so the dependency-property callback alone never sees updates. Without observing the
        // collection itself, category rollups keep the previous selection's rows after a friend
        // or game switch, showing the wrong game's categories and drill contents.
        private void ObserveCategorySummarySourceCollection()
        {
            if (_observedCategorySummarySource != null)
            {
                _observedCategorySummarySource.CollectionChanged -= OnCategorySummarySourceCollectionChanged;
                _observedCategorySummarySource = null;
            }

            if (CategorySummarySource is INotifyCollectionChanged incc)
            {
                _observedCategorySummarySource = incc;
                incc.CollectionChanged += OnCategorySummarySourceCollectionChanged;
            }
        }

        private void OnCategorySummarySourceCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (_isCategoryMode)
            {
                RequestItemsSourceContentRefresh();
            }
        }

        // A game or friend switch replaces ItemsSource and CategorySummarySource back to back
        // (both mutated in place, so each raises its own Reset), and running the full category
        // pipeline per Reset meant two tree builds and two wholesale republishes per click. One
        // deferred pass at DataBind priority runs after every Reset in the frame has landed and
        // still ahead of the render pass, so nothing stale ever paints.
        private bool _itemsSourceContentRefreshQueued;

        private void RequestItemsSourceContentRefresh()
        {
            if (_itemsSourceContentRefreshQueued)
            {
                return;
            }

            _itemsSourceContentRefreshQueued = true;
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    _itemsSourceContentRefreshQueued = false;
                    OnItemsSourceContentChanged();
                }),
                System.Windows.Threading.DispatcherPriority.DataBind);
        }

        private void OnItemsSourceContentChanged()
        {
            using (PerfScope.Start(Logger, "CategoryGrid.SourceContentChanged", thresholdMs: 25,
                context: $"categoryMode={_isCategoryMode} items={ItemsSource?.Count() ?? 0}"))
            {
                RecomputeHasAnyFavorites();

                // Re-evaluate toggle availability first: a game switch or a newly loaded multi-game feed
                // may add or remove the category toggle (and drop us out of category mode) before the
                // rest of this method reads _isCategoryMode.
                SyncModeToggle();

                if (!_isCategoryMode)
                {
                    RecomputeEffectiveAchievements();
                    return;
                }

                // Reconcile the drill before rebuilding: the summaries are the children of wherever the
                // drill now points, so a stale path would build the wrong level.
                if (!HasMultipleCategories())
                {
                    ClearDrillSelection();
                }
                else if (IsDrilled)
                {
                    ReconcileDrillPath();
                }

                RefreshDrillState();
                ApplyCategoryViewState();
                ApplyControlBarModeState();
            }
        }

        /// <summary>
        /// Truncates the drill to the deepest level that still exists. A delta that removes one
        /// leaf should step up a level, not eject the user all the way to the root.
        /// </summary>
        private void ReconcileDrillPath()
        {
            var labels = (CategorySummarySource ?? ItemsSource ?? Enumerable.Empty<AchievementDisplayItem>())
                .Where(i => i != null)
                .Select(i => i.CategoryLabel)
                .ToList();

            while (IsDrilled)
            {
                var candidate = DrilledPath;
                if (labels.Any(label => CategoryPathHelper.IsSelfOrDescendantOf(label, candidate)))
                {
                    return;
                }

                _drillPath.RemoveAt(_drillPath.Count - 1);
                // The scope belonged to the level that just fell away, not to the ancestor being
                // stepped up to; the ancestor's own scope resolves below once the path settles.
                _drillScope = DrillScope.Subtree;
            }

            if (IsDrilled)
            {
                _drillScope = ResolveDrillScope(DrilledPath);
                return;
            }

            ClearDrillSelection();
        }

        /// <summary>
        /// The scope a drill on <paramref name="path"/> would open, resolved the same way a click
        /// resolves it: the subtree when the row is collapsed (never while search or sort suspends
        /// collapsing and the rows display their own numbers), and the node's own achievements
        /// otherwise - except a pure container, whose own view would be empty, so it opens the
        /// subtree. Used where a drill is entered without a clicked row: breadcrumb hops and
        /// post-delta reconciliation.
        /// </summary>
        private DrillScope ResolveDrillScope(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return DrillScope.Subtree;
            }

            var searchActive = !string.IsNullOrWhiteSpace(_categorySearchText);
            var collapsed = !searchActive && !_categorySortDirection.HasValue &&
                _collapsedCategoryPaths.Contains(path);

            var row = _allCategorySummaries?
                .OfType<CategorySummaryItem>()
                .FirstOrDefault(c => CategoryPathHelper.IsSame(c.CategoryPath, path));

            return collapsed || row == null || row.DirectAchievementCount == 0
                ? DrillScope.Subtree
                : DrillScope.Own;
        }

        /// <summary>
        /// Rebuilds the category rows (when the source content changed) and the header row
        /// describing the row the drill was entered through, so the header always restates the
        /// numbers that were clicked - the subtree rollup when the row was collapsed, the direct
        /// achievements when it was expanded.
        ///
        /// Drill navigation passes <paramref name="rebuildSummaries"/> false: the click changes
        /// nothing about the achievements, so <see cref="_allCategorySummaries"/> is still
        /// current, and a rebuild would republish all-new row instances - throwing away the
        /// list's realized containers and the incremental-publish master for no data change.
        /// </summary>
        private void RefreshDrillState(bool rebuildSummaries = true)
        {
            if (rebuildSummaries || _allCategorySummaries == null)
            {
                RebuildCategorySummaries();
            }

            var drilled = DrilledPath;
            if (drilled == null)
            {
                SelectedCategorySummaryItems = null;
                return;
            }

            // Fresh rows rather than the list's own instances: those carry stamped tree
            // connectors, and the header row must not draw an indent. Leaf names, because the
            // header path above the grid already carries the ancestry. Built from just the
            // drilled subtree's achievements rather than the whole source - the header row's
            // counts, art, and type all resolve from its own subtree, so the scoped build
            // produces the same row without a second whole-tree pass.
            var items = (CategorySummarySource ?? ItemsSource)?
                .Where(i => i != null && CategoryPathHelper.IsSelfOrDescendantOf(i.CategoryLabel, drilled))
                .ToList();
            CategorySummaryItem match = null;
            if (items != null && items.Count > 0)
            {
                match = CategorySummaryBuilder
                    .BuildTree(items, ResolveCategoryCompletionBadgeMode(), useLeafNames: true)
                    .OfType<CategorySummaryItem>()
                    .FirstOrDefault(c => CategoryPathHelper.IsSame(c.CategoryPath, drilled));

                if (match != null)
                {
                    // A delta can dissolve an own-scoped drill - the last direct achievement
                    // recategorized away - which would leave the grid empty under a live header.
                    // The subtree scope always has something to show here (items was non-empty).
                    if (_drillScope == DrillScope.Own && match.DirectAchievementCount == 0)
                    {
                        _drillScope = DrillScope.Subtree;
                    }

                    // The header restates the numbers that were clicked: the subtree rollup for a
                    // subtree drill, the row's own achievements for an own drill. The fresh row
                    // left the builder holding its own reading.
                    match.ApplyStats(_drillScope == DrillScope.Subtree
                        ? CategoryStatsScope.Subtree
                        : CategoryStatsScope.Own);

                    // Badge permission is positional in the configured order of the whole list
                    // (First allows only the first category overall), which the scoped build
                    // cannot know - its drilled node always comes out first. The list's own row
                    // carries the stamped answer.
                    var listRow = _allCategorySummaries?
                        .OfType<CategorySummaryItem>()
                        .FirstOrDefault(c => CategoryPathHelper.IsSame(c.CategoryPath, drilled));
                    if (listRow != null)
                    {
                        match.AllowCompletionBadge = listRow.AllowCompletionBadge;
                    }
                }
            }

            SelectedCategorySummaryItems = match == null ? null : new[] { (GameSummaryItem)match };
        }

        private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // The list stays live at depth, so this must not bail out merely because we are drilled.
            if (!IsCategoryGroupingEffective() || !CategoryListVisible)
            {
                return;
            }

            var selected = e?.AddedItems != null && e.AddedItems.Count > 0
                ? e.AddedItems[0] as CategorySummaryItem
                : CategoryListGrid?.SelectedItem as CategorySummaryItem;
            if (selected != null)
            {
                DrillIntoCategory(selected);
            }
        }

        // Category rows default to the builder's source order (custom category order, then
        // provider order); header clicks cycle ascending -> descending -> back to that default.
        private void CategoryList_Sorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true;
            var sortAction = GameSummariesSortHelper.ResolveSourceOrderedGridSortAction(
                e.Column?.SortMemberPath,
                _categorySortPath,
                _categorySortDirection);
            if (sortAction.Kind == GameSummariesGridSortActionKind.None)
            {
                return;
            }

            if (sortAction.Kind == GameSummariesGridSortActionKind.ResetToDefault)
            {
                _categorySortPath = null;
                _categorySortDirection = null;
            }
            else
            {
                _categorySortPath = sortAction.SortMemberPath;
                _categorySortDirection = sortAction.Direction;
            }

            // Rebuild rather than re-filter: a sorted list no longer reads as a tree, so the rows
            // have to retitle themselves with their full paths.
            RebuildCategorySummaries();
        }

        private void CategoryList_RowPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (TryResolveCategorySummaryRow(e, out var row))
            {
                _pendingCategoryRightClickRow = row;
                e.Handled = true;
            }
        }

        private void CategoryList_RowPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (TryResolveCategorySummaryRow(e, out var row))
            {
                var targetRow = _pendingCategoryRightClickRow ?? row;
                _pendingCategoryRightClickRow = null;
                OpenCategorySummaryContextMenu(targetRow);
                e.Handled = true;
            }
        }

        private static bool TryResolveCategorySummaryRow(MouseButtonEventArgs e, out DataGridRow row)
        {
            row = e?.Source as DataGridRow
                  ?? VisualTreeHelpers.FindVisualParent<DataGridRow>(e?.OriginalSource as DependencyObject);
            return row?.DataContext is CategorySummaryItem;
        }

        private bool OpenCategorySummaryContextMenu(DataGridRow row)
        {
            if (!(row?.DataContext is CategorySummaryItem summary) ||
                !TryResolveCategoryGameId(summary, out var gameId))
            {
                return false;
            }

            var menu = new ContextMenu();
            menu.Items.Add(GameRowContextMenuBuilder.CreateMenuItem(
                this,
                "LOCPlayAch_ManageAchievements_Category_Context_ManageCategories",
                () => PlayniteAchievementsPlugin.Instance?.OpenManageAchievementsView(
                    gameId,
                    ManageAchievementsTab.Category,
                    selectManageCategoriesSubTab: true)));
            ContextMenuStyleHelper.ApplyAchievementContextMenuStyle(this, menu);
            row.ContextMenu = menu;
            menu.PlacementTarget = row;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
            return true;
        }

        private bool TryResolveCategoryGameId(CategorySummaryItem summary, out Guid gameId)
        {
            if (summary?.PlayniteGameId.HasValue == true &&
                summary.PlayniteGameId.Value != Guid.Empty)
            {
                gameId = summary.PlayniteGameId.Value;
                return true;
            }

            var fallback = (ItemsSource ?? Enumerable.Empty<AchievementDisplayItem>())
                .Select(item => item?.PlayniteGameId)
                .Where(id => id.HasValue && id.Value != Guid.Empty)
                .Select(id => id.Value)
                .Distinct()
                .Take(2)
                .ToList();
            if (fallback.Count == 1)
            {
                gameId = fallback[0];
                return true;
            }

            gameId = Guid.Empty;
            return false;
        }

        private void CategoryDrillHeader_RowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsCategoryGroupingEffective() && IsDrilled)
            {
                CategoryBackToList();
                if (e != null)
                {
                    e.Handled = true;
                }
            }
        }

        private void SortDrilledAchievements(DataGridSortingEventArgs e)
        {
            if (e?.Column == null || string.IsNullOrWhiteSpace(e.Column.SortMemberPath))
            {
                return;
            }

            var sortDirection = DataGridSortingHelper.HandleSorting(AchievementsDataGrid, e, AchievementsDataGrid);
            if (!sortDirection.HasValue)
            {
                // Cleared sort: restore the category's source order.
                RecomputeEffectiveAchievements();
                return;
            }

            if (_drillItems == null)
            {
                return;
            }

            var items = _drillItems.ToList();
            if (items.Count == 0)
            {
                return;
            }

            var currentSortPath = string.Empty;
            ListSortDirection? currentSortDirection = null;
            if (!AchievementSortHelper.TrySortItems(
                    items,
                    e.Column.SortMemberPath,
                    sortDirection.Value,
                    SortScope,
                    ref currentSortPath,
                    ref currentSortDirection))
            {
                return;
            }

            AchievementSortHelper.ApplyGoalsFirst(items);
            _drillItems.ReplaceAll(items);
        }

        // Public entry point for a host header to navigate back to the category summary list; the
        // path segments above the grid are the only way back.
        public void ExitDrilledCategory() => CategoryBackToList();

        /// <summary>
        /// Selects and scrolls to the given achievement. When category grouping is active
        /// (e.g. the surface starts in category mode), first drills into the achievement's
        /// category so the row is actually visible and highlighted.
        /// </summary>
        public void FocusAchievementItem(AchievementDisplayItem item)
        {
            if (item == null)
            {
                return;
            }

            if (IsCategoryGroupingEffective())
            {
                // Drill straight to the achievement's own path rather than matching a summary row:
                // at depth the intermediate levels are not materialized in the current level's rows.
                var label = CategoryPathHelper.NormalizePath(item.CategoryLabel);
                if (!CategoryPathHelper.IsSame(DrilledPath, label))
                {
                    DrillToPath(label);
                }
            }

            var grid = AchievementsDataGrid;
            if (grid == null)
            {
                return;
            }

            grid.SelectedItem = item;
            grid.UpdateLayout();
            grid.ScrollIntoView(item);
            // The immediate scroll no-ops when the hosting window has not been measured yet
            // (focus applied while the window is still opening) or when a category drill just
            // replaced the item collection; re-assert once layout has settled.
            grid.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (ReferenceEquals(grid.SelectedItem, item))
                    {
                        grid.ScrollIntoView(item);
                    }
                }),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        // Back always lands on the category list, whatever the depth: the list holds every node, so
        // unwinding a path level by level would be steps through views the user never chose.
        // ResetAchievementFilters runs inside PopDrilledCategory, so the next drill starts clean.
        private void CategoryBackToList()
        {
            PopDrilledCategory();
        }

        // Resets the Unlocked/Locked/Hidden toggles to their default (all on). Each IsChecked setter
        // routes back through the control bar's adapter, so the achievement list re-filters.
        private void ResetAchievementFilters()
        {
            var bar = ControlBar;
            if (bar == null)
            {
                return;
            }

            foreach (var toggle in bar.Items.OfType<GridToggleFilter>())
            {
                toggle.IsChecked = true;
            }
        }

        private static string CategoryModeText(string key, string fallback)
        {
            var value = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        /// <summary>
        /// Occurs when a column header is clicked for sorting.
        /// Subscribe to handle sorting externally when UseExternalSorting is true.
        /// </summary>
        public event EventHandler<DataGridSortingEventArgs> Sorting;

        /// <summary>
        /// Routed event raised when a row receives a right mouse button down.
        /// </summary>
        public static readonly RoutedEvent RowPreviewMouseRightButtonDownEvent =
            EventManager.RegisterRoutedEvent("RowPreviewMouseRightButtonDown", RoutingStrategy.Bubble,
                typeof(MouseButtonEventHandler), typeof(AchievementDataGridControl));

        /// <summary>
        /// Occurs when the right mouse button is pressed on a row.
        /// </summary>
        public event MouseButtonEventHandler RowPreviewMouseRightButtonDown
        {
            add => AddHandler(RowPreviewMouseRightButtonDownEvent, value);
            remove => RemoveHandler(RowPreviewMouseRightButtonDownEvent, value);
        }

        /// <summary>
        /// Routed event raised when a row receives a right mouse button up.
        /// </summary>
        public static readonly RoutedEvent RowPreviewMouseRightButtonUpEvent =
            EventManager.RegisterRoutedEvent("RowPreviewMouseRightButtonUp", RoutingStrategy.Bubble,
                typeof(MouseButtonEventHandler), typeof(AchievementDataGridControl));

        /// <summary>
        /// Occurs when the right mouse button is released on a row.
        /// </summary>
        public event MouseButtonEventHandler RowPreviewMouseRightButtonUp
        {
            add => AddHandler(RowPreviewMouseRightButtonUpEvent, value);
            remove => RemoveHandler(RowPreviewMouseRightButtonUpEvent, value);
        }

        public static readonly RoutedEvent RowPreviewMouseLeftButtonDownEvent =
            EventManager.RegisterRoutedEvent("RowPreviewMouseLeftButtonDown", RoutingStrategy.Bubble,
                typeof(MouseButtonEventHandler), typeof(AchievementDataGridControl));

        public event MouseButtonEventHandler RowPreviewMouseLeftButtonDown
        {
            add => AddHandler(RowPreviewMouseLeftButtonDownEvent, value);
            remove => RemoveHandler(RowPreviewMouseLeftButtonDownEvent, value);
        }

        public AchievementDataGridControl()
        {
            InitializeComponent();
            RarityAppearanceHelper.BindAnimateRarityGlows(this, AnimateRarityGlowsProperty);
            RarityAppearanceHelper.BindSoftGlowTiers(this, SoftGlowTiersProperty);
            RarityAppearanceHelper.BindRayGlowTiers(this, RayGlowTiersProperty);
            RarityAppearanceHelper.BindShowHardcoreBorder(this, ShowHardcoreBorderProperty);
            DataContextChanged += OnDataContextChanged;
            Unloaded += OnUnloaded;
            AddHandler(
                CategoryTreeGuide.CollapseToggleClickedEvent,
                new RoutedEventHandler(OnCategoryCollapseToggleClicked));
            UpdateColumnHeadersVisibility();
        }

        private static void OnColumnVisibilityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AchievementDataGridControl control)
            {
                control.UpdateColumnPersistenceContextOverrides();
                control.UpdateColumnVisibility();
                control._columnPersistence?.Refresh();
            }
        }

        private static void OnColumnSettingsKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AchievementDataGridControl control)
            {
                // A new key means a new surface (e.g. the friends grid switching from the recent
                // feed to a selected friend+game pair), so re-arm the one-shot start-in-category
                // application for the incoming surface.
                control._startInCategoryModeApplied = false;
                control.UpdateUnlockDateMode();
                control.ReattachColumnPersistence();
                control.SyncModeToggle();
            }
        }

        private void ReattachColumnPersistence()
        {
            if (_columnPersistence == null)
            {
                return;
            }

            _columnPersistence.Dispose();
            _columnPersistence = null;
            AttachColumnPersistence();
            UpdateColumnVisibility();
            UpdateColumnHeadersVisibility();
            DataGridAlignmentBehavior.Refresh(AchievementsDataGrid);
        }

        private void UpdateColumnVisibility()
        {
            if (AchievementsDataGrid == null || AchievementsDataGrid.Columns == null)
            {
                return;
            }

            // Update Status column visibility - force collapsed when HideStatusColumn is true
            var statusColumn = AchievementsDataGrid.Columns.FirstOrDefault(c => c.GetValue(FrameworkElement.NameProperty) as string == "StatusColumn") as DataGridTemplateColumn;
            if (statusColumn != null)
            {
                SetResizableColumnVisibility(
                    statusColumn,
                    !HideStatusColumn,
                    DefaultStatusColumnWidth,
                    MinimumStatusColumnWidth,
                    MaximumStatusColumnWidth);
            }

            // Update Game column visibility - force collapsed when ShowGameColumn is false
            var gameColumn = AchievementsDataGrid.Columns.FirstOrDefault(c => c.GetValue(FrameworkElement.NameProperty) as string == "GameColumn") as DataGridTemplateColumn;
            if (gameColumn != null)
            {
                SetResizableColumnVisibility(
                    gameColumn,
                    ShowGameColumn,
                    DefaultGameImageColumnWidth,
                    MinimumGameImageColumnWidth,
                    MaximumGameImageColumnWidth);
            }

            var friendAvatarColumn = AchievementsDataGrid.Columns.FirstOrDefault(c => c.GetValue(FrameworkElement.NameProperty) as string == "FriendAvatarColumn") as DataGridTemplateColumn;
            if (friendAvatarColumn != null)
            {
                SetResizableColumnVisibility(
                    friendAvatarColumn,
                    ShowFriendColumn,
                    DefaultFriendAvatarColumnWidth,
                    MinimumFriendAvatarColumnWidth,
                    MaximumFriendAvatarColumnWidth);
            }

            var friendColumn = AchievementsDataGrid.Columns.FirstOrDefault(c => c.GetValue(FrameworkElement.NameProperty) as string == "FriendColumn") as DataGridTemplateColumn;
            if (friendColumn != null)
            {
                SetResizableColumnVisibility(
                    friendColumn,
                    ShowFriendColumn,
                    DefaultFriendColumnWidth,
                    MinimumFriendColumnWidth,
                    MaximumFriendColumnWidth);
            }
        }

        private static void OnShowColumnHeadersChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AchievementDataGridControl control)
            {
                control.UpdateColumnHeadersVisibility();
            }
        }

        private static void OnDelayInitialRenderUntilNormalizedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AchievementDataGridControl control && control._columnPersistence != null)
            {
                control._columnPersistence.DelayInitialRenderUntilNormalized = e.NewValue is bool value && value;
            }
        }

        private void UpdateColumnHeadersVisibility()
        {
            if (AchievementsDataGrid != null)
            {
                AchievementsDataGrid.HeadersVisibility = ShowColumnHeaders
                    ? DataGridHeadersVisibility.Column
                    : DataGridHeadersVisibility.None;
            }
        }

        private static void SetResizableColumnVisibility(
            DataGridColumn column,
            bool isVisible,
            double defaultWidth,
            double minWidth,
            double maxWidth)
        {
            if (column == null)
            {
                return;
            }

            if (isVisible)
            {
                column.Visibility = Visibility.Visible;
                column.MinWidth = minWidth;
                column.MaxWidth = maxWidth;
                if (column.Width.IsAbsolute && column.Width.Value <= 0)
                {
                    column.Width = new DataGridLength(defaultWidth, DataGridLengthUnitType.Pixel);
                }

                return;
            }

            column.Visibility = Visibility.Collapsed;
            column.MinWidth = 0;
            column.MaxWidth = 0;
            column.Width = new DataGridLength(0, DataGridLengthUnitType.Pixel);
        }

        private void UpdateColumnPersistenceContextOverrides()
        {
            if (_columnPersistence == null)
            {
                return;
            }

            SetForcedColumnCollapsed(_columnPersistence, StatusColumnKey, HideStatusColumn);
            SetForcedColumnCollapsed(_columnPersistence, GameColumnKey, !ShowGameColumn);
            SetForcedColumnCollapsed(_columnPersistence, FriendAvatarColumnKey, !ShowFriendColumn);
            SetForcedColumnCollapsed(_columnPersistence, FriendColumnKey, !ShowFriendColumn);
        }

        private static void SetForcedColumnCollapsed(
            DataGridColumnLayoutService layout,
            string columnKey,
            bool forceCollapsed)
        {
            if (layout == null || string.IsNullOrWhiteSpace(columnKey))
            {
                return;
            }

            if (forceCollapsed)
            {
                layout.ForcedCollapsedKeys.Add(columnKey);
                layout.ExcludedVisibilityKeys.Add(columnKey);
                return;
            }

            layout.ForcedCollapsedKeys.Remove(columnKey);
            layout.ExcludedVisibilityKeys.Remove(columnKey);
        }

        private static void OnRowSizingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AchievementDataGridControl control)
            {
                control.UpdateRealizedRowHeights();
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateColumnVisibility();
            UpdateColumnHeadersVisibility();
            UpdateRealizedRowHeights();
            UpdateUnlockDateMode();
            SyncModeToggle();
            ApplyCategoryViewState();

            if (_isAttached)
            {
                return;
            }

            // Tracks the current Persisted instance: CancelEdit replaces it, and a direct
            // subscription would leave this grid on the orphan, keeping the reverted
            // unlock-date mode for the rest of the session.
            var settings = PlayniteAchievementsPlugin.Instance?.Settings;
            if (settings?.Persisted != null)
            {
                _persistedSubscription = new PersistedSettingsSubscription(
                    settings,
                    OnPersistedSettingsChanged,
                    () => OnPersistedSettingsChanged(this, new PropertyChangedEventArgs(null)));
            }

            AttachColumnPersistence();
            // Column visibility is now handled by ForcedCollapsedKeys during Attach()
            _isAttached = true;
        }

        private void OnPersistedSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName == nameof(PersistedSettings.UnlockDateDisplayMode))
            {
                UpdateUnlockDateMode();
            }

            // Category rows repaint without reopening the window. Grids not currently in category
            // mode pick new modes up from the rebuild that entering category mode already does.
            if (_isCategoryMode)
            {
                if (string.IsNullOrEmpty(e.PropertyName))
                {
                    // A blank name means the whole Persisted instance changed (CancelEdit swaps
                    // it), so the drill re-resolves against freshly built rows.
                    RefreshDrillState();
                    ApplyCategoryViewState();
                }
                else if (e.PropertyName == nameof(PersistedSettings.CategoryCompletionBadgeMode))
                {
                    // Rebuilding re-stamps AllowCompletionBadge and reassigns CategorySummaries.
                    RebuildCategorySummaries();
                }
            }
        }

        private void UpdateUnlockDateMode()
        {
            var persisted = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            UnlockDateMode = persisted.UnlockDateDisplayMode;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            UpdateRealizedRowHeights();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            // Restore the control bar so a bar reused across navigations does not keep our injected
            // items or hidden dropdowns; everything is re-applied on the next load.
            if (_controlBarWithToggle != null)
            {
                RestoreControlBar(_controlBarWithToggle);
                _controlBarWithToggle = null;
            }
        }

        private void AchievementsDataGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            ApplyFixedRowHeight(e.Row);
        }

        private void UpdateRealizedRowHeights()
        {
            if (AchievementsDataGrid == null)
            {
                return;
            }

            foreach (var item in AchievementsDataGrid.Items)
            {
                if (AchievementsDataGrid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row)
                {
                    ApplyFixedRowHeight(row);
                }
            }
        }

        private void ApplyFixedRowHeight(DataGridRow row)
        {
            if (row == null)
            {
                return;
            }

            var fixedHeight = ResolveFixedRowHeight();
            if (fixedHeight.HasValue)
            {
                row.Height = fixedHeight.Value;
                row.MinHeight = fixedHeight.Value;
                return;
            }

            row.ClearValue(FrameworkElement.HeightProperty);
            row.ClearValue(FrameworkElement.MinHeightProperty);
        }

        private double? ResolveFixedRowHeight()
        {
            var height = FixedRowHeight;
            if (!height.HasValue ||
                double.IsNaN(height.Value) ||
                double.IsInfinity(height.Value) ||
                height.Value <= 0)
            {
                return null;
            }

            return Math.Max(PersistedSettings.MinimumGridRowHeight, height.Value);
        }

        private void AttachColumnPersistence()
        {
            var settings = PlayniteAchievementsPlugin.Instance?.Settings;
            if (settings == null)
            {
                return;
            }

            DataGridAlignmentBehavior.SetColumnCellAlignmentOverridesProvider(
                AchievementsDataGrid,
                () => GetAlignmentsByKey(settings));
            DataGridAlignmentBehavior.SetColumnCellVerticalAlignmentOverridesProvider(
                AchievementsDataGrid,
                () => GetCellVerticalAlignmentsByKey(settings));
            DataGridAlignmentBehavior.SetColumnHeaderHorizontalAlignmentOverridesProvider(
                AchievementsDataGrid,
                () => GetHeaderAlignmentsByKey(settings));

            _columnPersistence = new DataGridColumnLayoutService(
                AchievementsDataGrid,
                Logger,
                () => GetMergedWidths(settings),
                map =>
                {
                    if (AllowLayoutPersistence)
                    {
                        SetWidthsByKey(settings, map);
                    }
                },
                () => GetVisibilityMap(settings),
                map =>
                {
                    if (AllowLayoutPersistence)
                    {
                        SetVisibilityByKey(settings, map);
                    }
                },
                () =>
                {
                    if (AllowLayoutPersistence)
                    {
                        SavePluginSettings(settings);
                    }
                },
                defaultWidthSeeds: DefaultImageColumnWidthSeeds,
                getOrder: () => GetOrderMap(settings),
                setOrder: map =>
                {
                    if (AllowLayoutPersistence)
                    {
                        SetOrderByKey(settings, map);
                    }
                },
                getCellAlignments: () => GetAlignmentMap(settings),
                setCellAlignments: map =>
                {
                    if (AllowLayoutPersistence)
                    {
                        SetAlignmentsByKey(settings, map);
                    }
                },
                getDefaultCellAlignment: () => settings.Persisted?.GridCellAlignment ?? GridAlignment.Left,
                getCellVerticalAlignments: () => GetCellVerticalAlignmentMap(settings),
                setCellVerticalAlignments: map =>
                {
                    if (AllowLayoutPersistence)
                    {
                        SetCellVerticalAlignmentsByKey(settings, map);
                    }
                },
                getDefaultCellVerticalAlignment: () => settings.Persisted?.GridCellVerticalAlignment ?? GridVerticalAlignment.Center,
                getHeaderHorizontalAlignments: () => GetHeaderAlignmentMap(settings),
                setHeaderHorizontalAlignments: map =>
                {
                    if (AllowLayoutPersistence)
                    {
                        SetHeaderAlignmentsByKey(settings, map);
                    }
                },
                getDefaultHeaderHorizontalAlignment: () => settings.Persisted?.GridColumnHeaderAlignment ?? GridAlignment.Center,
                applyCellAlignments: () => DataGridAlignmentBehavior.Refresh(AchievementsDataGrid),
                isRuntimeDefaultWidth: IsLegacyImageColumnRuntimeDefaultWidth);
            _columnPersistence.DelayInitialRenderUntilNormalized = DelayInitialRenderUntilNormalized;

            UpdateColumnPersistenceContextOverrides();
            UpdateColumnVisibility();

            _columnPersistence.Attach();
        }

        private Dictionary<string, double> GetMergedWidths(PlayniteAchievementsSettings settings)
        {
            var merged = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            // Use key-specific widths if available
            var keyMap = GetWidthsByKey(settings);
            if (keyMap != null)
            {
                foreach (var pair in keyMap)
                {
                    if (IsValidWidth(pair.Value))
                    {
                        merged[pair.Key] = pair.Value;
                    }
                }
            }

            if (ShouldUseSingleGameWidthFallback())
            {
                var singleGameMap = settings?.Persisted?.GridOptions
                    ?.GetAchievement(GridOptionKeys.Achievement.SingleGame)
                    ?.Columns
                    ?.Widths;
                if (singleGameMap != null)
                {
                    foreach (var pair in singleGameMap)
                    {
                        if (!merged.ContainsKey(pair.Key) && IsValidWidth(pair.Value))
                        {
                            merged[pair.Key] = pair.Value;
                        }
                    }
                }
            }

            return merged;
        }

        private bool ShouldUseSingleGameWidthFallback()
        {
            switch (ColumnSettingsKey)
            {
                case "OverviewRecentAchievements":
                case "FriendsOverviewRecentAchievements":
                case "FriendsOverviewSelectedFriendAchievements":
                case "FriendsOverviewSelectedGameAchievements":
                case "FriendsOverviewSelectedFriendGameAchievements":
                case "Overview":
                case "OverviewSelectedGameAchievements":
                case "OverviewGame":
                case "StartPageAchievements":
                case "StartPageFriendAchievements":
                    return false;
                default:
                    return true;
            }
        }

        private Dictionary<string, bool> GetVisibilityMap(PlayniteAchievementsSettings settings)
        {
            var map = GetVisibilityByKey(settings);
            map = ApplyContextDefaultVisibility(settings, map);
            if (AllowLayoutPersistence || map == null)
            {
                return map;
            }

            return new Dictionary<string, bool>(map, StringComparer.OrdinalIgnoreCase);
        }

        private Dictionary<string, bool> ApplyContextDefaultVisibility(
            PlayniteAchievementsSettings settings,
            Dictionary<string, bool> map)
        {
            var defaults = GetDefaultVisibility(ColumnSettingsKey);
            return defaults != null
                ? ApplyVisibilityDefaults(settings, map, defaults)
                : map;
        }

        private static IReadOnlyDictionary<string, bool> GetDefaultVisibility(string columnSettingsKey)
        {
            if (!string.IsNullOrWhiteSpace(columnSettingsKey) &&
                DefaultVisibilityByColumnSettingsKey.TryGetValue(columnSettingsKey, out var defaults))
            {
                return defaults;
            }

            return DefaultVisibilityByColumnSettingsKey.TryGetValue("Default", out var fallback)
                ? fallback
                : null;
        }

        private Dictionary<string, bool> ApplyVisibilityDefaults(
            PlayniteAchievementsSettings settings,
            Dictionary<string, bool> map,
            IReadOnlyDictionary<string, bool> defaults)
        {
            if (defaults == null || defaults.Count == 0)
            {
                return map;
            }

            if (map == null)
            {
                map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                if (AllowLayoutPersistence)
                {
                    SetVisibilityByKey(settings, map);
                }
            }

            foreach (var pair in defaults)
            {
                if (!map.ContainsKey(pair.Key))
                {
                    map[pair.Key] = pair.Value;
                }
            }

            return map;
        }

        private Dictionary<string, int> GetOrderMap(PlayniteAchievementsSettings settings)
        {
            var map = GetOrderByKey(settings);
            map = ApplyContextDefaultOrder(settings, map);
            if (AllowLayoutPersistence || map == null)
            {
                return map;
            }

            return new Dictionary<string, int>(map, StringComparer.OrdinalIgnoreCase);
        }

        private Dictionary<string, int> ApplyContextDefaultOrder(
            PlayniteAchievementsSettings settings,
            Dictionary<string, int> map)
        {
            if (!DefaultOrderByColumnSettingsKey.TryGetValue(ColumnSettingsKey ?? string.Empty, out var defaults) ||
                defaults == null ||
                defaults.Count == 0)
            {
                return map;
            }

            if (map != null && defaults.Keys.All(map.ContainsKey))
            {
                return map;
            }

            var defaultMap = defaults.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
            if (AllowLayoutPersistence)
            {
                SetOrderByKey(settings, defaultMap);
            }

            return defaultMap;
        }

        private Dictionary<string, GridAlignment> GetAlignmentMap(PlayniteAchievementsSettings settings)
        {
            var map = GetAlignmentsByKey(settings);
            if (AllowLayoutPersistence || map == null)
            {
                return map;
            }

            return new Dictionary<string, GridAlignment>(map, StringComparer.OrdinalIgnoreCase);
        }

        private Dictionary<string, GridVerticalAlignment> GetCellVerticalAlignmentMap(PlayniteAchievementsSettings settings)
        {
            var map = GetCellVerticalAlignmentsByKey(settings);
            if (AllowLayoutPersistence || map == null)
            {
                return map;
            }

            return new Dictionary<string, GridVerticalAlignment>(map, StringComparer.OrdinalIgnoreCase);
        }

        private Dictionary<string, GridAlignment> GetHeaderAlignmentMap(PlayniteAchievementsSettings settings)
        {
            var map = GetHeaderAlignmentsByKey(settings);
            if (AllowLayoutPersistence || map == null)
            {
                return map;
            }

            return new Dictionary<string, GridAlignment>(map, StringComparer.OrdinalIgnoreCase);
        }

        private Dictionary<string, bool> GetVisibilityByKey(PlayniteAchievementsSettings settings)
        {
            return GetColumnLayoutOptions(settings)?.Visibility;
        }

        private void SetVisibilityByKey(PlayniteAchievementsSettings settings, Dictionary<string, bool> map)
        {
            var options = GetColumnLayoutOptions(settings);
            if (options != null)
            {
                options.Visibility = map;
            }
        }

        private Dictionary<string, double> GetWidthsByKey(PlayniteAchievementsSettings settings)
        {
            return GetColumnLayoutOptions(settings)?.Widths;
        }

        private Dictionary<string, int> GetOrderByKey(PlayniteAchievementsSettings settings)
        {
            return GetColumnLayoutOptions(settings)?.Order;
        }

        private Dictionary<string, GridAlignment> GetAlignmentsByKey(PlayniteAchievementsSettings settings)
        {
            return GetColumnLayoutOptions(settings)?.CellAlignments;
        }

        private Dictionary<string, GridVerticalAlignment> GetCellVerticalAlignmentsByKey(PlayniteAchievementsSettings settings)
        {
            return GetColumnLayoutOptions(settings)?.CellVerticalAlignments;
        }

        private Dictionary<string, GridAlignment> GetHeaderAlignmentsByKey(PlayniteAchievementsSettings settings)
        {
            return GetColumnLayoutOptions(settings)?.HeaderAlignments;
        }

        private void SetOrderByKey(PlayniteAchievementsSettings settings, Dictionary<string, int> map)
        {
            var options = GetColumnLayoutOptions(settings);
            if (options != null)
            {
                options.Order = map;
            }
        }

        private void SetAlignmentsByKey(PlayniteAchievementsSettings settings, Dictionary<string, GridAlignment> map)
        {
            var options = GetColumnLayoutOptions(settings);
            if (options != null)
            {
                options.CellAlignments = map;
            }
        }

        private void SetCellVerticalAlignmentsByKey(
            PlayniteAchievementsSettings settings,
            Dictionary<string, GridVerticalAlignment> map)
        {
            var options = GetColumnLayoutOptions(settings);
            if (options != null)
            {
                options.CellVerticalAlignments = map;
            }
        }

        private void SetHeaderAlignmentsByKey(PlayniteAchievementsSettings settings, Dictionary<string, GridAlignment> map)
        {
            var options = GetColumnLayoutOptions(settings);
            if (options != null)
            {
                options.HeaderAlignments = map;
            }
        }

        private void SetWidthsByKey(PlayniteAchievementsSettings settings, Dictionary<string, double> map)
        {
            var options = GetColumnLayoutOptions(settings);
            if (options != null)
            {
                options.Widths = map;
            }
        }

        private GridColumnLayoutOptions GetColumnLayoutOptions(PlayniteAchievementsSettings settings)
        {
            var persisted = settings?.Persisted;
            if (persisted == null)
            {
                return null;
            }

            var id = GridOptionsCatalog.ResolveAchievementId(ColumnSettingsKey);
            return persisted.GridOptions.GetAchievement(id).Columns;
        }

        private static bool IsValidWidth(double width)
        {
            return !double.IsNaN(width) && !double.IsInfinity(width) && width > 0;
        }

        private static bool IsLegacyImageColumnRuntimeDefaultWidth(string key, double width)
        {
            return !string.IsNullOrWhiteSpace(key) &&
                   LegacyImageColumnRuntimeDefaults.TryGetValue(key, out var legacyWidth) &&
                   Math.Abs(ColumnWidthNormalization.RoundPixelWidth(width) -
                            ColumnWidthNormalization.RoundPixelWidth(legacyWidth)) <= 0.2;
        }

        private static void SavePluginSettings(PlayniteAchievementsSettings settings)
        {
            var plugin = PlayniteAchievementsPlugin.Instance;
            if (plugin == null || settings == null)
            {
                return;
            }

            try
            {
                plugin.SavePluginSettings(settings);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to persist column layout settings.");
            }
        }

        private void DataGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            // While drilled into a category the grid shows a self-contained filtered subset, so sort
            // it in-memory regardless of the surface's external-sorting setting (the external handler
            // sorts the full collection, which would not reorder the visible subset).
            if (IsCategoryGroupingEffective() && IsDrilled)
            {
                SortDrilledAchievements(e);
                return;
            }

            // Raise the Sorting event to allow external handling
            Sorting?.Invoke(this, e);

            if (e.Handled || UseExternalSorting)
            {
                return;
            }

            if (e.Column == null || string.IsNullOrWhiteSpace(e.Column.SortMemberPath))
            {
                DataGridSortingHelper.HandleSorting(sender, e, AchievementsDataGrid);
                return;
            }

            if (e.Column.SortDirection == null && _preSortItems == null)
            {
                _preSortItems = ItemsSource?.ToList();
            }

            // Default: in-memory sorting
            var sortDirection = DataGridSortingHelper.HandleSorting(sender, e, AchievementsDataGrid);
            if (!sortDirection.HasValue)
            {
                RestorePreSortOrder();
                return;
            }

            // Sort in-memory by reordering ItemsSource
            var items = ItemsSource?.ToList();
            if (items == null || items.Count == 0)
            {
                return;
            }

            var currentSortPath = string.Empty;
            ListSortDirection? currentSortDirection = null;
            if (!AchievementSortHelper.TrySortItems(
                    items,
                    e.Column.SortMemberPath,
                    sortDirection.Value,
                    SortScope,
                    ref currentSortPath,
                    ref currentSortDirection))
            {
                return;
            }

            AchievementSortHelper.ApplyGoalsFirst(items);
            ReplaceItemsInSource(items);
        }

        private void RestorePreSortOrder()
        {
            if (_preSortItems == null || _preSortItems.Count == 0)
            {
                _preSortItems = null;
                return;
            }

            var current = ItemsSource?.ToList();
            if (current == null || current.Count == 0)
            {
                _preSortItems = null;
                return;
            }

            var originalOrder = _preSortItems
                .Select((item, index) => new { item, index })
                .Where(entry => entry.item != null)
                .GroupBy(entry => entry.item)
                .ToDictionary(group => group.Key, group => group.First().index);
            var restored = current
                .Select((item, index) => new
                {
                    item,
                    currentIndex = index,
                    originalIndex = item != null && originalOrder.TryGetValue(item, out var originalIndex)
                        ? originalIndex
                        : int.MaxValue
                })
                .OrderBy(entry => entry.originalIndex)
                .ThenBy(entry => entry.currentIndex)
                .Select(entry => entry.item)
                .ToList();

            ReplaceItemsInSource(restored);
            _preSortItems = null;
        }

        private void ReplaceItemsInSource(List<AchievementDisplayItem> items)
        {
            if (items == null)
            {
                return;
            }

            if (ItemsSource is BulkObservableCollection<AchievementDisplayItem> bulkItems)
            {
                bulkItems.ReplaceAll(items);
            }
            else if (ItemsSource is IList<AchievementDisplayItem> listItems && !listItems.IsReadOnly)
            {
                CollectionHelper.SynchronizeReferenceCollectionByPosition(
                    listItems,
                    items,
                    updateExisting: null);
            }
            else
            {
                ItemsSource = items;
            }
        }

        private void AchievementRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsHyperlinkClick(e?.OriginalSource))
            {
                return;
            }

            // A click on an in-cell button (e.g. the Captures button) must not also toggle the row's
            // reveal/selection.
            if (IsButtonClick(e?.OriginalSource))
            {
                return;
            }

            if (ForwardRowMouseEvent(e, RowPreviewMouseLeftButtonDownEvent, sender))
            {
                return;
            }

            if (sender is DataGridRow row && row.DataContext is AchievementDisplayItem item)
            {
                if (TryActivateAchievementItem(item, consumeWhenNoAction: false))
                {
                    e.Handled = true;
                }
            }
        }

        private static bool IsHyperlinkClick(object source)
        {
            return source is DependencyObject dependencyObject &&
                   VisualTreeHelpers.FindVisualParent<Hyperlink>(dependencyObject) != null;
        }

        private static bool IsButtonClick(object source)
        {
            return source is DependencyObject dependencyObject &&
                   VisualTreeHelpers.FindVisualParent<ButtonBase>(dependencyObject) != null;
        }

        private void CapturesButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is AchievementDisplayItem item)
            {
                PlayniteAchievementsPlugin.Instance?.OpenCapturesViewer(item);
            }

            e.Handled = true;
        }

        private bool TryActivateAchievementItem(AchievementDisplayItem item, bool consumeWhenNoAction)
        {
            if (item == null || !item.CanReveal)
            {
                return consumeWhenNoAction && item != null;
            }

            var command = RevealCommand;
            if (command != null && command.CanExecute(item))
            {
                command.Execute(item);
            }
            else
            {
                item.ToggleReveal();
            }

            return true;
        }

        private void AchievementRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            ForwardRowMouseEvent(e, RowPreviewMouseRightButtonDownEvent, sender);
        }

        private void AchievementRow_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            ForwardRowMouseEvent(e, RowPreviewMouseRightButtonUpEvent, sender);
        }

        private bool ForwardRowMouseEvent(MouseButtonEventArgs sourceEvent, RoutedEvent routedEvent, object source)
        {
            if (sourceEvent == null || routedEvent == null)
            {
                return false;
            }

            var forwardedEvent = new MouseButtonEventArgs(
                sourceEvent.MouseDevice,
                sourceEvent.Timestamp,
                sourceEvent.ChangedButton)
            {
                RoutedEvent = routedEvent,
                Source = source
            };
            RaiseEvent(forwardedEvent);
            if (!forwardedEvent.Handled)
            {
                return false;
            }

            sourceEvent.Handled = true;
            return true;
        }

        private void DataGridColumnMenu_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!AllowColumnVisibilityMenu)
            {
                e.Handled = true;
                return;
            }

            if (!(sender is DataGrid grid))
            {
                return;
            }

            var header = VisualTreeHelpers.FindVisualParent<DataGridColumnHeader>(e.OriginalSource as DependencyObject);
            if (header?.Column == null)
            {
                return;
            }

            e.Handled = true;

            OpenColumnVisibilityMenu(grid, header, useControllerPlacement: false);
        }

        public bool OpenColumnVisibilityMenuForController()
        {
            var header = Services.UI.FullscreenControllerNavigationService.GetFocusedDataGridColumnHeader(AchievementsDataGrid);
            if (header == null)
            {
                return false;
            }

            return OpenColumnVisibilityMenu(
                AchievementsDataGrid,
                header,
                useControllerPlacement: true);
        }


        public bool IsColumnHeaderFocusedForController()
        {
            return Services.UI.FullscreenControllerNavigationService.IsFocusWithinDataGridColumnHeader(AchievementsDataGrid);
        }

        public bool ActivateFocusedColumnHeaderForController()
        {
            return Services.UI.FullscreenControllerNavigationService.ActivateFocusedDataGridColumnHeader(AchievementsDataGrid);
        }

        public bool OpenFocusedControlBarMenuForController()
        {
            return ControlBarHost?.OpenFocusedSelectorForController() == true;
        }

        public bool IsControlBarFocusedForController()
        {
            return ControlBarHost?.IsKeyboardFocusWithin == true;
        }

        public IList<UIElement> GetControlBarControllerElements()
        {
            return ControlBarHost?.GetControllerElements() ?? new List<UIElement>();
        }

        private bool OpenColumnVisibilityMenu(DataGrid grid, FrameworkElement owner, bool useControllerPlacement)
        {
            if (!AllowColumnVisibilityMenu || grid == null || owner == null)
            {
                return false;
            }

            UpdateColumnPersistenceContextOverrides();
            var menu = _columnPersistence?.BuildColumnVisibilityMenu((owner as DataGridColumnHeader)?.Column);
            if (menu == null || menu.Items.Count == 0)
            {
                return false;
            }

            Views.Helpers.ContextMenuStyleHelper.ApplyAchievementContextMenuStyle(owner, menu);
            if (useControllerPlacement)
            {
                return Services.UI.FullscreenControllerNavigationService.OpenContextMenu(owner, menu);
            }

            menu.Placement = PlacementMode.Bottom;
            menu.PlacementTarget = owner;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
            menu.IsOpen = true;
            return true;
        }

        /// <summary>
        /// Gets the internal DataGrid for direct access when needed.
        /// Used for scroll reset and other operations that require direct DataGrid access.
        /// </summary>
        public DataGrid InternalDataGrid => AchievementsDataGrid;

        public bool ActivateSelectedItem()
        {
            var item = AchievementsDataGrid?.SelectedItem as AchievementDisplayItem
                       ?? AchievementsDataGrid?.CurrentItem as AchievementDisplayItem;
            return TryActivateAchievementItem(item, consumeWhenNoAction: true);
        }

        /// <summary>
        /// Sets the sort indicator on a specific column, clearing others.
        /// Used for external sorting mode where the parent controls sort order.
        /// </summary>
        /// <param name="sortMemberPath">The SortMemberPath of the column to set the indicator on.</param>
        /// <param name="direction">The sort direction, or null to clear all indicators.</param>
        public void SetSortIndicator(string sortMemberPath, ListSortDirection? direction)
        {
            DataGridSortingHelper.SetSortIndicator(AchievementsDataGrid, sortMemberPath, direction);
        }

        /// <summary>
        /// Refreshes column persistence settings from storage.
        /// </summary>
        public void Refresh()
        {
            _columnPersistence?.Refresh();
            UpdateUnlockDateMode();
        }

        public void Dispose()
        {
            if (!_isAttached)
            {
                return;
            }

            _columnPersistence?.Dispose();
            _columnPersistence = null;
            _persistedSubscription?.Dispose();
            _persistedSubscription = null;

            if (_observedItemsSource != null)
            {
                _observedItemsSource.CollectionChanged -= OnItemsSourceCollectionChanged;
                _observedItemsSource = null;
            }

            if (_observedCategorySummarySource != null)
            {
                _observedCategorySummarySource.CollectionChanged -= OnCategorySummarySourceCollectionChanged;
                _observedCategorySummarySource = null;
            }
            DataGridAlignmentBehavior.SetColumnCellAlignmentOverridesProvider(AchievementsDataGrid, null);
            DataGridAlignmentBehavior.SetColumnCellVerticalAlignmentOverridesProvider(AchievementsDataGrid, null);
            DataGridAlignmentBehavior.SetColumnHeaderHorizontalAlignmentOverridesProvider(AchievementsDataGrid, null);
            _isAttached = false;
        }
    }
}

