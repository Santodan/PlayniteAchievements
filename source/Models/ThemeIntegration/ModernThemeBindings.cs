using System.Collections.Generic;
using System.Collections.ObjectModel;
using System;
using System.Linq;
using Playnite.SDK.Data;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.Models.ThemeIntegration
{
    /// <summary>
    /// Modern PlayniteAchievements binding surface.
    /// Runtime-only and populated from a shared theme runtime state.
    /// </summary>
    public class ModernThemeBindings : ObservableObject
    {
        [DontSerialize]
        private bool _hasAchievements;
        [DontSerialize]
        private Guid? _selectedGameId;
        [DontSerialize]
        private bool _hasCustomAchievementOrder;
        [DontSerialize]
        private bool _isCompleted;
        [DontSerialize]
        private int _achievementCount;
        [DontSerialize]
        private int _unlockedCount;
        [DontSerialize]
        private int _lockedCount;
        [DontSerialize]
        private double _progressPercentage;
        [DontSerialize]
        private AchievementRarityStats _common = new AchievementRarityStats();
        [DontSerialize]
        private AchievementRarityStats _uncommon = new AchievementRarityStats();
        [DontSerialize]
        private AchievementRarityStats _rare = new AchievementRarityStats();
        [DontSerialize]
        private AchievementRarityStats _ultraRare = new AchievementRarityStats();
        [DontSerialize]
        private AchievementRarityStats _rareAndUltraRare = new AchievementRarityStats();
        [DontSerialize]
        private List<AchievementDetail> _allAchievements = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _achievementDefaultOrder = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _achievementsNewestFirst = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _achievementsOldestFirst = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _achievementsRarityAsc = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _achievementsRarityDesc = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _dynamicAchievements = new List<AchievementDetail>();
        [DontSerialize]
        private string _dynamicAchievementsGameKey = string.Empty;
        [DontSerialize]
        private string _dynamicAchievementsGameLabel = string.Empty;
        [DontSerialize]
        private string _dynamicAchievementsFilterKey = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicAchievementsFilterLabel = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicAchievementsCategoryLabelFilterKey = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicAchievementsCategoryLabelFilterLabel = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicAchievementsSortKey = DynamicThemeViewKeys.UnlockTime;
        [DontSerialize]
        private string _dynamicAchievementsSortLabel = DynamicThemeViewKeys.UnlockTime;
        [DontSerialize]
        private string _dynamicAchievementsSortDirectionKey = DynamicThemeViewKeys.Descending;
        [DontSerialize]
        private string _dynamicAchievementsSortDirectionLabel = DynamicThemeViewKeys.Descending;
        [DontSerialize]
        private string _dynamicAchievementsDefaultFilterKey = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicAchievementsDefaultSortKey = DynamicThemeViewKeys.UnlockTime;
        [DontSerialize]
        private string _dynamicAchievementsDefaultSortDirectionKey = DynamicThemeViewKeys.Descending;
        [DontSerialize]
        private List<AchievementDisplayItem> _allAchievementDisplayItems;
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicAchievementsFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicAchievementsSortOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicAchievementsSortDirectionOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicAchievementGameOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicAchievementStatusFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicAchievementProgressFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicAchievementRarityFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicAchievementTrophyFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicAchievementCategoryTypeFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicAchievementCategoryLabelFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicAchievementCustomizationFilterOptions = new BulkObservableCollection<DynamicThemeOption>();

        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _completedGamesAsc = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _completedGamesDesc = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _gameSummariesAsc = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _gameSummariesDesc = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _dynamicGameSummaries = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private string _dynamicGameSummariesProviderKey = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicGameSummariesProviderLabel = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicGameSummariesGameKey = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicGameSummariesGameLabel = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicGameSummariesSortKey = DynamicThemeViewKeys.LastUnlock;
        [DontSerialize]
        private string _dynamicGameSummariesSortLabel = DynamicThemeViewKeys.LastUnlock;
        [DontSerialize]
        private string _dynamicGameSummariesSortDirectionKey = DynamicThemeViewKeys.Descending;
        [DontSerialize]
        private string _dynamicGameSummariesSortDirectionLabel = DynamicThemeViewKeys.Descending;
        [DontSerialize]
        private string _dynamicGameSummariesFilterKey = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicGameSummariesFilterLabel = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicGameSummariesDefaultProviderKey = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicGameSummariesDefaultFilterKey = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicGameSummariesDefaultSortKey = DynamicThemeViewKeys.LastUnlock;
        [DontSerialize]
        private string _dynamicGameSummariesDefaultSortDirectionKey = DynamicThemeViewKeys.Descending;
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicGameSummariesProviderOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicGameSummariesFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicGameSummariesSortOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicGameSummariesSortDirectionOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _dynamicCategorySummaries = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private bool _hasCategorySummaries;
        [DontSerialize]
        private string _dynamicCategorySummariesFilterKey = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicCategorySummariesFilterLabel = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicCategorySummariesSortKey = DynamicThemeViewKeys.Default;
        [DontSerialize]
        private string _dynamicCategorySummariesSortLabel = DynamicThemeViewKeys.Default;
        [DontSerialize]
        private string _dynamicCategorySummariesSortDirectionKey = DynamicThemeViewKeys.Descending;
        [DontSerialize]
        private string _dynamicCategorySummariesSortDirectionLabel = DynamicThemeViewKeys.Descending;
        [DontSerialize]
        private string _dynamicCategorySummariesDefaultFilterKey = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicCategorySummariesDefaultSortKey = DynamicThemeViewKeys.Default;
        [DontSerialize]
        private string _dynamicCategorySummariesDefaultSortDirectionKey = DynamicThemeViewKeys.Descending;
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicCategorySummariesFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicCategorySummariesSortOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicCategorySummariesSortDirectionOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicGameProgressFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicGameActivityFilterOptions = new BulkObservableCollection<DynamicThemeOption>();

        [DontSerialize]
        private readonly BulkObservableCollection<FriendSummaryItem> _dynamicFriendSummaries = new BulkObservableCollection<FriendSummaryItem>();
        [DontSerialize]
        private readonly BulkObservableCollection<FriendGameAchievementSummary> _dynamicFriendGameSummaries = new BulkObservableCollection<FriendGameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<FriendAchievementDisplayItem> _dynamicFriendAchievements = new BulkObservableCollection<FriendAchievementDisplayItem>();
        [DontSerialize]
        private FriendSummaryItem _dynamicFriendScopeSummary;
        [DontSerialize]
        private string _dynamicFriendScopeProviderKey = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicFriendScopeProviderLabel = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicFriendScopeUserKey = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicFriendScopeUserLabel = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicFriendScopeGameKey = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicFriendScopeGameLabel = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicFriendSummariesFilterKey = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicFriendSummariesFilterLabel = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicFriendSummariesSortKey = DynamicThemeViewKeys.LastUnlock;
        [DontSerialize]
        private string _dynamicFriendSummariesSortLabel = DynamicThemeViewKeys.LastUnlock;
        [DontSerialize]
        private string _dynamicFriendSummariesSortDirectionKey = DynamicThemeViewKeys.Descending;
        [DontSerialize]
        private string _dynamicFriendSummariesSortDirectionLabel = DynamicThemeViewKeys.Descending;
        [DontSerialize]
        private string _dynamicFriendGameSummariesFilterKey = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicFriendGameSummariesFilterLabel = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicFriendGameSummariesSortKey = DynamicThemeViewKeys.LastUnlock;
        [DontSerialize]
        private string _dynamicFriendGameSummariesSortLabel = DynamicThemeViewKeys.LastUnlock;
        [DontSerialize]
        private string _dynamicFriendGameSummariesSortDirectionKey = DynamicThemeViewKeys.Descending;
        [DontSerialize]
        private string _dynamicFriendGameSummariesSortDirectionLabel = DynamicThemeViewKeys.Descending;
        [DontSerialize]
        private string _dynamicFriendAchievementsFilterKey = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicFriendAchievementsFilterLabel = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicFriendAchievementsSortKey = DynamicThemeViewKeys.UnlockTime;
        [DontSerialize]
        private string _dynamicFriendAchievementsSortLabel = DynamicThemeViewKeys.UnlockTime;
        [DontSerialize]
        private string _dynamicFriendAchievementsSortDirectionKey = DynamicThemeViewKeys.Descending;
        [DontSerialize]
        private string _dynamicFriendAchievementsSortDirectionLabel = DynamicThemeViewKeys.Descending;
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicFriendScopeProviderOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicFriendScopeUserOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicFriendScopeGameOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicFriendSummariesFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicFriendSummariesSortOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicFriendSummariesSortDirectionOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicFriendGameSummariesFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicFriendGameSummariesSortOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicFriendGameSummariesSortDirectionOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicFriendAchievementsFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicFriendAchievementsSortOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicFriendAchievementsSortDirectionOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicFriendSummaryLastUnlockFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicFriendGameProgressFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicFriendGameActivityFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicFriendAchievementStatusFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicFriendAchievementProgressFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicFriendAchievementRarityFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicFriendAchievementTrophyFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicFriendAchievementCategoryTypeFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicFriendAchievementCustomizationFilterOptions = new BulkObservableCollection<DynamicThemeOption>();

        [DontSerialize]
        private List<AchievementDetail> _allAchievementsUnlockAsc = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _allAchievementsUnlockDesc = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _allAchievementsRarityAsc = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _allAchievementsRarityDesc = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _dynamicLibraryAchievements = new List<AchievementDetail>();
        [DontSerialize]
        private string _dynamicLibraryAchievementsProviderKey = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicLibraryAchievementsProviderLabel = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicLibraryAchievementsGameKey = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicLibraryAchievementsGameLabel = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicLibraryAchievementsSortKey = DynamicThemeViewKeys.UnlockTime;
        [DontSerialize]
        private string _dynamicLibraryAchievementsSortLabel = DynamicThemeViewKeys.UnlockTime;
        [DontSerialize]
        private string _dynamicLibraryAchievementsSortDirectionKey = DynamicThemeViewKeys.Descending;
        [DontSerialize]
        private string _dynamicLibraryAchievementsSortDirectionLabel = DynamicThemeViewKeys.Descending;
        [DontSerialize]
        private string _dynamicLibraryAchievementsFilterKey = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicLibraryAchievementsFilterLabel = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicLibraryAchievementsDefaultProviderKey = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicLibraryAchievementsDefaultFilterKey = DynamicThemeViewKeys.All;
        [DontSerialize]
        private string _dynamicLibraryAchievementsDefaultSortKey = DynamicThemeViewKeys.UnlockTime;
        [DontSerialize]
        private string _dynamicLibraryAchievementsDefaultSortDirectionKey = DynamicThemeViewKeys.Descending;
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicLibraryAchievementsProviderOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicLibraryAchievementsFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicLibraryAchievementsSortOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicLibraryAchievementsSortDirectionOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicLibraryAchievementStatusFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicLibraryAchievementProgressFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicLibraryAchievementRarityFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicLibraryAchievementTrophyFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicLibraryAchievementCategoryTypeFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private readonly BulkObservableCollection<DynamicThemeOption> _dynamicLibraryAchievementCustomizationFilterOptions = new BulkObservableCollection<DynamicThemeOption>();
        [DontSerialize]
        private List<AchievementDetail> _mostRecentUnlocks = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _rarestRecentUnlocks = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _mostRecentUnlocksTop3 = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _mostRecentUnlocksTop5 = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _mostRecentUnlocksTop10 = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _rarestRecentUnlocksTop3 = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _rarestRecentUnlocksTop5 = new List<AchievementDetail>();
        [DontSerialize]
        private List<AchievementDetail> _rarestRecentUnlocksTop10 = new List<AchievementDetail>();
        [DontSerialize]
        private AchievementRarityStats _totalCommon = new AchievementRarityStats();
        [DontSerialize]
        private AchievementRarityStats _totalUncommon = new AchievementRarityStats();
        [DontSerialize]
        private AchievementRarityStats _totalRare = new AchievementRarityStats();
        [DontSerialize]
        private AchievementRarityStats _totalUltraRare = new AchievementRarityStats();
        [DontSerialize]
        private AchievementRarityStats _totalRareAndUltraRare = new AchievementRarityStats();
        [DontSerialize]
        private AchievementRarityStats _totalOverall = new AchievementRarityStats();
        [DontSerialize]
        private int _collectorScore;
        [DontSerialize]
        private int _collectorLevel;
        [DontSerialize]
        private double _collectorLevelProgress;
        [DontSerialize]
        private string _collectorRank = "Bronze5";
        [DontSerialize]
        private int _prestigeScore;
        [DontSerialize]
        private int _prestigeLevel;
        [DontSerialize]
        private double _prestigeLevelProgress;
        [DontSerialize]
        private string _prestigeRank = "Bronze5";

        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _steamGames = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _gogGames = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _epicGames = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _battleNetGames = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _eaGames = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _xboxGames = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _psnGames = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _retroAchievementsGames = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _appleGames = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _googlePlayGames = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _hoyoverseGames = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _ubisoftGames = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _rpcs3Games = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _xeniaGames = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _shadPS4Games = new BulkObservableCollection<GameAchievementSummary>();
        [DontSerialize]
        private readonly BulkObservableCollection<GameAchievementSummary> _manualGames = new BulkObservableCollection<GameAchievementSummary>();

        [DontSerialize]
        public bool HasAchievements
        {
            get => _hasAchievements;
            set => SetValue(ref _hasAchievements, value);
        }

        [DontSerialize]
        public Guid? SelectedGameId
        {
            get => _selectedGameId;
            set => SetValue(ref _selectedGameId, value);
        }

        [DontSerialize]
        public bool HasCustomAchievementOrder
        {
            get => _hasCustomAchievementOrder;
            set => SetValue(ref _hasCustomAchievementOrder, value);
        }

        [DontSerialize]
        public bool IsCompleted
        {
            get => _isCompleted;
            set => SetValue(ref _isCompleted, value);
        }

        [DontSerialize]
        public int AchievementCount
        {
            get => _achievementCount;
            set => SetValue(ref _achievementCount, value);
        }

        [DontSerialize]
        public int UnlockedCount
        {
            get => _unlockedCount;
            set => SetValue(ref _unlockedCount, value);
        }

        [DontSerialize]
        public int LockedCount
        {
            get => _lockedCount;
            set => SetValue(ref _lockedCount, value);
        }

        [DontSerialize]
        public double ProgressPercentage
        {
            get => _progressPercentage;
            set => SetValue(ref _progressPercentage, value);
        }

        [DontSerialize]
        public AchievementRarityStats Common
        {
            get => _common;
            set => SetValue(ref _common, value);
        }

        [DontSerialize]
        public AchievementRarityStats Uncommon
        {
            get => _uncommon;
            set => SetValue(ref _uncommon, value);
        }

        [DontSerialize]
        public AchievementRarityStats Rare
        {
            get => _rare;
            set => SetValue(ref _rare, value);
        }

        [DontSerialize]
        public AchievementRarityStats UltraRare
        {
            get => _ultraRare;
            set => SetValue(ref _ultraRare, value);
        }

        [DontSerialize]
        public AchievementRarityStats RareAndUltraRare
        {
            get => _rareAndUltraRare;
            set => SetValue(ref _rareAndUltraRare, value);
        }

        [DontSerialize]
        public List<AchievementDetail> AllAchievements
        {
            get => _allAchievements;
            set
            {
                if (EqualityComparer<List<AchievementDetail>>.Default.Equals(_allAchievements, value))
                {
                    return;
                }

                _allAchievements = value;
                OnPropertyChanged();
                _allAchievementDisplayItems = null;
                OnPropertyChanged(nameof(AllAchievementDisplayItems));
            }
        }

        [DontSerialize]
        public List<AchievementDisplayItem> AllAchievementDisplayItems
        {
            get
            {
                if (_allAchievementDisplayItems != null)
                {
                    return _allAchievementDisplayItems;
                }

                if (_allAchievements == null || _allAchievements.Count == 0)
                {
                    _allAchievementDisplayItems = new List<AchievementDisplayItem>();
                    return _allAchievementDisplayItems;
                }

                var settings = PlayniteAchievementsPlugin.Instance?.Settings;
                var persisted = settings?.Persisted;
                var showHiddenIcon = persisted?.ShowHiddenIcon ?? false;
                var showHiddenTitle = persisted?.ShowHiddenTitle ?? false;
                var showHiddenDescription = persisted?.ShowHiddenDescription ?? false;
                var showHiddenSuffix = persisted?.ShowHiddenSuffix ?? true;
                var showLockedIcon = persisted?.ShowLockedIcon ?? true;
                var useSeparateLockedIcons = persisted?.UseSeparateLockedIconsWhenAvailable ?? false;
                var showRarityBar = persisted?.ShowCompactListRarityBar ?? true;

                var items = new List<AchievementDisplayItem>(_allAchievements.Count);
                foreach (var achievement in _allAchievements)
                {
                    var item = new AchievementDisplayItem();
                    var gameName = achievement.Game?.Name ?? "Unknown";
                    var gameId = achievement.Game?.Id;
                    item.UpdateFrom(
                        achievement,
                        gameName,
                        gameId,
                        showHiddenIcon,
                        showHiddenTitle,
                        showHiddenDescription,
                        showHiddenSuffix,
                        showLockedIcon,
                        ResolveUseSeparateLockedIcons(persisted, gameId, useSeparateLockedIcons),
                        showRarityBar,
                        categoryOrderIndex: achievement.CategoryOrderIndex);
                    items.Add(item);
                }

                _allAchievementDisplayItems = items;
                return _allAchievementDisplayItems;
            }
        }

        public void RefreshDisplayItems(
            bool showHiddenIcon,
            bool showHiddenTitle,
            bool showHiddenDescription,
            bool showHiddenSuffix,
            bool showLockedIcon,
            bool useSeparateLockedIconsWhenAvailable,
            bool showRarityBar)
        {
            if (_allAchievements == null || _allAchievements.Count == 0)
            {
                _allAchievementDisplayItems = new List<AchievementDisplayItem>();
                OnPropertyChanged(nameof(AllAchievementDisplayItems));
                return;
            }

            var items = new List<AchievementDisplayItem>(_allAchievements.Count);
            var persisted = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted;
            foreach (var achievement in _allAchievements)
            {
                var item = new AchievementDisplayItem();
                var gameName = achievement.Game?.Name ?? "Unknown";
                var gameId = achievement.Game?.Id;
                item.UpdateFrom(
                    achievement,
                    gameName,
                    gameId,
                    showHiddenIcon,
                    showHiddenTitle,
                    showHiddenDescription,
                    showHiddenSuffix,
                    showLockedIcon,
                    ResolveUseSeparateLockedIcons(persisted, gameId, useSeparateLockedIconsWhenAvailable),
                    showRarityBar,
                    categoryOrderIndex: achievement.CategoryOrderIndex);
                items.Add(item);
            }

            _allAchievementDisplayItems = items;
            OnPropertyChanged(nameof(AllAchievementDisplayItems));
        }

        [DontSerialize]
        public List<AchievementDetail> AchievementDefaultOrder
        {
            get => _achievementDefaultOrder;
            set => SetValue(ref _achievementDefaultOrder, value);
        }

        [DontSerialize]
        public List<AchievementDetail> AchievementsNewestFirst
        {
            get => _achievementsNewestFirst;
            set => SetValue(ref _achievementsNewestFirst, value);
        }

        [DontSerialize]
        public List<AchievementDetail> AchievementsOldestFirst
        {
            get => _achievementsOldestFirst;
            set => SetValue(ref _achievementsOldestFirst, value);
        }

        [DontSerialize]
        public List<AchievementDetail> AchievementsRarityAsc
        {
            get => _achievementsRarityAsc;
            set => SetValue(ref _achievementsRarityAsc, value);
        }

        [DontSerialize]
        public List<AchievementDetail> AchievementsRarityDesc
        {
            get => _achievementsRarityDesc;
            set => SetValue(ref _achievementsRarityDesc, value);
        }

        [DontSerialize]
        public List<AchievementDetail> DynamicAchievements
        {
            get => _dynamicAchievements;
            set => SetValue(ref _dynamicAchievements, value);
        }

        [DontSerialize]
        public string DynamicAchievementsGameKey
        {
            get => _dynamicAchievementsGameKey;
            set => SetValue(ref _dynamicAchievementsGameKey, value ?? string.Empty);
        }

        [DontSerialize]
        public string DynamicAchievementsGameLabel
        {
            get => _dynamicAchievementsGameLabel;
            set => SetValue(ref _dynamicAchievementsGameLabel, value ?? string.Empty);
        }

        [DontSerialize]
        public string DynamicAchievementsFilterKey
        {
            get => _dynamicAchievementsFilterKey;
            set => SetValue(ref _dynamicAchievementsFilterKey, value);
        }

        [DontSerialize]
        public string DynamicAchievementsFilterLabel
        {
            get => _dynamicAchievementsFilterLabel;
            set => SetValue(ref _dynamicAchievementsFilterLabel, value);
        }

        [DontSerialize]
        public string DynamicAchievementsCategoryLabelFilterKey
        {
            get => _dynamicAchievementsCategoryLabelFilterKey;
            set => SetValue(ref _dynamicAchievementsCategoryLabelFilterKey, value);
        }

        [DontSerialize]
        public string DynamicAchievementsCategoryLabelFilterLabel
        {
            get => _dynamicAchievementsCategoryLabelFilterLabel;
            set => SetValue(ref _dynamicAchievementsCategoryLabelFilterLabel, value);
        }

        [DontSerialize]
        public string DynamicAchievementsSortKey
        {
            get => _dynamicAchievementsSortKey;
            set => SetValue(ref _dynamicAchievementsSortKey, value);
        }

        [DontSerialize]
        public string DynamicAchievementsSortLabel
        {
            get => _dynamicAchievementsSortLabel;
            set => SetValue(ref _dynamicAchievementsSortLabel, value);
        }

        [DontSerialize]
        public string DynamicAchievementsSortDirectionKey
        {
            get => _dynamicAchievementsSortDirectionKey;
            set => SetValue(ref _dynamicAchievementsSortDirectionKey, value);
        }

        [DontSerialize]
        public string DynamicAchievementsSortDirectionLabel
        {
            get => _dynamicAchievementsSortDirectionLabel;
            set => SetValue(ref _dynamicAchievementsSortDirectionLabel, value);
        }

        [DontSerialize]
        public string DynamicAchievementsDefaultFilterKey
        {
            get => _dynamicAchievementsDefaultFilterKey;
            set => SetValue(ref _dynamicAchievementsDefaultFilterKey, value);
        }

        [DontSerialize]
        public string DynamicAchievementsDefaultSortKey
        {
            get => _dynamicAchievementsDefaultSortKey;
            set => SetValue(ref _dynamicAchievementsDefaultSortKey, value);
        }

        [DontSerialize]
        public string DynamicAchievementsDefaultSortDirectionKey
        {
            get => _dynamicAchievementsDefaultSortDirectionKey;
            set => SetValue(ref _dynamicAchievementsDefaultSortDirectionKey, value);
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicAchievementsFilterOptions
        {
            get => _dynamicAchievementsFilterOptions;
            set => ReplaceCollection(_dynamicAchievementsFilterOptions, value, nameof(DynamicAchievementsFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicAchievementsSortOptions
        {
            get => _dynamicAchievementsSortOptions;
            set => ReplaceCollection(_dynamicAchievementsSortOptions, value, nameof(DynamicAchievementsSortOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicAchievementsSortDirectionOptions
        {
            get => _dynamicAchievementsSortDirectionOptions;
            set => ReplaceCollection(_dynamicAchievementsSortDirectionOptions, value, nameof(DynamicAchievementsSortDirectionOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicAchievementGameOptions
        {
            get => _dynamicAchievementGameOptions;
            set => ReplaceCollection(_dynamicAchievementGameOptions, value, nameof(DynamicAchievementGameOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicAchievementStatusFilterOptions
        {
            get => _dynamicAchievementStatusFilterOptions;
            set => ReplaceCollection(_dynamicAchievementStatusFilterOptions, value, nameof(DynamicAchievementStatusFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicAchievementProgressFilterOptions
        {
            get => _dynamicAchievementProgressFilterOptions;
            set => ReplaceCollection(_dynamicAchievementProgressFilterOptions, value, nameof(DynamicAchievementProgressFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicAchievementRarityFilterOptions
        {
            get => _dynamicAchievementRarityFilterOptions;
            set => ReplaceCollection(_dynamicAchievementRarityFilterOptions, value, nameof(DynamicAchievementRarityFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicAchievementTrophyFilterOptions
        {
            get => _dynamicAchievementTrophyFilterOptions;
            set => ReplaceCollection(_dynamicAchievementTrophyFilterOptions, value, nameof(DynamicAchievementTrophyFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicAchievementCategoryTypeFilterOptions
        {
            get => _dynamicAchievementCategoryTypeFilterOptions;
            set => ReplaceCollection(_dynamicAchievementCategoryTypeFilterOptions, value, nameof(DynamicAchievementCategoryTypeFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicAchievementCategoryLabelFilterOptions
        {
            get => _dynamicAchievementCategoryLabelFilterOptions;
            set => ReplaceCollection(_dynamicAchievementCategoryLabelFilterOptions, value, nameof(DynamicAchievementCategoryLabelFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicAchievementCustomizationFilterOptions
        {
            get => _dynamicAchievementCustomizationFilterOptions;
            set => ReplaceCollection(_dynamicAchievementCustomizationFilterOptions, value, nameof(DynamicAchievementCustomizationFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicLibraryAchievementStatusFilterOptions
        {
            get => _dynamicLibraryAchievementStatusFilterOptions;
            set => ReplaceCollection(_dynamicLibraryAchievementStatusFilterOptions, value, nameof(DynamicLibraryAchievementStatusFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicLibraryAchievementProgressFilterOptions
        {
            get => _dynamicLibraryAchievementProgressFilterOptions;
            set => ReplaceCollection(_dynamicLibraryAchievementProgressFilterOptions, value, nameof(DynamicLibraryAchievementProgressFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicLibraryAchievementRarityFilterOptions
        {
            get => _dynamicLibraryAchievementRarityFilterOptions;
            set => ReplaceCollection(_dynamicLibraryAchievementRarityFilterOptions, value, nameof(DynamicLibraryAchievementRarityFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicLibraryAchievementTrophyFilterOptions
        {
            get => _dynamicLibraryAchievementTrophyFilterOptions;
            set => ReplaceCollection(_dynamicLibraryAchievementTrophyFilterOptions, value, nameof(DynamicLibraryAchievementTrophyFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicLibraryAchievementCategoryTypeFilterOptions
        {
            get => _dynamicLibraryAchievementCategoryTypeFilterOptions;
            set => ReplaceCollection(_dynamicLibraryAchievementCategoryTypeFilterOptions, value, nameof(DynamicLibraryAchievementCategoryTypeFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicLibraryAchievementCustomizationFilterOptions
        {
            get => _dynamicLibraryAchievementCustomizationFilterOptions;
            set => ReplaceCollection(_dynamicLibraryAchievementCustomizationFilterOptions, value, nameof(DynamicLibraryAchievementCustomizationFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> CompletedGamesAsc
        {
            get => _completedGamesAsc;
            set => ReplaceCollection(_completedGamesAsc, value, nameof(CompletedGamesAsc));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> CompletedGamesDesc
        {
            get => _completedGamesDesc;
            set => ReplaceCollection(_completedGamesDesc, value, nameof(CompletedGamesDesc));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> GameSummariesAsc
        {
            get => _gameSummariesAsc;
            set => ReplaceCollection(_gameSummariesAsc, value, nameof(GameSummariesAsc));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> GameSummariesDesc
        {
            get => _gameSummariesDesc;
            set => ReplaceCollection(_gameSummariesDesc, value, nameof(GameSummariesDesc));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> DynamicGameSummaries
        {
            get => _dynamicGameSummaries;
            set => ReplaceCollection(_dynamicGameSummaries, value, nameof(DynamicGameSummaries));
        }

        [DontSerialize]
        public string DynamicGameSummariesProviderKey
        {
            get => _dynamicGameSummariesProviderKey;
            set => SetValue(ref _dynamicGameSummariesProviderKey, value);
        }

        [DontSerialize]
        public string DynamicGameSummariesProviderLabel
        {
            get => _dynamicGameSummariesProviderLabel;
            set => SetValue(ref _dynamicGameSummariesProviderLabel, value);
        }

        [DontSerialize]
        public string DynamicGameSummariesGameKey
        {
            get => _dynamicGameSummariesGameKey;
            set => SetValue(ref _dynamicGameSummariesGameKey, value);
        }

        [DontSerialize]
        public string DynamicGameSummariesGameLabel
        {
            get => _dynamicGameSummariesGameLabel;
            set => SetValue(ref _dynamicGameSummariesGameLabel, value);
        }

        [DontSerialize]
        public string DynamicGameSummariesFilterKey
        {
            get => _dynamicGameSummariesFilterKey;
            set => SetValue(ref _dynamicGameSummariesFilterKey, value);
        }

        [DontSerialize]
        public string DynamicGameSummariesFilterLabel
        {
            get => _dynamicGameSummariesFilterLabel;
            set => SetValue(ref _dynamicGameSummariesFilterLabel, value);
        }

        [DontSerialize]
        public string DynamicGameSummariesSortKey
        {
            get => _dynamicGameSummariesSortKey;
            set => SetValue(ref _dynamicGameSummariesSortKey, value);
        }

        [DontSerialize]
        public string DynamicGameSummariesSortLabel
        {
            get => _dynamicGameSummariesSortLabel;
            set => SetValue(ref _dynamicGameSummariesSortLabel, value);
        }

        [DontSerialize]
        public string DynamicGameSummariesSortDirectionKey
        {
            get => _dynamicGameSummariesSortDirectionKey;
            set => SetValue(ref _dynamicGameSummariesSortDirectionKey, value);
        }

        [DontSerialize]
        public string DynamicGameSummariesSortDirectionLabel
        {
            get => _dynamicGameSummariesSortDirectionLabel;
            set => SetValue(ref _dynamicGameSummariesSortDirectionLabel, value);
        }

        [DontSerialize]
        public string DynamicGameSummariesDefaultProviderKey
        {
            get => _dynamicGameSummariesDefaultProviderKey;
            set => SetValue(ref _dynamicGameSummariesDefaultProviderKey, value);
        }

        [DontSerialize]
        public string DynamicGameSummariesDefaultFilterKey
        {
            get => _dynamicGameSummariesDefaultFilterKey;
            set => SetValue(ref _dynamicGameSummariesDefaultFilterKey, value);
        }

        [DontSerialize]
        public string DynamicGameSummariesDefaultSortKey
        {
            get => _dynamicGameSummariesDefaultSortKey;
            set => SetValue(ref _dynamicGameSummariesDefaultSortKey, value);
        }

        [DontSerialize]
        public string DynamicGameSummariesDefaultSortDirectionKey
        {
            get => _dynamicGameSummariesDefaultSortDirectionKey;
            set => SetValue(ref _dynamicGameSummariesDefaultSortDirectionKey, value);
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicGameSummariesProviderOptions
        {
            get => _dynamicGameSummariesProviderOptions;
            set => ReplaceCollection(_dynamicGameSummariesProviderOptions, value, nameof(DynamicGameSummariesProviderOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicGameSummariesFilterOptions
        {
            get => _dynamicGameSummariesFilterOptions;
            set => ReplaceCollection(_dynamicGameSummariesFilterOptions, value, nameof(DynamicGameSummariesFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicGameSummariesSortOptions
        {
            get => _dynamicGameSummariesSortOptions;
            set => ReplaceCollection(_dynamicGameSummariesSortOptions, value, nameof(DynamicGameSummariesSortOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicGameSummariesSortDirectionOptions
        {
            get => _dynamicGameSummariesSortDirectionOptions;
            set => ReplaceCollection(_dynamicGameSummariesSortDirectionOptions, value, nameof(DynamicGameSummariesSortDirectionOptions));
        }

        /// <summary>
        /// Per-category rollup of the selected game's achievements, projected into
        /// game-summary items so existing trophy-card templates render them unchanged.
        /// Empty (and <see cref="HasCategorySummaries"/> false) for games with fewer
        /// than two distinct category labels.
        /// </summary>
        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> DynamicCategorySummaries
        {
            get => _dynamicCategorySummaries;
            set => ReplaceCollection(_dynamicCategorySummaries, value, nameof(DynamicCategorySummaries));
        }

        [DontSerialize]
        public bool HasCategorySummaries
        {
            get => _hasCategorySummaries;
            set => SetValue(ref _hasCategorySummaries, value);
        }

        [DontSerialize]
        public string DynamicCategorySummariesFilterKey
        {
            get => _dynamicCategorySummariesFilterKey;
            set => SetValue(ref _dynamicCategorySummariesFilterKey, value);
        }

        [DontSerialize]
        public string DynamicCategorySummariesFilterLabel
        {
            get => _dynamicCategorySummariesFilterLabel;
            set => SetValue(ref _dynamicCategorySummariesFilterLabel, value);
        }

        [DontSerialize]
        public string DynamicCategorySummariesSortKey
        {
            get => _dynamicCategorySummariesSortKey;
            set => SetValue(ref _dynamicCategorySummariesSortKey, value);
        }

        [DontSerialize]
        public string DynamicCategorySummariesSortLabel
        {
            get => _dynamicCategorySummariesSortLabel;
            set => SetValue(ref _dynamicCategorySummariesSortLabel, value);
        }

        [DontSerialize]
        public string DynamicCategorySummariesSortDirectionKey
        {
            get => _dynamicCategorySummariesSortDirectionKey;
            set => SetValue(ref _dynamicCategorySummariesSortDirectionKey, value);
        }

        [DontSerialize]
        public string DynamicCategorySummariesSortDirectionLabel
        {
            get => _dynamicCategorySummariesSortDirectionLabel;
            set => SetValue(ref _dynamicCategorySummariesSortDirectionLabel, value);
        }

        [DontSerialize]
        public string DynamicCategorySummariesDefaultFilterKey
        {
            get => _dynamicCategorySummariesDefaultFilterKey;
            set => SetValue(ref _dynamicCategorySummariesDefaultFilterKey, value);
        }

        [DontSerialize]
        public string DynamicCategorySummariesDefaultSortKey
        {
            get => _dynamicCategorySummariesDefaultSortKey;
            set => SetValue(ref _dynamicCategorySummariesDefaultSortKey, value);
        }

        [DontSerialize]
        public string DynamicCategorySummariesDefaultSortDirectionKey
        {
            get => _dynamicCategorySummariesDefaultSortDirectionKey;
            set => SetValue(ref _dynamicCategorySummariesDefaultSortDirectionKey, value);
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicCategorySummariesFilterOptions
        {
            get => _dynamicCategorySummariesFilterOptions;
            set => ReplaceCollection(_dynamicCategorySummariesFilterOptions, value, nameof(DynamicCategorySummariesFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicCategorySummariesSortOptions
        {
            get => _dynamicCategorySummariesSortOptions;
            set => ReplaceCollection(_dynamicCategorySummariesSortOptions, value, nameof(DynamicCategorySummariesSortOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicCategorySummariesSortDirectionOptions
        {
            get => _dynamicCategorySummariesSortDirectionOptions;
            set => ReplaceCollection(_dynamicCategorySummariesSortDirectionOptions, value, nameof(DynamicCategorySummariesSortDirectionOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicGameProgressFilterOptions
        {
            get => _dynamicGameProgressFilterOptions;
            set => ReplaceCollection(_dynamicGameProgressFilterOptions, value, nameof(DynamicGameProgressFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicGameActivityFilterOptions
        {
            get => _dynamicGameActivityFilterOptions;
            set => ReplaceCollection(_dynamicGameActivityFilterOptions, value, nameof(DynamicGameActivityFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicFriendSummaryLastUnlockFilterOptions
        {
            get => _dynamicFriendSummaryLastUnlockFilterOptions;
            set => ReplaceCollection(_dynamicFriendSummaryLastUnlockFilterOptions, value, nameof(DynamicFriendSummaryLastUnlockFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicFriendGameProgressFilterOptions
        {
            get => _dynamicFriendGameProgressFilterOptions;
            set => ReplaceCollection(_dynamicFriendGameProgressFilterOptions, value, nameof(DynamicFriendGameProgressFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicFriendGameActivityFilterOptions
        {
            get => _dynamicFriendGameActivityFilterOptions;
            set => ReplaceCollection(_dynamicFriendGameActivityFilterOptions, value, nameof(DynamicFriendGameActivityFilterOptions));
        }

        /// <summary>
        /// Invoked when a consumer reads one of the friend data properties. The theme
        /// integration service uses this to build the friend runtime state on demand instead
        /// of eagerly retaining it for themes that never bind friend data.
        /// </summary>
        [DontSerialize]
        internal Action FriendDataRequested { get; set; }

        private void NotifyFriendDataRequested() => FriendDataRequested?.Invoke();

        [DontSerialize]
        public ObservableCollection<FriendSummaryItem> DynamicFriendSummaries
        {
            get
            {
                NotifyFriendDataRequested();
                return _dynamicFriendSummaries;
            }
            set => ReplaceCollection(_dynamicFriendSummaries, value, nameof(DynamicFriendSummaries));
        }

        [DontSerialize]
        public ObservableCollection<FriendGameAchievementSummary> DynamicFriendGameSummaries
        {
            get
            {
                NotifyFriendDataRequested();
                return _dynamicFriendGameSummaries;
            }
            set => ReplaceCollection(_dynamicFriendGameSummaries, value, nameof(DynamicFriendGameSummaries));
        }

        [DontSerialize]
        public ObservableCollection<FriendAchievementDisplayItem> DynamicFriendAchievements
        {
            get
            {
                NotifyFriendDataRequested();
                return _dynamicFriendAchievements;
            }
            set => ReplaceCollection(_dynamicFriendAchievements, value, nameof(DynamicFriendAchievements));
        }

        /// <summary>
        /// The friend currently selected by the friend scope (<see cref="DynamicFriendScopeUserKey"/>),
        /// exposed as a single bindable item so themes can show that friend's whole-library rarity/trophy
        /// rollup alongside their scoped achievements list. Null when no individual friend is scoped.
        /// </summary>
        [DontSerialize]
        public FriendSummaryItem DynamicFriendScopeSummary
        {
            get
            {
                NotifyFriendDataRequested();
                return _dynamicFriendScopeSummary;
            }
            set => SetValue(ref _dynamicFriendScopeSummary, value);
        }

        [DontSerialize]
        public string DynamicFriendScopeProviderKey
        {
            get => _dynamicFriendScopeProviderKey;
            set => SetValue(ref _dynamicFriendScopeProviderKey, value);
        }

        [DontSerialize]
        public string DynamicFriendScopeProviderLabel
        {
            get => _dynamicFriendScopeProviderLabel;
            set => SetValue(ref _dynamicFriendScopeProviderLabel, value);
        }

        [DontSerialize]
        public string DynamicFriendScopeUserKey
        {
            get => _dynamicFriendScopeUserKey;
            set => SetValue(ref _dynamicFriendScopeUserKey, value);
        }

        [DontSerialize]
        public string DynamicFriendScopeUserLabel
        {
            get => _dynamicFriendScopeUserLabel;
            set => SetValue(ref _dynamicFriendScopeUserLabel, value);
        }

        [DontSerialize]
        public string DynamicFriendScopeGameKey
        {
            get => _dynamicFriendScopeGameKey;
            set => SetValue(ref _dynamicFriendScopeGameKey, value);
        }

        [DontSerialize]
        public string DynamicFriendScopeGameLabel
        {
            get => _dynamicFriendScopeGameLabel;
            set => SetValue(ref _dynamicFriendScopeGameLabel, value);
        }

        [DontSerialize]
        public string DynamicFriendSummariesFilterKey
        {
            get => _dynamicFriendSummariesFilterKey;
            set => SetValue(ref _dynamicFriendSummariesFilterKey, value);
        }

        [DontSerialize]
        public string DynamicFriendSummariesFilterLabel
        {
            get => _dynamicFriendSummariesFilterLabel;
            set => SetValue(ref _dynamicFriendSummariesFilterLabel, value);
        }

        [DontSerialize]
        public string DynamicFriendSummariesSortKey
        {
            get => _dynamicFriendSummariesSortKey;
            set => SetValue(ref _dynamicFriendSummariesSortKey, value);
        }

        [DontSerialize]
        public string DynamicFriendSummariesSortLabel
        {
            get => _dynamicFriendSummariesSortLabel;
            set => SetValue(ref _dynamicFriendSummariesSortLabel, value);
        }

        [DontSerialize]
        public string DynamicFriendSummariesSortDirectionKey
        {
            get => _dynamicFriendSummariesSortDirectionKey;
            set => SetValue(ref _dynamicFriendSummariesSortDirectionKey, value);
        }

        [DontSerialize]
        public string DynamicFriendSummariesSortDirectionLabel
        {
            get => _dynamicFriendSummariesSortDirectionLabel;
            set => SetValue(ref _dynamicFriendSummariesSortDirectionLabel, value);
        }

        [DontSerialize]
        public string DynamicFriendGameSummariesFilterKey
        {
            get => _dynamicFriendGameSummariesFilterKey;
            set => SetValue(ref _dynamicFriendGameSummariesFilterKey, value);
        }

        [DontSerialize]
        public string DynamicFriendGameSummariesFilterLabel
        {
            get => _dynamicFriendGameSummariesFilterLabel;
            set => SetValue(ref _dynamicFriendGameSummariesFilterLabel, value);
        }

        [DontSerialize]
        public string DynamicFriendGameSummariesSortKey
        {
            get => _dynamicFriendGameSummariesSortKey;
            set => SetValue(ref _dynamicFriendGameSummariesSortKey, value);
        }

        [DontSerialize]
        public string DynamicFriendGameSummariesSortLabel
        {
            get => _dynamicFriendGameSummariesSortLabel;
            set => SetValue(ref _dynamicFriendGameSummariesSortLabel, value);
        }

        [DontSerialize]
        public string DynamicFriendGameSummariesSortDirectionKey
        {
            get => _dynamicFriendGameSummariesSortDirectionKey;
            set => SetValue(ref _dynamicFriendGameSummariesSortDirectionKey, value);
        }

        [DontSerialize]
        public string DynamicFriendGameSummariesSortDirectionLabel
        {
            get => _dynamicFriendGameSummariesSortDirectionLabel;
            set => SetValue(ref _dynamicFriendGameSummariesSortDirectionLabel, value);
        }

        [DontSerialize]
        public string DynamicFriendAchievementsFilterKey
        {
            get => _dynamicFriendAchievementsFilterKey;
            set => SetValue(ref _dynamicFriendAchievementsFilterKey, value);
        }

        [DontSerialize]
        public string DynamicFriendAchievementsFilterLabel
        {
            get => _dynamicFriendAchievementsFilterLabel;
            set => SetValue(ref _dynamicFriendAchievementsFilterLabel, value);
        }

        [DontSerialize]
        public string DynamicFriendAchievementsSortKey
        {
            get => _dynamicFriendAchievementsSortKey;
            set => SetValue(ref _dynamicFriendAchievementsSortKey, value);
        }

        [DontSerialize]
        public string DynamicFriendAchievementsSortLabel
        {
            get => _dynamicFriendAchievementsSortLabel;
            set => SetValue(ref _dynamicFriendAchievementsSortLabel, value);
        }

        [DontSerialize]
        public string DynamicFriendAchievementsSortDirectionKey
        {
            get => _dynamicFriendAchievementsSortDirectionKey;
            set => SetValue(ref _dynamicFriendAchievementsSortDirectionKey, value);
        }

        [DontSerialize]
        public string DynamicFriendAchievementsSortDirectionLabel
        {
            get => _dynamicFriendAchievementsSortDirectionLabel;
            set => SetValue(ref _dynamicFriendAchievementsSortDirectionLabel, value);
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicFriendScopeProviderOptions
        {
            get
            {
                NotifyFriendDataRequested();
                return _dynamicFriendScopeProviderOptions;
            }
            set => ReplaceCollection(_dynamicFriendScopeProviderOptions, value, nameof(DynamicFriendScopeProviderOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicFriendScopeUserOptions
        {
            get
            {
                NotifyFriendDataRequested();
                return _dynamicFriendScopeUserOptions;
            }
            set => ReplaceCollection(_dynamicFriendScopeUserOptions, value, nameof(DynamicFriendScopeUserOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicFriendScopeGameOptions
        {
            get
            {
                NotifyFriendDataRequested();
                return _dynamicFriendScopeGameOptions;
            }
            set => ReplaceCollection(_dynamicFriendScopeGameOptions, value, nameof(DynamicFriendScopeGameOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicFriendSummariesFilterOptions
        {
            get => _dynamicFriendSummariesFilterOptions;
            set => ReplaceCollection(_dynamicFriendSummariesFilterOptions, value, nameof(DynamicFriendSummariesFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicFriendSummariesSortOptions
        {
            get => _dynamicFriendSummariesSortOptions;
            set => ReplaceCollection(_dynamicFriendSummariesSortOptions, value, nameof(DynamicFriendSummariesSortOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicFriendSummariesSortDirectionOptions
        {
            get => _dynamicFriendSummariesSortDirectionOptions;
            set => ReplaceCollection(_dynamicFriendSummariesSortDirectionOptions, value, nameof(DynamicFriendSummariesSortDirectionOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicFriendGameSummariesFilterOptions
        {
            get => _dynamicFriendGameSummariesFilterOptions;
            set => ReplaceCollection(_dynamicFriendGameSummariesFilterOptions, value, nameof(DynamicFriendGameSummariesFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicFriendGameSummariesSortOptions
        {
            get => _dynamicFriendGameSummariesSortOptions;
            set => ReplaceCollection(_dynamicFriendGameSummariesSortOptions, value, nameof(DynamicFriendGameSummariesSortOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicFriendGameSummariesSortDirectionOptions
        {
            get => _dynamicFriendGameSummariesSortDirectionOptions;
            set => ReplaceCollection(_dynamicFriendGameSummariesSortDirectionOptions, value, nameof(DynamicFriendGameSummariesSortDirectionOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicFriendAchievementsFilterOptions
        {
            get => _dynamicFriendAchievementsFilterOptions;
            set => ReplaceCollection(_dynamicFriendAchievementsFilterOptions, value, nameof(DynamicFriendAchievementsFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicFriendAchievementsSortOptions
        {
            get => _dynamicFriendAchievementsSortOptions;
            set => ReplaceCollection(_dynamicFriendAchievementsSortOptions, value, nameof(DynamicFriendAchievementsSortOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicFriendAchievementsSortDirectionOptions
        {
            get => _dynamicFriendAchievementsSortDirectionOptions;
            set => ReplaceCollection(_dynamicFriendAchievementsSortDirectionOptions, value, nameof(DynamicFriendAchievementsSortDirectionOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicFriendAchievementStatusFilterOptions
        {
            get => _dynamicFriendAchievementStatusFilterOptions;
            set => ReplaceCollection(_dynamicFriendAchievementStatusFilterOptions, value, nameof(DynamicFriendAchievementStatusFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicFriendAchievementProgressFilterOptions
        {
            get => _dynamicFriendAchievementProgressFilterOptions;
            set => ReplaceCollection(_dynamicFriendAchievementProgressFilterOptions, value, nameof(DynamicFriendAchievementProgressFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicFriendAchievementRarityFilterOptions
        {
            get => _dynamicFriendAchievementRarityFilterOptions;
            set => ReplaceCollection(_dynamicFriendAchievementRarityFilterOptions, value, nameof(DynamicFriendAchievementRarityFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicFriendAchievementTrophyFilterOptions
        {
            get => _dynamicFriendAchievementTrophyFilterOptions;
            set => ReplaceCollection(_dynamicFriendAchievementTrophyFilterOptions, value, nameof(DynamicFriendAchievementTrophyFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicFriendAchievementCategoryTypeFilterOptions
        {
            get => _dynamicFriendAchievementCategoryTypeFilterOptions;
            set => ReplaceCollection(_dynamicFriendAchievementCategoryTypeFilterOptions, value, nameof(DynamicFriendAchievementCategoryTypeFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicFriendAchievementCustomizationFilterOptions
        {
            get => _dynamicFriendAchievementCustomizationFilterOptions;
            set => ReplaceCollection(_dynamicFriendAchievementCustomizationFilterOptions, value, nameof(DynamicFriendAchievementCustomizationFilterOptions));
        }

        [DontSerialize]
        public AchievementRarityStats TotalCommon
        {
            get => _totalCommon;
            set => SetValue(ref _totalCommon, value);
        }

        [DontSerialize]
        public AchievementRarityStats TotalUncommon
        {
            get => _totalUncommon;
            set => SetValue(ref _totalUncommon, value);
        }

        [DontSerialize]
        public AchievementRarityStats TotalRare
        {
            get => _totalRare;
            set => SetValue(ref _totalRare, value);
        }

        [DontSerialize]
        public AchievementRarityStats TotalUltraRare
        {
            get => _totalUltraRare;
            set => SetValue(ref _totalUltraRare, value);
        }

        [DontSerialize]
        public AchievementRarityStats TotalRareAndUltraRare
        {
            get => _totalRareAndUltraRare;
            set => SetValue(ref _totalRareAndUltraRare, value);
        }

        [DontSerialize]
        public AchievementRarityStats TotalOverall
        {
            get => _totalOverall;
            set => SetValue(ref _totalOverall, value);
        }

        [DontSerialize]
        public int CollectorScore
        {
            get => _collectorScore;
            set => SetValue(ref _collectorScore, value < 0 ? 0 : value);
        }

        [DontSerialize]
        public int CollectorLevel
        {
            get => _collectorLevel;
            set => SetValue(ref _collectorLevel, value < 0 ? 0 : value);
        }

        [DontSerialize]
        public double CollectorLevelProgress
        {
            get => _collectorLevelProgress;
            set => SetValue(ref _collectorLevelProgress, value);
        }

        [DontSerialize]
        public string CollectorRank
        {
            get => _collectorRank;
            set => SetValue(ref _collectorRank, value ?? "Bronze5");
        }

        [DontSerialize]
        public int PrestigeScore
        {
            get => _prestigeScore;
            set => SetValue(ref _prestigeScore, value < 0 ? 0 : value);
        }

        [DontSerialize]
        public int PrestigeLevel
        {
            get => _prestigeLevel;
            set => SetValue(ref _prestigeLevel, value < 0 ? 0 : value);
        }

        [DontSerialize]
        public double PrestigeLevelProgress
        {
            get => _prestigeLevelProgress;
            set => SetValue(ref _prestigeLevelProgress, value);
        }

        [DontSerialize]
        public string PrestigeRank
        {
            get => _prestigeRank;
            set => SetValue(ref _prestigeRank, value ?? "Bronze5");
        }

        [DontSerialize]
        public List<AchievementDetail> AllAchievementsUnlockAsc
        {
            get => _allAchievementsUnlockAsc;
            set => SetValue(ref _allAchievementsUnlockAsc, value);
        }

        [DontSerialize]
        public List<AchievementDetail> AllAchievementsUnlockDesc
        {
            get => _allAchievementsUnlockDesc;
            set => SetValue(ref _allAchievementsUnlockDesc, value);
        }

        [DontSerialize]
        public List<AchievementDetail> AllAchievementsRarityAsc
        {
            get => _allAchievementsRarityAsc;
            set => SetValue(ref _allAchievementsRarityAsc, value);
        }

        [DontSerialize]
        public List<AchievementDetail> AllAchievementsRarityDesc
        {
            get => _allAchievementsRarityDesc;
            set => SetValue(ref _allAchievementsRarityDesc, value);
        }

        [DontSerialize]
        public List<AchievementDetail> DynamicLibraryAchievements
        {
            get => _dynamicLibraryAchievements;
            set => SetValue(ref _dynamicLibraryAchievements, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsProviderKey
        {
            get => _dynamicLibraryAchievementsProviderKey;
            set => SetValue(ref _dynamicLibraryAchievementsProviderKey, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsProviderLabel
        {
            get => _dynamicLibraryAchievementsProviderLabel;
            set => SetValue(ref _dynamicLibraryAchievementsProviderLabel, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsGameKey
        {
            get => _dynamicLibraryAchievementsGameKey;
            set => SetValue(ref _dynamicLibraryAchievementsGameKey, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsGameLabel
        {
            get => _dynamicLibraryAchievementsGameLabel;
            set => SetValue(ref _dynamicLibraryAchievementsGameLabel, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsFilterKey
        {
            get => _dynamicLibraryAchievementsFilterKey;
            set => SetValue(ref _dynamicLibraryAchievementsFilterKey, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsFilterLabel
        {
            get => _dynamicLibraryAchievementsFilterLabel;
            set => SetValue(ref _dynamicLibraryAchievementsFilterLabel, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsSortKey
        {
            get => _dynamicLibraryAchievementsSortKey;
            set => SetValue(ref _dynamicLibraryAchievementsSortKey, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsSortLabel
        {
            get => _dynamicLibraryAchievementsSortLabel;
            set => SetValue(ref _dynamicLibraryAchievementsSortLabel, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsSortDirectionKey
        {
            get => _dynamicLibraryAchievementsSortDirectionKey;
            set => SetValue(ref _dynamicLibraryAchievementsSortDirectionKey, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsSortDirectionLabel
        {
            get => _dynamicLibraryAchievementsSortDirectionLabel;
            set => SetValue(ref _dynamicLibraryAchievementsSortDirectionLabel, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsDefaultProviderKey
        {
            get => _dynamicLibraryAchievementsDefaultProviderKey;
            set => SetValue(ref _dynamicLibraryAchievementsDefaultProviderKey, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsDefaultFilterKey
        {
            get => _dynamicLibraryAchievementsDefaultFilterKey;
            set => SetValue(ref _dynamicLibraryAchievementsDefaultFilterKey, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsDefaultSortKey
        {
            get => _dynamicLibraryAchievementsDefaultSortKey;
            set => SetValue(ref _dynamicLibraryAchievementsDefaultSortKey, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsDefaultSortDirectionKey
        {
            get => _dynamicLibraryAchievementsDefaultSortDirectionKey;
            set => SetValue(ref _dynamicLibraryAchievementsDefaultSortDirectionKey, value);
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicLibraryAchievementsProviderOptions
        {
            get => _dynamicLibraryAchievementsProviderOptions;
            set => ReplaceCollection(_dynamicLibraryAchievementsProviderOptions, value, nameof(DynamicLibraryAchievementsProviderOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicLibraryAchievementsFilterOptions
        {
            get => _dynamicLibraryAchievementsFilterOptions;
            set => ReplaceCollection(_dynamicLibraryAchievementsFilterOptions, value, nameof(DynamicLibraryAchievementsFilterOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicLibraryAchievementsSortOptions
        {
            get => _dynamicLibraryAchievementsSortOptions;
            set => ReplaceCollection(_dynamicLibraryAchievementsSortOptions, value, nameof(DynamicLibraryAchievementsSortOptions));
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicLibraryAchievementsSortDirectionOptions
        {
            get => _dynamicLibraryAchievementsSortDirectionOptions;
            set => ReplaceCollection(_dynamicLibraryAchievementsSortDirectionOptions, value, nameof(DynamicLibraryAchievementsSortDirectionOptions));
        }

        [DontSerialize]
        public List<AchievementDetail> MostRecentUnlocks
        {
            get => _mostRecentUnlocks;
            set => SetValue(ref _mostRecentUnlocks, value);
        }

        [DontSerialize]
        public List<AchievementDetail> RarestRecentUnlocks
        {
            get => _rarestRecentUnlocks;
            set => SetValue(ref _rarestRecentUnlocks, value);
        }

        [DontSerialize]
        public List<AchievementDetail> MostRecentUnlocksTop3
        {
            get => _mostRecentUnlocksTop3;
            set => SetValue(ref _mostRecentUnlocksTop3, value);
        }

        [DontSerialize]
        public List<AchievementDetail> MostRecentUnlocksTop5
        {
            get => _mostRecentUnlocksTop5;
            set => SetValue(ref _mostRecentUnlocksTop5, value);
        }

        [DontSerialize]
        public List<AchievementDetail> MostRecentUnlocksTop10
        {
            get => _mostRecentUnlocksTop10;
            set => SetValue(ref _mostRecentUnlocksTop10, value);
        }

        [DontSerialize]
        public List<AchievementDetail> RarestRecentUnlocksTop3
        {
            get => _rarestRecentUnlocksTop3;
            set => SetValue(ref _rarestRecentUnlocksTop3, value);
        }

        [DontSerialize]
        public List<AchievementDetail> RarestRecentUnlocksTop5
        {
            get => _rarestRecentUnlocksTop5;
            set => SetValue(ref _rarestRecentUnlocksTop5, value);
        }

        [DontSerialize]
        public List<AchievementDetail> RarestRecentUnlocksTop10
        {
            get => _rarestRecentUnlocksTop10;
            set => SetValue(ref _rarestRecentUnlocksTop10, value);
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> SteamGames
        {
            get => _steamGames;
            set => ReplaceCollection(_steamGames, value, nameof(SteamGames));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> GOGGames
        {
            get => _gogGames;
            set => ReplaceCollection(_gogGames, value, nameof(GOGGames));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> EpicGames
        {
            get => _epicGames;
            set => ReplaceCollection(_epicGames, value, nameof(EpicGames));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> BattleNetGames
        {
            get => _battleNetGames;
            set => ReplaceCollection(_battleNetGames, value, nameof(BattleNetGames));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> EAGames
        {
            get => _eaGames;
            set => ReplaceCollection(_eaGames, value, nameof(EAGames));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> XboxGames
        {
            get => _xboxGames;
            set => ReplaceCollection(_xboxGames, value, nameof(XboxGames));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> PSNGames
        {
            get => _psnGames;
            set => ReplaceCollection(_psnGames, value, nameof(PSNGames));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> RetroAchievementsGames
        {
            get => _retroAchievementsGames;
            set => ReplaceCollection(_retroAchievementsGames, value, nameof(RetroAchievementsGames));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> AppleGames
        {
            get => _appleGames;
            set => ReplaceCollection(_appleGames, value, nameof(AppleGames));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> GooglePlayGames
        {
            get => _googlePlayGames;
            set => ReplaceCollection(_googlePlayGames, value, nameof(GooglePlayGames));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> HoyoverseGames
        {
            get => _hoyoverseGames;
            set => ReplaceCollection(_hoyoverseGames, value, nameof(HoyoverseGames));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> UbisoftGames
        {
            get => _ubisoftGames;
            set => ReplaceCollection(_ubisoftGames, value, nameof(UbisoftGames));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> RPCS3Games
        {
            get => _rpcs3Games;
            set => ReplaceCollection(_rpcs3Games, value, nameof(RPCS3Games));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> XeniaGames
        {
            get => _xeniaGames;
            set => ReplaceCollection(_xeniaGames, value, nameof(XeniaGames));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> ShadPS4Games
        {
            get => _shadPS4Games;
            set => ReplaceCollection(_shadPS4Games, value, nameof(ShadPS4Games));
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> ManualGames
        {
            get => _manualGames;
            set => ReplaceCollection(_manualGames, value, nameof(ManualGames));
        }

        private void ReplaceCollection<T>(BulkObservableCollection<T> target, IEnumerable<T> value, string propertyName)
        {
            var items = (value ?? new List<T>()).ToList();
            if (target.Count == items.Count && target.SequenceEqual(items))
            {
                return;
            }

            target.ReplaceAll(items);
            OnPropertyChanged(propertyName);
        }

        private static bool ResolveUseSeparateLockedIcons(
            Models.Settings.PersistedSettings settings,
            System.Guid? playniteGameId,
            bool fallbackValue)
        {
            if (settings == null || !playniteGameId.HasValue || playniteGameId.Value == System.Guid.Empty)
            {
                return fallbackValue;
            }

            return Services.GameCustomData.GameCustomDataLookup.ShouldUseSeparateLockedIcons(playniteGameId, settings);
        }
    }
}
