using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Controls;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views
{
    public partial class ManualAchievementSortDialog : UserControl, IDisposable
    {
        private sealed class SortOption
        {
            public SortOption(string path, string label, bool isRetro = false, bool isSourceOrder = false)
            {
                Path = path;
                Label = label;
                IsRetro = isRetro;
                IsSourceOrder = isSourceOrder;
            }

            public string Path { get; }

            public string Label { get; }

            public bool IsRetro { get; }

            public bool IsSourceOrder { get; }
        }

        private readonly PlayniteAchievementsSettings _settings;
        private readonly Action _saveSettings;
        private readonly List<GameSummaryItem> _overviewSource = new List<GameSummaryItem>();
        private readonly List<AchievementDisplayItem> _recentSource = new List<AchievementDisplayItem>();
        private readonly List<AchievementDisplayItem> _allSource = new List<AchievementDisplayItem>();
        private readonly List<AchievementDisplayItem> _selectedSource = new List<AchievementDisplayItem>();

        private DataGridColumnLayoutService _gamesOverviewPersistence;

        private string _overviewSortPath;
        private ListSortDirection _overviewSortDirection;
        private readonly List<(string Path, ListSortDirection Direction)> _overviewSecondarySorts
            = new List<(string Path, ListSortDirection Direction)>();

        private string _recentSortPath;
        private ListSortDirection _recentSortDirection;
        private readonly List<(string Path, ListSortDirection Direction)> _recentSecondarySorts
            = new List<(string Path, ListSortDirection Direction)>();

        private string _allSortPath;
        private ListSortDirection _allSortDirection;
        private readonly List<(string Path, ListSortDirection Direction)> _allSecondarySorts
            = new List<(string Path, ListSortDirection Direction)>();

        private string _selectedSortPath;
        private ListSortDirection _selectedSortDirection;
        private readonly List<(string Path, ListSortDirection Direction)> _selectedSecondarySorts
            = new List<(string Path, ListSortDirection Direction)>();

        private readonly List<SortOption> _overviewOptions;
        private readonly List<SortOption> _achievementOptionsWithGame;
        private readonly List<SortOption> _achievementOptionsNoGame;

        public ObservableCollection<GameSummaryItem> GamesOverviewItems { get; } = new ObservableCollection<GameSummaryItem>();

        public ObservableCollection<AchievementDisplayItem> RecentItems { get; } = new ObservableCollection<AchievementDisplayItem>();

        public ObservableCollection<AchievementDisplayItem> AllItems { get; } = new ObservableCollection<AchievementDisplayItem>();

        public ObservableCollection<AchievementDisplayItem> SelectedItems { get; } = new ObservableCollection<AchievementDisplayItem>();

        public PlayniteAchievementsSettings SettingsContext => _settings;

        public bool? DialogResult { get; private set; }

        public event EventHandler RequestClose;

        public ManualAchievementSortDialog(PlayniteAchievementsSettings settings, Action saveSettings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _saveSettings = saveSettings ?? (() => { });

            _overviewOptions = new List<SortOption>
            {
                CreateSourceOrderOption(),
                new SortOption("SortingName", ResourceProvider.GetString("LOCPlayAch_Column_Name") ?? "Name"),
                new SortOption("LastPlayed", ResourceProvider.GetString("LOCPlayAch_Column_LastPlayed") ?? "Last Played"),
                new SortOption("PlaytimeSeconds", ResourceProvider.GetString("LOCPlayAch_Column_Playtime") ?? "Playtime"),
                new SortOption("Progression", ResourceProvider.GetString("LOCPlayAch_Progress") ?? "Progress"),
                new SortOption("TotalAchievements", ResourceProvider.GetString("LOCPlayAch_Column_Total") ?? "Total")
            };

            _achievementOptionsWithGame = CreateAchievementSortOptions(includeGameColumn: true);
            _achievementOptionsNoGame = CreateAchievementSortOptions(includeGameColumn: false);

            InitializeComponent();
            DataContext = this;

            LoadCurrentSorts();
            InitializePreviewItems();
            InitializeSortSelectors();
            ApplyAllSortsToPreview();

            Loaded += ManualAchievementSortDialog_Loaded;
            Unloaded += ManualAchievementSortDialog_Unloaded;
        }

        public static bool TryShowDialog(PlayniteAchievementsSettings settings, Action saveSettings)
        {
            var dialog = new ManualAchievementSortDialog(settings, saveSettings);
            var window = PlayniteUiProvider.CreateExtensionWindow(
                ResourceProvider.GetString("LOCPlayAch_Settings_ManualSort_Title") ?? "Custom (Manual) Sort Configuration",
                dialog,
                new WindowOptions
                {
                    Width = 1160,
                    Height = 820,
                    CanBeResizable = true,
                    ShowCloseButton = true,
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false
                });

            window.MinWidth = 980;
            window.MinHeight = 700;
            dialog.RequestClose += (_, __) => window.Close();
            window.ShowDialog();
            return dialog.DialogResult == true;
        }

        private static List<SortOption> CreateAchievementSortOptions(bool includeGameColumn)
        {
            var options = new List<SortOption>
            {
                CreateSourceOrderOption(),
                new SortOption("DisplayName", ResourceProvider.GetString("LOCPlayAch_Column_Achievement") ?? "Achievement"),
                new SortOption("UnlockTime", ResourceProvider.GetString("LOCPlayAch_Common_Unlocked") ?? "Unlocked"),
                new SortOption("CategoryType", ResourceProvider.GetString("LOCPlayAch_Common_Label_Type") ?? "Type"),
                new SortOption("CategoryLabel", ResourceProvider.GetString("LOCPlayAch_Common_Label_Category") ?? "Category"),
                new SortOption("TrophyType", ResourceProvider.GetString("LOCPlayAch_Column_Trophy") ?? "Trophy"),
                new SortOption("RaritySortValue", ResourceProvider.GetString("LOCPlayAch_Column_Rarity") ?? "Rarity"),
                new SortOption("Points", ResourceProvider.GetString("LOCPlayAch_Column_Points") ?? "Points"),
                new SortOption("DisplayOrder", ResourceProvider.GetString("LOCPlayAch_SortMode_RetroAchievements") ?? "RetroAchievements", isRetro: true)
            };

            if (includeGameColumn)
            {
                options.Insert(1, new SortOption("SortingName", ResourceProvider.GetString("LOCPlayAch_Column_Game") ?? "Game"));
            }

            return options;
        }

        private static SortOption CreateSourceOrderOption()
        {
            return new SortOption(
                null,
                ResourceProvider.GetString("LOCPlayAch_SortMode_SourceOrder") ?? "Provider / Source Order (No Column Sort)",
                isSourceOrder: true);
        }

        private void ManualAchievementSortDialog_Loaded(object sender, RoutedEventArgs e)
        {
            AttachGamesOverviewPersistence();
            UpdateDirectionButtons();
        }

        private void ManualAchievementSortDialog_Unloaded(object sender, RoutedEventArgs e)
        {
            Dispose();
        }

        private void AttachGamesOverviewPersistence()
        {
            if (_gamesOverviewPersistence != null || _settings?.Persisted == null)
            {
                return;
            }            _gamesOverviewPersistence = new DataGridColumnLayoutService(
                GamesOverviewGrid,
                null,
                () => _settings.Persisted.OverviewGameSummariesColumnWidths,
                map => _settings.Persisted.OverviewGameSummariesColumnWidths = map,
                () => _settings.Persisted.OverviewGameSummariesColumnVisibility,
                map => _settings.Persisted.OverviewGameSummariesColumnVisibility = map,
                _saveSettings,
                getOrder: () => _settings.Persisted.OverviewGameSummariesColumnOrder,
                setOrder: map => _settings.Persisted.OverviewGameSummariesColumnOrder = map,
                getCellAlignments: () => _settings.Persisted.OverviewGameSummariesColumnAlignments,
                setCellAlignments: map => _settings.Persisted.OverviewGameSummariesColumnAlignments = map,
                getDefaultCellAlignment: () => _settings.Persisted.GridCellAlignment,
                getCellVerticalAlignments: () => _settings.Persisted.OverviewGameSummariesColumnVerticalAlignments,
                setCellVerticalAlignments: map => _settings.Persisted.OverviewGameSummariesColumnVerticalAlignments = map,
                getDefaultCellVerticalAlignment: () => _settings.Persisted.GridCellVerticalAlignment,
                getHeaderHorizontalAlignments: () => _settings.Persisted.OverviewGameSummariesColumnHeaderAlignments,
                setHeaderHorizontalAlignments: map => _settings.Persisted.OverviewGameSummariesColumnHeaderAlignments = map,
                getDefaultHeaderHorizontalAlignment: () => _settings.Persisted.GridColumnHeaderAlignment);

            _gamesOverviewPersistence.Attach();
        }

        private void LoadCurrentSorts()
        {
            var persisted = _settings.Persisted;
            _overviewSortPath = persisted.GamesOverviewCustomSortUsesSourceOrder
                ? null
                : (string.IsNullOrWhiteSpace(persisted.GamesOverviewCustomSortPath)
                    ? "SortingName"
                    : persisted.GamesOverviewCustomSortPath);
            _overviewSortDirection = persisted.GamesOverviewCustomSortDescending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
            _overviewSecondarySorts.Clear();
            _overviewSecondarySorts.AddRange(DeserializeSecondarySorts(persisted.GamesOverviewCustomSecondarySorts));

            _recentSortPath = persisted.RecentAchievementsCustomSortUsesSourceOrder
                ? null
                : (string.IsNullOrWhiteSpace(persisted.RecentAchievementsCustomSortPath)
                    ? "UnlockTime"
                    : persisted.RecentAchievementsCustomSortPath);
            _recentSortDirection = persisted.RecentAchievementsCustomSortDescending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
            _recentSecondarySorts.Clear();
            _recentSecondarySorts.AddRange(DeserializeSecondarySorts(persisted.RecentAchievementsCustomSecondarySorts));

            _allSortPath = persisted.SidebarAllAchievementsCustomSortUsesSourceOrder
                ? null
                : (string.IsNullOrWhiteSpace(persisted.SidebarAllAchievementsCustomSortPath)
                    ? "UnlockTime"
                    : persisted.SidebarAllAchievementsCustomSortPath);
            _allSortDirection = persisted.SidebarAllAchievementsCustomSortDescending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
            _allSecondarySorts.Clear();
            _allSecondarySorts.AddRange(DeserializeSecondarySorts(persisted.SidebarAllAchievementsCustomSecondarySorts));

            _selectedSortPath = persisted.SidebarSelectedGameCustomSortUsesSourceOrder
                ? null
                : (string.IsNullOrWhiteSpace(persisted.SidebarSelectedGameCustomSortPath)
                    ? (string.IsNullOrWhiteSpace(persisted.CustomSortPath)
                        ? "UnlockTime"
                        : persisted.CustomSortPath)
                    : persisted.SidebarSelectedGameCustomSortPath);
            var selectedDesc = !string.IsNullOrWhiteSpace(persisted.SidebarSelectedGameCustomSortPath)
                ? persisted.SidebarSelectedGameCustomSortDescending
                : persisted.CustomSortDescending;
            _selectedSortDirection = selectedDesc ? ListSortDirection.Descending : ListSortDirection.Ascending;
            _selectedSecondarySorts.Clear();
            _selectedSecondarySorts.AddRange(DeserializeSecondarySorts(persisted.SidebarSelectedGameCustomSecondarySorts));
        }

        private void InitializePreviewItems()
        {
            _overviewSource.Clear();
            _overviewSource.AddRange(CreateOverviewPreviewItems());
            ReplaceCollection(GamesOverviewItems, _overviewSource);

            _recentSource.Clear();
            _recentSource.AddRange(CreateAchievementPreviewItems("Recent"));
            ReplaceCollection(RecentItems, _recentSource);

            _allSource.Clear();
            _allSource.AddRange(CreateAchievementPreviewItems("All"));
            ReplaceCollection(AllItems, _allSource);

            _selectedSource.Clear();
            _selectedSource.AddRange(CreateAchievementPreviewItems("Selected"));
            ReplaceCollection(SelectedItems, _selectedSource);
        }

        private void InitializeSortSelectors()
        {
            OverviewSortCombo.ItemsSource = _overviewOptions;
            OverviewSortCombo.SelectedItem = FindOption(_overviewOptions, _overviewSortPath);

            RecentSortCombo.ItemsSource = _achievementOptionsWithGame;
            RecentSortCombo.SelectedItem = FindOption(_achievementOptionsWithGame, _recentSortPath);

            AllSortCombo.ItemsSource = _achievementOptionsWithGame;
            AllSortCombo.SelectedItem = FindOption(_achievementOptionsWithGame, _allSortPath);

            SelectedSortCombo.ItemsSource = _achievementOptionsNoGame;
            SelectedSortCombo.SelectedItem = FindOption(_achievementOptionsNoGame, _selectedSortPath);
        }

        private static SortOption FindOption(IEnumerable<SortOption> options, string sortPath)
        {
            if (string.IsNullOrWhiteSpace(sortPath))
            {
                return options.FirstOrDefault(option => option.IsSourceOrder);
            }

            return options.FirstOrDefault(option => string.Equals(option.Path, sortPath, StringComparison.Ordinal))
                ?? options.FirstOrDefault();
        }

        private void ApplyAllSortsToPreview()
        {
            ApplyOverviewSort(_overviewSortPath, _overviewSortDirection, _overviewSecondarySorts);
            ApplyAchievementSort(RecentItems, _recentSortPath, _recentSortDirection, AchievementSortScope.RecentAchievements, _recentSecondarySorts);
            ApplyAchievementSort(AllItems, _allSortPath, _allSortDirection, AchievementSortScope.GameAchievements, _allSecondarySorts);
            ApplyAchievementSort(SelectedItems, _selectedSortPath, _selectedSortDirection, AchievementSortScope.GameAchievements, _selectedSecondarySorts);
            RefreshSortLevelBadges();
            UpdateDirectionButtons();
        }

        private List<GameSummaryItem> CreateOverviewPreviewItems()
        {
            var baseDate = DateTime.Today;
            return new List<GameSummaryItem>
            {
                new GameSummaryItem { GameName = "Celeste", SortingName = "Celeste", TotalAchievements = 30, UnlockedAchievements = 28, LastPlayed = baseDate.AddDays(-1).AddHours(21).AddMinutes(10), PlaytimeSeconds = 84200 },
                new GameSummaryItem { GameName = "Hades", SortingName = "Hades", TotalAchievements = 49, UnlockedAchievements = 36, LastPlayed = baseDate.AddDays(-1).AddHours(9).AddMinutes(40), PlaytimeSeconds = 126400 },
                new GameSummaryItem { GameName = "Dead Cells", SortingName = "Dead Cells", TotalAchievements = 58, UnlockedAchievements = 18, LastPlayed = baseDate.AddDays(-4).AddHours(23).AddMinutes(5), PlaytimeSeconds = 40500 },
                new GameSummaryItem { GameName = "Hollow Knight", SortingName = "Hollow Knight", TotalAchievements = 63, UnlockedAchievements = 63, LastPlayed = baseDate.AddDays(-2).AddHours(15).AddMinutes(30), PlaytimeSeconds = 152000 },
                new GameSummaryItem { GameName = "Cuphead", SortingName = "Cuphead", TotalAchievements = 42, UnlockedAchievements = 17, LastPlayed = baseDate.AddDays(-7).AddHours(8).AddMinutes(20), PlaytimeSeconds = 29200 }
            };
        }

        private List<AchievementDisplayItem> CreateAchievementPreviewItems(string gameName)
        {
            var items = MockDataHelper.CreateMockDataGridItems(
                showRarityBar: true,
                showRarityGlow: true,
                showHiddenIcon: true,
                showHiddenTitle: true,
                showHiddenDescription: true,
                showHiddenSuffix: true,
                showLockedIcon: true)
                .Select(item => item.Clone())
                .ToList();

            var baseUtcDate = DateTime.UtcNow.Date;
            var previewUnlockTimes = new List<DateTime?>
            {
                baseUtcDate.AddDays(-1).AddHours(22).AddMinutes(15),
                baseUtcDate.AddDays(-1).AddHours(8).AddMinutes(30),
                baseUtcDate.AddDays(-3).AddHours(19).AddMinutes(5),
                baseUtcDate.AddDays(-6).AddHours(11).AddMinutes(45),
                null,
            };

            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                item.GameName = gameName + " Preview";
                item.SortingName = item.GameName;
                if (i < previewUnlockTimes.Count)
                {
                    item.UnlockTimeUtc = previewUnlockTimes[i];
                }
                if (item.Source == null)
                {
                    item.DisplayName = item.DisplayName;
                }

                if (item.Source != null)
                {
                    item.Source.DisplayOrder = i;
                }
            }

            return items;
        }

        private void GamesOverviewGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            var isAdditive = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (isAdditive && !string.IsNullOrWhiteSpace(_overviewSortPath))
            {
                var additiveDirection = DataGridSortingHelper.HandleSorting(
                    sender,
                    e,
                    GamesOverviewGrid,
                    clearOtherColumns: false);
                if (!additiveDirection.HasValue)
                {
                    return;
                }

                UpdateSecondarySorts(
                    _overviewSecondarySorts,
                    _overviewSortPath,
                    e.Column.SortMemberPath,
                    additiveDirection.Value,
                    isAdditive: true);
                ApplyOverviewSort(_overviewSortPath, _overviewSortDirection, _overviewSecondarySorts);
                RefreshSortLevelBadges();
                UpdateDirectionButtons();
                return;
            }

            e.Handled = true;
            var sortAction = GameSummariesSortHelper.ResolveGridSortAction(
                e.Column?.SortMemberPath,
                _overviewSortPath,
                string.IsNullOrWhiteSpace(_overviewSortPath)
                    ? (ListSortDirection?)null
                    : _overviewSortDirection,
                _settings?.Persisted);
            if (sortAction.Kind == GameSummariesGridSortActionKind.None)
            {
                return;
            }

            if (sortAction.Kind == GameSummariesGridSortActionKind.ResetToDefault)
            {
                _overviewSortPath = null;
                _overviewSecondarySorts.Clear();
                ReplaceCollection(GamesOverviewItems, _overviewSource);
                OverviewSortCombo.SelectedItem = FindOption(_overviewOptions, null);
                RefreshSortLevelBadges();
                UpdateDirectionButtons();
                return;
            }

            if (sortAction.Direction.HasValue)
            {
                _overviewSecondarySorts.Clear();
                _overviewSortPath = sortAction.SortMemberPath;
                _overviewSortDirection = sortAction.Direction.Value;
            }

            ApplyOverviewSort(_overviewSortPath, _overviewSortDirection, _overviewSecondarySorts);
            OverviewSortCombo.SelectedItem = FindOption(_overviewOptions, _overviewSortPath);
            RefreshSortLevelBadges();
            UpdateDirectionButtons();
        }

        private void RecentPreviewGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            ApplyAchievementGridSortFromHeader(
                e,
                RecentPreviewGrid,
                RecentItems,
                _recentSource,
                AchievementSortScope.RecentAchievements,
                AchievementSortSurface.OverviewRecentAchievements,
                ref _recentSortPath,
                ref _recentSortDirection,
                _recentSecondarySorts,
                RecentSortCombo,
                _achievementOptionsWithGame);

            RefreshSortLevelBadges();
            UpdateDirectionButtons();
        }

        private void AllPreviewGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            ApplyAchievementGridSortFromHeader(
                e,
                AllPreviewGrid,
                AllItems,
                _allSource,
                AchievementSortScope.GameAchievements,
                AchievementSortSurface.AchievementDataGrid,
                ref _allSortPath,
                ref _allSortDirection,
                _allSecondarySorts,
                AllSortCombo,
                _achievementOptionsWithGame);

            RefreshSortLevelBadges();
            UpdateDirectionButtons();
        }

        private void SelectedPreviewGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            ApplyAchievementGridSortFromHeader(
                e,
                SelectedPreviewGrid,
                SelectedItems,
                _selectedSource,
                AchievementSortScope.GameAchievements,
                AchievementSortSurface.OverviewSelectedGame,
                ref _selectedSortPath,
                ref _selectedSortDirection,
                _selectedSecondarySorts,
                SelectedSortCombo,
                _achievementOptionsNoGame);

            RefreshSortLevelBadges();
            UpdateDirectionButtons();
        }

        private void ApplyAchievementGridSortFromHeader(
            DataGridSortingEventArgs e,
            AchievementDataGridControl gridControl,
            ObservableCollection<AchievementDisplayItem> targetCollection,
            IReadOnlyList<AchievementDisplayItem> sourceItems,
            AchievementSortScope scope,
            AchievementSortSurface surface,
            ref string currentPath,
            ref ListSortDirection currentDirection,
            List<(string Path, ListSortDirection Direction)> secondaries,
            ComboBox combo,
            List<SortOption> options)
        {
            var isAdditive = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            if (isAdditive && !string.IsNullOrWhiteSpace(currentPath))
            {
                var additiveDirection = DataGridSortingHelper.HandleSorting(
                    gridControl,
                    e,
                    gridControl.InternalDataGrid,
                    clearOtherColumns: false);
                if (!additiveDirection.HasValue)
                {
                    return;
                }

                UpdateSecondarySorts(
                    secondaries,
                    currentPath,
                    e.Column.SortMemberPath,
                    additiveDirection.Value,
                    isAdditive: true);
                SortAchievementPreview(
                    targetCollection,
                    scope,
                    currentPath,
                    currentDirection,
                    secondaries);
                return;
            }

            e.Handled = true;
            var sortAction = AchievementSortHelper.ResolveGridSortAction(
                e.Column?.SortMemberPath,
                currentPath,
                string.IsNullOrWhiteSpace(currentPath)
                    ? (ListSortDirection?)null
                    : currentDirection,
                _settings?.Persisted,
                surface,
                e.Column?.SortDirection);
            if (sortAction.Kind == AchievementGridSortActionKind.None)
            {
                return;
            }

            if (sortAction.Kind == AchievementGridSortActionKind.ResetToDefault)
            {
                currentPath = null;
                secondaries.Clear();
                ReplaceCollection(targetCollection, sourceItems);
                combo.SelectedItem = FindOption(options, null);
                return;
            }

            if (sortAction.Direction.HasValue)
            {
                secondaries.Clear();
                currentPath = sortAction.SortMemberPath;
                currentDirection = sortAction.Direction.Value;
            }

            SortAchievementPreview(
                targetCollection,
                scope,
                currentPath,
                currentDirection,
                secondaries);
            combo.SelectedItem = FindOption(options, currentPath);
        }

        private static void SortAchievementPreview(
            ObservableCollection<AchievementDisplayItem> targetCollection,
            AchievementSortScope scope,
            string currentPath,
            ListSortDirection currentDirection,
            IReadOnlyList<(string Path, ListSortDirection Direction)> secondaries)
        {
            var items = targetCollection.ToList();
            var sortPath = currentPath;
            var sortDirection = (ListSortDirection?)currentDirection;
            if (!AchievementSortHelper.TrySortItems(
                    items,
                    sortPath,
                    currentDirection,
                    scope,
                    ref sortPath,
                    ref sortDirection))
            {
                return;
            }

            if (sortDirection.HasValue)
            {
                currentDirection = sortDirection.Value;
            }

            ApplySecondarySorts(items, secondaries, scope, currentPath, currentDirection);
            ReplaceCollection(targetCollection, items);
        }

        private void ApplyOverviewSort(
            string sortPath,
            ListSortDirection direction,
            IReadOnlyList<(string Path, ListSortDirection Direction)> secondaries)
        {
            var items = _overviewSource.ToList();
            var currentPath = sortPath;
            var currentDirection = direction;
            if (!GameSummariesSortHelper.TrySortItems(items, sortPath, direction, ref currentPath, ref currentDirection))
            {
                return;
            }

            ReplaceCollection(GamesOverviewItems, items);
        }

        private static void ApplyAchievementSort(
            ObservableCollection<AchievementDisplayItem> target,
            string sortPath,
            ListSortDirection direction,
            AchievementSortScope scope,
            IReadOnlyList<(string Path, ListSortDirection Direction)> secondaries)
        {
            var items = target.ToList();
            var currentPath = sortPath;
            var currentDirection = (ListSortDirection?)direction;
            if (!AchievementSortHelper.TrySortItems(
                    items,
                    sortPath,
                    direction,
                    scope,
                    ref currentPath,
                    ref currentDirection,
                    null))
            {
                return;
            }

            ApplySecondarySorts(items, secondaries, scope, sortPath, direction);
            ReplaceCollection(target, items);
        }

        private void ApplyOverviewSortButton_Click(object sender, RoutedEventArgs e)
        {
            if (OverviewSortCombo.SelectedItem is SortOption option)
            {
                if (option.IsSourceOrder)
                {
                    _overviewSortPath = null;
                    _overviewSecondarySorts.Clear();
                    ReplaceCollection(GamesOverviewItems, _overviewSource);
                    RefreshSortLevelBadges();
                    UpdateDirectionButtons();
                    return;
                }

                _overviewSortPath = option.Path;
                ApplyOverviewSort(_overviewSortPath, _overviewSortDirection, _overviewSecondarySorts);
                RefreshSortLevelBadges();
            }
        }

        private void ApplyRecentSortButton_Click(object sender, RoutedEventArgs e)
        {
            if (RecentSortCombo.SelectedItem is SortOption option)
            {
                if (option.IsSourceOrder)
                {
                    _recentSortPath = null;
                    _recentSecondarySorts.Clear();
                    ReplaceCollection(RecentItems, _recentSource);
                    RefreshSortLevelBadges();
                    UpdateDirectionButtons();
                    return;
                }

                _recentSortPath = option.Path;
                if (option.IsRetro)
                {
                    _recentSortDirection = ListSortDirection.Ascending;
                }

                ApplyAchievementSort(RecentItems, _recentSortPath, _recentSortDirection, AchievementSortScope.RecentAchievements, _recentSecondarySorts);
                RefreshSortLevelBadges();
                UpdateDirectionButtons();
            }
        }

        private void ApplyAllSortButton_Click(object sender, RoutedEventArgs e)
        {
            if (AllSortCombo.SelectedItem is SortOption option)
            {
                if (option.IsSourceOrder)
                {
                    _allSortPath = null;
                    _allSecondarySorts.Clear();
                    ReplaceCollection(AllItems, _allSource);
                    RefreshSortLevelBadges();
                    UpdateDirectionButtons();
                    return;
                }

                _allSortPath = option.Path;
                if (option.IsRetro)
                {
                    _allSortDirection = ListSortDirection.Ascending;
                }

                ApplyAchievementSort(AllItems, _allSortPath, _allSortDirection, AchievementSortScope.GameAchievements, _allSecondarySorts);
                RefreshSortLevelBadges();
                UpdateDirectionButtons();
            }
        }

        private void ApplySelectedSortButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedSortCombo.SelectedItem is SortOption option)
            {
                if (option.IsSourceOrder)
                {
                    _selectedSortPath = null;
                    _selectedSecondarySorts.Clear();
                    ReplaceCollection(SelectedItems, _selectedSource);
                    RefreshSortLevelBadges();
                    UpdateDirectionButtons();
                    return;
                }

                _selectedSortPath = option.Path;
                if (option.IsRetro)
                {
                    _selectedSortDirection = ListSortDirection.Ascending;
                }

                ApplyAchievementSort(SelectedItems, _selectedSortPath, _selectedSortDirection, AchievementSortScope.GameAchievements, _selectedSecondarySorts);
                RefreshSortLevelBadges();
                UpdateDirectionButtons();
            }
        }

        private void RefreshSortLevelBadges()
        {
            SetSortLevels(GamesOverviewGrid, _overviewSortPath, _overviewSortDirection, _overviewSecondarySorts);
            SetSortLevels(RecentPreviewGrid?.InternalDataGrid, _recentSortPath, _recentSortDirection, _recentSecondarySorts);
            SetSortLevels(AllPreviewGrid?.InternalDataGrid, _allSortPath, _allSortDirection, _allSecondarySorts);
            SetSortLevels(SelectedPreviewGrid?.InternalDataGrid, _selectedSortPath, _selectedSortDirection, _selectedSecondarySorts);
        }

        private static void SetSortLevels(
            DataGrid grid,
            string primaryPath,
            ListSortDirection primaryDirection,
            IReadOnlyList<(string Path, ListSortDirection Direction)> secondaries)
        {
            if (grid?.Columns == null)
            {
                return;
            }

            foreach (var column in grid.Columns)
            {
                if (column == null)
                {
                    continue;
                }

                column.SortDirection = null;
                SetColumnSortLevel(grid, column, null);
            }

            if (string.IsNullOrWhiteSpace(primaryPath))
            {
                return;
            }

            var primaryColumn = grid.Columns.FirstOrDefault(column => string.Equals(column?.SortMemberPath, primaryPath, StringComparison.Ordinal));
            if (primaryColumn != null)
            {
                primaryColumn.SortDirection = primaryDirection;
                SetColumnSortLevel(grid, primaryColumn, "1");
            }

            if (secondaries == null)
            {
                return;
            }

            for (var i = 0; i < secondaries.Count; i++)
            {
                var (path, direction) = secondaries[i];
                if (string.IsNullOrWhiteSpace(path) || string.Equals(path, primaryPath, StringComparison.Ordinal))
                {
                    continue;
                }

                var column = grid.Columns.FirstOrDefault(c => string.Equals(c?.SortMemberPath, path, StringComparison.Ordinal));
                if (column != null)
                {
                    column.SortDirection = direction;
                    SetColumnSortLevel(grid, column, (i + 2).ToString());
                }
            }
        }

        private static DataGridColumnHeader GetColumnHeader(DataGrid grid, DataGridColumn column)
        {
            if (grid == null || column == null)
            {
                return null;
            }

            return FindVisualChildren<DataGridColumnHeader>(grid)
                .FirstOrDefault(header => header.Column == column);
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
            {
                yield break;
            }

            var childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                {
                    yield return typedChild;
                }

                foreach (var descendant in FindVisualChildren<T>(child))
                {
                    yield return descendant;
                }
            }
        }

        private static void SetColumnSortLevel(DataGrid grid, DataGridColumn column, string level)
        {
            var header = GetColumnHeader(grid, column);
            if (header != null)
            {
                header.Tag = level;
            }
        }

        private void OverviewDirectionButton_Click(object sender, RoutedEventArgs e)
        {
            _overviewSortDirection = Toggle(_overviewSortDirection);
            UpdateDirectionButtons();
        }

        private void RecentDirectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsRetroSelected(RecentSortCombo))
            {
                return;
            }

            _recentSortDirection = Toggle(_recentSortDirection);
            UpdateDirectionButtons();
        }

        private void AllDirectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsRetroSelected(AllSortCombo))
            {
                return;
            }

            _allSortDirection = Toggle(_allSortDirection);
            UpdateDirectionButtons();
        }

        private void SelectedDirectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsRetroSelected(SelectedSortCombo))
            {
                return;
            }

            _selectedSortDirection = Toggle(_selectedSortDirection);
            UpdateDirectionButtons();
        }

        private static ListSortDirection Toggle(ListSortDirection direction)
        {
            return direction == ListSortDirection.Descending ? ListSortDirection.Ascending : ListSortDirection.Descending;
        }

        private void UpdateDirectionButtons()
        {
            SetDirectionGlyph(OverviewDirectionIcon, _overviewSortDirection);
            SetDirectionGlyph(RecentDirectionIcon, _recentSortDirection);
            SetDirectionGlyph(AllDirectionIcon, _allSortDirection);
            SetDirectionGlyph(SelectedDirectionIcon, _selectedSortDirection);

            OverviewDirectionButton.IsEnabled =
                OverviewSortCombo?.SelectedItem is SortOption overview && !overview.IsSourceOrder;
            RecentDirectionButton.IsEnabled =
                RecentSortCombo?.SelectedItem is SortOption recent &&
                !recent.IsSourceOrder &&
                !recent.IsRetro;
            AllDirectionButton.IsEnabled =
                AllSortCombo?.SelectedItem is SortOption all &&
                !all.IsSourceOrder &&
                !all.IsRetro;
            SelectedDirectionButton.IsEnabled =
                SelectedSortCombo?.SelectedItem is SortOption selected &&
                !selected.IsSourceOrder &&
                !selected.IsRetro;
        }

        private static bool IsRetroSelected(ComboBox combo)
        {
            return combo?.SelectedItem is SortOption option && option.IsRetro;
        }

        private static void SetDirectionGlyph(TextBlock icon, ListSortDirection direction)
        {
            if (icon != null)
            {
                icon.Text = direction == ListSortDirection.Descending ? "\uea66" : "\uea65";
            }
        }

        private static void UpdateSecondarySorts(
            List<(string Path, ListSortDirection Direction)> secondaries,
            string currentPrimaryPath,
            string newSortPath,
            ListSortDirection newDirection,
            bool isAdditive)
        {
            if (!isAdditive)
            {
                secondaries.Clear();
                return;
            }

            var existing = secondaries.FindIndex(s => string.Equals(s.Path, newSortPath, StringComparison.Ordinal));
            if (existing >= 0)
            {
                var (path, dir) = secondaries[existing];
                secondaries[existing] = (path, dir == ListSortDirection.Descending ? ListSortDirection.Ascending : ListSortDirection.Descending);
            }
            else if (!string.Equals(newSortPath, currentPrimaryPath, StringComparison.Ordinal))
            {
                secondaries.Add((newSortPath, newDirection));
            }
        }

        private static void ApplySecondarySorts(
            List<AchievementDisplayItem> items,
            IReadOnlyList<(string Path, ListSortDirection Direction)> secondaries,
            AchievementSortScope scope,
            string primaryPath,
            ListSortDirection primaryDirection)
        {
            if (items == null || items.Count == 0 || secondaries == null || secondaries.Count == 0)
            {
                return;
            }

            var secondaryComparisons = secondaries
                .Select(s => AchievementSortHelper.GetComparison(s.Path, s.Direction, scope))
                .Where(c => c != null)
                .ToList();
            if (secondaryComparisons.Count == 0)
            {
                return;
            }

            var primaryComparison = AchievementSortHelper.GetComparison(primaryPath, primaryDirection, scope);
            var indexed = items.Select((item, i) => (item, i)).ToList();
            indexed.Sort((x, y) =>
            {
                if (primaryComparison != null)
                {
                    var primary = primaryComparison(x.item, y.item);
                    if (primary != 0)
                    {
                        return primary;
                    }
                }

                foreach (var secondary in secondaryComparisons)
                {
                    var result = secondary(x.item, y.item);
                    if (result != 0)
                    {
                        return result;
                    }
                }

                return x.i.CompareTo(y.i);
            });

            items.Clear();
            items.AddRange(indexed.Select(entry => entry.item));
        }

        private static string SerializeSecondarySorts(IEnumerable<(string Path, ListSortDirection Direction)> secondaries)
        {
            if (secondaries == null)
            {
                return string.Empty;
            }

            return string.Join("|", secondaries
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
                .Select(entry => $"{entry.Path}:{(entry.Direction == ListSortDirection.Descending ? "Desc" : "Asc")}"));
        }

        private static List<(string Path, ListSortDirection Direction)> DeserializeSecondarySorts(string serialized)
        {
            var result = new List<(string Path, ListSortDirection Direction)>();
            if (string.IsNullOrWhiteSpace(serialized))
            {
                return result;
            }

            var parts = serialized.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var token = part.Trim();
                var separator = token.LastIndexOf(':');
                if (separator <= 0 || separator >= token.Length - 1)
                {
                    continue;
                }

                var path = token.Substring(0, separator).Trim();
                var directionToken = token.Substring(separator + 1).Trim();
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var direction = string.Equals(directionToken, "Desc", StringComparison.OrdinalIgnoreCase)
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending;
                result.Add((path, direction));
            }

            return result;
        }

        private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateDirectionButtons();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyCurrentSelectionStates();
            PersistSortSettings();            _saveSettings?.Invoke();
            DialogResult = true;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void ApplyCurrentSelectionStates()
        {
            if (OverviewSortCombo.SelectedItem is SortOption overview)
            {
                _overviewSortPath = overview.IsSourceOrder ? null : overview.Path;
                if (overview.IsSourceOrder)
                {
                    _overviewSecondarySorts.Clear();
                }
            }

            if (RecentSortCombo.SelectedItem is SortOption recent)
            {
                _recentSortPath = recent.IsSourceOrder ? null : recent.Path;
                if (recent.IsSourceOrder)
                {
                    _recentSecondarySorts.Clear();
                }
                if (recent.IsRetro)
                {
                    _recentSortDirection = ListSortDirection.Ascending;
                }
            }

            if (AllSortCombo.SelectedItem is SortOption all)
            {
                _allSortPath = all.IsSourceOrder ? null : all.Path;
                if (all.IsSourceOrder)
                {
                    _allSecondarySorts.Clear();
                }
                if (all.IsRetro)
                {
                    _allSortDirection = ListSortDirection.Ascending;
                }
            }

            if (SelectedSortCombo.SelectedItem is SortOption selected)
            {
                _selectedSortPath = selected.IsSourceOrder ? null : selected.Path;
                if (selected.IsSourceOrder)
                {
                    _selectedSecondarySorts.Clear();
                }
                if (selected.IsRetro)
                {
                    _selectedSortDirection = ListSortDirection.Ascending;
                }
            }
        }

        private void PersistSortSettings()
        {
            var persisted = _settings.Persisted;
            // Saving this dialog is an explicit request to use these four manual sort
            // configurations. Keep the selector and persisted paths atomic so a later
            // snapshot cannot apply the Display > Overview defaults instead.
            persisted.DefaultAchievementSortMode = CompactListSortMode.Custom;
            persisted.GamesOverviewCustomSortPath = _overviewSortPath;
            persisted.GamesOverviewCustomSortDescending = _overviewSortDirection == ListSortDirection.Descending;
            persisted.GamesOverviewCustomSecondarySorts = SerializeSecondarySorts(_overviewSecondarySorts);
            persisted.GamesOverviewCustomSortUsesSourceOrder = string.IsNullOrWhiteSpace(_overviewSortPath);

            persisted.RecentAchievementsCustomSortPath = _recentSortPath;
            persisted.RecentAchievementsCustomSortDescending = _recentSortDirection == ListSortDirection.Descending;
            persisted.RecentAchievementsCustomSecondarySorts = SerializeSecondarySorts(_recentSecondarySorts);
            persisted.RecentAchievementsCustomSortUsesSourceOrder = string.IsNullOrWhiteSpace(_recentSortPath);

            persisted.SidebarAllAchievementsCustomSortPath = _allSortPath;
            persisted.SidebarAllAchievementsCustomSortDescending = _allSortDirection == ListSortDirection.Descending;
            persisted.SidebarAllAchievementsCustomSecondarySorts = SerializeSecondarySorts(_allSecondarySorts);
            persisted.SidebarAllAchievementsCustomSortUsesSourceOrder = string.IsNullOrWhiteSpace(_allSortPath);

            persisted.SidebarSelectedGameCustomSortPath = _selectedSortPath;
            persisted.SidebarSelectedGameCustomSortDescending = _selectedSortDirection == ListSortDirection.Descending;
            persisted.SidebarSelectedGameCustomSecondarySorts = SerializeSecondarySorts(_selectedSecondarySorts);
            persisted.SidebarSelectedGameCustomSortUsesSourceOrder = string.IsNullOrWhiteSpace(_selectedSortPath);

            // Keep legacy field aligned so older custom call-sites still get valid manual state.
            persisted.CustomSortPath = _selectedSortPath;
            persisted.CustomSortDescending = _selectedSortDirection == ListSortDirection.Descending;
        }

        private void GamesOverviewGrid_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is DataGrid grid))
            {
                return;
            }

            var row = ItemsControl.ContainerFromElement(grid, e.OriginalSource as DependencyObject) as DataGridRow;
            if (row != null)
            {
                return;
            }

            e.Handled = true;
            var menu = _gamesOverviewPersistence?.BuildColumnVisibilityMenu();
            if (menu == null || menu.Items.Count == 0)
            {
                return;
            }

            menu.Placement = PlacementMode.RelativePoint;
            menu.PlacementTarget = grid;
            menu.HorizontalOffset = e.GetPosition(grid).X;
            menu.VerticalOffset = e.GetPosition(grid).Y;
            menu.IsOpen = true;
        }

        private static void ReplaceCollection<T>(ObservableCollection<T> collection, IEnumerable<T> values)
        {
            collection.Clear();
            foreach (var value in values)
            {
                collection.Add(value);
            }
        }

        public void Dispose()
        {
            _gamesOverviewPersistence?.Detach();
            _gamesOverviewPersistence = null;
        }
    }
}
