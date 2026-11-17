using PlayniteAchievements.Models.Achievements;
using System;
using System.Globalization;

namespace PlayniteAchievements.Services.Database.Rows
{
    /// <summary>
    /// One achievement's row in the custom layer mirror: the SQL-side copy of the custom-data
    /// store's override record and per-achievement state, so the resolved views carry the user's
    /// customization without anyone reading the custom-data store.
    /// </summary>
    /// <remarks>
    /// An authored row (<see cref="IsAuthored"/>) is an achievement the user created, which no
    /// platform definition backs; it carries every value itself, already resolved.
    /// </remarks>
    internal sealed class AchievementOverrideMirrorEntry
    {
        public string ApiName { get; set; }

        public string DisplayName { get; set; }

        public string Description { get; set; }

        /// <summary>The icon to show, relative to the icon cache like the definitions' icons.</summary>
        public string UnlockedIconPath { get; set; }

        public string LockedIconPath { get; set; }

        public int? Points { get; set; }

        public string TrophyType { get; set; }

        public string Category { get; set; }

        public string Tags { get; set; }

        public bool? Hidden { get; set; }

        /// <summary>The user's unlock time, applied to an unlocked row of a game without a manual link.</summary>
        public DateTime? UnlockTimeUtc { get; set; }

        /// <summary>The <see cref="AchievementField"/> bits the user cleared.</summary>
        public long Cleared { get; set; }

        public bool IsFiltered { get; set; }

        public bool IsSummaryFiltered { get; set; }

        public string Note { get; set; }

        /// <summary>Position in the game's goal list; null when the achievement is not a goal.</summary>
        public int? GoalOrder { get; set; }

        /// <summary>Position in the user's custom order; null when the game has none or omits it.</summary>
        public int? CustomOrder { get; set; }

        /// <summary>In the game's stored capstone set, which applies once the user edited capstones.</summary>
        public bool CapstoneAssigned { get; set; }

        public bool IsAuthored { get; set; }

        public bool Unlocked { get; set; }

        public int? ProgressNum { get; set; }

        public int? ProgressDenom { get; set; }

        public int? ScaledPoints { get; set; }

        public string CategoryKind { get; set; }

        public double? GlobalPercentUnlocked { get; set; }

        public string Rarity { get; set; }

        public bool IsCapstone { get; set; }

        /// <summary>Takes the fields of a custom layer.</summary>
        public void SetLayer(AchievementLayerValues layer)
        {
            DisplayName = layer?.DisplayName;
            Description = layer?.Description;
            Points = layer?.Points;
            TrophyType = layer?.TrophyType;
            Category = layer?.Category;
            Tags = layer?.Tags;
            Hidden = layer?.Hidden;
            Cleared |= (long)(layer?.Cleared ?? AchievementField.None);
        }

        /// <summary>Takes every value of an authored achievement as the plugin shows it.</summary>
        public void SetAuthored(AchievementDetail row)
        {
            IsAuthored = true;
            DisplayName = row.DisplayName;
            Description = row.Description;
            UnlockedIconPath = row.UnlockedIconPath;
            LockedIconPath = row.LockedIconPath;
            Points = row.Points;
            ScaledPoints = row.ScaledPoints;
            TrophyType = row.TrophyType;
            Category = row.Category;
            CategoryKind = row.CategoryKind;
            Tags = row.Tags;
            Hidden = row.Hidden;
            Unlocked = row.Unlocked;
            UnlockTimeUtc = row.UnlockTimeUtc;
            ProgressNum = row.ProgressNum;
            ProgressDenom = row.ProgressDenom;
            GlobalPercentUnlocked = row.GlobalPercentUnlocked;
            Rarity = row.Rarity.ToString();
            IsCapstone = row.IsCapstone;
            Cleared = 0;
        }

        /// <summary>
        /// True when the row carries nothing a query would read, so it can be left out of the
        /// mirror entirely rather than stored as an all-default row.
        /// </summary>
        public bool IsEmpty =>
            !IsAuthored &&
            string.IsNullOrWhiteSpace(DisplayName) &&
            string.IsNullOrWhiteSpace(Description) &&
            string.IsNullOrWhiteSpace(UnlockedIconPath) &&
            string.IsNullOrWhiteSpace(LockedIconPath) &&
            !Points.HasValue &&
            string.IsNullOrWhiteSpace(TrophyType) &&
            string.IsNullOrWhiteSpace(Category) &&
            string.IsNullOrWhiteSpace(Tags) &&
            !Hidden.HasValue &&
            !UnlockTimeUtc.HasValue &&
            Cleared == 0 &&
            !IsFiltered &&
            !IsSummaryFiltered &&
            string.IsNullOrWhiteSpace(Note) &&
            !GoalOrder.HasValue &&
            !CustomOrder.HasValue &&
            !CapstoneAssigned;

        /// <summary>
        /// Canonical text used to diff a game's desired rows against what is stored, so an
        /// unchanged save stays WAL-silent.
        /// </summary>
        public string ToSignature()
        {
            string Int(int? value) => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
            string Text(string value) => (value ?? string.Empty).Trim();
            string Flag(bool value) => value ? "1" : string.Empty;
            return string.Join(
                "\n",
                Text(ApiName),
                Text(DisplayName),
                Text(Description),
                Text(UnlockedIconPath),
                Text(LockedIconPath),
                Int(Points),
                Text(TrophyType),
                Text(Category),
                Text(Tags),
                Hidden.HasValue ? (Hidden.Value ? "1" : "-") : string.Empty,
                UnlockTimeUtc.HasValue ? UnlockTimeUtc.Value.ToString("o", CultureInfo.InvariantCulture) : string.Empty,
                Cleared == 0 ? string.Empty : Cleared.ToString(CultureInfo.InvariantCulture),
                Flag(IsFiltered),
                Flag(IsSummaryFiltered),
                Text(Note),
                Int(GoalOrder),
                Int(CustomOrder),
                Flag(CapstoneAssigned),
                Flag(IsAuthored),
                Flag(Unlocked),
                Int(ProgressNum),
                Int(ProgressDenom),
                Int(ScaledPoints),
                Text(CategoryKind),
                GlobalPercentUnlocked.HasValue ? GlobalPercentUnlocked.Value.ToString("R", CultureInfo.InvariantCulture) : string.Empty,
                Text(Rarity),
                Flag(IsCapstone));
        }
    }
}
