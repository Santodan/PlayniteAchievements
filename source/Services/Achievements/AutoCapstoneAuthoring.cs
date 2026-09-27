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
    /// Authors auto capstones, the one place both the editor's Auto Capstone button and automatic
    /// generation go through, so the two cannot drift apart in what they write.
    /// </summary>
    public sealed class AutoCapstoneAuthoring
    {
        private const string BaseId = "auto-capstone";

        private readonly GameCustomDataStore _store;
        private readonly AchievementOverridesService _overridesService;
        private readonly Func<Guid, GameAchievementData> _resolveGameData;
        private readonly Func<ManagedCustomIconService> _iconService;
        private readonly ILogger _logger;

        public AutoCapstoneAuthoring(
            GameCustomDataStore store,
            AchievementOverridesService overridesService,
            Func<Guid, GameAchievementData> resolveGameData,
            Func<ManagedCustomIconService> iconService,
            ILogger logger)
        {
            _store = store;
            _overridesService = overridesService;
            _resolveGameData = resolveGameData;
            _iconService = iconService;
            _logger = logger;
        }

        /// <summary>The description every auto capstone is authored with.</summary>
        public static string Description => ResourceProvider.GetString(AutoCapstoneTemplate.DescriptionKey);

        /// <summary>
        /// The game's hydrated achievements, authored ones included, in the order the game shows
        /// them, which is the order a platinum is picked in.
        /// </summary>
        public IReadOnlyList<AchievementDetail> LoadAchievementsInOrder(Guid gameId)
        {
            var gameData = _resolveGameData?.Invoke(gameId);
            return AchievementOrderHelper.ApplyOrder(
                gameData?.Achievements ?? new List<AchievementDetail>(),
                achievement => achievement?.ApiName,
                gameData?.AchievementOrder);
        }

        /// <summary>The game's platinum to nominate for the whole game, or null when it has none.</summary>
        public static AchievementDetail SelectPlatinum(IEnumerable<AchievementDetail> achievementsInOrder)
        {
            return AutoCapstoneTemplate.SelectPlatinum(
                achievementsInOrder,
                achievement => achievement.TrophyType,
                achievement => achievement.CategoryType);
        }

        /// <summary>
        /// Nominates a platinum as the game's capstone and marks the game as handled.
        /// </summary>
        public bool NominatePlatinum(Guid gameId, AchievementDetail platinum)
        {
            if (gameId == Guid.Empty || string.IsNullOrWhiteSpace(platinum?.ApiName))
            {
                return false;
            }

            var nominated = _overridesService.SetCapstone(gameId, platinum.ApiName, true);
            if (nominated?.Success != true)
            {
                return false;
            }

            _overridesService.MarkAutoCapstoneGenerated(gameId);
            return true;
        }

        /// <summary>
        /// Authors an auto capstone, files it, nominates it, and marks the game as handled.
        /// </summary>
        /// <param name="category">The category it stands for, or null for the whole game.</param>
        /// <returns>The new capstone's ApiName, or null when there was nothing for it to stand for.</returns>
        public async Task<string> AuthorAsync(Guid gameId, string category = null)
        {
            if (gameId == Guid.Empty || _store == null || _overridesService == null)
            {
                return null;
            }

            var stored = _store.TryLoad(gameId, out var data) ? data : null;
            var gameData = _resolveGameData?.Invoke(gameId);
            var achievements = gameData?.Achievements ?? new List<AchievementDetail>();

            // Worked out now, so a game already finished gets a capstone already unlocked and the
            // first refresh after this sees no crossing to announce.
            var normalizedCategory = AchievementCategoryTypeHelper.NormalizeCategory(category);
            var derived = AutoCapstoneCalculator.Derive(achievements, normalizedCategory);
            if (derived == null)
            {
                return null;
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
                Description = Description,
                Unlocked = derived.Unlocked,
                UnlockTimeUtc = derived.Unlocked ? derived.UnlockTimeUtc : null,
                UnlockedIconPath = await MaterializeIconAsync(gameId, game, existing, apiName).ConfigureAwait(false),
                TrophyType = AutoCapstoneTemplate.PlatinumTrophyType,
                Hidden = false,
                Rarity = derived.Rarity ?? "Common",
                GlobalPercentUnlocked = derived.GlobalPercentUnlocked,
                IsAutoCapstone = true,
                IsWholeGameAutoCapstone = normalizedCategory == null
            };

            // A chosen category is filed outright; the whole game is filed with the main game.
            _overridesService.AddGeneratedAutoCapstone(gameId, definition, normalizedCategory ?? derived.Category);

            var nominated = _overridesService.SetCapstone(gameId, apiName, true);
            if (nominated?.Success != true)
            {
                _logger?.Warn($"Authored an auto capstone for gameId={gameId} but could not nominate it.");
            }

            return apiName;
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
    }
}
