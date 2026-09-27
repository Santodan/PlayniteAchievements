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
            OpenEditorRegistry.Closed += OnEditorClosed;
        }

        private readonly HashSet<Guid> _waitingForEditor = new HashSet<Guid>();

        /// <summary>
        /// Brings a game held back while its editor was open up to date once it closes. Quietly:
        /// whatever unlock the held write would have announced happened while the user was looking
        /// at the game, and announcing it on close would read as the editor doing it.
        /// </summary>
        private void OnEditorClosed(Guid gameId)
        {
            lock (_pendingSync)
            {
                if (!_waitingForEditor.Remove(gameId))
                {
                    return;
                }
            }

            try
            {
                Maintain(gameId, announceUnlocks: false);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Auto capstone maintenance after the editor closed failed for gameId={gameId}.");
            }
        }

        private readonly object _pendingSync = new object();
        private readonly Dictionary<Guid, List<AchievementUnlockedEventArgs>> _pending =
            new Dictionary<Guid, List<AchievementUnlockedEventArgs>>();

        /// <summary>
        /// True for a game whose capstone unlock is announced by someone else. The in-game monitor
        /// sends a game's own unlocks after the refresh that recorded them returns, so a capstone
        /// announced from inside that refresh would arrive ahead of the achievement that earned it.
        /// Such a game's crossings are held for <see cref="TakePendingAnnouncements"/> instead.
        /// </summary>
        public Func<Guid, bool> DefersAnnouncements { get; set; }

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
        /// The capstone unlocks held for a game whose announcements are deferred, emptying the
        /// hold. Empty when there are none.
        /// </summary>
        public IReadOnlyList<AchievementUnlockedEventArgs> TakePendingAnnouncements(Guid gameId)
        {
            lock (_pendingSync)
            {
                if (!_pending.TryGetValue(gameId, out var held))
                {
                    return Array.Empty<AchievementUnlockedEventArgs>();
                }

                _pending.Remove(gameId);
                return held;
            }
        }

        /// <summary>
        /// Brings one game's auto capstone up to date, and announces the unlock when the game has
        /// just been finished.
        /// </summary>
        /// <param name="announceUnlocks">
        /// False to keep the capstone in step without announcing it, for an edit the user made by
        /// hand rather than an unlock the game reported.
        /// </param>
        public void Maintain(Guid gameId, bool announceUnlocks = true)
        {
            if (gameId == Guid.Empty || _store == null || _overridesService == null)
            {
                return;
            }

            // The open editor saves the definitions from its own rows, which would overwrite this
            // write; the game is brought up to date when the editor closes instead.
            if (OpenEditorRegistry.IsOpen(gameId))
            {
                lock (_pendingSync)
                {
                    _waitingForEditor.Add(gameId);
                }

                return;
            }

            var definitions = _store.LoadOrDefault(gameId)?.CustomAchievements;
            if (definitions == null || definitions.Count == 0)
            {
                return;
            }

            // Every auto capstone, not just the first: a game can hold one per category, each
            // standing for its own, so maintaining only one left the rest frozen at the values
            // they were authored with.
            var indexes = new List<int>();
            for (var i = 0; i < definitions.Count; i++)
            {
                if (definitions[i]?.IsAutoCapstone == true)
                {
                    indexes.Add(i);
                }
            }

            if (indexes.Count == 0)
            {
                return;
            }

            var gameData = _resolveGameData?.Invoke(gameId);
            var replacement = definitions
                .Select(definition => definition?.Clone())
                .Where(definition => definition != null)
                .ToList();

            var announce = new List<CustomAchievementDefinition>();
            var changed = false;

            foreach (var index in indexes)
            {
                var current = definitions[index];
                var apiName = CustomAchievementProjectionService.BuildApiName(current.Id);

                // Everything it stands for except itself, so it is not left waiting on its own
                // unlock. The scope comes from its hydrated row: the definition's category is only
                // ever the default, while the category the user filed it in lives in the overrides.
                var derived = AutoCapstoneCalculator.DeriveForCapstone(
                    gameData?.Achievements,
                    apiName,
                    current.IsWholeGameAutoCapstone,
                    current.Category);
                if (derived == null)
                {
                    continue;
                }

                var updated = current.Clone();
                updated.Unlocked = derived.Unlocked;
                updated.UnlockTimeUtc = derived.Unlocked ? derived.UnlockTimeUtc : null;
                updated.GlobalPercentUnlocked = derived.GlobalPercentUnlocked;
                updated.Rarity = derived.Rarity ?? updated.Rarity;
                if (!HasChanges(current, updated))
                {
                    continue;
                }

                replacement[index] = updated;
                changed = true;

                // Only the crossing is worth announcing: an already-finished category stays
                // finished, and a capstone is authored with its unlock already worked out, so this
                // cannot fire for one that was complete before the capstone existed.
                if (!current.Unlocked && updated.Unlocked)
                {
                    announce.Add(updated);
                }
            }

            if (!changed)
            {
                return;
            }

            // One write for the whole set: each store update fans out a synchronous whole-library
            // recompute, so one per capstone would pay for it several times over.
            _overridesService.SetCustomAchievements(gameId, replacement);

            if (!announceUnlocks || announce.Count == 0)
            {
                return;
            }

            var events = announce
                .Select(definition => BuildUnlockEvent(gameId, definition, gameData))
                .ToList();
            if (DefersAnnouncements?.Invoke(gameId) == true)
            {
                lock (_pendingSync)
                {
                    if (!_pending.TryGetValue(gameId, out var held))
                    {
                        held = new List<AchievementUnlockedEventArgs>();
                        _pending[gameId] = held;
                    }

                    held.AddRange(events);
                }

                return;
            }

            foreach (var args in events)
            {
                _notifyUnlocked?.Invoke(args);
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
        /// The capstone's unlock, for the same path every other unlock takes, flagged as a capstone
        /// so it gets the completion-grade treatment rather than reading as one more achievement.
        /// </summary>
        private static AchievementUnlockedEventArgs BuildUnlockEvent(
            Guid gameId,
            CustomAchievementDefinition definition,
            GameAchievementData gameData)
        {
            var game = API.Instance?.Database?.Games?.Get(gameId);
            return new AchievementUnlockedEventArgs
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
            };
        }

        private static string ResolvePlayniteAsset(string databasePath)
        {
            return string.IsNullOrWhiteSpace(databasePath)
                ? null
                : API.Instance?.Database?.GetFullFilePath(databasePath);
        }
    }
}
