using System;

namespace PlayniteAchievements.Models.Achievements
{
    /// <summary>
    /// The per-achievement values a data layer can supply. Platform, metadata and custom data all
    /// describe an achievement with these fields, and <see cref="Services.Achievements.LayerResolver"/>
    /// picks each one from the highest layer that has it.
    /// </summary>
    [Flags]
    public enum AchievementField
    {
        None = 0,
        DisplayName = 1 << 0,
        Description = 1 << 1,
        UnlockedIcon = 1 << 2,
        LockedIcon = 1 << 3,
        Points = 1 << 4,
        TrophyType = 1 << 5,
        Category = 1 << 6,
        CategoryKind = 1 << 7,
        Tags = 1 << 8,

        /// <summary>The global unlock percent and its tier, as one value.</summary>
        Rarity = 1 << 9,

        /// <summary>The achievement's position in the list.</summary>
        Order = 1 << 10,
        Hidden = 1 << 11,

        /// <summary>The unlock time. Only the custom layer overrides it.</summary>
        UnlockTime = 1 << 12,

        Text = DisplayName | Description,
        Icons = UnlockedIcon | LockedIcon,
        Categories = Category | CategoryKind,
        All = Text | Icons | Points | TrophyType | Categories | Tags | Rarity | Order | Hidden | UnlockTime
    }

    /// <summary>The layers an achievement's values come from, lowest precedence first.</summary>
    public enum DataLayer
    {
        None = 0,
        Platform = 1,
        Metadata = 2,
        Custom = 3
    }
}
