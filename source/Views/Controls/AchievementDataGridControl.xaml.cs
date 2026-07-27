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
        private PersistedSettings _subscribedPersisted;
        private List<AchievementDisplayItem> _preSortItems;
        private const double DefaultStatusColumnWidth = 40;
        private const double DefaultIconColumnWidth = 72;
        private const double DefaultGameImageColumnWidth = 96;
        private const double DefaultFriendAvatarColumnWidth = 44;
        private const double DefaultFriendColumnWidth = 140;
        private const double DefaultTrophyIconColumnWidth = 72;
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
                ["RarityTier"] = DefaultTrophyIconColumnWidth
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
                ["Default"] = CreateAchievementVisibility(),
                ["SingleGame"] = CreateAchievementVisibility(),
                ["DesktopTheme"] = CreateAchievementVisibility(),
                ["OverviewSelectedGameAchievements"] = CreateAchievementVisibility(),
                ["OverviewGame"] = CreateAchievementVisibility(),
                ["OverviewRecentAchievements"] = CreateAchievementVisibility(status: false, game: true),
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
                ["Overview"] = CreateAchievementVisibility(status: false, game: true),
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
            bool points = false)
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
                ["Points"] = points
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
        // category dropdowns). Toggling it swaps the flat achievement grid for a per-category
        // summary grid; clicking a category drills into a filtered achievement list. All state is
        // self-contained here so any surface hosting this control opts in with a single attribute.

        private bool _isCategoryMode;
        private string _drilledCategory;
        private GridModeToggle _modeToggle;
        private GridMultiSelectFilter _connectedCategoryFilter;
        private GridActionButton _backButton;
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

        public static readonly DependencyProperty HideBackButtonProperty =
            DependencyProperty.Register(nameof(HideBackButton), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(false));

        // When true, the in-grid Back button is never created, letting a host's own breadcrumb
        // header (which calls ExitDrilledCategory()) be the only way back to the category list.
        public bool HideBackButton
        {
            get => (bool)GetValue(HideBackButtonProperty);
            set => SetValue(HideBackButtonProperty, value);
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

        // Reports the currently drilled-into category label (null when not drilled) so a host can
        // scope its own header counts to the drilled category. Written by the control; bind OneWayToSource.
        public string DrilledCategory
        {
            get => (string)GetValue(DrilledCategoryProperty);
            set => SetValue(DrilledCategoryProperty, value);
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

        // Mirrors the category dropdowns' auto-hide rule (>1 distinct label): the mode toggle is
        // only meaningful when there is more than one category to group the achievements into.
        private bool HasMultipleCategories()
        {
            var items = CategorySummarySource ?? ItemsSource;
            if (items == null)
            {
                return false;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (item == null)
                {
                    continue;
                }

                seen.Add(AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(item.CategoryLabel));
                if (seen.Count > 1)
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

            if (_backButton == null && !HideBackButton)
            {
                _backButton = new GridActionButton(
                    CategoryModeText("LOCPlayAch_Common_Back", "Back"),
                    CategoryBackToList,
                    CategoryModeText("LOCPlayAch_Common_Back", "Back"));
            }

            if (!ReferenceEquals(_controlBarWithToggle, ControlBar))
            {
                // The last multi-select filter is the category-label dropdown (Type is added first);
                // it becomes the right half of the segmented unit in flat mode.
                GridMultiSelectFilter labelFilter = null;
                for (var i = 0; i < ControlBar.Items.Count; i++)
                {
                    if (ControlBar.Items[i] is GridMultiSelectFilter filter)
                    {
                        labelFilter = filter;
                    }
                }

                _connectedCategoryFilter = labelFilter;
                _controlBarWithToggle = ControlBar;
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

        // Positions the category-mode Back and toggle controls for the current mode. The toggle always
        // stays in the trailing (right-side) items, spliced beside the category dropdown in flat mode;
        // it never relocates across the bar. Only the Back button moves to the leading zone, left of
        // the search box, while in category mode.
        private void UpdateModeControlPlacement()
        {
            var bar = _controlBarWithToggle;
            if (bar == null || _modeToggle == null)
            {
                return;
            }

            if (!bar.Items.Contains(_modeToggle))
            {
                var insertIndex = bar.Items.Count;
                for (var i = 0; i < bar.Items.Count; i++)
                {
                    if (bar.Items[i] is GridMultiSelectFilter)
                    {
                        insertIndex = i;
                    }
                }

                bar.Items.Insert(Math.Min(insertIndex, bar.Items.Count), _modeToggle);
            }

            if (_isCategoryMode)
            {
                if (_connectedCategoryFilter != null)
                {
                    _connectedCategoryFilter.ConnectedLeft = false;
                }

                if (_backButton != null && !bar.LeadingItems.Contains(_backButton))
                {
                    bar.LeadingItems.Insert(0, _backButton);
                }
            }
            else
            {
                if (_backButton != null)
                {
                    bar.LeadingItems.Remove(_backButton);
                }

                if (_connectedCategoryFilter != null)
                {
                    // Only adopt the segmented style when the toggle is actually shown beside it;
                    // otherwise the dropdown reverts to its standalone bordered style.
                    _connectedCategoryFilter.ConnectedLeft = _modeToggle.EffectiveIsVisible;
                }
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
            bar.Items.Remove(_backButton);
            bar.LeadingItems.Remove(_modeToggle);
            bar.LeadingItems.Remove(_backButton);
            foreach (var item in bar.Items)
            {
                if (item is GridMultiSelectFilter filter)
                {
                    filter.IsVisible = true;
                    filter.ConnectedLeft = false;
                }
                else if (item is GridToggleFilter toggle)
                {
                    toggle.IsVisible = true;
                }
            }

            _connectedCategoryFilter = null;

            if (_originalSearch != null && ReferenceEquals(bar.Search, _categorySearch))
            {
                bar.Search = _originalSearch;
            }
        }

        // Shows/hides the injected items and swaps the search box to match the active nested grid:
        // category dropdowns are hidden in category mode, Back shows only when drilled, and the
        // search box filters category names in the list but achievements once drilled in.
        private void ApplyControlBarModeState()
        {
            var bar = _controlBarWithToggle;
            if (bar == null)
            {
                return;
            }

            var grouping = IsCategoryGroupingEffective();
            var drilled = grouping && _drilledCategory != null;
            var list = grouping && !drilled;

            // Reflow Back/toggle between the leading zone and the segmented unit for the current mode.
            UpdateModeControlPlacement();

            foreach (var item in bar.Items)
            {
                if (item is GridMultiSelectFilter)
                {
                    item.IsVisible = !_isCategoryMode;
                }
                else if (item is GridToggleFilter)
                {
                    // The Unlocked/Locked/Hidden toggles filter achievements, so hide them in the
                    // category list (rows are categories) but restore them flat and when drilled in.
                    item.IsVisible = !list;
                }
            }

            if (_backButton != null)
            {
                _backButton.IsVisible = drilled;
            }

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
            _drilledCategory = null;
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
            _allCategorySummaries = items == null || items.Count == 0
                ? null
                : CategorySummaryBuilder.Build(items);
            ApplyCategoryNameFilter();
        }

        private void ApplyCategoryNameFilter()
        {
            var all = _allCategorySummaries;
            if (all == null)
            {
                CategorySummaries = null;
                return;
            }

            var visible = all;
            if (!string.IsNullOrWhiteSpace(_categorySearchText))
            {
                var needle = _categorySearchText.Trim();
                visible = all
                    .Where(c => (c.GameName ?? string.Empty).IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
            }

            // A manual column sort overlays the builder's default order (custom category order,
            // then provider order); sorting a copy keeps _allCategorySummaries as the reset target.
            if (_categorySortDirection.HasValue && !string.IsNullOrWhiteSpace(_categorySortPath))
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

            CategorySummaries = visible;
            CategoryListGrid?.SetSortIndicator(_categorySortPath, _categorySortDirection);
        }

        private void DrillIntoCategory(CategorySummaryItem item)
        {
            if (item == null)
            {
                return;
            }

            _drilledCategory = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(item.CategoryLabel);
            SelectedCategorySummaryItems = new[] { (GameSummaryItem)item };
            ApplyCategoryViewState();
            ApplyControlBarModeState();
        }

        private void ApplyCategoryViewState()
        {
            var grouping = IsCategoryGroupingEffective();
            if (!grouping && _drilledCategory != null)
            {
                _drilledCategory = null;
                SelectedCategorySummaryItems = null;
                if (CategoryListGrid != null)
                {
                    CategoryListGrid.SelectedItem = null;
                }
            }

            var drill = grouping && _drilledCategory != null;
            var list = grouping && !drill;
            CategoryListVisible = list;
            DrillHeaderVisible = drill && !HideCategorySummaryRow;
            AchievementGridVisible = !list;
            DrilledCategory = drill ? _drilledCategory : null;
            RecomputeEffectiveAchievements();
        }

        private void RecomputeEffectiveAchievements()
        {
            if (IsCategoryGroupingEffective() && _drilledCategory != null)
            {
                var filtered = (ItemsSource ?? Enumerable.Empty<AchievementDisplayItem>())
                    .Where(i => string.Equals(
                        AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(i?.CategoryLabel),
                        _drilledCategory,
                        StringComparison.OrdinalIgnoreCase))
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
            OnItemsSourceContentChanged();
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
                OnItemsSourceContentChanged();
            }
        }

        private void OnItemsSourceContentChanged()
        {
            // Re-evaluate toggle availability first: a game switch or a newly loaded multi-game feed
            // may add or remove the category toggle (and drop us out of category mode) before the
            // rest of this method reads _isCategoryMode.
            SyncModeToggle();

            if (!_isCategoryMode)
            {
                RecomputeEffectiveAchievements();
                return;
            }

            RebuildCategorySummaries();

            if (!HasMultipleCategories())
            {
                _drilledCategory = null;
                SelectedCategorySummaryItems = null;
                if (CategoryListGrid != null)
                {
                    CategoryListGrid.SelectedItem = null;
                }
            }
            else if (_drilledCategory != null)
            {
                var match = CategorySummaries?
                    .OfType<CategorySummaryItem>()
                    .FirstOrDefault(c => string.Equals(c.CategoryLabel, _drilledCategory, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    // The drilled category vanished (e.g. game switched); fall back to the list.
                    _drilledCategory = null;
                    SelectedCategorySummaryItems = null;
                    if (CategoryListGrid != null)
                    {
                        CategoryListGrid.SelectedItem = null;
                    }
                }
                else
                {
                    SelectedCategorySummaryItems = new[] { (GameSummaryItem)match };
                }
            }

            ApplyCategoryViewState();
            ApplyControlBarModeState();
        }

        private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsCategoryGroupingEffective() || _drilledCategory != null)
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

            ApplyCategoryNameFilter();
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
            if (IsCategoryGroupingEffective() && _drilledCategory != null)
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

            _drillItems.ReplaceAll(items);
        }

        // Public entry point for a host's own breadcrumb header to navigate back to the category
        // summary list, for surfaces where HideBackButton suppresses the in-grid Back button.
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
                var label = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(item.CategoryLabel);
                if (!string.Equals(_drilledCategory, label, StringComparison.OrdinalIgnoreCase))
                {
                    var match = CategorySummaries?
                        .OfType<CategorySummaryItem>()
                        .FirstOrDefault(c => string.Equals(
                            AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(c.CategoryLabel),
                            label,
                            StringComparison.OrdinalIgnoreCase));
                    if (match == null)
                    {
                        return;
                    }

                    DrillIntoCategory(match);
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

        private void CategoryBackToList()
        {
            _drilledCategory = null;
            SelectedCategorySummaryItems = null;
            if (CategoryListGrid != null)
            {
                CategoryListGrid.SelectedItem = null;
            }

            ApplyCategoryViewState();
            ApplyControlBarModeState();

            // Returning to the list starts the next drill clean; summaries stay full regardless.
            ResetAchievementFilters();
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
            DataContextChanged += OnDataContextChanged;
            Unloaded += OnUnloaded;
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

            var persisted = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted;
            if (persisted != null)
            {
                _subscribedPersisted = persisted;
                _subscribedPersisted.PropertyChanged += OnPersistedSettingsChanged;
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
            if (IsCategoryGroupingEffective() && _drilledCategory != null)
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
            if (_subscribedPersisted != null)
            {
                _subscribedPersisted.PropertyChanged -= OnPersistedSettingsChanged;
                _subscribedPersisted = null;
            }

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

