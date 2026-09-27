using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Gives games an auto capstone automatically, once each, when the setting is on.
    /// </summary>
    /// <remarks>
    /// A game is handled once and never again: it gets an authored auto capstone standing for the
    /// whole game, or its own platinum nominated, or, when it already had a capstone, nothing at
    /// all. Whichever it was, the game is marked, so a capstone the user deletes stays deleted.
    /// A game with no provider data yet is left unmarked and looked at again after a later refresh.
    /// </remarks>
    public sealed class AutoCapstoneGenerator
    {
        private const string BaseId = "auto-capstone";

        private readonly GameCustomDataStore _store;
        private readonly AchievementOverridesService _overridesService;
        private readonly Func<Guid, GameAchievementData> _resolveGameData;
        private readonly Func<bool> _isEnabled;
        private readonly Func<ManagedCustomIconService> _iconService;
        private readonly ILogger _logger;

        // One pass at a time: a retroactive pass and a refresh's pass reaching the same game
        // together would each find it unhandled and author two capstones.
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly object _waitingSync = new object();
        private readonly HashSet<Guid> _waitingForEditor = new HashSet<Guid>();

        public AutoCapstoneGenerator(
            GameCustomDataStore store,
            AchievementOverridesService overridesService,
            Func<Guid, GameAchievementData> resolveGameData,
            Func<bool> isEnabled,
            Func<ManagedCustomIconService> iconService,
            ILogger logger)
        {
            _store = store;
            _overridesService = overridesService;
            _resolveGameData = resolveGameData;
            _isEnabled = isEnabled;
            _iconService = iconService;
            _logger = logger;
            OpenEditorRegistry.Closed += OnEditorClosed;
        }

        /// <summary>
        /// Handles every game in <paramref name="gameIds"/> that is not handled yet.
        /// </summary>
        /// <param name="progress">Called after each game with how many are done and the total.</param>
        public async Task GenerateAsync(
            IEnumerable<Guid> gameIds,
            CancellationToken cancel = default,
            Action<int, int> progress = null)
        {
            if (_isEnabled?.Invoke() != true || _store == null || _overridesService == null)
            {
                return;
            }

            var ids = (gameIds ?? Enumerable.Empty<Guid>())
                .Where(gameId => gameId != Guid.Empty)
                .Distinct()
                .ToList();
            if (ids.Count == 0)
            {
                return;
            }

            await _gate.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                for (var i = 0; i < ids.Count; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    try
                    {
                        await GenerateOneAsync(ids[i]).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn(ex, $"Automatic capstone generation failed for gameId={ids[i]}.");
                    }

                    progress?.Invoke(i + 1, ids.Count);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task GenerateOneAsync(Guid gameId)
        {
            if (_overridesService.IsExcludedFromRefreshes(gameId))
            {
                return;
            }

            // The open editor saves the definitions from its own rows, so an achievement added
            // underneath it would be gone at its next save. The game is looked at when it closes.
            if (OpenEditorRegistry.IsOpen(gameId))
            {
                lock (_waitingSync)
                {
                    _waitingForEditor.Add(gameId);
                }

                return;
            }

            var stored = _store.TryLoad(gameId, out var data) ? data : null;

            // Checked before hydrating: once the library has been handled, every refresh reaches
            // here for games with the marker, and hydrating each one only to skip it adds up.
            if (stored?.AutoCapstoneGenerated == true)
            {
                return;
            }

            var gameData = _resolveGameData?.Invoke(gameId);
            var ordered = AchievementOrderHelper.ApplyOrder(
                gameData?.Achievements ?? new List<AchievementDetail>(),
                achievement => achievement?.ApiName,
                gameData?.AchievementOrder);

            var decision = AutoCapstoneEligibility.Decide(
                stored?.AutoCapstoneGenerated == true,
                stored?.CustomAchievements?.Any(definition => definition?.IsAutoCapstone == true) == true,
                ordered);

            switch (decision.Action)
            {
                case AutoCapstoneGenerationAction.MarkHandled:
                    _overridesService.MarkAutoCapstoneGenerated(gameId);
                    break;

                case AutoCapstoneGenerationAction.NominatePlatinum:
                    var nominated = _overridesService.SetCapstone(gameId, decision.Platinum.ApiName, true);
                    if (nominated?.Success == true)
                    {
                        _overridesService.MarkAutoCapstoneGenerated(gameId);
                    }

                    break;

                case AutoCapstoneGenerationAction.Author:
                    await AuthorAsync(gameId, stored, gameData, ordered).ConfigureAwait(false);
                    break;
            }
        }

        private async Task AuthorAsync(
            Guid gameId,
            GameCustomDataFile stored,
            GameAchievementData gameData,
            IReadOnlyList<AchievementDetail> achievements)
        {
            // Worked out now, so a game already finished gets a capstone already unlocked and the
            // first refresh after this sees no crossing to announce.
            var derived = AutoCapstoneCalculator.Derive(achievements);
            if (derived == null)
            {
                // Everything is filtered away: nothing for it to stand for yet.
                return;
            }

            var game = API.Instance?.Database?.Games?.Get(gameId);
            var name = (game?.Name ?? gameData?.GameName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Custom_AutoCapstone");
            }

            var existing = stored?.CustomAchievements?
                .Where(definition => definition != null)
                .ToList() ?? new List<CustomAchievementDefinition>();
            var id = ResolveUniqueId(existing);
            var apiName = CustomAchievementProjectionService.BuildApiName(id);

            var definition = new CustomAchievementDefinition
            {
                Id = id,
                DisplayName = name,
                Description = ResourceProvider.GetString(AutoCapstoneTemplate.DescriptionKey),
                Unlocked = derived.Unlocked,
                UnlockTimeUtc = derived.Unlocked ? derived.UnlockTimeUtc : null,
                UnlockedIconPath = await MaterializeIconAsync(gameId, game, existing, apiName).ConfigureAwait(false),
                TrophyType = AutoCapstoneTemplate.PlatinumTrophyType,
                Hidden = false,
                Rarity = derived.Rarity ?? "Common",
                GlobalPercentUnlocked = derived.GlobalPercentUnlocked,
                IsAutoCapstone = true,
                IsWholeGameAutoCapstone = true
            };

            // Filed where everything it stands for already sits, when that is one place.
            _overridesService.AddGeneratedAutoCapstone(gameId, definition, derived.Category);

            var nominated = _overridesService.SetCapstone(gameId, apiName, true);
            if (nominated?.Success != true)
            {
                _logger?.Warn($"Authored an auto capstone for gameId={gameId} but could not nominate it.");
            }
        }

        /// <summary>
        /// A stable id, so the capstone's ApiName reads the same in every game, suffixed only when
        /// the game already authored something with it.
        /// </summary>
        private static string ResolveUniqueId(IReadOnlyList<CustomAchievementDefinition> existing)
        {
            var used = new HashSet<string>(
                existing.Select(definition => CustomAchievementProjectionService.NormalizeId(definition.Id))
                    .Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);

            var candidate = BaseId;
            for (var suffix = 2; used.Contains(candidate); suffix++)
            {
                candidate = BaseId + "-" + suffix;
            }

            return candidate;
        }

        /// <summary>
        /// Copies the capstone's art into the game's icon cache, the way the editor's save does for
        /// an icon dropped onto an authored achievement. The source path stands in when the copy
        /// cannot be made, which the next editor save materializes in turn.
        /// </summary>
        private async Task<string> MaterializeIconAsync(
            Guid gameId,
            Playnite.SDK.Models.Game game,
            IReadOnlyList<CustomAchievementDefinition> existing,
            string apiName)
        {
            var source = AutoCapstoneTemplate.ResolveIconSource(game, _logger);
            var iconService = _iconService?.Invoke();
            if (string.IsNullOrWhiteSpace(source) || iconService == null)
            {
                return source;
            }

            // Stems for every authored achievement, not only this one, because a collision between
            // two of them is what decides the suffix each gets.
            var stems = AchievementIconCachePathBuilder.BuildFileStems(
                existing.Select(definition => CustomAchievementProjectionService.BuildApiName(definition.Id))
                    .Concat(new[] { apiName }));
            if (!stems.TryGetValue(apiName, out var stem) || string.IsNullOrWhiteSpace(stem))
            {
                return source;
            }

            try
            {
                var managed = await iconService
                    .MaterializeCustomIconAsync(
                        source,
                        gameId.ToString("D"),
                        stem,
                        AchievementIconVariant.Unlocked,
                        CancellationToken.None,
                        overwriteExistingTarget: true)
                    .ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(managed) ? source : managed;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed caching the auto capstone icon for gameId={gameId}.");
                return source;
            }
        }

        private void OnEditorClosed(Guid gameId)
        {
            lock (_waitingSync)
            {
                if (!_waitingForEditor.Remove(gameId))
                {
                    return;
                }
            }

            _ = GenerateAsync(new[] { gameId });
        }
    }
}
