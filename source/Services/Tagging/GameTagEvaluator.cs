using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.Tagging;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;

namespace PlayniteAchievements.Services.Tagging
{
    /// <summary>
    /// The applicable kinds for a game plus whether the game counts as completed. Both come out
    /// of one evaluation so a sync pass loads each game's achievement data once.
    /// </summary>
    internal struct GameTagEvaluation
    {
        public List<TagType> TagTypes;
        public bool IsCompleted;

        /// <summary>
        /// True when the evaluation stopped before looking at completion, as it does for an
        /// excluded game, so <see cref="IsCompleted"/> says nothing about the game.
        /// </summary>
        public bool CompletionUnknown;

        /// <summary>Completion for the status rule: null when unknown.</summary>
        public bool? Completion => CompletionUnknown ? (bool?)null : IsCompleted;
    }

    /// <summary>
    /// Decides which kinds a game gets, from its cached achievement data and its custom data.
    /// Knows nothing about Playnite tags or features; <see cref="TagSyncService"/> applies the
    /// result to each field.
    /// </summary>
    internal sealed class GameTagEvaluator
    {
        // A Func rather than a captured instance: CancelEdit replaces the persisted settings.
        private readonly Func<PersistedSettings> _settings;

        public GameTagEvaluator(Func<PersistedSettings> settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        private PersistedSettings Settings => _settings();

        /// <summary>
        /// Every kind read from the game's custom data alone: the Customized pair, each area's
        /// kinds, and where the user's own achievements come from (custom achievements, a manual
        /// link, a linked file). These apply to excluded games too.
        /// </summary>
        public static readonly IReadOnlyList<TagType> CustomDataTagTypes =
            new[] { TagType.Customized, TagType.NotCustomized }
                .Concat(CustomizationTagAreas.All.SelectMany(area => area.TagTypes))
                .Concat(new[] { TagType.CustomAchievements, TagType.ManualAchievements, TagType.LinkedFile })
                .ToList();

        /// <summary>
        /// Determines all applicable kinds for a game. A game can have several (e.g.
        /// HasAchievements + InProgress). Excluded is exclusive and removes the others;
        /// ExcludedFromSummaries can coexist with achievement status kinds.
        /// </summary>
        public GameTagEvaluation Evaluate(Game game)
        {
            var types = new List<TagType>();
            var result = new GameTagEvaluation { TagTypes = types, IsCompleted = false };

            if (game == null)
            {
                types.Add(TagType.NoAchievements);
                return result;
            }

            var gameId = game.Id;
            // Loaded once, and only when something needs it: excluded games stop before the
            // status tags, but the layered customization tags may still read it.
            var achievementDataService = PlayniteAchievementsPlugin.Instance?.AchievementDataService;
            var loaded = new Lazy<GameAchievementData>(() => achievementDataService?.GetGameAchievementData(gameId));
            types.AddRange(DetermineCustomDataTagTypes(gameId, () => loaded.Value));

            // Full exclusion is exclusive - no other tags
            if (GameCustomDataLookup.IsExcludedFromRefreshes(gameId, Settings))
            {
                types.Add(TagType.Excluded);
                result.CompletionUnknown = true;
                return result;
            }

            // Check if excluded from summaries (can coexist with other tags)
            var excludedFromSummaries = GameCustomDataLookup.IsExcludedFromSummaries(gameId, Settings);
            if (excludedFromSummaries)
            {
                types.Add(TagType.ExcludedFromSummaries);
            }

            // Load achievement data through the central data service when available.
            var data = loaded.Value;

            // HasAchievements defaults to true on unscanned stubs (so bulk scans do not skip
            // them) and can survive as true with an empty list on rows written by the legacy
            // JSON import or old schema migrations. Tags must match what the UI reports, so
            // require actual achievements like SummaryCacheReader does, not just the flag.
            if (data == null || !data.HasAchievements ||
                data.Achievements == null || data.Achievements.Count == 0)
            {
                types.Add(TagType.NoAchievements);
                return result;
            }

            // If the game is excluded by user
            if (data.ExcludedByUser)
            {
                types.Add(TagType.Excluded);
                result.CompletionUnknown = true;
                return result;
            }

            // Platform achievements are every row but the projected custom ones; a manually
            // tracked list counts. Custom ones are the user's own, an auto capstone aside.
            if (data.Achievements.Any(a => a != null && !CustomAchievementProjectionService.IsCustomApiName(a.ApiName)))
            {
                types.Add(TagType.HasAchievements);
            }

            // Completed means every achievement unlocked, or every capstone earned. Raw cached data
            // has not been through hydration, so its IsCapstone flags are the provider seed and the
            // stored set has to be applied here before the rule is applied.
            var capstones = GameCustomDataLookup.GetCapstoneSet(gameId, Settings);
            var isCompleted = capstones.Materialized
                ? IsCompletedUnderStoredCapstones(data, capstones)
                : data.IsCompleted;

            result.IsCompleted = isCompleted;

            // Add status tag based on progress
            if (isCompleted)
            {
                types.Add(TagType.Completed);
            }
            else
            {
                var unlockedCount = data.Achievements?.Count(a => a?.Unlocked == true) ?? 0;
                if (unlockedCount > 0)
                {
                    types.Add(TagType.InProgress);
                }
            }

            return result;
        }

        /// <summary>
        /// The <see cref="CustomDataTagTypes"/> that apply to a game: the customization kinds,
        /// then Custom Achievements, Manual Tracking and Linked File. A manually tracked list
        /// also counts as Platform Achievements, which the caller decides from the cached data.
        /// </summary>
        public IEnumerable<TagType> DetermineCustomDataTagTypes(Guid gameId, Func<GameAchievementData> loadData)
        {
            var types = DetermineCustomizationTagTypes(gameId, loadData).ToList();

            if (GameCustomDataLookup.GetCustomAchievements(gameId, Settings)?.Any(definition =>
                    definition != null && !definition.IsAutoCapstone && !string.IsNullOrWhiteSpace(definition.DisplayName)) == true)
            {
                types.Add(TagType.CustomAchievements);
            }

            if (ManualAchievementsProvider.TryGetManualLink(gameId, out _))
            {
                types.Add(TagType.ManualAchievements);
            }

            if (GameCustomDataLookup.HasLinkedFile(gameId))
            {
                types.Add(TagType.LinkedFile);
            }

            return types;
        }

        /// <summary>
        /// The customization kinds that apply to a game: one side of the Customized pair and the
        /// applicable kinds of each area. Areas are only classified when one of their kinds is
        /// enabled, and the game's data is only loaded when a Platform, Metadata or None kind is,
        /// so the default setup pays nothing for them.
        /// </summary>
        private IEnumerable<TagType> DetermineCustomizationTagTypes(Guid gameId, Func<GameAchievementData> loadData)
        {
            var types = new List<TagType>
            {
                GameCustomDataLookup.HasVisibleCustomization(gameId, Settings)
                    ? TagType.Customized
                    : TagType.NotCustomized
            };

            if (!AnyAreaTagEnabled(_ => true))
            {
                return types;
            }

            var layers = default(GameDataLayers);
            if (AnyAreaTagEnabled(layer => layer == TagLayer.Platform || layer == TagLayer.Metadata || layer == TagLayer.None))
            {
                var data = loadData?.Invoke();
                layers = new GameDataLayers
                {
                    Platform = data?.PlatformFields,
                    Metadata = data?.MetadataFields,
                    Present = Metadata.DataLayers.Present(data?.Achievements)
                };
            }

            types.AddRange(CustomizationTagAreas.TagsFor(GameCustomDataLookup.GetCustomizationAreas(gameId, Settings), layers));
            return types;
        }

        /// <summary>Whether any area kind on an enabled layer is on, in the tag configs.</summary>
        private bool AnyAreaTagEnabled(Func<TagLayer, bool> layerFilter)
        {
            var tagConfigs = Settings?.TaggingSettings?.TagConfigs;
            return tagConfigs != null &&
                   CustomizationTagAreas.All
                       .SelectMany(area => area.Tags)
                       .Where(pair => layerFilter(pair.Key))
                       .Any(pair => tagConfigs.TryGetValue(pair.Value, out var config) && config?.IsEnabled == true);
        }

        /// <summary>
        /// Completion under a game's stored capstone set, for raw cached data whose IsCapstone
        /// flags are still the provider seed.
        /// </summary>
        private static bool IsCompletedUnderStoredCapstones(
            GameAchievementData data,
            CapstoneSet capstones)
        {
            var achievements = data?.Achievements;
            if (achievements == null || achievements.Count == 0)
            {
                return false;
            }

            if (achievements.All(a => a?.Unlocked == true))
            {
                return true;
            }

            var resolver = CapstoneResolver.Resolve(achievements, capstones.Assignments, true);
            return resolver.Count > 0 &&
                   resolver.EffectiveApiNames.All(apiName =>
                       achievements.Any(a =>
                           a != null &&
                           a.Unlocked &&
                           string.Equals((a.ApiName ?? string.Empty).Trim(), apiName, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
