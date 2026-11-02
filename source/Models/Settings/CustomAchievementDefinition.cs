using System;
using Newtonsoft.Json;

namespace PlayniteAchievements.Models.Settings
{
    public sealed class CustomAchievementDefinition
    {
        public string Id { get; set; }

        public string DisplayName { get; set; }

        public string Description { get; set; }

        public bool Unlocked { get; set; }

        public DateTime? UnlockTimeUtc { get; set; }

        public string UnlockedIconPath { get; set; }

        public string LockedIconPath { get; set; }

        public int? Points { get; set; }

        public int? ScaledPoints { get; set; }

        public string Category { get; set; }

        /// <summary>Pipe-joined tag keys. Null for no tags.</summary>
        public string Tags { get; set; }

        /// <summary>
        /// This achievement's own kind, standing in for the provider kind a custom achievement does
        /// not have; the kind stored on its category still wins. Stored under its pre-v9 name, when
        /// it held kinds and tags together.
        /// </summary>
        [JsonProperty("CategoryType")]
        public string CategoryKind { get; set; }

        public string TrophyType { get; set; }

        public bool Hidden { get; set; }

        public bool IsCapstone { get; set; }

        public string Rarity { get; set; }

        public double? GlobalPercentUnlocked { get; set; }

        public int? ProgressNum { get; set; }

        public int? ProgressDenom { get; set; }

        /// <summary>
        /// True for the achievement the editor's Auto Capstone authored to stand for finishing the
        /// game. It marks the one achievement whose rarity and unlock the plugin keeps in step with
        /// the achievements it stands for, so a refresh knows which one to maintain and a second
        /// press of the button updates it rather than authoring another.
        /// </summary>
        public bool IsAutoCapstone { get; set; }

        /// <summary>
        /// True when an auto capstone stands for the whole game rather than for the category it is
        /// filed in. Where it is filed says nothing about its scope: a whole-game capstone on a game
        /// whose achievements span several categories is filed in the default one, and reading the
        /// scope off the filing would narrow it to that category. False for a category capstone,
        /// and for every auto capstone authored before this was stored, which keep standing for
        /// wherever they are filed. Moving the capstone re-sets it from where it lands: a named
        /// category makes it that category's, the default makes it the whole game's. Renaming or
        /// nesting its category is not a move and leaves it alone.
        /// </summary>
        public bool IsWholeGameAutoCapstone { get; set; }

        public CustomAchievementDefinition Clone()
        {
            return new CustomAchievementDefinition
            {
                Id = Id,
                DisplayName = DisplayName,
                Description = Description,
                Unlocked = Unlocked,
                UnlockTimeUtc = UnlockTimeUtc,
                UnlockedIconPath = UnlockedIconPath,
                LockedIconPath = LockedIconPath,
                Points = Points,
                ScaledPoints = ScaledPoints,
                Category = Category,
                Tags = Tags,
                CategoryKind = CategoryKind,
                TrophyType = TrophyType,
                Hidden = Hidden,
                IsCapstone = IsCapstone,
                Rarity = Rarity,
                GlobalPercentUnlocked = GlobalPercentUnlocked,
                ProgressNum = ProgressNum,
                ProgressDenom = ProgressDenom,
                IsAutoCapstone = IsAutoCapstone,
                IsWholeGameAutoCapstone = IsWholeGameAutoCapstone
            };
        }
    }
}
