using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Keeps an auto capstone's scope in step with where the user moves it.
    /// </summary>
    /// <remarks>
    /// Kept apart from the store, like <see cref="AutoCapstoneCalculator"/>, so the rule can be read
    /// and tested without a game behind it.
    /// </remarks>
    public static class AutoCapstoneScope
    {
        /// <summary>
        /// Re-scopes every auto capstone whose category changed: moved into a named category it
        /// stands for that category, moved back to the default it stands for the whole game.
        /// </summary>
        /// <remarks>
        /// Only for a move. A category renamed or nested in the Categories tree carries its members
        /// along, and a whole-game capstone filed with the main game must keep standing for the
        /// whole game when that category is renamed.
        /// </remarks>
        /// <param name="categoryBefore">An achievement's category label before the write, by ApiName.</param>
        /// <param name="categoryAfter">Its category label after the write, by ApiName.</param>
        /// <returns>The definitions with the moved capstones re-scoped, or null when none changed scope.</returns>
        public static List<CustomAchievementDefinition> Rescope(
            IReadOnlyList<CustomAchievementDefinition> definitions,
            Func<string, string> categoryBefore,
            Func<string, string> categoryAfter)
        {
            if (definitions == null || definitions.Count == 0 || categoryBefore == null || categoryAfter == null)
            {
                return null;
            }

            var result = definitions.Select(definition => definition?.Clone()).ToList();
            var changed = false;
            for (var i = 0; i < result.Count; i++)
            {
                var definition = result[i];
                if (definition?.IsAutoCapstone != true)
                {
                    continue;
                }

                var apiName = CustomAchievementProjectionService.BuildApiName(definition.Id);
                if (apiName == null)
                {
                    continue;
                }

                var before = AchievementCategoryHelper.NormalizeCategoryOrDefault(categoryBefore(apiName));
                var after = AchievementCategoryHelper.NormalizeCategoryOrDefault(categoryAfter(apiName));
                if (string.Equals(before, after, CategoryPathHelper.LabelComparison))
                {
                    continue;
                }

                var wholeGame = string.Equals(
                    after,
                    AchievementCategoryHelper.DefaultCategoryLabel,
                    StringComparison.OrdinalIgnoreCase);
                if (definition.IsWholeGameAutoCapstone != wholeGame)
                {
                    definition.IsWholeGameAutoCapstone = wholeGame;
                    changed = true;
                }
            }

            return changed ? result.Where(definition => definition != null).ToList() : null;
        }
    }
}
