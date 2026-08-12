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
    /// Reorders the achievements the user is working toward. Goal membership is set from the
    /// achievement row context menu; this tab owns the order and removal.
    /// </summary>
    public sealed class ManageAchievementsGoalsViewModel : ObservableObject
    {
        private readonly Guid _gameId;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly ManageAchievementsDataSnapshotProvider _gameDataSnapshotProvider;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;

        private bool _hasGoals;

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
                // the tab shows exactly what the achievement surfaces pin.
                var goalAchievements = achievements
                    .Where(a => a.IsGoal)
                    .OrderBy(a => a.GoalOrderIndex)
                    .ToList();

                var appearanceSnapshot = AchievementDisplayItem.CreateAppearanceSettingsSnapshot(
                    _settings,
                    _gameId,
                    gameData?.UseSeparateLockedIconsWhenAvailable);
                var categoryMemo = new AchievementDisplayItem.CategoryPresentationMemo();

                var rows = goalAchievements
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
                HasGoals = rows.Count > 0;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed loading goal rows for gameId={_gameId}");
                CollectionHelper.SynchronizeCollection(GoalRows, new List<AchievementDisplayItem>());
                HasGoals = false;
            }
        }

        public bool RemoveGoal(string apiName)
        {
            var normalized = (apiName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            _achievementOverridesService.SetAchievementGoal(_gameId, normalized, isGoal: false);
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
            return TryMoveItems(source, selectedIndexes, source.Count - 1, insertAfterTarget: true);
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
                var apiName = (source[i]?.ApiName ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(apiName) && selectedApiNameSet.Contains(apiName))
                {
                    indexes.Add(i);
                }
            }

            return indexes;
        }

        private void PersistCurrentOrder()
        {
            var orderedApiNames = GoalRows
                .Where(row => row != null && !string.IsNullOrWhiteSpace(row.ApiName))
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
