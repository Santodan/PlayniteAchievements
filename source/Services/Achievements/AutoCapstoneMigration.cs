using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Brings auto capstones authored by earlier versions in line with how they are authored now.
    /// </summary>
    /// <remarks>
    /// Three things changed. The description names the base game rather than every achievement.
    /// A capstone standing for the whole game now says so, where earlier ones were read by where
    /// they were filed. And a whole-game capstone is filed with the main game, where earlier ones
    /// could land in the default category once a provider moved every achievement into named
    /// categories. The first applies to every game; the other two need the game's achievements,
    /// so a game with none loaded keeps its scope and filing, which the calculator's own fallback
    /// for a capstone alone in its category still covers.
    /// </remarks>
    public static class AutoCapstoneMigration
    {
        /// <returns>How many games were changed.</returns>
        public static int Run(
            GameCustomDataStore store,
            AchievementOverridesService overridesService,
            AutoCapstoneAuthoring authoring,
            AutoCapstoneMaintainer maintainer,
            ILogger logger)
        {
            if (store == null || overridesService == null || authoring == null)
            {
                return 0;
            }

            var migrated = 0;
            foreach (var record in store.LoadAll())
            {
                var gameId = record?.PlayniteGameId ?? Guid.Empty;
                if (gameId == Guid.Empty ||
                    record.CustomAchievements?.Any(definition => definition?.IsAutoCapstone == true) != true)
                {
                    continue;
                }

                try
                {
                    if (MigrateGame(gameId, record, overridesService, authoring))
                    {
                        maintainer?.Maintain(gameId, announceUnlocks: false);
                        migrated++;
                    }
                }
                catch (Exception ex)
                {
                    logger?.Warn(ex, $"Auto capstone migration failed for gameId={gameId}.");
                }
            }

            return migrated;
        }

        private static bool MigrateGame(
            Guid gameId,
            GameCustomDataFile record,
            AchievementOverridesService overridesService,
            AutoCapstoneAuthoring authoring)
        {
            var achievements = authoring.LoadAchievementsInOrder(gameId);
            var hasProviderData = achievements.Any(achievement => achievement != null && !achievement.IsCustom);

            var definitions = record.CustomAchievements
                .Where(definition => definition != null)
                .Select(definition => definition.Clone())
                .ToList();
            var refiled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var changed = false;

            foreach (var definition in definitions.Where(definition => definition.IsAutoCapstone))
            {
                if (string.Equals(definition.Description, AutoCapstoneAuthoring.LegacyDescription, StringComparison.Ordinal))
                {
                    definition.Description = AutoCapstoneAuthoring.Description;
                    changed = true;
                }

                if (!hasProviderData)
                {
                    continue;
                }

                var apiName = CustomAchievementProjectionService.BuildApiName(definition.Id);
                if (!AutoCapstoneCalculator.IsAloneInItsCategory(achievements, apiName))
                {
                    continue;
                }

                // Alone in its category, it stood for the whole game when it was authored and has
                // nothing else left to stand for there.
                if (!definition.IsWholeGameAutoCapstone)
                {
                    definition.IsWholeGameAutoCapstone = true;
                    changed = true;
                }

                var target = ResolveMainGameCategory(achievements, apiName);
                if (!string.IsNullOrWhiteSpace(target))
                {
                    refiled[apiName] = target;
                    changed = true;
                }
            }

            if (!changed)
            {
                return false;
            }

            overridesService.RewriteCustomAchievementsAndFiling(gameId, definitions, refiled);
            return true;
        }

        /// <summary>
        /// Where a whole-game capstone sitting alone in the default category belongs: with the main
        /// game, as it would be filed if authored now. Null to leave it where it is -- it is not in
        /// the default category, the main game cannot be told, or that category already has a
        /// capstone it would be doubled up with.
        /// </summary>
        private static string ResolveMainGameCategory(IReadOnlyList<AchievementDetail> achievements, string apiName)
        {
            var own = achievements.FirstOrDefault(achievement => string.Equals(
                achievement?.ApiName,
                apiName,
                StringComparison.OrdinalIgnoreCase));
            if (own == null ||
                !string.Equals(
                    AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(own.Category),
                    AchievementCategoryTypeHelper.DefaultCategoryLabel,
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var others = achievements.Where(achievement => achievement != null && !ReferenceEquals(achievement, own)).ToList();
            var target = AutoCapstoneCalculator.Derive(others)?.Category;
            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            var alreadyHasOne = others.Any(achievement =>
                achievement.IsCapstone &&
                string.Equals(
                    AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(achievement.Category),
                    target,
                    StringComparison.OrdinalIgnoreCase));
            return alreadyHasOne ? null : target;
        }
    }
}
