using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace PlayniteAchievements.Models.Settings
{
    public static class GridOptionKeys
    {
        public static class Achievement
        {
            public const string Default = "Default";
            public const string SingleGame = "SingleGame";
            public const string OverviewRecent = "OverviewRecent";
            public const string OverviewSelectedGame = "OverviewSelectedGame";
            public const string FriendsOverviewRecent = "FriendsOverviewRecent";
            public const string FriendsOverviewSelectedFriend = "FriendsOverviewSelectedFriend";
            public const string FriendsOverviewSelectedGame = "FriendsOverviewSelectedGame";
            public const string FriendsOverviewSelectedFriendGame = "FriendsOverviewSelectedFriendGame";
            public const string ViewFriendsAchievements = "ViewFriendsAchievements";
            public const string ViewFriendsAchievementsSelectedFriend = "ViewFriendsAchievementsSelectedFriend";
            public const string StartPageRecent = "StartPageRecent";
            public const string StartPageFriendAchievements = "StartPageFriendAchievements";
            public const string DesktopTheme = "DesktopTheme";
        }

        public static class GameSummaries
        {
            public const string Overview = "Overview";
            public const string StartPage = "StartPage";
            public const string ViewAchievements = "ViewAchievements";
            public const string FriendsOverview = "FriendsOverview";
            public const string FriendsOverviewSelectedFriend = "FriendsOverviewSelectedFriend";
            public const string ViewFriendsAchievements = "ViewFriendsAchievements";
            public const string ViewFriendsAchievementsSelectedFriend = "ViewFriendsAchievementsSelectedFriend";
        }

        public static class FriendSummaries
        {
            public const string FriendsOverview = "FriendsOverview";
            public const string ViewFriendsAchievements = "ViewFriendsAchievements";
        }

        public static class CategorySummaries
        {
            public const string ViewAchievements = "ViewAchievements";
            public const string OverviewSelectedGame = "OverviewSelectedGame";
            public const string FriendsOverview = "FriendsOverview";
            public const string ViewFriendsAchievements = "ViewFriendsAchievements";
            public const string DesktopTheme = "DesktopTheme";
        }
    }

    public sealed class GridColumnLayoutOptions : PlayniteAchievements.Common.ObservableObject
    {
        private Dictionary<string, bool> _visibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, double> _widths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GridAlignment> _cellAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GridVerticalAlignment> _cellVerticalAlignments = new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GridAlignment> _headerAlignments = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, bool> Visibility
        {
            get => _visibility;
            set => SetValue(ref _visibility, NormalizeVisibility(value));
        }

        public Dictionary<string, double> Widths
        {
            get => _widths;
            set => SetValue(ref _widths, NormalizeWidths(value));
        }

        public Dictionary<string, int> Order
        {
            get => _order;
            set => SetValue(ref _order, NormalizeOrder(value));
        }

        public Dictionary<string, GridAlignment> CellAlignments
        {
            get => _cellAlignments;
            set => SetValue(ref _cellAlignments, NormalizeAlignments(value));
        }

        public Dictionary<string, GridVerticalAlignment> CellVerticalAlignments
        {
            get => _cellVerticalAlignments;
            set => SetValue(ref _cellVerticalAlignments, NormalizeVerticalAlignments(value));
        }

        public Dictionary<string, GridAlignment> HeaderAlignments
        {
            get => _headerAlignments;
            set => SetValue(ref _headerAlignments, NormalizeAlignments(value));
        }

        public GridColumnLayoutOptions Clone()
        {
            return new GridColumnLayoutOptions
            {
                Visibility = Visibility,
                Widths = Widths,
                Order = Order,
                CellAlignments = CellAlignments,
                CellVerticalAlignments = CellVerticalAlignments,
                HeaderAlignments = HeaderAlignments
            };
        }

        public static GridColumnLayoutOptions CreateWithProgressRightAlignment()
        {
            var options = new GridColumnLayoutOptions();
            options.CellAlignments[PersistedSettings.ProgressColumnKey] = GridAlignment.Right;
            return options;
        }

        internal static Dictionary<string, bool> NormalizeVisibility(Dictionary<string, bool> value)
        {
            return value != null
                ? new Dictionary<string, bool>(value, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        internal static Dictionary<string, double> NormalizeWidths(Dictionary<string, double> value)
        {
            var normalized = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (value == null)
            {
                return normalized;
            }

            foreach (var pair in value)
            {
                var key = (pair.Key ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(key) &&
                    !double.IsNaN(pair.Value) &&
                    !double.IsInfinity(pair.Value) &&
                    pair.Value > 0)
                {
                    normalized[key] = pair.Value;
                }
            }

            return normalized;
        }

        internal static Dictionary<string, int> NormalizeOrder(Dictionary<string, int> value)
        {
            var normalized = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (value == null)
            {
                return normalized;
            }

            foreach (var pair in value)
            {
                var key = (pair.Key ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(key) && pair.Value >= 0)
                {
                    normalized[key] = pair.Value;
                }
            }

            return normalized;
        }

        internal static Dictionary<string, GridAlignment> NormalizeAlignments(Dictionary<string, GridAlignment> value)
        {
            var normalized = new Dictionary<string, GridAlignment>(StringComparer.OrdinalIgnoreCase);
            if (value == null)
            {
                return normalized;
            }

            foreach (var pair in value)
            {
                var key = (pair.Key ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(key) && Enum.IsDefined(typeof(GridAlignment), pair.Value))
                {
                    normalized[key] = pair.Value;
                }
            }

            return normalized;
        }

        internal static Dictionary<string, GridVerticalAlignment> NormalizeVerticalAlignments(
            Dictionary<string, GridVerticalAlignment> value)
        {
            var normalized = new Dictionary<string, GridVerticalAlignment>(StringComparer.OrdinalIgnoreCase);
            if (value == null)
            {
                return normalized;
            }

            foreach (var pair in value)
            {
                var key = (pair.Key ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(key) && Enum.IsDefined(typeof(GridVerticalAlignment), pair.Value))
                {
                    normalized[key] = pair.Value;
                }
            }

            return normalized;
        }
    }

    public class GridCommonOptions : PlayniteAchievements.Common.ObservableObject
    {
        private bool _showColumnHeaders = true;
        private bool _showControlBar = true;
        private double? _rowHeight;
        private int? _maxRows;
        private GridColumnLayoutOptions _columns = new GridColumnLayoutOptions();

        public bool ShowColumnHeaders
        {
            get => _showColumnHeaders;
            set => SetValue(ref _showColumnHeaders, value);
        }

        public bool ShowControlBar
        {
            get => _showControlBar;
            set => SetValue(ref _showControlBar, value);
        }

        public double? RowHeight
        {
            get => _rowHeight;
            set => SetValue(ref _rowHeight, PersistedSettings.NormalizeGridRowHeight(value));
        }

        public int? MaxRows
        {
            get => _maxRows;
            set => SetValue(ref _maxRows, PersistedSettings.NormalizeGridMaxRows(value));
        }

        public GridColumnLayoutOptions Columns
        {
            get => _columns ?? (_columns = new GridColumnLayoutOptions());
            set => SetValue(ref _columns, value ?? new GridColumnLayoutOptions());
        }

        protected void CopyCommonTo(GridCommonOptions target)
        {
            target.ShowColumnHeaders = ShowColumnHeaders;
            target.ShowControlBar = ShowControlBar;
            target.RowHeight = RowHeight;
            target.MaxRows = MaxRows;
            target.Columns = Columns?.Clone() ?? new GridColumnLayoutOptions();
        }
    }

    public sealed class AchievementGridOptions : GridCommonOptions
    {
        private bool _useCoverImages;
        private bool _showRarityGlow = true;
        private bool _colorNamesByRarity;
        private bool _colorRarityColumnsByRarity;
        private DateDisplayMode _unlockDateMode = DateDisplayMode.DateAndTime;
        private CompactListSortMode _sortMode = CompactListSortMode.UnlockTime;
        private bool _sortDescending = true;
        private double? _maxHeight;
        private bool _startInCategoryMode;
        private bool _hideCategorySummaryRow;

        public bool UseCoverImages
        {
            get => _useCoverImages;
            set => SetValue(ref _useCoverImages, value);
        }

        public bool ShowRarityGlow
        {
            get => _showRarityGlow;
            set => SetValue(ref _showRarityGlow, value);
        }

        public bool ColorNamesByRarity
        {
            get => _colorNamesByRarity;
            set => SetValue(ref _colorNamesByRarity, value);
        }

        public bool ColorRarityColumnsByRarity
        {
            get => _colorRarityColumnsByRarity;
            set => SetValue(ref _colorRarityColumnsByRarity, value);
        }

        public DateDisplayMode UnlockDateMode
        {
            get => _unlockDateMode;
            set => SetValue(ref _unlockDateMode, value);
        }

        public CompactListSortMode SortMode
        {
            get => _sortMode;
            set => SetValue(ref _sortMode, value);
        }

        public bool SortDescending
        {
            get => _sortDescending;
            set => SetValue(ref _sortDescending, value);
        }

        public double? MaxHeight
        {
            get => _maxHeight;
            set => SetValue(ref _maxHeight, value);
        }

        public bool StartInCategoryMode
        {
            get => _startInCategoryMode;
            set => SetValue(ref _startInCategoryMode, value);
        }

        // When true, the in-grid category summary row hides once a category is selected, leaving
        // the achievement list full vertical space; a breadcrumb header becomes the only way back.
        public bool HideCategorySummaryRow
        {
            get => _hideCategorySummaryRow;
            set => SetValue(ref _hideCategorySummaryRow, value);
        }

        public AchievementGridOptions Clone()
        {
            var clone = new AchievementGridOptions();
            CopyCommonTo(clone);
            clone.UseCoverImages = UseCoverImages;
            clone.ShowRarityGlow = ShowRarityGlow;
            clone.ColorNamesByRarity = ColorNamesByRarity;
            clone.ColorRarityColumnsByRarity = ColorRarityColumnsByRarity;
            clone.UnlockDateMode = UnlockDateMode;
            clone.SortMode = SortMode;
            clone.SortDescending = SortDescending;
            clone.MaxHeight = MaxHeight;
            clone.StartInCategoryMode = StartInCategoryMode;
            clone.HideCategorySummaryRow = HideCategorySummaryRow;
            return clone;
        }
    }

    public sealed class GameSummaryGridOptions : GridCommonOptions
    {
        private bool _useCoverImages = true;
        private bool _showMetadataPlatform = true;
        private bool _showMetadataPlaytime = true;
        private bool _showMetadataRegion = true;
        private bool _showCompletionGlow = true;
        private bool _colorRarityColumnsByRarity;
        private bool _showNameAboveProgress;
        private DateDisplayMode _lastPlayedDateMode = DateDisplayMode.DateAndTime;
        private GameSummariesSortMode _sortMode = GameSummariesSortMode.RecentUnlock;
        private bool _sortDescending = true;

        public bool UseCoverImages
        {
            get => _useCoverImages;
            set => SetValue(ref _useCoverImages, value);
        }

        public bool ShowMetadataPlatform
        {
            get => _showMetadataPlatform;
            set => SetValue(ref _showMetadataPlatform, value);
        }

        public bool ShowMetadataPlaytime
        {
            get => _showMetadataPlaytime;
            set => SetValue(ref _showMetadataPlaytime, value);
        }

        public bool ShowMetadataRegion
        {
            get => _showMetadataRegion;
            set => SetValue(ref _showMetadataRegion, value);
        }

        public bool ShowCompletionGlow
        {
            get => _showCompletionGlow;
            set => SetValue(ref _showCompletionGlow, value);
        }

        // Colors the progress-footer badge count labels with the tier's configured rarity color,
        // mirroring the achievement grids' ColorRarityColumnsByRarity option.
        public bool ColorRarityColumnsByRarity
        {
            get => _colorRarityColumnsByRarity;
            set => SetValue(ref _colorRarityColumnsByRarity, value);
        }

        // Renders the game name above the progress bar inside the progress cell.
        public bool ShowNameAboveProgress
        {
            get => _showNameAboveProgress;
            set => SetValue(ref _showNameAboveProgress, value);
        }

        public DateDisplayMode LastPlayedDateMode
        {
            get => _lastPlayedDateMode;
            set => SetValue(ref _lastPlayedDateMode, value);
        }

        public GameSummariesSortMode SortMode
        {
            get => _sortMode;
            set => SetValue(ref _sortMode, value);
        }

        public bool SortDescending
        {
            get => _sortDescending;
            set => SetValue(ref _sortDescending, value);
        }

        public GameSummaryGridOptions Clone()
        {
            var clone = new GameSummaryGridOptions();
            CopyCommonTo(clone);
            clone.UseCoverImages = UseCoverImages;
            clone.ShowMetadataPlatform = ShowMetadataPlatform;
            clone.ShowMetadataPlaytime = ShowMetadataPlaytime;
            clone.ShowMetadataRegion = ShowMetadataRegion;
            clone.ShowCompletionGlow = ShowCompletionGlow;
            clone.ColorRarityColumnsByRarity = ColorRarityColumnsByRarity;
            clone.ShowNameAboveProgress = ShowNameAboveProgress;
            clone.LastPlayedDateMode = LastPlayedDateMode;
            clone.SortMode = SortMode;
            clone.SortDescending = SortDescending;
            return clone;
        }
    }

    public sealed class FriendSummaryGridOptions : GridCommonOptions
    {
        private DateDisplayMode _lastUnlockDateMode = DateDisplayMode.DateAndTime;
        private FriendSummariesSortMode _sortMode = FriendSummariesSortMode.RecentUnlock;
        private bool _sortDescending = true;

        public DateDisplayMode LastUnlockDateMode
        {
            get => _lastUnlockDateMode;
            set => SetValue(ref _lastUnlockDateMode, value);
        }

        public FriendSummariesSortMode SortMode
        {
            get => _sortMode;
            set => SetValue(ref _sortMode, value);
        }

        public bool SortDescending
        {
            get => _sortDescending;
            set => SetValue(ref _sortDescending, value);
        }

        public FriendSummaryGridOptions Clone()
        {
            var clone = new FriendSummaryGridOptions();
            CopyCommonTo(clone);
            clone.LastUnlockDateMode = LastUnlockDateMode;
            clone.SortMode = SortMode;
            clone.SortDescending = SortDescending;
            return clone;
        }
    }

    public sealed class CategorySummaryGridOptions : PlayniteAchievements.Common.ObservableObject
    {
        private GridColumnLayoutOptions _columns = GridColumnLayoutOptions.CreateWithProgressRightAlignment();
        private bool _showColumnHeaders = true;
        private double? _rowHeight;
        private bool _useCoverImages;
        private bool _showCompletionGlow = true;
        private bool _colorRarityColumnsByRarity;
        private bool _showNameAboveProgress;

        public GridColumnLayoutOptions Columns
        {
            get => _columns ?? (_columns = GridColumnLayoutOptions.CreateWithProgressRightAlignment());
            set => SetValue(ref _columns, value ?? GridColumnLayoutOptions.CreateWithProgressRightAlignment());
        }

        public bool ShowColumnHeaders
        {
            get => _showColumnHeaders;
            set => SetValue(ref _showColumnHeaders, value);
        }

        public double? RowHeight
        {
            get => _rowHeight;
            set => SetValue(ref _rowHeight, PersistedSettings.NormalizeGridRowHeight(value));
        }

        // Selects the fallback for categories without art: game cover when true, game icon
        // when false. Category art itself always wins in both modes.
        public bool UseCoverImages
        {
            get => _useCoverImages;
            set => SetValue(ref _useCoverImages, value);
        }

        public bool ShowCompletionGlow
        {
            get => _showCompletionGlow;
            set => SetValue(ref _showCompletionGlow, value);
        }

        // Colors the progress-footer badge count labels with the tier's configured rarity color,
        // mirroring the achievement grids' ColorRarityColumnsByRarity option.
        public bool ColorRarityColumnsByRarity
        {
            get => _colorRarityColumnsByRarity;
            set => SetValue(ref _colorRarityColumnsByRarity, value);
        }

        // Renders the category name above the progress bar inside the progress cell.
        public bool ShowNameAboveProgress
        {
            get => _showNameAboveProgress;
            set => SetValue(ref _showNameAboveProgress, value);
        }

        public CategorySummaryGridOptions Clone()
        {
            return new CategorySummaryGridOptions
            {
                Columns = Columns?.Clone() ?? GridColumnLayoutOptions.CreateWithProgressRightAlignment(),
                ShowColumnHeaders = ShowColumnHeaders,
                RowHeight = RowHeight,
                UseCoverImages = UseCoverImages,
                ShowCompletionGlow = ShowCompletionGlow,
                ColorRarityColumnsByRarity = ColorRarityColumnsByRarity,
                ShowNameAboveProgress = ShowNameAboveProgress
            };
        }
    }

    public sealed class GridOptionsCatalog : PlayniteAchievements.Common.ObservableObject
    {
        internal const string AchievementKindName = "Achievement";
        internal const string GameSummariesKindName = "GameSummaries";
        internal const string FriendSummariesKindName = "FriendSummaries";
        internal const string CategorySummariesKindName = "CategorySummaries";

        private Dictionary<string, AchievementGridOptions> _achievement =
            new Dictionary<string, AchievementGridOptions>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, GameSummaryGridOptions> _gameSummaries =
            new Dictionary<string, GameSummaryGridOptions>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, FriendSummaryGridOptions> _friendSummaries =
            new Dictionary<string, FriendSummaryGridOptions>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, CategorySummaryGridOptions> _categorySummaries =
            new Dictionary<string, CategorySummaryGridOptions>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<PlayniteAchievements.Common.ObservableObject, PropertyChangedEventHandler> _optionSubscriptions =
            new Dictionary<PlayniteAchievements.Common.ObservableObject, PropertyChangedEventHandler>();

        /// <summary>
        /// Raised when a property of any option object stored in the catalog changes.
        /// Carries (kind name, surface key, member name). Kind names are
        /// <see cref="AchievementKindName"/>, <see cref="GameSummariesKindName"/>,
        /// <see cref="FriendSummariesKindName"/> and <see cref="CategorySummariesKindName"/>.
        /// </summary>
        internal event Action<string, string, string> OptionChanged;

        public GridOptionsCatalog()
        {
            EnsureDefaults();
        }

        public Dictionary<string, AchievementGridOptions> Achievement
        {
            get
            {
                EnsureDefaults();
                return _achievement;
            }
            set
            {
                DetachOptions(_achievement);
                SetValue(ref _achievement, Normalize(value, item => item?.Clone()));
                EnsureDefaults();
            }
        }

        public Dictionary<string, GameSummaryGridOptions> GameSummaries
        {
            get
            {
                EnsureDefaults();
                return _gameSummaries;
            }
            set
            {
                DetachOptions(_gameSummaries);
                SetValue(ref _gameSummaries, Normalize(value, item => item?.Clone()));
                EnsureDefaults();
            }
        }

        public Dictionary<string, FriendSummaryGridOptions> FriendSummaries
        {
            get
            {
                EnsureDefaults();
                return _friendSummaries;
            }
            set
            {
                DetachOptions(_friendSummaries);
                SetValue(ref _friendSummaries, Normalize(value, item => item?.Clone()));
                EnsureDefaults();
            }
        }

        public Dictionary<string, CategorySummaryGridOptions> CategorySummaries
        {
            get
            {
                EnsureDefaults();
                return _categorySummaries;
            }
            set
            {
                DetachOptions(_categorySummaries);
                SetValue(ref _categorySummaries, Normalize(value, item => item?.Clone()));
                EnsureDefaults();
            }
        }

        public AchievementGridOptions GetAchievement(string id)
        {
            var key = string.IsNullOrWhiteSpace(id) ? GridOptionKeys.Achievement.Default : id;
            EnsureDefaults();
            if (!_achievement.TryGetValue(key, out var options) || options == null)
            {
                options = CreateDefaultAchievement(key);
                _achievement[key] = options;
                AttachOptions(AchievementKindName, key, options);
            }

            return options;
        }

        public GameSummaryGridOptions GetGameSummaries(string id)
        {
            var key = string.IsNullOrWhiteSpace(id) ? GridOptionKeys.GameSummaries.Overview : id;
            EnsureDefaults();
            if (!_gameSummaries.TryGetValue(key, out var options) || options == null)
            {
                options = CreateDefaultGameSummaries(key);
                _gameSummaries[key] = options;
                AttachOptions(GameSummariesKindName, key, options);
            }

            return options;
        }

        public FriendSummaryGridOptions GetFriendSummaries(string id)
        {
            var key = string.IsNullOrWhiteSpace(id) ? GridOptionKeys.FriendSummaries.FriendsOverview : id;
            EnsureDefaults();
            if (!_friendSummaries.TryGetValue(key, out var options) || options == null)
            {
                options = CreateDefaultFriendSummaries(key);
                _friendSummaries[key] = options;
                AttachOptions(FriendSummariesKindName, key, options);
            }

            return options;
        }

        public CategorySummaryGridOptions GetCategorySummaries(string id)
        {
            var key = string.IsNullOrWhiteSpace(id) ? GridOptionKeys.CategorySummaries.ViewAchievements : id;
            EnsureDefaults();
            if (!_categorySummaries.TryGetValue(key, out var options) || options == null)
            {
                options = new CategorySummaryGridOptions();
                _categorySummaries[key] = options;
                AttachOptions(CategorySummariesKindName, key, options);
            }

            return options;
        }

        public GridOptionsCatalog Clone()
        {
            return new GridOptionsCatalog
            {
                Achievement = Achievement,
                GameSummaries = GameSummaries,
                FriendSummaries = FriendSummaries,
                CategorySummaries = CategorySummaries
            };
        }

        public static string ResolveAchievementId(string columnSettingsKey)
        {
            switch (columnSettingsKey)
            {
                case "DesktopTheme":
                    return GridOptionKeys.Achievement.DesktopTheme;
                case "SingleGame":
                    return GridOptionKeys.Achievement.SingleGame;
                case "OverviewRecentAchievements":
                case "Overview":
                    return GridOptionKeys.Achievement.OverviewRecent;
                case "FriendsOverviewRecentAchievements":
                    return GridOptionKeys.Achievement.FriendsOverviewRecent;
                case "FriendsOverviewSelectedFriendAchievements":
                    return GridOptionKeys.Achievement.FriendsOverviewSelectedFriend;
                case "FriendsOverviewSelectedGameAchievements":
                    return GridOptionKeys.Achievement.FriendsOverviewSelectedGame;
                case "FriendsOverviewSelectedFriendGameAchievements":
                    return GridOptionKeys.Achievement.FriendsOverviewSelectedFriendGame;
                case "ViewFriendsAchievements":
                case "ViewFriendsAchievementsAchievements":
                    return GridOptionKeys.Achievement.ViewFriendsAchievements;
                case "ViewFriendsAchievementsSelectedFriendAchievements":
                    return GridOptionKeys.Achievement.ViewFriendsAchievementsSelectedFriend;
                case "OverviewSelectedGameAchievements":
                case "OverviewGame":
                    return GridOptionKeys.Achievement.OverviewSelectedGame;
                case "StartPageAchievements":
                    return GridOptionKeys.Achievement.StartPageRecent;
                case "StartPageFriendAchievements":
                    return GridOptionKeys.Achievement.StartPageFriendAchievements;
                default:
                    return GridOptionKeys.Achievement.Default;
            }
        }

        public static string ResolveFriendSummariesId(string columnSettingsKey)
        {
            switch (columnSettingsKey)
            {
                case "ViewFriendsAchievementsFriends":
                    return GridOptionKeys.FriendSummaries.ViewFriendsAchievements;
                case "FriendsOverviewFriendSummaries":
                default:
                    return GridOptionKeys.FriendSummaries.FriendsOverview;
            }
        }

        public static string ResolveGameSummariesId(string columnSettingsKey)
        {
            switch (columnSettingsKey)
            {
                case "StartPageGameSummaries":
                case "StartPageOverview":
                    return GridOptionKeys.GameSummaries.StartPage;
                case "ViewAchievementsGameSummaries":
                    return GridOptionKeys.GameSummaries.ViewAchievements;
                case "FriendsOverviewGameSummaries":
                    return GridOptionKeys.GameSummaries.FriendsOverview;
                case "FriendsOverviewSelectedFriendGameSummaries":
                    return GridOptionKeys.GameSummaries.FriendsOverviewSelectedFriend;
                case "ViewFriendsAchievementsGameSummaries":
                    return GridOptionKeys.GameSummaries.ViewFriendsAchievements;
                case "ViewFriendsAchievementsSelectedFriendGameSummaries":
                    return GridOptionKeys.GameSummaries.ViewFriendsAchievementsSelectedFriend;
                default:
                    return GridOptionKeys.GameSummaries.Overview;
            }
        }

        public static string ResolveCategorySummariesId(string columnSettingsKey)
        {
            switch (columnSettingsKey)
            {
                case "OverviewSelectedGameCategorySummaries":
                case "OverviewGameCategorySummaries":
                    return GridOptionKeys.CategorySummaries.OverviewSelectedGame;
                case "FriendsOverviewCategorySummaries":
                case "FriendsOverviewRecentAchievementsCategorySummaries":
                    return GridOptionKeys.CategorySummaries.FriendsOverview;
                case "ViewFriendsAchievementsCategorySummaries":
                    return GridOptionKeys.CategorySummaries.ViewFriendsAchievements;
                case "DesktopThemeCategorySummaries":
                    return GridOptionKeys.CategorySummaries.DesktopTheme;
                case "ViewAchievementsCategorySummaries":
                case "SingleGameCategorySummaries":
                default:
                    return GridOptionKeys.CategorySummaries.ViewAchievements;
            }
        }

        private void EnsureDefaults()
        {
            Ensure(_achievement, GridOptionKeys.Achievement.Default, () => CreateDefaultAchievement(GridOptionKeys.Achievement.Default));
            Ensure(_achievement, GridOptionKeys.Achievement.SingleGame, () => CreateDefaultAchievement(GridOptionKeys.Achievement.SingleGame));
            Ensure(_achievement, GridOptionKeys.Achievement.OverviewRecent, () => CreateDefaultAchievement(GridOptionKeys.Achievement.OverviewRecent));
            Ensure(_achievement, GridOptionKeys.Achievement.OverviewSelectedGame, () => CreateDefaultAchievement(GridOptionKeys.Achievement.OverviewSelectedGame));
            Ensure(_achievement, GridOptionKeys.Achievement.FriendsOverviewRecent, () => CreateDefaultAchievement(GridOptionKeys.Achievement.FriendsOverviewRecent));
            Ensure(_achievement, GridOptionKeys.Achievement.FriendsOverviewSelectedFriend, () => CreateDefaultAchievement(GridOptionKeys.Achievement.FriendsOverviewSelectedFriend));
            Ensure(_achievement, GridOptionKeys.Achievement.FriendsOverviewSelectedGame, () => CreateDefaultAchievement(GridOptionKeys.Achievement.FriendsOverviewSelectedGame));
            Ensure(_achievement, GridOptionKeys.Achievement.FriendsOverviewSelectedFriendGame, () => CreateDefaultAchievement(GridOptionKeys.Achievement.FriendsOverviewSelectedFriendGame));
            Ensure(_achievement, GridOptionKeys.Achievement.ViewFriendsAchievements, () => CreateDefaultAchievement(GridOptionKeys.Achievement.ViewFriendsAchievements));
            Ensure(_achievement, GridOptionKeys.Achievement.ViewFriendsAchievementsSelectedFriend, () => CreateDefaultAchievement(GridOptionKeys.Achievement.ViewFriendsAchievementsSelectedFriend));
            Ensure(_achievement, GridOptionKeys.Achievement.StartPageRecent, () => CreateDefaultAchievement(GridOptionKeys.Achievement.StartPageRecent));
            Ensure(_achievement, GridOptionKeys.Achievement.StartPageFriendAchievements, () => CreateDefaultAchievement(GridOptionKeys.Achievement.StartPageFriendAchievements));
            Ensure(_achievement, GridOptionKeys.Achievement.DesktopTheme, () => CreateDefaultAchievement(GridOptionKeys.Achievement.DesktopTheme));

            Ensure(_gameSummaries, GridOptionKeys.GameSummaries.Overview, () => CreateDefaultGameSummaries(GridOptionKeys.GameSummaries.Overview));
            Ensure(_gameSummaries, GridOptionKeys.GameSummaries.StartPage, () => CreateDefaultGameSummaries(GridOptionKeys.GameSummaries.StartPage));
            Ensure(_gameSummaries, GridOptionKeys.GameSummaries.ViewAchievements, () => CreateDefaultGameSummaries(GridOptionKeys.GameSummaries.ViewAchievements));
            Ensure(_gameSummaries, GridOptionKeys.GameSummaries.FriendsOverview, () => CreateDefaultGameSummaries(GridOptionKeys.GameSummaries.FriendsOverview));
            Ensure(_gameSummaries, GridOptionKeys.GameSummaries.FriendsOverviewSelectedFriend, () => CreateDefaultGameSummaries(GridOptionKeys.GameSummaries.FriendsOverviewSelectedFriend));
            Ensure(_gameSummaries, GridOptionKeys.GameSummaries.ViewFriendsAchievements, () => CreateDefaultGameSummaries(GridOptionKeys.GameSummaries.ViewFriendsAchievements));
            Ensure(_gameSummaries, GridOptionKeys.GameSummaries.ViewFriendsAchievementsSelectedFriend, () => CreateDefaultGameSummaries(GridOptionKeys.GameSummaries.ViewFriendsAchievementsSelectedFriend));

            Ensure(_friendSummaries, GridOptionKeys.FriendSummaries.FriendsOverview, () => CreateDefaultFriendSummaries(GridOptionKeys.FriendSummaries.FriendsOverview));
            Ensure(_friendSummaries, GridOptionKeys.FriendSummaries.ViewFriendsAchievements, () => CreateDefaultFriendSummaries(GridOptionKeys.FriendSummaries.ViewFriendsAchievements));

            Ensure(_categorySummaries, GridOptionKeys.CategorySummaries.ViewAchievements, () => new CategorySummaryGridOptions());
            Ensure(_categorySummaries, GridOptionKeys.CategorySummaries.OverviewSelectedGame, () => new CategorySummaryGridOptions());
            Ensure(_categorySummaries, GridOptionKeys.CategorySummaries.FriendsOverview, () => new CategorySummaryGridOptions());
            Ensure(_categorySummaries, GridOptionKeys.CategorySummaries.ViewFriendsAchievements, () => new CategorySummaryGridOptions());
            Ensure(_categorySummaries, GridOptionKeys.CategorySummaries.DesktopTheme, () => new CategorySummaryGridOptions());

            RefreshOptionSubscriptions();
        }

        /// <summary>
        /// Subscribes to every option object currently stored in the catalog and prunes
        /// subscriptions for objects that were replaced (e.g. by in-place JSON population).
        /// Idempotent; runs after every <see cref="EnsureDefaults"/> so objects that enter the
        /// catalog through any path are observed.
        /// </summary>
        private void RefreshOptionSubscriptions()
        {
            AttachOptionsAll(AchievementKindName, _achievement);
            AttachOptionsAll(GameSummariesKindName, _gameSummaries);
            AttachOptionsAll(FriendSummariesKindName, _friendSummaries);
            AttachOptionsAll(CategorySummariesKindName, _categorySummaries);

            var liveCount = _achievement.Count + _gameSummaries.Count + _friendSummaries.Count + _categorySummaries.Count;
            if (_optionSubscriptions.Count > liveCount)
            {
                PruneStaleSubscriptions();
            }
        }

        private void AttachOptionsAll<T>(string kindName, Dictionary<string, T> options)
            where T : PlayniteAchievements.Common.ObservableObject
        {
            foreach (var pair in options)
            {
                AttachOptions(kindName, pair.Key, pair.Value);
            }
        }

        private void AttachOptions(string kindName, string surfaceKey, PlayniteAchievements.Common.ObservableObject options)
        {
            if (options == null || _optionSubscriptions.ContainsKey(options))
            {
                return;
            }

            PropertyChangedEventHandler handler = (sender, e) => OptionChanged?.Invoke(kindName, surfaceKey, e?.PropertyName);
            options.PropertyChanged += handler;
            _optionSubscriptions[options] = handler;
        }

        private void DetachOptions<T>(Dictionary<string, T> options)
            where T : PlayniteAchievements.Common.ObservableObject
        {
            foreach (var value in options.Values)
            {
                DetachOptions(value);
            }
        }

        private void DetachOptions(PlayniteAchievements.Common.ObservableObject options)
        {
            if (options != null && _optionSubscriptions.TryGetValue(options, out var handler))
            {
                options.PropertyChanged -= handler;
                _optionSubscriptions.Remove(options);
            }
        }

        private void PruneStaleSubscriptions()
        {
            var live = new HashSet<PlayniteAchievements.Common.ObservableObject>();
            foreach (var value in _achievement.Values) { live.Add(value); }
            foreach (var value in _gameSummaries.Values) { live.Add(value); }
            foreach (var value in _friendSummaries.Values) { live.Add(value); }
            foreach (var value in _categorySummaries.Values) { live.Add(value); }

            foreach (var stale in _optionSubscriptions.Keys.Where(key => !live.Contains(key)).ToList())
            {
                DetachOptions(stale);
            }
        }

        private static AchievementGridOptions CreateDefaultAchievement(string key)
        {
            var options = new AchievementGridOptions();
            if (string.Equals(key, GridOptionKeys.Achievement.Default, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, GridOptionKeys.Achievement.DesktopTheme, StringComparison.OrdinalIgnoreCase))
            {
                options.MaxHeight = PersistedSettings.DefaultAchievementDataGridMaxHeight;
            }

            if (string.Equals(key, GridOptionKeys.Achievement.StartPageRecent, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, GridOptionKeys.Achievement.StartPageFriendAchievements, StringComparison.OrdinalIgnoreCase))
            {
                options.MaxRows = PersistedSettings.DefaultStartPageGridMaxRows;
                options.ShowControlBar = false;
            }

            return options;
        }

        private static GameSummaryGridOptions CreateDefaultGameSummaries(string key)
        {
            var options = new GameSummaryGridOptions
            {
                Columns = GridColumnLayoutOptions.CreateWithProgressRightAlignment()
            };

            if (string.Equals(key, GridOptionKeys.GameSummaries.StartPage, StringComparison.OrdinalIgnoreCase))
            {
                options.MaxRows = PersistedSettings.DefaultStartPageGridMaxRows;
                options.ShowControlBar = false;
            }
            else if (string.Equals(key, GridOptionKeys.GameSummaries.ViewAchievements, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(key, GridOptionKeys.GameSummaries.ViewFriendsAchievements, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(key, GridOptionKeys.GameSummaries.ViewFriendsAchievementsSelectedFriend, StringComparison.OrdinalIgnoreCase))
            {
                options.UseCoverImages = false;
            }

            return options;
        }

        private static FriendSummaryGridOptions CreateDefaultFriendSummaries(string key)
        {
            return new FriendSummaryGridOptions();
        }

        private static void Ensure<T>(Dictionary<string, T> dictionary, string key, Func<T> factory)
            where T : class
        {
            if (!dictionary.TryGetValue(key, out var existing) || existing == null)
            {
                dictionary[key] = factory();
            }
        }

        private static Dictionary<string, T> Normalize<T>(Dictionary<string, T> value, Func<T, T> clone)
            where T : class
        {
            var normalized = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            if (value == null)
            {
                return normalized;
            }

            foreach (var pair in value)
            {
                var key = (pair.Key ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(key) || pair.Value == null)
                {
                    continue;
                }

                normalized[key] = clone(pair.Value);
            }

            return normalized;
        }
    }
}
