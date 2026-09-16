using System;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// The ways a single achievement can carry user customization. Mirrors what a revert clears
    /// for one achievement: the stored override record's fields, plus the whole-collection facets
    /// that are keyed by ApiName.
    /// </summary>
    /// <remarks>
    /// The achievement order is deliberately absent. It is stored as one positional list for the
    /// whole game, so moving a single row shifts every row after it, and marking all of those as
    /// customized would say nothing. Order is surfaced at the game level instead, by
    /// <c>HasCustomOrder</c> and the Reset Order command.
    /// </remarks>
    [Flags]
    public enum AchievementCustomizationFacet
    {
        None = 0,

        /// <summary>
        /// The achievement was authored by the user rather than supplied by a provider. Exclusive
        /// of every other flag: with no provider behind it there is nothing to differ from, and a
        /// revert deletes it outright rather than restoring anything.
        /// </summary>
        Authored = 1 << 0,

        DisplayName = 1 << 1,
        Description = 1 << 2,
        Points = 1 << 3,
        TrophyType = 1 << 4,
        UnlockTime = 1 << 5,
        Category = 1 << 6,
        CategoryType = 1 << 7,
        Note = 1 << 8,
        UnlockedIcon = 1 << 9,
        LockedIcon = 1 << 10,
        Hidden = 1 << 11,
        FilterScope = 1 << 12,
        Goal = 1 << 13,
        Capstone = 1 << 14
    }

    /// <summary>
    /// One achievement's current values beside the provider's own, for
    /// <see cref="AchievementCustomizationRules.Resolve"/>.
    /// </summary>
    /// <remarks>
    /// Text is compared as given, so the caller normalizes first -- blank to null, and categories
    /// through the category helper -- which keeps these rules free of the helpers the grid row
    /// depends on and testable on their own.
    /// </remarks>
    public sealed class AchievementCustomizationInputs
    {
        public bool IsAuthored { get; set; }

        public string DisplayName { get; set; }

        public string ProviderDisplayName { get; set; }

        public string Description { get; set; }

        public string ProviderDescription { get; set; }

        public int? Points { get; set; }

        public int? ProviderPoints { get; set; }

        public string TrophyType { get; set; }

        public string ProviderTrophyType { get; set; }

        public DateTime? UnlockTimeUtc { get; set; }

        public DateTime? ProviderUnlockTimeUtc { get; set; }

        public string Category { get; set; }

        public string ProviderCategory { get; set; }

        public string CategoryType { get; set; }

        public string ProviderCategoryType { get; set; }

        /// <summary>A note is user text throughout: no provider supplies one, so any note is one.</summary>
        public string Note { get; set; }

        public string UnlockedIconPath { get; set; }

        public string ProviderUnlockedIconPath { get; set; }

        public string LockedIconPath { get; set; }

        public string ProviderLockedIconPath { get; set; }

        public bool Hidden { get; set; }

        public bool ProviderHidden { get; set; }

        public bool IsFiltered { get; set; }

        public bool IsSummaryFiltered { get; set; }

        /// <summary>Goals are a user concept, so being one is itself the customization.</summary>
        public bool IsGoal { get; set; }

        public bool IsCapstone { get; set; }

        public bool ProviderIsCapstone { get; set; }
    }

    /// <summary>
    /// Decides which facets of an achievement the user has customized. Kept free of any view-model
    /// base class so the rules can be tested directly rather than through the grid row.
    /// </summary>
    public static class AchievementCustomizationRules
    {
        public static AchievementCustomizationFacet Resolve(AchievementCustomizationInputs inputs)
        {
            if (inputs == null)
            {
                return AchievementCustomizationFacet.None;
            }

            // An authored achievement has no provider values to differ from, so the comparisons
            // below would read its every field as an override. It is one thing: the user's own.
            if (inputs.IsAuthored)
            {
                return AchievementCustomizationFacet.Authored;
            }

            var facets = AchievementCustomizationFacet.None;

            if (!string.Equals(inputs.DisplayName, inputs.ProviderDisplayName, StringComparison.Ordinal))
            {
                facets |= AchievementCustomizationFacet.DisplayName;
            }

            if (!string.Equals(inputs.Description, inputs.ProviderDescription, StringComparison.Ordinal))
            {
                facets |= AchievementCustomizationFacet.Description;
            }

            if (inputs.Points != inputs.ProviderPoints)
            {
                facets |= AchievementCustomizationFacet.Points;
            }

            if (!string.Equals(inputs.TrophyType, inputs.ProviderTrophyType, StringComparison.OrdinalIgnoreCase))
            {
                facets |= AchievementCustomizationFacet.TrophyType;
            }

            if (inputs.UnlockTimeUtc != inputs.ProviderUnlockTimeUtc)
            {
                facets |= AchievementCustomizationFacet.UnlockTime;
            }

            if (!string.Equals(inputs.Category, inputs.ProviderCategory, StringComparison.OrdinalIgnoreCase))
            {
                facets |= AchievementCustomizationFacet.Category;
            }

            if (!string.Equals(inputs.CategoryType, inputs.ProviderCategoryType, StringComparison.OrdinalIgnoreCase))
            {
                facets |= AchievementCustomizationFacet.CategoryType;
            }

            if (!string.IsNullOrWhiteSpace(inputs.Note))
            {
                facets |= AchievementCustomizationFacet.Note;
            }

            // Matched to how the icon overrides are written: an entry is stored only where a
            // non-blank path differs from the provider's art, so a row that has fallen back to the
            // provider's icon -- or has none at all -- is not carrying one.
            if (IsIconOverride(inputs.UnlockedIconPath, inputs.ProviderUnlockedIconPath))
            {
                facets |= AchievementCustomizationFacet.UnlockedIcon;
            }

            if (IsIconOverride(inputs.LockedIconPath, inputs.ProviderLockedIconPath))
            {
                facets |= AchievementCustomizationFacet.LockedIcon;
            }

            if (inputs.Hidden != inputs.ProviderHidden)
            {
                facets |= AchievementCustomizationFacet.Hidden;
            }

            if (inputs.IsFiltered || inputs.IsSummaryFiltered)
            {
                facets |= AchievementCustomizationFacet.FilterScope;
            }

            if (inputs.IsGoal)
            {
                facets |= AchievementCustomizationFacet.Goal;
            }

            if (inputs.IsCapstone != inputs.ProviderIsCapstone)
            {
                facets |= AchievementCustomizationFacet.Capstone;
            }

            return facets;
        }

        private static bool IsIconOverride(string current, string provider)
        {
            return !string.IsNullOrWhiteSpace(current) &&
                   !string.Equals(current, provider, StringComparison.OrdinalIgnoreCase);
        }
    }
}
