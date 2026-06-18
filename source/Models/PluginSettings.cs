using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services;
using PlayniteAchievements.ViewModels;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.Models
{
    /// <summary>
    /// Main settings class for PlayniteAchievements plugin.
    /// This class contains two kinds of members:
    /// 1) Persisted configuration (via PersistedSettings property, saved via Playnite's plugin settings JSON).
    /// 2) Runtime/theme integration surfaces (updated live, never serialized).
    ///
    /// Theme integration data is organized into:
    /// - Persisted: User-configurable settings
    /// - Theme: Unified modern theme integration data (per-game + library overview)
    /// - LegacyTheme: Legacy compatibility data (SuccessStory, Aniki ReMake, old inline properties)
    ///
    /// Keep runtime/theme members clearly marked with [DontSerialize] on both the backing field
    /// and the public property to prevent settings bloat and improve performance.
    /// </summary>
    public class PlayniteAchievementsSettings : ObservableObject
    {
        private static readonly List<AchievementDetail> EmptyAchievementList = new List<AchievementDetail>();
        private static readonly AchievementRarityStats EmptyRarityStats = new AchievementRarityStats();

        #region Composition Properties

        /// <summary>
        /// Persisted user settings (serialized to plugin settings JSON).
        /// </summary>
        public PersistedSettings Persisted { get; private set; }

        /// <summary>
        /// Unified modern theme integration data (runtime, not serialized).
        /// Contains both per-game achievement data and library overview data.
        /// </summary>
        [DontSerialize]
        private ModernThemeBindings _modernTheme;

        public ModernThemeBindings ModernTheme
        {
            get => _modernTheme ?? (_modernTheme = new ModernThemeBindings());
            private set => _modernTheme = value;
        }

        /// <summary>
        /// Legacy theme compatibility data (runtime, not serialized).
        /// Contains SuccessStory compatibility, Aniki ReMake compatibility, and old inline properties.
        /// </summary>
        [DontSerialize]
        private LegacyThemeBindings _legacyTheme;

        public LegacyThemeBindings LegacyTheme
        {
            get => _legacyTheme ?? (_legacyTheme = new LegacyThemeBindings());
            private set => _legacyTheme = value;
        }

        [DontSerialize]
        public event EventHandler DynamicThemeDefaultsChanged;

        #endregion

        #region Commands

        [DontSerialize]
        private ICommand _openFullscreenAchievementWindow;
        [DontSerialize]
        private ICommand _singleGameRefreshCommand;
        [DontSerialize]
        private ICommand _recentRefreshCommand;
        [DontSerialize]
        private ICommand _favoritesRefreshCommand;
        [DontSerialize]
        private ICommand _fullRefreshCommand;
        [DontSerialize]
        private ICommand _installedRefreshCommand;
        [DontSerialize]
        private ICommand _openAchievementWindow;
        [DontSerialize]
        private ICommand _openGameAchievementWindow;
        [DontSerialize]
        private ICommand _openViewAchievementsWindow;
        [DontSerialize]
        private ICommand _openManageAchievementsWindow;
        [DontSerialize]
        private ICommand _setDynamicAchievementsGameCommand;
        [DontSerialize]
        private ICommand _setDynamicAchievementsFilterCommand;
        [DontSerialize]
        private ICommand _sortDynamicAchievementsCommand;
        [DontSerialize]
        private ICommand _setDynamicAchievementsSortDirectionCommand;
        [DontSerialize]
        private ICommand _filterDynamicLibraryAchievementsByProviderCommand;
        [DontSerialize]
        private ICommand _setDynamicLibraryAchievementsFilterCommand;
        [DontSerialize]
        private ICommand _sortDynamicLibraryAchievementsCommand;
        [DontSerialize]
        private ICommand _setDynamicLibraryAchievementsSortDirectionCommand;
        [DontSerialize]
        private ICommand _filterDynamicGameSummariesByProviderCommand;
        [DontSerialize]
        private ICommand _setDynamicGameSummariesFilterCommand;
        [DontSerialize]
        private ICommand _sortDynamicGameSummariesCommand;
        [DontSerialize]
        private ICommand _setDynamicGameSummariesSortDirectionCommand;
        [DontSerialize]
        private ICommand _resetDynamicAchievementsCommand;
        [DontSerialize]
        private ICommand _resetDynamicLibraryAchievementsCommand;
        [DontSerialize]
        private ICommand _resetDynamicGameSummariesCommand;

        [DontSerialize]
        public ICommand OpenFullscreenAchievementWindow
        {
            get => _openFullscreenAchievementWindow;
            set => SetValue(ref _openFullscreenAchievementWindow, value);
        }

        [DontSerialize]
        public ICommand SingleGameRefreshCommand
        {
            get => _singleGameRefreshCommand;
            set => SetValue(ref _singleGameRefreshCommand, value);
        }

        [DontSerialize]
        public ICommand RecentRefreshCommand
        {
            get => _recentRefreshCommand;
            set => SetValue(ref _recentRefreshCommand, value);
        }

        [DontSerialize]
        public ICommand FavoritesRefreshCommand
        {
            get => _favoritesRefreshCommand;
            set => SetValue(ref _favoritesRefreshCommand, value);
        }

        [DontSerialize]
        public ICommand FullRefreshCommand
        {
            get => _fullRefreshCommand;
            set => SetValue(ref _fullRefreshCommand, value);
        }

        [DontSerialize]
        public ICommand InstalledRefreshCommand
        {
            get => _installedRefreshCommand;
            set => SetValue(ref _installedRefreshCommand, value);
        }

        [DontSerialize]
        public ICommand OpenAchievementWindow
        {
            get => _openAchievementWindow;
            set => SetValue(ref _openAchievementWindow, value);
        }

        [DontSerialize]
        public ICommand OpenGameAchievementWindow
        {
            get => _openGameAchievementWindow;
            set => SetValue(ref _openGameAchievementWindow, value);
        }

        [DontSerialize]
        public ICommand OpenViewAchievementsWindow
        {
            get => _openViewAchievementsWindow;
            set => SetValue(ref _openViewAchievementsWindow, value);
        }

        [DontSerialize]
        public ICommand OpenManageAchievementsWindow
        {
            get => _openManageAchievementsWindow;
            set => SetValue(ref _openManageAchievementsWindow, value);
        }

        [DontSerialize]
        public ICommand SetDynamicAchievementsGameCommand
        {
            get => _setDynamicAchievementsGameCommand;
            set => SetValue(ref _setDynamicAchievementsGameCommand, value);
        }

        [DontSerialize]
        public ICommand SetDynamicAchievementsFilterCommand
        {
            get => _setDynamicAchievementsFilterCommand;
            set => SetValue(ref _setDynamicAchievementsFilterCommand, value);
        }

        [DontSerialize]
        public ICommand SortDynamicAchievementsCommand
        {
            get => _sortDynamicAchievementsCommand;
            set => SetValue(ref _sortDynamicAchievementsCommand, value);
        }

        [DontSerialize]
        public ICommand SetDynamicAchievementsSortDirectionCommand
        {
            get => _setDynamicAchievementsSortDirectionCommand;
            set => SetValue(ref _setDynamicAchievementsSortDirectionCommand, value);
        }

        [DontSerialize]
        public ICommand FilterDynamicLibraryAchievementsByProviderCommand
        {
            get => _filterDynamicLibraryAchievementsByProviderCommand;
            set => SetValue(ref _filterDynamicLibraryAchievementsByProviderCommand, value);
        }

        [DontSerialize]
        public ICommand SetDynamicLibraryAchievementsFilterCommand
        {
            get => _setDynamicLibraryAchievementsFilterCommand;
            set => SetValue(ref _setDynamicLibraryAchievementsFilterCommand, value);
        }

        [DontSerialize]
        public ICommand SortDynamicLibraryAchievementsCommand
        {
            get => _sortDynamicLibraryAchievementsCommand;
            set => SetValue(ref _sortDynamicLibraryAchievementsCommand, value);
        }

        [DontSerialize]
        public ICommand SetDynamicLibraryAchievementsSortDirectionCommand
        {
            get => _setDynamicLibraryAchievementsSortDirectionCommand;
            set => SetValue(ref _setDynamicLibraryAchievementsSortDirectionCommand, value);
        }

        [DontSerialize]
        public ICommand FilterDynamicGameSummariesByProviderCommand
        {
            get => _filterDynamicGameSummariesByProviderCommand;
            set => SetValue(ref _filterDynamicGameSummariesByProviderCommand, value);
        }

        [DontSerialize]
        public ICommand SetDynamicGameSummariesFilterCommand
        {
            get => _setDynamicGameSummariesFilterCommand;
            set => SetValue(ref _setDynamicGameSummariesFilterCommand, value);
        }

        [DontSerialize]
        public ICommand SortDynamicGameSummariesCommand
        {
            get => _sortDynamicGameSummariesCommand;
            set => SetValue(ref _sortDynamicGameSummariesCommand, value);
        }

        [DontSerialize]
        public ICommand SetDynamicGameSummariesSortDirectionCommand
        {
            get => _setDynamicGameSummariesSortDirectionCommand;
            set => SetValue(ref _setDynamicGameSummariesSortDirectionCommand, value);
        }

        [DontSerialize]
        public ICommand ResetDynamicAchievementsCommand
        {
            get => _resetDynamicAchievementsCommand;
            set => SetValue(ref _resetDynamicAchievementsCommand, value);
        }

        [DontSerialize]
        public ICommand ResetDynamicLibraryAchievementsCommand
        {
            get => _resetDynamicLibraryAchievementsCommand;
            set => SetValue(ref _resetDynamicLibraryAchievementsCommand, value);
        }

        [DontSerialize]
        public ICommand ResetDynamicGameSummariesCommand
        {
            get => _resetDynamicGameSummariesCommand;
            set => SetValue(ref _resetDynamicGameSummariesCommand, value);
        }



        #endregion

        private void NotifyDynamicThemeDefaultsChanged(string propertyName)
        {
            OnPropertyChanged(propertyName);
            DynamicThemeDefaultsChanged?.Invoke(this, EventArgs.Empty);
        }

        private static void ExecuteThemeCommand(ICommand command, object value)
        {
            if (command == null)
            {
                return;
            }

            var parameter = value is DynamicThemeOption option ? option.Key : value;
            if (parameter is string text && string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (command.CanExecute(parameter))
            {
                command.Execute(parameter);
            }
        }

        private static string GetAchievementGroupFilter(string filterKey, string groupKey)
        {
            return GetAchievementGroupFilter(filterKey, new[] { groupKey });
        }

        private static string GetAchievementGroupFilter(string filterKey, IEnumerable<string> groupKeys)
        {
            return DynamicThemeOptionGroups.GetGroupSelection(
                filterKey,
                groupKeys,
                DynamicThemeOptionGroups.AchievementFilterGroupMap);
        }

        private static string GetGameSummaryGroupFilter(string filterKey, string groupKey)
        {
            return GetGameSummaryGroupFilter(filterKey, new[] { groupKey });
        }

        private static string GetGameSummaryGroupFilter(string filterKey, IEnumerable<string> groupKeys)
        {
            return DynamicThemeOptionGroups.GetGroupSelection(
                filterKey,
                groupKeys,
                DynamicThemeOptionGroups.GameSummaryFilterGroupMap);
        }

        private void SetDynamicAchievementsGroupFilter(string groupKey, object value)
        {
            SetDynamicAchievementsGroupFilter(new[] { groupKey }, value);
        }

        private void SetDynamicAchievementsGroupFilter(IEnumerable<string> groupKeys, object value)
        {
            ExecuteThemeCommand(
                SetDynamicAchievementsFilterCommand,
                DynamicThemeOptionGroups.SetGroupSelection(
                    DynamicAchievementsFilterKey,
                    groupKeys,
                    value is DynamicThemeOption option ? option.Key : value?.ToString(),
                    DynamicThemeOptionGroups.AchievementFilterKeyMap,
                    DynamicThemeOptionGroups.AchievementFilterGroupMap));
        }

        private void SetDynamicLibraryAchievementsGroupFilter(string groupKey, object value)
        {
            SetDynamicLibraryAchievementsGroupFilter(new[] { groupKey }, value);
        }

        private void SetDynamicLibraryAchievementsGroupFilter(IEnumerable<string> groupKeys, object value)
        {
            ExecuteThemeCommand(
                SetDynamicLibraryAchievementsFilterCommand,
                DynamicThemeOptionGroups.SetGroupSelection(
                    DynamicLibraryAchievementsFilterKey,
                    groupKeys,
                    value is DynamicThemeOption option ? option.Key : value?.ToString(),
                    DynamicThemeOptionGroups.AchievementFilterKeyMap,
                    DynamicThemeOptionGroups.AchievementFilterGroupMap));
        }

        private void SetDynamicGameSummariesGroupFilter(string groupKey, object value)
        {
            SetDynamicGameSummariesGroupFilter(new[] { groupKey }, value);
        }

        private void SetDynamicGameSummariesGroupFilter(IEnumerable<string> groupKeys, object value)
        {
            ExecuteThemeCommand(
                SetDynamicGameSummariesFilterCommand,
                DynamicThemeOptionGroups.SetGroupSelection(
                    DynamicGameSummariesFilterKey,
                    groupKeys,
                    value is DynamicThemeOption option ? option.Key : value?.ToString(),
                    DynamicThemeOptionGroups.GameSummaryFilterKeyMap,
                    DynamicThemeOptionGroups.GameSummaryFilterGroupMap));
        }

        #region Modern Theme Integration

        // === Per-Game Achievement Data ===

        [DontSerialize]
        public bool HasAchievements => ModernTheme.HasAchievements;

        [DontSerialize]
        public int AchievementCount => ModernTheme.AchievementCount;

        [DontSerialize]
        public int UnlockedCount => ModernTheme.UnlockedCount;

        [DontSerialize]
        public int LockedCount => ModernTheme.LockedCount;

        [DontSerialize]
        public double ProgressPercentage => ModernTheme.ProgressPercentage;

        [DontSerialize]
        public bool IsCompleted => ModernTheme.IsCompleted;

        [DontSerialize]
        public List<AchievementDetail> Achievements => ModernTheme.AllAchievements ?? EmptyAchievementList;

        [DontSerialize]
        public List<AchievementDetail> AchievementDefaultOrder => ModernTheme.AchievementDefaultOrder ?? EmptyAchievementList;

        [DontSerialize]
        public List<AchievementDetail> AchievementsNewestFirst => ModernTheme.AchievementsNewestFirst ?? EmptyAchievementList;

        [DontSerialize]
        public List<AchievementDetail> AchievementsOldestFirst => ModernTheme.AchievementsOldestFirst ?? EmptyAchievementList;

        [DontSerialize]
        public List<AchievementDetail> AchievementsRarityAsc => ModernTheme.AchievementsRarityAsc ?? EmptyAchievementList;

        [DontSerialize]
        public List<AchievementDetail> AchievementsRarityDesc => ModernTheme.AchievementsRarityDesc ?? EmptyAchievementList;

        [DontSerialize]
        public AchievementRarityStats Common => ModernTheme.Common ?? EmptyRarityStats;

        [DontSerialize]
        public AchievementRarityStats Uncommon => ModernTheme.Uncommon ?? EmptyRarityStats;

        [DontSerialize]
        public AchievementRarityStats Rare => ModernTheme.Rare ?? EmptyRarityStats;

        [DontSerialize]
        public AchievementRarityStats UltraRare => ModernTheme.UltraRare ?? EmptyRarityStats;

        [DontSerialize]
        public AchievementRarityStats RareAndUltraRare => ModernTheme.RareAndUltraRare ?? EmptyRarityStats;

        [DontSerialize]
        public List<AchievementDetail> DynamicAchievements => ModernTheme.DynamicAchievements ?? EmptyAchievementList;

        [DontSerialize]
        public string DynamicAchievementsGameKey
        {
            get => ModernTheme.DynamicAchievementsGameKey ?? string.Empty;
            set => ExecuteThemeCommand(SetDynamicAchievementsGameCommand, value);
        }

        [DontSerialize]
        public string DynamicAchievementsGameLabel => ModernTheme.DynamicAchievementsGameLabel ?? string.Empty;

        [DontSerialize]
        public string DynamicAchievementsFilterKey
        {
            get => ModernTheme.DynamicAchievementsFilterKey ?? DynamicThemeViewKeys.All;
            set => ExecuteThemeCommand(SetDynamicAchievementsFilterCommand, value);
        }

        [DontSerialize]
        public string DynamicAchievementsFilterLabel => ModernTheme.DynamicAchievementsFilterLabel ?? DynamicThemeViewKeys.All;

        [DontSerialize]
        public string DynamicAchievementsStatusFilterKey
        {
            get => GetAchievementGroupFilter(DynamicAchievementsFilterKey, DynamicThemeOptionGroups.AchievementStatusGroup);
            set => SetDynamicAchievementsGroupFilter(DynamicThemeOptionGroups.AchievementStatusGroup, value);
        }

        [DontSerialize]
        public string DynamicAchievementsProgressFilterKey
        {
            get => GetAchievementGroupFilter(DynamicAchievementsFilterKey, DynamicThemeOptionGroups.AchievementProgressGroup);
            set => SetDynamicAchievementsGroupFilter(DynamicThemeOptionGroups.AchievementProgressGroup, value);
        }

        [DontSerialize]
        public string DynamicAchievementsRarityFilterKey
        {
            get => GetAchievementGroupFilter(DynamicAchievementsFilterKey, DynamicThemeOptionGroups.AchievementRarityGroup);
            set => SetDynamicAchievementsGroupFilter(DynamicThemeOptionGroups.AchievementRarityGroup, value);
        }

        [DontSerialize]
        public string DynamicAchievementsTrophyFilterKey
        {
            get => GetAchievementGroupFilter(DynamicAchievementsFilterKey, DynamicThemeOptionGroups.AchievementTrophyGroup);
            set => SetDynamicAchievementsGroupFilter(DynamicThemeOptionGroups.AchievementTrophyGroup, value);
        }

        [DontSerialize]
        public string DynamicAchievementsCustomizationFilterKey
        {
            get => GetAchievementGroupFilter(DynamicAchievementsFilterKey, DynamicThemeOptionGroups.AchievementCustomizationGroups);
            set => SetDynamicAchievementsGroupFilter(DynamicThemeOptionGroups.AchievementCustomizationGroups, value);
        }

        [DontSerialize]
        public string DynamicAchievementsSortKey
        {
            get => ModernTheme.DynamicAchievementsSortKey ?? DynamicThemeViewKeys.Default;
            set => ExecuteThemeCommand(SortDynamicAchievementsCommand, value);
        }

        [DontSerialize]
        public string DynamicAchievementsSortLabel => ModernTheme.DynamicAchievementsSortLabel ?? DynamicThemeViewKeys.Default;

        [DontSerialize]
        public string DynamicAchievementsSortDirectionKey
        {
            get => ModernTheme.DynamicAchievementsSortDirectionKey ?? DynamicThemeViewKeys.Descending;
            set => ExecuteThemeCommand(SetDynamicAchievementsSortDirectionCommand, value);
        }

        [DontSerialize]
        public string DynamicAchievementsSortDirectionLabel => ModernTheme.DynamicAchievementsSortDirectionLabel ?? DynamicThemeViewKeys.Descending;

        [DontSerialize]
        public string DynamicAchievementsDefaultFilterKey
        {
            get => ModernTheme.DynamicAchievementsDefaultFilterKey ?? DynamicThemeViewKeys.All;
            set
            {
                ModernTheme.DynamicAchievementsDefaultFilterKey = value;
                NotifyDynamicThemeDefaultsChanged(nameof(DynamicAchievementsDefaultFilterKey));
            }
        }

        [DontSerialize]
        public string DynamicAchievementsDefaultSortKey
        {
            get => ModernTheme.DynamicAchievementsDefaultSortKey ?? DynamicThemeViewKeys.Default;
            set
            {
                ModernTheme.DynamicAchievementsDefaultSortKey = value;
                NotifyDynamicThemeDefaultsChanged(nameof(DynamicAchievementsDefaultSortKey));
            }
        }

        [DontSerialize]
        public string DynamicAchievementsDefaultSortDirectionKey
        {
            get => ModernTheme.DynamicAchievementsDefaultSortDirectionKey ?? DynamicThemeViewKeys.Descending;
            set
            {
                ModernTheme.DynamicAchievementsDefaultSortDirectionKey = value;
                NotifyDynamicThemeDefaultsChanged(nameof(DynamicAchievementsDefaultSortDirectionKey));
            }
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicAchievementsFilterOptions => ModernTheme.DynamicAchievementsFilterOptions;

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicAchievementsSortOptions => ModernTheme.DynamicAchievementsSortOptions;

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicAchievementsSortDirectionOptions => ModernTheme.DynamicAchievementsSortDirectionOptions;

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicAchievementGameOptions => ModernTheme.DynamicAchievementGameOptions;

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicAchievementStatusFilterOptions => ModernTheme.DynamicAchievementStatusFilterOptions;

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicAchievementProgressFilterOptions => ModernTheme.DynamicAchievementProgressFilterOptions;

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicAchievementRarityFilterOptions => ModernTheme.DynamicAchievementRarityFilterOptions;

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicAchievementTrophyFilterOptions => ModernTheme.DynamicAchievementTrophyFilterOptions;

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicAchievementCustomizationFilterOptions => ModernTheme.DynamicAchievementCustomizationFilterOptions;

        // === Library Overview Data ===

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> CompletedGamesAsc
        {
            get => ModernTheme.CompletedGamesAsc;
            set => ModernTheme.CompletedGamesAsc = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> CompletedGamesDesc
        {
            get => ModernTheme.CompletedGamesDesc;
            set => ModernTheme.CompletedGamesDesc = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> GameSummariesAsc
        {
            get => ModernTheme.GameSummariesAsc;
            set => ModernTheme.GameSummariesAsc = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> GameSummariesDesc
        {
            get => ModernTheme.GameSummariesDesc;
            set => ModernTheme.GameSummariesDesc = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> DynamicGameSummaries
        {
            get => ModernTheme.DynamicGameSummaries;
            set => ModernTheme.DynamicGameSummaries = value;
        }

        [DontSerialize]
        public string DynamicGameSummariesProviderKey
        {
            get => ModernTheme.DynamicGameSummariesProviderKey ?? DynamicThemeViewKeys.All;
            set => ExecuteThemeCommand(FilterDynamicGameSummariesByProviderCommand, value);
        }

        [DontSerialize]
        public string DynamicGameSummariesProviderLabel => ModernTheme.DynamicGameSummariesProviderLabel ?? DynamicThemeViewKeys.All;

        [DontSerialize]
        public string DynamicGameSummariesFilterKey
        {
            get => ModernTheme.DynamicGameSummariesFilterKey ?? DynamicThemeViewKeys.All;
            set => ExecuteThemeCommand(SetDynamicGameSummariesFilterCommand, value);
        }

        [DontSerialize]
        public string DynamicGameSummariesFilterLabel => ModernTheme.DynamicGameSummariesFilterLabel ?? DynamicThemeViewKeys.All;

        [DontSerialize]
        public string DynamicGameSummariesProgressFilterKey
        {
            get => GetGameSummaryGroupFilter(DynamicGameSummariesFilterKey, DynamicThemeOptionGroups.GameProgressGroups);
            set => SetDynamicGameSummariesGroupFilter(DynamicThemeOptionGroups.GameProgressGroups, value);
        }

        [DontSerialize]
        public string DynamicGameSummariesActivityFilterKey
        {
            get => GetGameSummaryGroupFilter(DynamicGameSummariesFilterKey, DynamicThemeOptionGroups.GameActivityGroups);
            set => SetDynamicGameSummariesGroupFilter(DynamicThemeOptionGroups.GameActivityGroups, value);
        }

        [DontSerialize]
        public string DynamicGameSummariesSortKey
        {
            get => ModernTheme.DynamicGameSummariesSortKey ?? DynamicThemeViewKeys.LastUnlock;
            set => ExecuteThemeCommand(SortDynamicGameSummariesCommand, value);
        }

        [DontSerialize]
        public string DynamicGameSummariesSortLabel => ModernTheme.DynamicGameSummariesSortLabel ?? DynamicThemeViewKeys.LastUnlock;

        [DontSerialize]
        public string DynamicGameSummariesSortDirectionKey
        {
            get => ModernTheme.DynamicGameSummariesSortDirectionKey ?? DynamicThemeViewKeys.Descending;
            set => ExecuteThemeCommand(SetDynamicGameSummariesSortDirectionCommand, value);
        }

        [DontSerialize]
        public string DynamicGameSummariesSortDirectionLabel => ModernTheme.DynamicGameSummariesSortDirectionLabel ?? DynamicThemeViewKeys.Descending;

        [DontSerialize]
        public string DynamicGameSummariesDefaultProviderKey
        {
            get => ModernTheme.DynamicGameSummariesDefaultProviderKey ?? DynamicThemeViewKeys.All;
            set
            {
                ModernTheme.DynamicGameSummariesDefaultProviderKey = value;
                NotifyDynamicThemeDefaultsChanged(nameof(DynamicGameSummariesDefaultProviderKey));
            }
        }

        [DontSerialize]
        public string DynamicGameSummariesDefaultFilterKey
        {
            get => ModernTheme.DynamicGameSummariesDefaultFilterKey ?? DynamicThemeViewKeys.All;
            set
            {
                ModernTheme.DynamicGameSummariesDefaultFilterKey = value;
                NotifyDynamicThemeDefaultsChanged(nameof(DynamicGameSummariesDefaultFilterKey));
            }
        }

        [DontSerialize]
        public string DynamicGameSummariesDefaultSortKey
        {
            get => ModernTheme.DynamicGameSummariesDefaultSortKey ?? DynamicThemeViewKeys.LastUnlock;
            set
            {
                ModernTheme.DynamicGameSummariesDefaultSortKey = value;
                NotifyDynamicThemeDefaultsChanged(nameof(DynamicGameSummariesDefaultSortKey));
            }
        }

        [DontSerialize]
        public string DynamicGameSummariesDefaultSortDirectionKey
        {
            get => ModernTheme.DynamicGameSummariesDefaultSortDirectionKey ?? DynamicThemeViewKeys.Descending;
            set
            {
                ModernTheme.DynamicGameSummariesDefaultSortDirectionKey = value;
                NotifyDynamicThemeDefaultsChanged(nameof(DynamicGameSummariesDefaultSortDirectionKey));
            }
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicGameSummariesProviderOptions => ModernTheme.DynamicGameSummariesProviderOptions;

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicGameSummariesFilterOptions => ModernTheme.DynamicGameSummariesFilterOptions;

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicGameSummariesSortOptions => ModernTheme.DynamicGameSummariesSortOptions;

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicGameSummariesSortDirectionOptions => ModernTheme.DynamicGameSummariesSortDirectionOptions;

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicGameProgressFilterOptions => ModernTheme.DynamicGameProgressFilterOptions;

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicGameActivityFilterOptions => ModernTheme.DynamicGameActivityFilterOptions;

        [DontSerialize]
        public List<AchievementDetail> AllAchievementsUnlockAsc
        {
            get => ModernTheme.AllAchievementsUnlockAsc ?? EmptyAchievementList;
            set => ModernTheme.AllAchievementsUnlockAsc = value;
        }

        [DontSerialize]
        public List<AchievementDetail> AllAchievementsUnlockDesc
        {
            get => ModernTheme.AllAchievementsUnlockDesc ?? EmptyAchievementList;
            set => ModernTheme.AllAchievementsUnlockDesc = value;
        }

        [DontSerialize]
        public List<AchievementDetail> AllAchievementsRarityAsc
        {
            get => ModernTheme.AllAchievementsRarityAsc ?? EmptyAchievementList;
            set => ModernTheme.AllAchievementsRarityAsc = value;
        }

        [DontSerialize]
        public List<AchievementDetail> AllAchievementsRarityDesc
        {
            get => ModernTheme.AllAchievementsRarityDesc ?? EmptyAchievementList;
            set => ModernTheme.AllAchievementsRarityDesc = value;
        }

        [DontSerialize]
        public List<AchievementDetail> DynamicLibraryAchievements
        {
            get => ModernTheme.DynamicLibraryAchievements ?? EmptyAchievementList;
            set => ModernTheme.DynamicLibraryAchievements = value;
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsProviderKey
        {
            get => ModernTheme.DynamicLibraryAchievementsProviderKey ?? DynamicThemeViewKeys.All;
            set => ExecuteThemeCommand(FilterDynamicLibraryAchievementsByProviderCommand, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsProviderLabel => ModernTheme.DynamicLibraryAchievementsProviderLabel ?? DynamicThemeViewKeys.All;

        [DontSerialize]
        public string DynamicLibraryAchievementsFilterKey
        {
            get => ModernTheme.DynamicLibraryAchievementsFilterKey ?? DynamicThemeViewKeys.All;
            set => ExecuteThemeCommand(SetDynamicLibraryAchievementsFilterCommand, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsFilterLabel => ModernTheme.DynamicLibraryAchievementsFilterLabel ?? DynamicThemeViewKeys.All;

        [DontSerialize]
        public string DynamicLibraryAchievementsStatusFilterKey
        {
            get => GetAchievementGroupFilter(DynamicLibraryAchievementsFilterKey, DynamicThemeOptionGroups.AchievementStatusGroup);
            set => SetDynamicLibraryAchievementsGroupFilter(DynamicThemeOptionGroups.AchievementStatusGroup, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsProgressFilterKey
        {
            get => GetAchievementGroupFilter(DynamicLibraryAchievementsFilterKey, DynamicThemeOptionGroups.AchievementProgressGroup);
            set => SetDynamicLibraryAchievementsGroupFilter(DynamicThemeOptionGroups.AchievementProgressGroup, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsRarityFilterKey
        {
            get => GetAchievementGroupFilter(DynamicLibraryAchievementsFilterKey, DynamicThemeOptionGroups.AchievementRarityGroup);
            set => SetDynamicLibraryAchievementsGroupFilter(DynamicThemeOptionGroups.AchievementRarityGroup, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsTrophyFilterKey
        {
            get => GetAchievementGroupFilter(DynamicLibraryAchievementsFilterKey, DynamicThemeOptionGroups.AchievementTrophyGroup);
            set => SetDynamicLibraryAchievementsGroupFilter(DynamicThemeOptionGroups.AchievementTrophyGroup, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsCustomizationFilterKey
        {
            get => GetAchievementGroupFilter(DynamicLibraryAchievementsFilterKey, DynamicThemeOptionGroups.AchievementCustomizationGroups);
            set => SetDynamicLibraryAchievementsGroupFilter(DynamicThemeOptionGroups.AchievementCustomizationGroups, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsSortKey
        {
            get => ModernTheme.DynamicLibraryAchievementsSortKey ?? DynamicThemeViewKeys.UnlockTime;
            set => ExecuteThemeCommand(SortDynamicLibraryAchievementsCommand, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsSortLabel => ModernTheme.DynamicLibraryAchievementsSortLabel ?? DynamicThemeViewKeys.UnlockTime;

        [DontSerialize]
        public string DynamicLibraryAchievementsSortDirectionKey
        {
            get => ModernTheme.DynamicLibraryAchievementsSortDirectionKey ?? DynamicThemeViewKeys.Descending;
            set => ExecuteThemeCommand(SetDynamicLibraryAchievementsSortDirectionCommand, value);
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsSortDirectionLabel => ModernTheme.DynamicLibraryAchievementsSortDirectionLabel ?? DynamicThemeViewKeys.Descending;

        [DontSerialize]
        public string DynamicLibraryAchievementsDefaultProviderKey
        {
            get => ModernTheme.DynamicLibraryAchievementsDefaultProviderKey ?? DynamicThemeViewKeys.All;
            set
            {
                ModernTheme.DynamicLibraryAchievementsDefaultProviderKey = value;
                NotifyDynamicThemeDefaultsChanged(nameof(DynamicLibraryAchievementsDefaultProviderKey));
            }
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsDefaultFilterKey
        {
            get => ModernTheme.DynamicLibraryAchievementsDefaultFilterKey ?? DynamicThemeViewKeys.All;
            set
            {
                ModernTheme.DynamicLibraryAchievementsDefaultFilterKey = value;
                NotifyDynamicThemeDefaultsChanged(nameof(DynamicLibraryAchievementsDefaultFilterKey));
            }
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsDefaultSortKey
        {
            get => ModernTheme.DynamicLibraryAchievementsDefaultSortKey ?? DynamicThemeViewKeys.UnlockTime;
            set
            {
                ModernTheme.DynamicLibraryAchievementsDefaultSortKey = value;
                NotifyDynamicThemeDefaultsChanged(nameof(DynamicLibraryAchievementsDefaultSortKey));
            }
        }

        [DontSerialize]
        public string DynamicLibraryAchievementsDefaultSortDirectionKey
        {
            get => ModernTheme.DynamicLibraryAchievementsDefaultSortDirectionKey ?? DynamicThemeViewKeys.Descending;
            set
            {
                ModernTheme.DynamicLibraryAchievementsDefaultSortDirectionKey = value;
                NotifyDynamicThemeDefaultsChanged(nameof(DynamicLibraryAchievementsDefaultSortDirectionKey));
            }
        }

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicLibraryAchievementsProviderOptions => ModernTheme.DynamicLibraryAchievementsProviderOptions;

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicLibraryAchievementsFilterOptions => ModernTheme.DynamicLibraryAchievementsFilterOptions;

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicLibraryAchievementsSortOptions => ModernTheme.DynamicLibraryAchievementsSortOptions;

        [DontSerialize]
        public ObservableCollection<DynamicThemeOption> DynamicLibraryAchievementsSortDirectionOptions => ModernTheme.DynamicLibraryAchievementsSortDirectionOptions;

        // All recent unlocks across all games (newest first).
        [DontSerialize]
        public List<AchievementDetail> MostRecentUnlocks
        {
            get => ModernTheme.MostRecentUnlocks ?? EmptyAchievementList;
            set => ModernTheme.MostRecentUnlocks = value;
        }

        [DontSerialize]
        public List<AchievementDetail> MostRecentUnlocksTop3
        {
            get => ModernTheme.MostRecentUnlocksTop3 ?? EmptyAchievementList;
            set => ModernTheme.MostRecentUnlocksTop3 = value;
        }

        [DontSerialize]
        public List<AchievementDetail> MostRecentUnlocksTop5
        {
            get => ModernTheme.MostRecentUnlocksTop5 ?? EmptyAchievementList;
            set => ModernTheme.MostRecentUnlocksTop5 = value;
        }

        [DontSerialize]
        public List<AchievementDetail> MostRecentUnlocksTop10
        {
            get => ModernTheme.MostRecentUnlocksTop10 ?? EmptyAchievementList;
            set => ModernTheme.MostRecentUnlocksTop10 = value;
        }

        // All rare recent unlocks across all games (rarest first), limited to the last 180 days.
        [DontSerialize]
        public List<AchievementDetail> RarestRecentUnlocks
        {
            get => ModernTheme.RarestRecentUnlocks ?? EmptyAchievementList;
            set => ModernTheme.RarestRecentUnlocks = value;
        }

        [DontSerialize]
        public List<AchievementDetail> RarestRecentUnlocksTop3
        {
            get => ModernTheme.RarestRecentUnlocksTop3 ?? EmptyAchievementList;
            set => ModernTheme.RarestRecentUnlocksTop3 = value;
        }

        [DontSerialize]
        public List<AchievementDetail> RarestRecentUnlocksTop5
        {
            get => ModernTheme.RarestRecentUnlocksTop5 ?? EmptyAchievementList;
            set => ModernTheme.RarestRecentUnlocksTop5 = value;
        }

        [DontSerialize]
        public List<AchievementDetail> RarestRecentUnlocksTop10
        {
            get => ModernTheme.RarestRecentUnlocksTop10 ?? EmptyAchievementList;
            set => ModernTheme.RarestRecentUnlocksTop10 = value;
        }

        // Per-provider game lists
        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> SteamGames
        {
            get => ModernTheme.SteamGames;
            set => ModernTheme.SteamGames = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> GOGGames
        {
            get => ModernTheme.GOGGames;
            set => ModernTheme.GOGGames = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> EpicGames
        {
            get => ModernTheme.EpicGames;
            set => ModernTheme.EpicGames = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> BattleNetGames
        {
            get => ModernTheme.BattleNetGames;
            set => ModernTheme.BattleNetGames = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> EAGames
        {
            get => ModernTheme.EAGames;
            set => ModernTheme.EAGames = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> XboxGames
        {
            get => ModernTheme.XboxGames;
            set => ModernTheme.XboxGames = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> PSNGames
        {
            get => ModernTheme.PSNGames;
            set => ModernTheme.PSNGames = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> RetroAchievementsGames
        {
            get => ModernTheme.RetroAchievementsGames;
            set => ModernTheme.RetroAchievementsGames = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> AppleGames
        {
            get => ModernTheme.AppleGames;
            set => ModernTheme.AppleGames = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> GooglePlayGames
        {
            get => ModernTheme.GooglePlayGames;
            set => ModernTheme.GooglePlayGames = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> HoyoverseGames
        {
            get => ModernTheme.HoyoverseGames;
            set => ModernTheme.HoyoverseGames = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> UbisoftGames
        {
            get => ModernTheme.UbisoftGames;
            set => ModernTheme.UbisoftGames = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> RPCS3Games
        {
            get => ModernTheme.RPCS3Games;
            set => ModernTheme.RPCS3Games = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> XeniaGames
        {
            get => ModernTheme.XeniaGames;
            set => ModernTheme.XeniaGames = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> ShadPS4Games
        {
            get => ModernTheme.ShadPS4Games;
            set => ModernTheme.ShadPS4Games = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> ManualGames
        {
            get => ModernTheme.ManualGames;
            set => ModernTheme.ManualGames = value;
        }

        #endregion

        #region Legacy Theme Compatibility

        // === SuccessStory Compatibility ===


        [DontSerialize]
        public AchievementRarityStats NoCommon => Uncommon;

        [DontSerialize]
        public bool HasData
        {
            // Legacy themes (including migrated SuccessStory themes) use HasData as a
            // selected-game flag, so keep this bound to per-game achievement state.
            get => ModernTheme.HasAchievements;
            set => ModernTheme.HasAchievements = value;
        }

        [DontSerialize]
        public bool HasDataAllGames
        {
            get => LegacyTheme.HasDataAllGames;
            set => LegacyTheme.HasDataAllGames = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> GamesWithAchievements
        {
            get => LegacyTheme.GamesWithAchievements;
            set => LegacyTheme.GamesWithAchievements = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> PlatinumGames
        {
            get => LegacyTheme.PlatinumGames;
            set => LegacyTheme.PlatinumGames = value;
        }

        [DontSerialize]
        public int TotalTrophies
        {
            get => LegacyTheme.TotalTrophies;
            set => LegacyTheme.TotalTrophies = value;
        }

        [DontSerialize]
        public int PlatinumTrophies
        {
            get => LegacyTheme.PlatinumTrophies;
            set => LegacyTheme.PlatinumTrophies = value;
        }

        [DontSerialize]
        public int GoldTrophies
        {
            get => LegacyTheme.GoldTrophies;
            set => LegacyTheme.GoldTrophies = value;
        }

        [DontSerialize]
        public int SilverTrophies
        {
            get => LegacyTheme.SilverTrophies;
            set => LegacyTheme.SilverTrophies = value;
        }

        [DontSerialize]
        public int BronzeTrophies
        {
            get => LegacyTheme.BronzeTrophies;
            set => LegacyTheme.BronzeTrophies = value;
        }

        [DontSerialize]
        public AchievementRarityStats TotalCommon
        {
            get => ModernTheme.TotalCommon ?? EmptyRarityStats;
            set => ModernTheme.TotalCommon = value;
        }

        [DontSerialize]
        public AchievementRarityStats TotalUncommon
        {
            get => ModernTheme.TotalUncommon ?? EmptyRarityStats;
            set => ModernTheme.TotalUncommon = value;
        }

        [DontSerialize]
        public AchievementRarityStats TotalRare
        {
            get => ModernTheme.TotalRare ?? EmptyRarityStats;
            set => ModernTheme.TotalRare = value;
        }

        [DontSerialize]
        public AchievementRarityStats TotalUltraRare
        {
            get => ModernTheme.TotalUltraRare ?? EmptyRarityStats;
            set => ModernTheme.TotalUltraRare = value;
        }

        [DontSerialize]
        public AchievementRarityStats TotalRareAndUltraRare
        {
            get => ModernTheme.TotalRareAndUltraRare ?? EmptyRarityStats;
            set => ModernTheme.TotalRareAndUltraRare = value;
        }

        [DontSerialize]
        public AchievementRarityStats TotalOverall
        {
            get => ModernTheme.TotalOverall ?? EmptyRarityStats;
            set => ModernTheme.TotalOverall = value;
        }

        [DontSerialize]
        public int CollectorScore
        {
            get => ModernTheme.CollectorScore;
            set => ModernTheme.CollectorScore = value;
        }

        [DontSerialize]
        public int CollectorLevel
        {
            get => ModernTheme.CollectorLevel;
            set => ModernTheme.CollectorLevel = value;
        }

        [DontSerialize]
        public double CollectorLevelProgress
        {
            get => ModernTheme.CollectorLevelProgress;
            set => ModernTheme.CollectorLevelProgress = value;
        }

        [DontSerialize]
        public string CollectorRank
        {
            get => ModernTheme.CollectorRank;
            set => ModernTheme.CollectorRank = value;
        }

        [DontSerialize]
        public int PrestigeScore
        {
            get => ModernTheme.PrestigeScore;
            set => ModernTheme.PrestigeScore = value;
        }

        [DontSerialize]
        public int PrestigeLevel
        {
            get => ModernTheme.PrestigeLevel;
            set => ModernTheme.PrestigeLevel = value;
        }

        [DontSerialize]
        public double PrestigeLevelProgress
        {
            get => ModernTheme.PrestigeLevelProgress;
            set => ModernTheme.PrestigeLevelProgress = value;
        }

        [DontSerialize]
        public string PrestigeRank
        {
            get => ModernTheme.PrestigeRank;
            set => ModernTheme.PrestigeRank = value;
        }

        [DontSerialize]
        public int Level
        {
            get => LegacyTheme.Level;
            set => LegacyTheme.Level = value;
        }

        [DontSerialize]
        public double LevelProgress
        {
            get => LegacyTheme.LevelProgress;
            set => LegacyTheme.LevelProgress = value;
        }

        [DontSerialize]
        public string Rank
        {
            get => LegacyTheme.Rank;
            set => LegacyTheme.Rank = value;
        }

        [DontSerialize]
        public bool EnableIntegrationCompact => true;

        [DontSerialize]
        public bool EnableIntegrationButton => true;

        [DontSerialize]
        public bool EnableIntegrationViewItem => true;

        [DontSerialize]
        public bool EnableIntegrationCompactUnlocked => true;

        [DontSerialize]
        public bool EnableIntegrationCompactLocked => true;

        [DontSerialize]
        public bool EnableIntegrationList => true;

        [DontSerialize]
        public bool EnableIntegrationUserStats => true;

        [DontSerialize]
        public bool EnableIntegrationChart => true;

        [DontSerialize]
        public bool Is100Percent => LegacyTheme.Is100Percent;

        [DontSerialize]
        public int Locked => LegacyTheme.Locked;

        [DontSerialize]
        public List<AchievementDetail> ListAchievements => LegacyTheme.ListAchievements ?? EmptyAchievementList;

        [DontSerialize]
        public List<AchievementDetail> ListAchUnlockDateAsc => LegacyTheme.ListAchUnlockDateAsc ?? EmptyAchievementList;

        [DontSerialize]
        public List<AchievementDetail> ListAchUnlockDateDesc => LegacyTheme.ListAchUnlockDateDesc ?? EmptyAchievementList;

        // === Old Inline Properties ===

        [DontSerialize]
        public bool HasDataLegacy
        {
            get => LegacyTheme.HasData;
            set => LegacyTheme.HasData = value;
        }

        [DontSerialize]
        public int Total
        {
            get => LegacyTheme.Total;
            set => LegacyTheme.Total = value;
        }

        [DontSerialize]
        public int Unlocked
        {
            get => LegacyTheme.Unlocked;
            set => LegacyTheme.Unlocked = value;
        }

        [DontSerialize]
        public double Percent
        {
            get => LegacyTheme.Percent;
            set => LegacyTheme.Percent = value;
        }

        // === Aniki ReMake Compatibility ===

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> AllGamesWithAchievements
        {
            get => LegacyTheme.AllGamesWithAchievements;
            set => LegacyTheme.AllGamesWithAchievements = value;
        }

        [DontSerialize]
        public ObservableCollection<GameAchievementSummary> PlatinumGamesAscending
        {
            get => LegacyTheme.PlatinumGamesAscending;
            set => LegacyTheme.PlatinumGamesAscending = value;
        }

        [DontSerialize]
        public string GSTotal
        {
            get => LegacyTheme.GSTotal;
            set => LegacyTheme.GSTotal = value;
        }

        [DontSerialize]
        public string GSPlat
        {
            get => LegacyTheme.GSPlat;
            set => LegacyTheme.GSPlat = value;
        }

        [DontSerialize]
        public string GS90
        {
            get => LegacyTheme.GS90;
            set => LegacyTheme.GS90 = value;
        }

        [DontSerialize]
        public string GS30
        {
            get => LegacyTheme.GS30;
            set => LegacyTheme.GS30 = value;
        }

        [DontSerialize]
        public string GS15
        {
            get => LegacyTheme.GS15;
            set => LegacyTheme.GS15 = value;
        }

        [DontSerialize]
        public string GSScore
        {
            get => LegacyTheme.GSScore;
            set => LegacyTheme.GSScore = value;
        }

        [DontSerialize]
        public string GSLevel
        {
            get => LegacyTheme.GSLevel;
            set => LegacyTheme.GSLevel = value;
        }

        [DontSerialize]
        public double GSLevelProgress
        {
            get => LegacyTheme.GSLevelProgress;
            set => LegacyTheme.GSLevelProgress = value;
        }

        [DontSerialize]
        public string GSRank
        {
            get => LegacyTheme.GSRank;
            set => LegacyTheme.GSRank = value;
        }

        #endregion

        #region Internal Fields and Methods

        [DontSerialize]
        internal PlayniteAchievementsPlugin _plugin;

        private void AttachThemeHandlers()
        {
            if (ModernTheme != null)
            {
                ModernTheme.PropertyChanged -= ModernTheme_PropertyChanged;
                ModernTheme.PropertyChanged += ModernTheme_PropertyChanged;
            }

            if (LegacyTheme != null)
            {
                LegacyTheme.PropertyChanged -= LegacyTheme_PropertyChanged;
                LegacyTheme.PropertyChanged += LegacyTheme_PropertyChanged;
            }
        }

        private void DetachThemeHandlers()
        {
            if (_modernTheme != null)
            {
                _modernTheme.PropertyChanged -= ModernTheme_PropertyChanged;
            }

            if (_legacyTheme != null)
            {
                _legacyTheme.PropertyChanged -= LegacyTheme_PropertyChanged;
            }
        }

        private void AttachPersistedHandlers()
        {
            if (Persisted == null)
            {
                return;
            }

            Persisted.PropertyChanged -= Persisted_PropertyChanged;
            Persisted.PropertyChanged += Persisted_PropertyChanged;
        }

        private void DetachPersistedHandlers(PersistedSettings persisted = null)
        {
            var target = persisted ?? Persisted;
            if (target == null)
            {
                return;
            }

            target.PropertyChanged -= Persisted_PropertyChanged;
        }

        private void Persisted_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            var propertyName = e?.PropertyName;
            if (AchievementDisplayItem.IsAppearanceSettingPropertyName(propertyName))
            {
                RefreshThemeDisplayItemsFromPersisted();
            }

            if (!string.IsNullOrWhiteSpace(propertyName))
            {
                OnPropertyChanged($"Persisted.{propertyName}");
            }
        }

        private void ModernTheme_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            var propertyName = e?.PropertyName;
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return;
            }

            switch (propertyName)
            {
                case nameof(ModernThemeBindings.HasAchievements):
                    OnPropertyChanged(nameof(HasAchievements));
                    OnPropertyChanged(nameof(HasData));
                    break;
                case nameof(ModernThemeBindings.AchievementCount):
                    OnPropertyChanged(nameof(AchievementCount));
                    break;
                case nameof(ModernThemeBindings.UnlockedCount):
                    OnPropertyChanged(nameof(UnlockedCount));
                    break;
                case nameof(ModernThemeBindings.ProgressPercentage):
                    OnPropertyChanged(nameof(ProgressPercentage));
                    break;
            }
        }

        private void LegacyTheme_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            var propertyName = e?.PropertyName;
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return;
            }

            switch (propertyName)
            {
                case nameof(LegacyThemeBindings.HasData):
                    OnPropertyChanged(nameof(HasDataLegacy));
                    break;
                case nameof(LegacyThemeBindings.Total):
                    OnPropertyChanged(nameof(Total));
                    break;
                case nameof(LegacyThemeBindings.Unlocked):
                    OnPropertyChanged(nameof(Unlocked));
                    break;
                case nameof(LegacyThemeBindings.Percent):
                    OnPropertyChanged(nameof(Percent));
                    break;
            }
        }

        private void RefreshThemeDisplayItemsFromPersisted()
        {
            var persisted = Persisted;
            if (persisted == null)
            {
                return;
            }

            ModernTheme.RefreshDisplayItems(
                persisted.ShowHiddenIcon,
                persisted.ShowHiddenTitle,
                persisted.ShowHiddenDescription,
                persisted.ShowHiddenSuffix,
                persisted.ShowLockedIcon,
                persisted.UseSeparateLockedIconsWhenAvailable,
                persisted.ShowRarityGlow,
                persisted.ShowCompactListRarityBar);
        }

        /// <summary>
        /// Copies persisted settings from another settings instance.
        /// Used by the ViewModel when applying settings changes.
        /// </summary>
        internal void CopyPersistedFrom(PlayniteAchievementsSettings other)
        {
            if (other == null)
            {
                return;
            }

            // Copy the entire PersistedSettings object
            DetachPersistedHandlers();
            Persisted = other.Persisted?.Clone() ?? new PersistedSettings();
            AttachPersistedHandlers();
            RefreshThemeDisplayItemsFromPersisted();
            OnPropertyChanged(nameof(Persisted));
        }

        /// <summary>
        /// Initializes theme properties that are not persisted.
        /// Called when settings are loaded from storage to ensure DontSerialize
        /// properties are properly initialized.
        /// </summary>
        internal void InitializeThemeProperties()
        {
            if (ModernTheme == null)
            {
                ModernTheme = new ModernThemeBindings();
            }
            if (LegacyTheme == null)
            {
                LegacyTheme = new LegacyThemeBindings();
            }

            AttachThemeHandlers();
            AttachPersistedHandlers();
            RefreshThemeDisplayItemsFromPersisted();
        }

        #endregion

        #region Constructors

        /// <summary>
        /// Parameterless constructor for deserialization.
        /// Initializes all composition properties with default instances.
        /// </summary>
        public PlayniteAchievementsSettings()
        {
            Persisted = new PersistedSettings();
            ModernTheme = new ModernThemeBindings();
            LegacyTheme = new LegacyThemeBindings();
            AttachThemeHandlers();
            AttachPersistedHandlers();
            RefreshThemeDisplayItemsFromPersisted();
        }

        /// <summary>
        /// Constructor for initialization with plugin reference.
        /// Initializes all composition properties with default instances.
        /// </summary>
        public PlayniteAchievementsSettings(PlayniteAchievementsPlugin plugin) : this()
        {
            _plugin = plugin;
        }

        #endregion

        #region Game Context

        [DontSerialize]
        private SelectedGameBindingContext _selectedGame;

        /// <summary>
        /// The currently selected game in Playnite's main view.
        /// Exposed for fullscreen themes to bind backgrounds, covers, logos, etc.
        /// Similar to SuccessStory's GameContext property.
        ///
        /// IMPORTANT: This is a stored property that gets set when OnGameSelected is called,
        /// not a computed property that queries MainView.SelectedGames. This ensures that
        /// fullscreen themes receive the correct game context even when MainView.SelectedGames
        /// may not be synchronized with the game context passed to GameContextChanged.
        /// </summary>
        [DontSerialize]
        public SelectedGameBindingContext SelectedGame
        {
            get => _selectedGame;
            set
            {
                if (AreSameSelectedGame(value, _selectedGame))
                {
                    return;
                }

                _selectedGame = value;
                OnPropertyChanged(nameof(SelectedGame));
                OnPropertyChanged(nameof(SelectedGameCoverPath));
                OnPropertyChanged(nameof(SelectedGameBackgroundPath));
            }
        }

        private bool AreSameSelectedGame(SelectedGameBindingContext left, SelectedGameBindingContext right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null)
            {
                return false;
            }

            return left.Id == right.Id &&
                   string.Equals(left.CoverImage, right.CoverImage, StringComparison.Ordinal) &&
                   string.Equals(left.BackgroundImage, right.BackgroundImage, StringComparison.Ordinal);
        }

        public void SetSelectedGame(Game game)
        {
            if (game == null)
            {
                SelectedGame = null;
                return;
            }

            SelectedGame = new SelectedGameBindingContext(
                game,
                () => SelectedGameCoverPath,
                () => SelectedGameBackgroundPath);
        }

        /// <summary>
        /// Full file path to the selected game's cover image.
        /// Themes can bind to this property to get the cover image path.
        /// </summary>
        [DontSerialize]
        public string SelectedGameCoverPath => ResolveSelectedGameImagePath(_selectedGame?.Game?.CoverImage);

        [DontSerialize]
        public string SelectedGameBackgroundPath => ResolveSelectedGameImagePath(_selectedGame?.Game?.BackgroundImage);

        private string ResolveSelectedGameImagePath(string imageId)
        {
            if (string.IsNullOrWhiteSpace(imageId))
            {
                return null;
            }

            var resolvedPath = _plugin?.PlayniteApi?.Database?.GetFullFilePath(imageId);
            if (string.IsNullOrWhiteSpace(resolvedPath))
            {
                return null;
            }

            resolvedPath = resolvedPath.Trim();
            if (resolvedPath.StartsWith("pack://", StringComparison.OrdinalIgnoreCase) ||
                resolvedPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                resolvedPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return resolvedPath;
            }

            return System.IO.File.Exists(resolvedPath)
                ? resolvedPath
                : null;
        }

        #endregion
    }
}

