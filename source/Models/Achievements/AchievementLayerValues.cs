using System;

namespace PlayniteAchievements.Models.Achievements
{
    /// <summary>
    /// One layer's values for one achievement. Every field is in one of three states: unset (null
    /// and not in <see cref="Cleared"/>), set (non-null), or cleared (in <see cref="Cleared"/>),
    /// meaning the layer supplies the field as empty and lower layers do not show through.
    /// </summary>
    public sealed class AchievementLayerValues
    {
        public static readonly AchievementLayerValues Empty = new AchievementLayerValues();

        public AchievementLayerValues(
            string displayName = null,
            string description = null,
            string unlockedIconPath = null,
            string lockedIconPath = null,
            int? points = null,
            string trophyType = null,
            string category = null,
            string categoryKind = null,
            string tags = null,
            double? globalPercentUnlocked = null,
            RarityTier? rarity = null,
            int? order = null,
            bool? hidden = null,
            DateTime? unlockTimeUtc = null,
            AchievementField cleared = AchievementField.None)
        {
            DisplayName = Blank(displayName);
            Description = Blank(description);
            UnlockedIconPath = Blank(unlockedIconPath);
            LockedIconPath = Blank(lockedIconPath);
            Points = points;
            TrophyType = Blank(trophyType);
            Category = Blank(category);
            CategoryKind = Blank(categoryKind);
            Tags = Blank(tags);
            GlobalPercentUnlocked = globalPercentUnlocked;
            Rarity = rarity;
            Order = order;
            Hidden = hidden;
            UnlockTimeUtc = unlockTimeUtc;
            // A field with a value is set, whatever the cleared mask says.
            Cleared = cleared & AchievementField.All & ~Set;
        }

        public string DisplayName { get; }

        public string Description { get; }

        public string UnlockedIconPath { get; }

        public string LockedIconPath { get; }

        public int? Points { get; }

        public string TrophyType { get; }

        public string Category { get; }

        public string CategoryKind { get; }

        public string Tags { get; }

        public double? GlobalPercentUnlocked { get; }

        public RarityTier? Rarity { get; }

        public int? Order { get; }

        public bool? Hidden { get; }

        public DateTime? UnlockTimeUtc { get; }

        /// <summary>Fields this layer supplies as empty.</summary>
        public AchievementField Cleared { get; }

        /// <summary>The fields this layer carries a value for, not counting cleared ones.</summary>
        public AchievementField Set
        {
            get
            {
                var set = AchievementField.None;
                if (DisplayName != null) set |= AchievementField.DisplayName;
                if (Description != null) set |= AchievementField.Description;
                if (UnlockedIconPath != null) set |= AchievementField.UnlockedIcon;
                if (LockedIconPath != null) set |= AchievementField.LockedIcon;
                if (Points.HasValue) set |= AchievementField.Points;
                if (TrophyType != null) set |= AchievementField.TrophyType;
                if (Category != null) set |= AchievementField.Category;
                if (CategoryKind != null) set |= AchievementField.CategoryKind;
                if (Tags != null) set |= AchievementField.Tags;
                if (GlobalPercentUnlocked.HasValue || Rarity.HasValue) set |= AchievementField.Rarity;
                if (Order.HasValue) set |= AchievementField.Order;
                if (Hidden.HasValue) set |= AchievementField.Hidden;
                if (UnlockTimeUtc.HasValue) set |= AchievementField.UnlockTime;
                return set;
            }
        }

        /// <summary>The fields this layer decides: set or cleared.</summary>
        public AchievementField Supplied => Set | Cleared;

        public bool IsEmpty => Supplied == AchievementField.None;

        private static string Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
