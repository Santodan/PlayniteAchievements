using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels.Items;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// Lists the game's goals in priority order, followed by every other achievement so goals can
    /// be set from here as well. Only the goal block is reorderable.
    /// </summary>
    public sealed class ManageAchievementsGoalsViewModel : ObservableObject
    {
        private readonly Guid _gameId;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly ManageAchievementsDataSnapshotProvider _gameDataSnapshotProvider;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;

        private bool _hasGoals;
        private bool _hasAchievements;

        public ManageAchievementsGoalsViewModel(
            Guid gameId,
            AchievementOverridesService achievementOverridesService,
            ManageAchievementsDataSnapshotProvider gameDataSnapshotProvider,
            PlayniteAchievementsSettings settings,
            ILogger logger)
        {
            _gameId = gameId;
            _achievementOverridesService = achievementOverridesService ?? throw new ArgumentNullException(nameof(achievementOverridesService));
            _gameDataSnapshotProvider = gameDataSnapshotProvider ?? throw new ArgumentNullException(nameof(gameDataSnapshotProvider));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger;

            GoalRows = new ObservableCollection<AchievementDisplayItem>();
            ReloadData();
        }

        public ObservableCollection<AchievementDisplayItem> GoalRows { get; }

        public bool HasGoals
        {
            get => _hasGoals;
            private set => SetValue(ref _hasGoals, value);
        }

        public bool HasAchievements
        {
            get => _hasAchievements;
            private set => SetValue(ref _hasAchievements, value);
        }

        public void ReloadData()
        {
            try
            {
                var revealedStateByApiName = GoalRows
                    .Where(row => row != null && !string.IsNullOrWhiteSpace(row.ApiName))
                    .GroupBy(row => row.ApiName.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().IsRevealed, StringComparer.OrdinalIgnoreCase);

                var gameData = _gameDataSnapshotProvider.GetHydratedGameData();
                var achievements = gameData?.Achievements?
                    .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                    .ToList() ?? new List<AchievementDetail>();

                PruneUnlockedGoals(gameData, achievements);

                // Hydration already resolved IsGoal (false once unlocked) and GoalOrderIndex, so
                // the tab shows exactly what the achievement surfaces pin. Still-locked
                // achievements follow so any of them can be promoted from here; unlocked ones are
                // left out because they can never become a goal.
                var orderedAchievements = AchievementSortHelper
                    .CreateDefaultSortedDetailList(achievements)
                    .Where(a => a.IsGoal || !a.Unlocked)
                    .OrderBy(a => a.IsGoal ? 0 : 1)
                    .ThenBy(a => a.IsGoal ? a.GoalOrderIndex : 0)
                    .ToList();

                var appearanceSnapshot = AchievementDisplayItem.CreateAppearanceSettingsSnapshot(
                    _settings,
                    _gameId,
                    gameData?.UseSeparateLockedIconsWhenAvailable);
                var categoryMemo = new AchievementDisplayItem.CategoryPresentationMemo();

                var rows = orderedAchievements
                    .Select(a => AchievementDisplayItem.Create(
                        gameData,
                        a,
                        _settings,
                        playniteGameIdOverride: _gameId,
                        appearanceSettings: appearanceSnapshot,
                        categoryMemo: categoryMemo))
                    .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                    .ToList();

                foreach (var row in rows)
                {
                    var apiName = (row.ApiName ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(apiName))
                    {
                        continue;
                    }

                    if (revealedStateByApiName.TryGetValue(apiName, out var isRevealed))
                    {
                        row.IsRevealed = isRevealed;
                    }
                }

                CollectionHelper.SynchronizeCollection(GoalRows, rows);
                HasAchievements = rows.Count > 0;
                HasGoals = rows.Any(row => row.IsGoal);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed loading goal rows for gameId={_gameId}");
                CollectionHelper.SynchronizeCollection(GoalRows, new List<AchievementDisplayItem>());
                HasAchievements = false;
                HasGoals = false;
            }
        }

        /// <summary>
        /// Promotes or retires a single achievement from within this tab.
        /// </summary>
        public bool SetGoal(string apiName, bool isGoal)
        {
            var normalized = (apiName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            _achievementOverridesService.SetAchievementGoal(_gameId, normalized, isGoal);
            _gameDataSnapshotProvider.Invalidate();
            ReloadData();
            return true;
        }

        public bool ClearGoals()
        {
            if (!HasGoals)
            {
                return false;
            }

            _achievementOverridesService.SetGoalAchievements(_gameId, Array.Empty<string>());
            _gameDataSnapshotProvider.Invalidate();
            ReloadData();
            return true;
        }

        public bool MoveItemsByApiName(
            IReadOnlyList<string> draggedApiNames,
            string targetApiName,
            bool insertAfterTarget)
        {
            if (draggedApiNames == null || draggedApiNames.Count == 0 || string.IsNullOrWhiteSpace(targetApiName))
            {
                return false;
            }

            var source = GoalRows.ToList();
            var selectedIndexes = ResolveSelectedIndexes(source, draggedApiNames);
            var targetIndex = source.FindIndex(item =>
                string.Equals(
                    (item?.ApiName ?? string.Empty).Trim(),
                    targetApiName.Trim(),
                    StringComparison.OrdinalIgnoreCase));

            // Only the goal block reorders. Dropping onto a non-goal row would otherwise pull
            // goals down past the divider and silently reshuffle the whole list.
            if (targetIndex < 0 || source[targetIndex]?.IsGoal != true)
            {
                return false;
            }

            return TryMoveItems(source, selectedIndexes, targetIndex, insertAfterTarget);
        }

        public bool MoveItemsToEndByApiName(IReadOnlyList<string> draggedApiNames)
        {
            if (draggedApiNames == null || draggedApiNames.Count == 0 || GoalRows.Count == 0)
            {
                return false;
            }

            var source = GoalRows.ToList();
            var selectedIndexes = ResolveSelectedIndexes(source, draggedApiNames);

            // "End" means the end of the goal block, not the end of the full achievement list.
            var lastGoalIndex = source.FindLastIndex(item => item?.IsGoal == true);
            return lastGoalIndex >= 0 &&
                   TryMoveItems(source, selectedIndexes, lastGoalIndex, insertAfterTarget: true);
        }

        private bool TryMoveItems(
            List<AchievementDisplayItem> source,
            IReadOnlyList<int> selectedIndexes,
            int targetIndex,
            bool insertAfterTarget)
        {
            if (source == null ||
                source.Count == 0 ||
                selectedIndexes == null ||
                selectedIndexes.Count == 0 ||
                targetIndex < 0)
            {
                return false;
            }

            if (!AchievementOrderHelper.TryReorder(
                source,
                selectedIndexes,
                targetIndex,
                insertAfterTarget,
                out var reordered))
            {
                return false;
            }

            CollectionHelper.SynchronizeCollection(GoalRows, reordered);
            PersistCurrentOrder();
            return true;
        }

        private static List<int> ResolveSelectedIndexes(
            IReadOnlyList<AchievementDisplayItem> source,
            IReadOnlyList<string> draggedApiNames)
        {
            var normalizedApiNames = AchievementOrderHelper.NormalizeApiNames(draggedApiNames);
            if (normalizedApiNames.Count == 0)
            {
                return new List<int>();
            }

            var selectedApiNameSet = new HashSet<string>(normalizedApiNames, StringComparer.OrdinalIgnoreCase);
            var indexes = new List<int>();

            for (var i = 0; i < source.Count; i++)
            {
                var row = source[i];
                var apiName = (row?.ApiName ?? string.Empty).Trim();
                // Non-goal rows never travel, even if a stale drag payload names one.
                if (row?.IsGoal == true &&
                    !string.IsNullOrWhiteSpace(apiName) &&
                    selectedApiNameSet.Contains(apiName))
                {
                    indexes.Add(i);
                }
            }

            return indexes;
        }

        private void PersistCurrentOrder()
        {
            var orderedApiNames = GoalRows
                .Where(row => row != null && row.IsGoal && !string.IsNullOrWhiteSpace(row.ApiName))
                .Select(row => row.ApiName)
                .ToList();

            _achievementOverridesService.SetGoalAchievements(_gameId, orderedApiNames);
            _gameDataSnapshotProvider.Invalidate();
            HasGoals = orderedApiNames.Count > 0;
        }

        /// <summary>
        /// Drops goals whose achievement has since been unlocked. Hydration already hides them, so
        /// this only keeps the stored list from accumulating completed entries.
        /// </summary>
        private void PruneUnlockedGoals(GameAchievementData gameData, IReadOnlyList<AchievementDetail> achievements)
        {
            var storedGoals = gameData?.GoalAchievements;
            if (storedGoals == null || storedGoals.Count == 0)
            {
                return;
            }

            var unlockedApiNames = achievements
                .Where(a => a.Unlocked)
                .Select(a => a.ApiName)
                .ToList();
            if (unlockedApiNames.Count == 0)
            {
                return;
            }

            if (_achievementOverridesService.PruneUnlockedGoals(_gameId, unlockedApiNames))
            {
                _gameDataSnapshotProvider.Invalidate();
            }
        }
    }
}
