using System;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// User customization for a single achievement, keyed by ApiName on
    /// <see cref="GameCustomDataFile.AchievementOverrides"/>. Every member is optional: a null
    /// value means "not customized", and resolution falls back to the provider's value.
    /// </summary>
    /// <remarks>
    /// This replaces the schema-7 parallel maps (category, category type, note and the two icon
    /// overrides) with one record per achievement, and adds the fields that previously only
    /// fully-custom achievements could carry. Rarity is deliberately absent: it stays
    /// provider-owned because the stored-rarity guard cannot distinguish a deliberate
    /// <c>Common</c> from "never filled in".
    /// </remarks>
    public sealed class AchievementOverride
    {
        public string DisplayName { get; set; }

        public string Description { get; set; }

        public int? Points { get; set; }

        public string TrophyType { get; set; }

        /// <summary>
        /// Corrected unlock timestamp for an achievement that is already unlocked. This does not
        /// change whether the achievement counts as unlocked: the lock state stays provider-owned,
        /// so an override here cannot alter unlocked counts, completion, or fire a notification.
        /// </summary>
        public DateTime? UnlockTimeUtc { get; set; }

        public string Category { get; set; }

        public string CategoryType { get; set; }

        public string Note { get; set; }

        public string UnlockedIconPath { get; set; }

        public string LockedIconPath { get; set; }

        /// <summary>
        /// True when no member carries a value, so the record can be pruned rather than stored
        /// as an empty row. Normalization relies on this to keep the "is this game customized"
        /// predicates accurate.
        /// </summary>
        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(DisplayName) &&
            string.IsNullOrWhiteSpace(Description) &&
            !Points.HasValue &&
            string.IsNullOrWhiteSpace(TrophyType) &&
            !UnlockTimeUtc.HasValue &&
            string.IsNullOrWhiteSpace(Category) &&
            string.IsNullOrWhiteSpace(CategoryType) &&
            string.IsNullOrWhiteSpace(Note) &&
            string.IsNullOrWhiteSpace(UnlockedIconPath) &&
            string.IsNullOrWhiteSpace(LockedIconPath);

        public AchievementOverride Clone()
        {
            return new AchievementOverride
            {
                DisplayName = DisplayName,
                Description = Description,
                Points = Points,
                TrophyType = TrophyType,
                UnlockTimeUtc = UnlockTimeUtc,
                Category = Category,
                CategoryType = CategoryType,
                Note = Note,
                UnlockedIconPath = UnlockedIconPath,
                LockedIconPath = LockedIconPath
            };
        }
    }
}
