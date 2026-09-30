using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Every template an auto capstone text could have been written with: each field's default
    /// in every shipped language, the templates the user has set, and the ones in effect now.
    /// </summary>
    public sealed class AutoCapstoneKnownTemplates
    {
        private readonly Dictionary<AutoCapstoneTextField, List<string>> _byField;

        public AutoCapstoneKnownTemplates(
            Func<AutoCapstoneTextField, IEnumerable<string>> shippedDefaults,
            IEnumerable<string> history,
            AutoCapstoneTemplates current)
        {
            var shared = (history ?? Enumerable.Empty<string>())
                .Where(template => !string.IsNullOrWhiteSpace(template))
                .ToList();

            _byField = new Dictionary<AutoCapstoneTextField, List<string>>();
            foreach (var field in AutoCapstoneText.Fields)
            {
                _byField[field] = (shippedDefaults?.Invoke(field) ?? Enumerable.Empty<string>())
                    .Concat(new[] { AutoCapstoneText.GetDefault(field), current?.Get(field) })
                    .Concat(shared)
                    .Where(template => !string.IsNullOrWhiteSpace(template))
                    .Select(template => template.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            }
        }

        public IReadOnlyList<string> For(AutoCapstoneTextField field)
        {
            return _byField.TryGetValue(field, out var templates) ? templates : new List<string>();
        }
    }

    /// <summary>
    /// Brings an auto capstone's title and description in line with the current templates when
    /// they still read as text a template wrote, the way tag sync recognizes a default tag name.
    /// Text that no known template renders to for this game is an edit and is left alone.
    /// </summary>
    public static class AutoCapstoneTextRewriter
    {
        /// <summary>
        /// Rewrites <paramref name="definition"/> in place.
        /// </summary>
        /// <param name="category">The category it is filed in, or null.</param>
        /// <param name="gameNames">
        /// The names it could have been written with, the one to write with first.
        /// </param>
        /// <returns>True when its title or description changed.</returns>
        public static bool Rewrite(
            CustomAchievementDefinition definition,
            string category,
            IReadOnlyList<string> gameNames,
            AutoCapstoneTemplates current,
            AutoCapstoneKnownTemplates known)
        {
            if (definition?.IsAutoCapstone != true || known == null || gameNames == null || gameNames.Count == 0)
            {
                return false;
            }

            var gameName = gameNames[0];
            var normalizedCategory = AchievementCategoryTypeHelper.NormalizeCategory(category);

            // A category capstone is read as one first. One authored before its scope was stored
            // reads as a category capstone too, so when nothing matches that way it is tried as
            // the whole game's, which is filed with the main game's category.
            if (!definition.IsWholeGameAutoCapstone && normalizedCategory != null)
            {
                var label = AchievementCategoryTypeHelper.ToCategoryLabelDisplayText(normalizedCategory);
                var (titleMatch, descriptionMatch) = Match(
                    definition, AutoCapstoneTextField.CategoryName, AutoCapstoneTextField.CategoryDescription,
                    gameNames, label, known);
                if (titleMatch || descriptionMatch)
                {
                    return Apply(
                        definition, titleMatch, descriptionMatch,
                        AutoCapstoneTextField.CategoryName, AutoCapstoneTextField.CategoryDescription,
                        gameName, label, current);
                }
            }

            var (gameTitleMatch, gameDescriptionMatch) = Match(
                definition, AutoCapstoneTextField.GameName, AutoCapstoneTextField.GameDescription,
                gameNames, null, known);
            return Apply(
                definition, gameTitleMatch, gameDescriptionMatch,
                AutoCapstoneTextField.GameName, AutoCapstoneTextField.GameDescription,
                gameName, null, current);
        }

        private static (bool Title, bool Description) Match(
            CustomAchievementDefinition definition,
            AutoCapstoneTextField titleField,
            AutoCapstoneTextField descriptionField,
            IReadOnlyList<string> gameNames,
            string label,
            AutoCapstoneKnownTemplates known)
        {
            return (
                RendersFromKnown(definition.DisplayName, known.For(titleField), gameNames, label),
                RendersFromKnown(definition.Description, known.For(descriptionField), gameNames, label));
        }

        private static bool RendersFromKnown(
            string stored,
            IReadOnlyList<string> templates,
            IReadOnlyList<string> gameNames,
            string label)
        {
            if (string.IsNullOrWhiteSpace(stored))
            {
                return false;
            }

            stored = stored.Trim();
            foreach (var template in templates)
            {
                foreach (var gameName in gameNames)
                {
                    var rendered = AutoCapstoneText.Render(template, gameName, label);
                    if (rendered != null && string.Equals(rendered, stored, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool Apply(
            CustomAchievementDefinition definition,
            bool rewriteTitle,
            bool rewriteDescription,
            AutoCapstoneTextField titleField,
            AutoCapstoneTextField descriptionField,
            string gameName,
            string label,
            AutoCapstoneTemplates current)
        {
            var changed = false;
            if (rewriteTitle)
            {
                var title = AutoCapstoneText.RenderOrDefault(current, titleField, gameName, label);
                if (!string.IsNullOrWhiteSpace(title) && !string.Equals(definition.DisplayName, title, StringComparison.Ordinal))
                {
                    definition.DisplayName = title;
                    changed = true;
                }
            }

            if (rewriteDescription)
            {
                var description = AutoCapstoneText.RenderOrDefault(current, descriptionField, gameName, label);
                if (!string.IsNullOrWhiteSpace(description) && !string.Equals(definition.Description, description, StringComparison.Ordinal))
                {
                    definition.Description = description;
                    changed = true;
                }
            }

            return changed;
        }
    }
}
