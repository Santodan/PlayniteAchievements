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
    public sealed class ManageAchievementsAchievementOrderViewModel : ObservableObject
    {
        private readonly Guid _gameId;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly ManageAchievementsDataSnapshotProvider _gameDataSnapshotProvider;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;

        private Dictionary<string, int> _defaultOrderIndexByApiName =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private bool _hasAchievements;
        private bool _hasCustomOrder;

        public ManageAchievementsAchievementOrderViewModel(
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

            AchievementRows = new ObservableCollection<AchievementDisplayItem>();
            ReloadData();
        }

        public ObservableCollection<AchievementDisplayItem> AchievementRows { get; }

        public bool HasAchievements
        {
            get => _hasAchievements;
            private set => SetValue(ref _hasAchievements, value);
        }

        public bool HasCustomOrder
        {
            get => _hasCustomOrder;
            private set => SetValue(ref _hasCustomOrder, value);
        }

        public void ReloadData()
        {
            try
            {
                var revealedStateByApiName = AchievementRows
                    .Where(row => row != null && !string.IsNullOrWhiteSpace(row.ApiName))
                    .GroupBy(row => row.ApiName.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().IsRevealed, StringComparer.OrdinalIgnoreCase);

                var gameData = _gameDataSnapshotProvider.GetHydratedGameData();
                var achievements = gameData?.Achievements?
                    .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                    .ToList() ?? new List<AchievementDetail>();
                _defaultOrderIndexByApiName = BuildOrderIndexMap(achievements);
                HasCustomOrder = gameData?.AchievementOrder != null && gameData.AchievementOrder.Count > 0;

                List<AchievementDetail> orderedAchievements;
                if (HasCustomOrder)
                {
                    orderedAchievements = AchievementOrderHelper.ApplyOrder(
                        achievements,
                        a => a.ApiName,
                        gameData.AchievementOrder);
                }
                else
                {
                    orderedAchievements = achievements;
                }

                // Per-game invariants hoisted out of the row loop: the appearance snapshot and
                // category art/order resolution are identical for every row in this pass.
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

                CollectionHelper.SynchronizeCollection(AchievementRows, rows);
                HasAchievements = rows.Count > 0;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed loading achievement order rows for gameId={_gameId}");
                _defaultOrderIndexByApiName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                CollectionHelper.SynchronizeCollection(AchievementRows, new List<AchievementDisplayItem>());
                HasAchievements = false;
                HasCustomOrder = false;
            }
        }

        public bool ResetCustomOrder()
        {
            if (!HasCustomOrder)
            {
                return false;
            }

            _achievementOverridesService.SetAchievementOrderOverride(_gameId, Array.Empty<string>());
            CollectionHelper.SynchronizeCollection(
                AchievementRows,
                AchievementRows
                    .OrderBy(row => ResolveDefaultOrderIndex(row))
                    .ThenBy(row => row?.DisplayName ?? row?.ApiName, StringComparer.OrdinalIgnoreCase)
                    .ToList());
            HasCustomOrder = false;
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

            var source = AchievementRows.ToList();
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
            if (draggedApiNames == null || draggedApiNames.Count == 0 || AchievementRows.Count == 0)
            {
                return false;
            }

            var source = AchievementRows.ToList();
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

            CollectionHelper.SynchronizeCollection(AchievementRows, reordered);
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
            var orderedApiNames = AchievementRows
                .Where(row => row != null && !string.IsNullOrWhiteSpace(row.ApiName))
                .Select(row => row.ApiName)
                .ToList();

            _achievementOverridesService.SetAchievementOrderOverride(_gameId, orderedApiNames);
            HasCustomOrder = orderedApiNames.Count > 0;
        }

        private static Dictionary<string, int> BuildOrderIndexMap(IEnumerable<AchievementDetail> achievements)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (var achievement in achievements ?? Enumerable.Empty<AchievementDetail>())
            {
                var apiName = (achievement?.ApiName ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(apiName) && !map.ContainsKey(apiName))
                {
                    map[apiName] = index;
                }

                index++;
            }

            return map;
        }

        private int ResolveDefaultOrderIndex(AchievementDisplayItem row)
        {
            var apiName = (row?.ApiName ?? string.Empty).Trim();
            return !string.IsNullOrWhiteSpace(apiName) &&
                   _defaultOrderIndexByApiName.TryGetValue(apiName, out var index)
                ? index
                : int.MaxValue;
        }
    }
}
