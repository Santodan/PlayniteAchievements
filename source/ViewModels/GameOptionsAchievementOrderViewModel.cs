using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels
{
    public sealed class GameOptionsAchievementOrderViewModel : ObservableObject
    {
        private readonly Guid _gameId;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly AchievementDataService _achievementDataService;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;

        private bool _hasAchievements;
        private bool _hasCustomOrder;

        public GameOptionsAchievementOrderViewModel(
            Guid gameId,
            AchievementOverridesService achievementOverridesService,
            AchievementDataService achievementDataService,
            PlayniteAchievementsSettings settings,
            ILogger logger)
        {
            _gameId = gameId;
            _achievementOverridesService = achievementOverridesService ?? throw new ArgumentNullException(nameof(achievementOverridesService));
            _achievementDataService = achievementDataService ?? throw new ArgumentNullException(nameof(achievementDataService));
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

                var gameData = _achievementDataService.GetGameAchievementData(_gameId);
                var achievements = gameData?.Achievements?
                    .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                    .ToList() ?? new List<AchievementDetail>();
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

                var projectionOptions = AchievementProjectionService.CreateOptions(_settings, gameData);
                var rows = orderedAchievements
                    .Select(a => AchievementProjectionService.CreateDisplayItem(
                        gameData,
                        a,
                        projectionOptions,
                        _gameId))
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
            ReloadData();
            return true;
        }

        public bool MoveItems(
            IReadOnlyList<AchievementDisplayItem> draggedItems,
            AchievementDisplayItem targetItem,
            bool insertAfterTarget)
        {
            if (draggedItems == null || draggedItems.Count == 0 || targetItem == null)
            {
                return false;
            }

            var source = AchievementRows.ToList();
            var selectedIndexes = draggedItems
                .Select(item => source.IndexOf(item))
                .Where(index => index >= 0)
                .Distinct()
                .OrderBy(index => index)
                .ToList();

            if (selectedIndexes.Count == 0)
            {
                return false;
            }

            var targetIndex = source.IndexOf(targetItem);
            if (targetIndex < 0)
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

        public bool MoveItemsToEnd(IReadOnlyList<AchievementDisplayItem> draggedItems)
        {
            if (draggedItems == null || draggedItems.Count == 0 || AchievementRows.Count == 0)
            {
                return false;
            }

            var source = AchievementRows.ToList();
            var selectedIndexes = draggedItems
                .Select(item => source.IndexOf(item))
                .Where(index => index >= 0)
                .Distinct()
                .OrderBy(index => index)
                .ToList();

            if (selectedIndexes.Count == 0)
            {
                return false;
            }

            if (!AchievementOrderHelper.TryReorder(
                source,
                selectedIndexes,
                source.Count - 1,
                insertAfterTarget: true,
                out var reordered))
            {
                return false;
            }

            CollectionHelper.SynchronizeCollection(AchievementRows, reordered);
            PersistCurrentOrder();
            return true;
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
    }
}
