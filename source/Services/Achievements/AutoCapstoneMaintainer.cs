using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Keeps a game's auto capstone in step with the achievements it stands for.
    /// </summary>
    /// <remarks>
    /// This runs after a refresh rather than when the capstone is read, because the two things it
    /// derives -- how rare the rarest achievement is, and whether they are all unlocked -- only
    /// change when provider data does. Recomputing on refresh is therefore exact rather than an
    /// approximation, and it writes into the stored definition, which is what both readers use:
    /// hydration and the summary merger each project custom achievements from it.
    /// </remarks>
    public sealed class AutoCapstoneMaintainer
    {
        private readonly GameCustomDataStore _store;
        private readonly AchievementOverridesService _overridesService;
        private readonly Func<Guid, GameAchievementData> _resolveGameData;
        private readonly Action<AchievementUnlockedEventArgs> _notifyUnlocked;
        private readonly ILogger _logger;

        public AutoCapstoneMaintainer(
            GameCustomDataStore store,
            AchievementOverridesService overridesService,
            Func<Guid, GameAchievementData> resolveGameData,
            Action<AchievementUnlockedEventArgs> notifyUnlocked,
            ILogger logger)
        {
            _store = store;
            _overridesService = overridesService;
            _resolveGameData = resolveGameData;
            _notifyUnlocked = notifyUnlocked;
            _logger = logger;
        }

        /// <summary>Maintains every game a refresh touched.</summary>
        public void Maintain(IEnumerable<Guid> gameIds)
        {
            foreach (var gameId in gameIds ?? Enumerable.Empty<Guid>())
            {
                try
                {
                    Maintain(gameId);
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"Auto capstone maintenance failed for gameId={gameId}.");
                }
            }
        }

        /// <summary>
        /// Brings one game's auto capstone up to date, and announces the unlock when the game has
        /// just been finished.
        /// </summary>
        public void Maintain(Guid gameId)
        {
            if (gameId == Guid.Empty || _store == null || _overridesService == null)
            {
                return;
            }

            var definitions = _store.LoadOrDefault(gameId)?.CustomAchievements;
            var index = definitions?.FindIndex(definition => definition?.IsAutoCapstone == true) ?? -1;
            if (index < 0)
            {
                return;
            }

            var current = definitions[index];
            var apiName = CustomAchievementProjectionService.BuildApiName(current.Id);
            var gameData = _resolveGameData?.Invoke(gameId);

            // Everything except the capstone itself: it stands for the others, so counting itself
            // would leave it waiting on its own unlock.
            var derived = AutoCapstoneCalculator.Derive(gameData?.Achievements?
                .Where(achievement => !string.Equals(achievement?.ApiName, apiName, StringComparison.OrdinalIgnoreCase)));
            if (derived == null)
            {
                return;
            }

            var updated = current.Clone();
            updated.Unlocked = derived.Unlocked;
            updated.UnlockTimeUtc = derived.Unlocked ? derived.UnlockTimeUtc : null;
            updated.GlobalPercentUnlocked = derived.GlobalPercentUnlocked;
            updated.Rarity = derived.Rarity ?? updated.Rarity;
            if (!HasChanges(current, updated))
            {
                return;
            }

            var replacement = definitions
                .Select(definition => definition?.Clone())
                .Where(definition => definition != null)
                .ToList();
            replacement[index] = updated;
            _overridesService.SetCustomAchievements(gameId, replacement);

            // Only the crossing is worth announcing: an already-finished game stays finished, and
            // the capstone is authored with its unlock already worked out, so this cannot fire for
            // a game that was complete before the capstone existed.
            if (!current.Unlocked && updated.Unlocked)
            {
                AnnounceUnlock(gameId, updated, gameData);
            }
        }

        private static bool HasChanges(CustomAchievementDefinition current, CustomAchievementDefinition updated)
        {
            return current.Unlocked != updated.Unlocked ||
                   !Nullable.Equals(current.UnlockTimeUtc, updated.UnlockTimeUtc) ||
                   !Nullable.Equals(current.GlobalPercentUnlocked, updated.GlobalPercentUnlocked) ||
                   !string.Equals(current.Rarity, updated.Rarity, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Sends the capstone's unlock down the same path every other unlock takes, flagged as a
        /// capstone so it gets the completion-grade treatment rather than reading as one more
        /// achievement.
        /// </summary>
        private void AnnounceUnlock(
            Guid gameId,
            CustomAchievementDefinition definition,
            GameAchievementData gameData)
        {
            if (_notifyUnlocked == null)
            {
                return;
            }

            var game = API.Instance?.Database?.Games?.Get(gameId);
            _notifyUnlocked(new AchievementUnlockedEventArgs
            {
                PlayniteGameId = gameId,
                GameName = game?.Name ?? gameData?.GameName,
                ProviderKey = CustomAchievementProjectionService.ProviderKey,
                GameIconPath = ResolvePlayniteAsset(game?.Icon),
                GameCoverPath = ResolvePlayniteAsset(game?.CoverImage),
                ApiName = CustomAchievementProjectionService.BuildApiName(definition.Id),
                DisplayName = definition.DisplayName,
                Description = definition.Description,
                IconPath = definition.UnlockedIconPath,
                LockedIconPath = definition.LockedIconPath,
                GlobalPercent = definition.GlobalPercentUnlocked,
                RarityTier = definition.Rarity,
                TrophyType = definition.TrophyType,
                Points = definition.Points,
                ScaledPoints = definition.ScaledPoints,
                UnlockTimeUtc = definition.UnlockTimeUtc,
                IsCapstone = true
            });
        }

        private static string ResolvePlayniteAsset(string databasePath)
        {
            return string.IsNullOrWhiteSpace(databasePath)
                ? null
                : API.Instance?.Database?.GetFullFilePath(databasePath);
        }
    }
}
