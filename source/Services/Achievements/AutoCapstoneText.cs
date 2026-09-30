using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>The four texts an auto capstone is authored with.</summary>
    public enum AutoCapstoneTextField
    {
        GameName,
        GameDescription,
        CategoryName,
        CategoryDescription
    }

    /// <summary>
    /// One resolved template per <see cref="AutoCapstoneTextField"/>: {0} is the game and {1} the
    /// category, which the whole-game templates can use too but always receive empty.
    /// </summary>
    public sealed class AutoCapstoneTemplates
    {
        public AutoCapstoneTemplates(
            string gameName,
            string gameDescription,
            string categoryName,
            string categoryDescription)
        {
            GameName = gameName ?? string.Empty;
            GameDescription = gameDescription ?? string.Empty;
            CategoryName = categoryName ?? string.Empty;
            CategoryDescription = categoryDescription ?? string.Empty;
        }

        public string GameName { get; }

        public string GameDescription { get; }

        public string CategoryName { get; }

        public string CategoryDescription { get; }

        public string Get(AutoCapstoneTextField field)
        {
            switch (field)
            {
                case AutoCapstoneTextField.GameName: return GameName;
                case AutoCapstoneTextField.GameDescription: return GameDescription;
                case AutoCapstoneTextField.CategoryName: return CategoryName;
                default: return CategoryDescription;
            }
        }

        /// <summary>The four templates as one string, to tell whether two sets differ.</summary>
        public string Signature => string.Join("\u001f", GameName, GameDescription, CategoryName, CategoryDescription);
    }

    /// <summary>
    /// Resolves, validates and renders the templates auto capstones are titled and described
    /// with, so authoring and the pass that brings existing capstones in line share one reading.
    /// </summary>
    public static class AutoCapstoneText
    {
        public const string DefaultGameNameTemplate = "{0}";

        /// <summary>The joiner auto capstones were titled with before it was configurable.</summary>
        public const string DefaultCategoryNameTemplate = "{0}: {1}";

        public const string CategoryDescriptionKey = "LOCPlayAch_ManageAchievements_Custom_AutoCapstoneCategoryDescription";

        public const string FallbackGameNameKey = "LOCPlayAch_ManageAchievements_Custom_AutoCapstone";

        private const int MaxHistory = 32;

        public static readonly IReadOnlyList<AutoCapstoneTextField> Fields = new[]
        {
            AutoCapstoneTextField.GameName,
            AutoCapstoneTextField.GameDescription,
            AutoCapstoneTextField.CategoryName,
            AutoCapstoneTextField.CategoryDescription
        };

        /// <summary>The current language's default template for <paramref name="field"/>.</summary>
        public static string GetDefault(AutoCapstoneTextField field)
        {
            switch (field)
            {
                case AutoCapstoneTextField.GameName:
                    return DefaultGameNameTemplate;
                case AutoCapstoneTextField.GameDescription:
                    return ResourceProvider.GetString(AutoCapstoneTemplate.DescriptionKey) ?? string.Empty;
                case AutoCapstoneTextField.CategoryName:
                    return DefaultCategoryNameTemplate;
                default:
                    return ToCategoryTemplate(ResourceProvider.GetString(CategoryDescriptionKey)) ?? string.Empty;
            }
        }

        public static AutoCapstoneTemplates Defaults()
        {
            return new AutoCapstoneTemplates(
                GetDefault(AutoCapstoneTextField.GameName),
                GetDefault(AutoCapstoneTextField.GameDescription),
                GetDefault(AutoCapstoneTextField.CategoryName),
                GetDefault(AutoCapstoneTextField.CategoryDescription));
        }

        /// <summary>
        /// The localized category description names the category as {0}; the template names it
        /// {1}, after the game. Formatting the one with a literal "{1}" converts it in every
        /// language without a second key. Null when the translation does not format.
        /// </summary>
        public static string ToCategoryTemplate(string localizedCategoryDescription)
        {
            if (string.IsNullOrWhiteSpace(localizedCategoryDescription))
            {
                return null;
            }

            try
            {
                return string.Format(localizedCategoryDescription, "{1}");
            }
            catch (FormatException)
            {
                return null;
            }
        }

        public static string GetStored(PersistedSettings settings, AutoCapstoneTextField field)
        {
            if (settings == null)
            {
                return null;
            }

            switch (field)
            {
                case AutoCapstoneTextField.GameName: return settings.AutoCapstoneGameNameTemplate;
                case AutoCapstoneTextField.GameDescription: return settings.AutoCapstoneGameDescriptionTemplate;
                case AutoCapstoneTextField.CategoryName: return settings.AutoCapstoneCategoryNameTemplate;
                default: return settings.AutoCapstoneCategoryDescriptionTemplate;
            }
        }

        public static void SetStored(PersistedSettings settings, AutoCapstoneTextField field, string value)
        {
            if (settings == null)
            {
                return;
            }

            switch (field)
            {
                case AutoCapstoneTextField.GameName: settings.AutoCapstoneGameNameTemplate = value; break;
                case AutoCapstoneTextField.GameDescription: settings.AutoCapstoneGameDescriptionTemplate = value; break;
                case AutoCapstoneTextField.CategoryName: settings.AutoCapstoneCategoryNameTemplate = value; break;
                default: settings.AutoCapstoneCategoryDescriptionTemplate = value; break;
            }
        }

        /// <summary>The templates in effect: each stored one that is usable, else the default.</summary>
        public static AutoCapstoneTemplates Resolve(PersistedSettings settings)
        {
            string ResolveField(AutoCapstoneTextField field)
            {
                var stored = GetStored(settings, field);
                return IsValidTemplate(stored) ? stored.Trim() : GetDefault(field);
            }

            return new AutoCapstoneTemplates(
                ResolveField(AutoCapstoneTextField.GameName),
                ResolveField(AutoCapstoneTextField.GameDescription),
                ResolveField(AutoCapstoneTextField.CategoryName),
                ResolveField(AutoCapstoneTextField.CategoryDescription));
        }

        /// <summary>True when the template is non-blank and formats with a game and a category.</summary>
        public static bool IsValidTemplate(string template)
        {
            return !string.IsNullOrWhiteSpace(template) && Render(template, "x", "y") != null;
        }

        /// <summary>
        /// The value to store for an edited template: null when it is blank or the current
        /// default, so it keeps following the language, otherwise the trimmed edit.
        /// </summary>
        public static string NormalizeForStore(string edited, string currentDefault)
        {
            if (string.IsNullOrWhiteSpace(edited))
            {
                return null;
            }

            edited = edited.Trim();
            return string.Equals(edited, currentDefault?.Trim(), StringComparison.Ordinal) ? null : edited;
        }

        /// <summary>
        /// The template filled in, trimmed, or null when it does not format or renders blank.
        /// </summary>
        public static string Render(string template, string gameName, string categoryLabel)
        {
            if (string.IsNullOrWhiteSpace(template))
            {
                return null;
            }

            try
            {
                var text = string.Format(template, gameName ?? string.Empty, categoryLabel ?? string.Empty).Trim();
                return text.Length == 0 ? null : text;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        /// <summary>
        /// The title and description a capstone is authored with, for the whole game when
        /// <paramref name="category"/> is null, else for that category.
        /// </summary>
        /// <param name="category">The category's raw label, or null for the whole game.</param>
        public static (string Title, string Description) Describe(
            AutoCapstoneTemplates templates,
            string gameName,
            string category)
        {
            templates = templates ?? Defaults();
            if (string.IsNullOrWhiteSpace(category))
            {
                return (
                    RenderOrDefault(templates, AutoCapstoneTextField.GameName, gameName, null),
                    RenderOrDefault(templates, AutoCapstoneTextField.GameDescription, gameName, null));
            }

            // The display label, not the raw path, so a nested category reads as the list shows it.
            var label = AchievementCategoryTypeHelper.ToCategoryLabelDisplayText(category);
            return (
                RenderOrDefault(templates, AutoCapstoneTextField.CategoryName, gameName, label),
                RenderOrDefault(templates, AutoCapstoneTextField.CategoryDescription, gameName, label));
        }

        public static string RenderOrDefault(
            AutoCapstoneTemplates templates,
            AutoCapstoneTextField field,
            string gameName,
            string categoryLabel)
        {
            var defaultTemplate = GetDefault(field);
            return Render(templates?.Get(field), gameName, categoryLabel)
                ?? Render(defaultTemplate, gameName, categoryLabel)
                ?? defaultTemplate;
        }

        /// <summary>
        /// Remembers a template the user set, most recent last, so text written with it still
        /// reads as default after it is replaced.
        /// </summary>
        /// <returns>True when the history changed.</returns>
        public static bool RecordInHistory(PersistedSettings settings, string template)
        {
            if (settings == null || string.IsNullOrWhiteSpace(template))
            {
                return false;
            }

            template = template.Trim();
            var history = settings.AutoCapstoneTemplateHistory ?? new List<string>();
            if (history.Count > 0 && string.Equals(history[history.Count - 1], template, StringComparison.Ordinal))
            {
                return false;
            }

            var updated = history
                .Where(entry => !string.Equals(entry, template, StringComparison.Ordinal))
                .Concat(new[] { template })
                .ToList();
            if (updated.Count > MaxHistory)
            {
                updated = updated.Skip(updated.Count - MaxHistory).ToList();
            }

            settings.AutoCapstoneTemplateHistory = updated;
            return true;
        }
    }
}
