using System;
using System.Collections.Generic;
using Newtonsoft.Json;

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

        /// <summary>
        /// True when the user emptied the description. A null <see cref="Description"/> alone
        /// means "not customized" and falls back to the provider's text, so this flag is what lets
        /// the description be removed and stay removed. Reverting drops the whole record.
        /// </summary>
        public bool ClearDescription { get; set; }

        public int? Points { get; set; }

        public string TrophyType { get; set; }

        /// <summary>
        /// True when the user set the trophy type to None. A null <see cref="TrophyType"/> alone
        /// means "not customized" and falls back to the provider's tier, so this flag is what lets
        /// the trophy be removed and stay removed. Reverting drops the whole record.
        /// </summary>
        public bool ClearTrophyType { get; set; }

        /// <summary>
        /// Corrected unlock timestamp for an achievement that is already unlocked. This does not
        /// change whether the achievement counts as unlocked: the lock state stays provider-owned,
        /// so an override here cannot alter unlocked counts, completion, or fire a notification.
        /// </summary>
        public DateTime? UnlockTimeUtc { get; set; }

        /// <summary>
        /// True when the user cleared the unlock timestamp outright. A null
        /// <see cref="UnlockTimeUtc"/> alone means "not customized" and falls back to the
        /// provider's timestamp, so an explicit flag is what lets the timestamp be removed and
        /// stay removed. Reverting the achievement drops the whole record and the provider's
        /// timestamp returns.
        /// </summary>
        public bool ClearUnlockTime { get; set; }

        public string Category { get; set; }

        /// <summary>
        /// The single category type override of files written before kinds and tags split. Read
        /// only so normalization can move its tags to <see cref="Tags"/>; it is never written back.
        /// </summary>
        [JsonProperty("CategoryType", NullValueHandling = NullValueHandling.Ignore)]
        public string LegacyCategoryType { get; set; }

        /// <summary>Pipe-joined tag keys replacing the provider's tags. Null means "not customized".</summary>
        public string Tags { get; set; }

        /// <summary>
        /// True when the user removed every tag. A null <see cref="Tags"/> alone means "not
        /// customized" and falls back to the provider's tags, so this flag is what lets the tags
        /// be removed and stay removed.
        /// </summary>
        public bool ClearTags { get; set; }

        public string Note { get; set; }

        public string UnlockedIconPath { get; set; }

        public string LockedIconPath { get; set; }

        /// <summary>
        /// Whether the achievement is treated as hidden, when the user disagrees with the
        /// provider. Null means "not customized": hiding is a presentation choice rather than a
        /// provider fact, so either value can be an override, and only an explicit one is stored.
        /// </summary>
        public bool? Hidden { get; set; }

        /// <summary>
        /// The custom layer this record supplies: its text, points, trophy type, category, tags and
        /// hidden flag, with each Clear flag read as a cleared field. A value wins over its Clear
        /// flag, except for tags, where ClearTags removes every assigned tag. Icons and unlock time
        /// are applied apart from the layers.
        /// </summary>
        public PlayniteAchievements.Models.Achievements.AchievementLayerValues ToLayer()
        {
            var cleared = PlayniteAchievements.Models.Achievements.AchievementField.None;
            if (ClearDescription) cleared |= PlayniteAchievements.Models.Achievements.AchievementField.Description;
            if (ClearTrophyType) cleared |= PlayniteAchievements.Models.Achievements.AchievementField.TrophyType;
            if (ClearTags || (Tags != null && string.IsNullOrWhiteSpace(Tags)))
            {
                cleared |= PlayniteAchievements.Models.Achievements.AchievementField.Tags;
            }

            return new PlayniteAchievements.Models.Achievements.AchievementLayerValues(
                displayName: DisplayName,
                description: Description,
                points: Points,
                trophyType: TrophyType,
                category: Category,
                tags: ClearTags ? null : Tags,
                hidden: Hidden,
                cleared: cleared);
        }

        /// <summary>
        /// Applies this record's unlock time to an unlocked row, the one override field kept outside
        /// the layers because a live unlock changes a loaded row's time. Unlock status itself stays
        /// provider-owned, and a manually tracked game records its own times, so neither is touched.
        /// A stored timestamp wins over the cleared state.
        /// </summary>
        public void ApplyUnlockTimeTo(PlayniteAchievements.Models.Achievements.IAchievementOverrideTarget target, bool hasManualLink)
        {
            if (target == null || !target.Unlocked || hasManualLink)
            {
                return;
            }

            if (UnlockTimeUtc.HasValue)
            {
                target.UnlockTimeUtc = UnlockTimeUtc;
            }
            else if (ClearUnlockTime)
            {
                target.UnlockTimeUtc = null;
            }
        }

        /// <summary>
        /// One field of each record, keyed by ApiName, leaving out records with no value for it.
        /// </summary>
        public static Dictionary<string, string> Project(
            IReadOnlyDictionary<string, AchievementOverride> overrides,
            Func<AchievementOverride, string> field)
        {
            var projected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in overrides ?? new Dictionary<string, AchievementOverride>())
            {
                var value = pair.Value == null ? null : field(pair.Value);
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(value))
                {
                    projected[pair.Key.Trim()] = value.Trim();
                }
            }

            return projected;
        }

        /// <summary>
        /// True when no member carries a value, so the record can be pruned rather than stored
        /// as an empty row. Normalization relies on this to keep the "is this game customized"
        /// predicates accurate.
        /// </summary>
        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(DisplayName) &&
            string.IsNullOrWhiteSpace(Description) &&
            !ClearDescription &&
            !Points.HasValue &&
            string.IsNullOrWhiteSpace(TrophyType) &&
            !ClearTrophyType &&
            !UnlockTimeUtc.HasValue &&
            !ClearUnlockTime &&
            string.IsNullOrWhiteSpace(Category) &&
            string.IsNullOrWhiteSpace(LegacyCategoryType) &&
            string.IsNullOrWhiteSpace(Tags) &&
            !ClearTags &&
            string.IsNullOrWhiteSpace(Note) &&
            string.IsNullOrWhiteSpace(UnlockedIconPath) &&
            string.IsNullOrWhiteSpace(LockedIconPath) &&
            !Hidden.HasValue;

        public AchievementOverride Clone()
        {
            return new AchievementOverride
            {
                DisplayName = DisplayName,
                Description = Description,
                ClearDescription = ClearDescription,
                Points = Points,
                TrophyType = TrophyType,
                ClearTrophyType = ClearTrophyType,
                UnlockTimeUtc = UnlockTimeUtc,
                ClearUnlockTime = ClearUnlockTime,
                Category = Category,
                LegacyCategoryType = LegacyCategoryType,
                Tags = Tags,
                ClearTags = ClearTags,
                Note = Note,
                UnlockedIconPath = UnlockedIconPath,
                LockedIconPath = LockedIconPath,
                Hidden = Hidden
            };
        }
    }
}
