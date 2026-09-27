using System;

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

        public string CategoryType { get; set; }

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
        /// wherever they are filed.
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
                CategoryType = CategoryType,
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
