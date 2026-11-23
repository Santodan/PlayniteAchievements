using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Restores the auto capstone mark on capstones that lost it.
    /// </summary>
    /// <remarks>
    /// Before 4.3.2, opening the editor could save a game's definitions while the grid bound its
    /// rows, before the rows carried the mark, and the save wrote the mark back as false. Nothing
    /// clears the mark on purpose, so an Auto Capstone-authored definition without it is one that
    /// save left behind. Kept apart from the store, like <see cref="AutoCapstoneScope"/>, so the rule
    /// can be read and tested without a game behind it.
    /// </remarks>
    public static class AutoCapstoneMarkerRepair
    {
        /// <summary>The id Auto Capstone authors with, suffixed "-2", "-3" when already taken.</summary>
        public const string BaseId = "auto-capstone";

        private static readonly Regex AuthoredId = new Regex(
            "^" + Regex.Escape(BaseId) + "(-[0-9]+)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// The definitions with the mark restored on every capstone Auto Capstone authored that
        /// lacks it, or null when none does.
        /// </summary>
        /// <remarks>
        /// A definition qualifies only with all three of Auto Capstone's own traits: its id, the
        /// platinum grade, and being nominated as a capstone. Its scope is read from where it is
        /// filed: the default category is the whole game; the main game's category is the whole
        /// game unless the game already has a whole-game one, since that is where a whole-game
        /// capstone is filed; any other category is that category's.
        /// </remarks>
        /// <param name="nominatedApiNames">The ApiNames the game nominates as capstones.</param>
        /// <param name="filedCategory">A definition's category label by ApiName, null for the default.</param>
        /// <param name="mainCategory">
        /// The category a whole-game capstone is filed in, null when the game's achievements give none.
        /// </param>
        public static List<CustomAchievementDefinition> Restore(
            IReadOnlyList<CustomAchievementDefinition> definitions,
            IEnumerable<string> nominatedApiNames,
            Func<string, string> filedCategory,
            string mainCategory)
        {
            if (definitions == null || definitions.Count == 0)
            {
                return null;
            }

            var nominated = new HashSet<string>(
                (nominatedApiNames ?? Enumerable.Empty<string>()).Where(apiName => !string.IsNullOrWhiteSpace(apiName)),
                StringComparer.OrdinalIgnoreCase);
            var result = definitions.Select(definition => definition?.Clone()).ToList();
            var hasWholeGame = result.Any(definition =>
                definition?.IsAutoCapstone == true && definition.IsWholeGameAutoCapstone);
            var main = string.IsNullOrWhiteSpace(mainCategory)
                ? null
                : AchievementCategoryHelper.NormalizeCategoryOrDefault(mainCategory);
            var changed = false;

            foreach (var definition in result)
            {
                if (definition == null || definition.IsAutoCapstone)
                {
                    continue;
                }

                var id = CustomAchievementProjectionService.NormalizeId(definition.Id);
                var apiName = CustomAchievementProjectionService.BuildApiName(definition.Id);
                if (id == null ||
                    !AuthoredId.IsMatch(id) ||
                    !string.Equals(definition.TrophyType?.Trim(), AutoCapstoneTemplate.PlatinumTrophyType, StringComparison.OrdinalIgnoreCase) ||
                    !nominated.Contains(apiName))
                {
                    continue;
                }

                var filed = AchievementCategoryHelper.NormalizeCategoryOrDefault(filedCategory?.Invoke(apiName));
                var wholeGame =
                    string.Equals(filed, AchievementCategoryHelper.DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase) ||
                    (!hasWholeGame && main != null && string.Equals(filed, main, CategoryPathHelper.LabelComparison));

                definition.IsAutoCapstone = true;
                definition.IsWholeGameAutoCapstone = wholeGame;
                hasWholeGame |= wholeGame;
                changed = true;
            }

            return changed ? result.Where(definition => definition != null).ToList() : null;
        }
    }
}
