using System;

namespace PlayniteAchievements.Models.Tagging
{
    /// <summary>
    /// Kinds of per-game customization the user can store, each tagged as its own
    /// Custom / Default pair.
    /// </summary>
    [Flags]
    public enum CustomizationArea
    {
        None = 0,

        /// <summary>Achievement icon overrides, or separate locked icons turned on.</summary>
        Icons = 1 << 0,

        /// <summary>Achievement trophy type overrides.</summary>
        TrophyTypes = 1 << 1,

        /// <summary>Achievement point overrides.</summary>
        Points = 1 << 2,

        /// <summary>Achievement notes.</summary>
        Notes = 1 << 3,

        /// <summary>Category assignments, kinds, order and art, summary category.</summary>
        Categories = 1 << 4,

        /// <summary>Custom achievement order.</summary>
        Order = 1 << 5,

        /// <summary>Capstones chosen or removed by the user, or a generated auto capstone.</summary>
        Capstones = 1 << 6,

        /// <summary>Goal achievements.</summary>
        Goals = 1 << 7,

        /// <summary>Platform override, provider ids and slugs, user-set metadata links and choices.</summary>
        Source = 1 << 8,

        /// <summary>Manual link, custom achievements or custom provider.</summary>
        ManualTracking = 1 << 9,

        /// <summary>Per-game notification appearance, sounds, behavior or capture.</summary>
        Notifications = 1 << 10,

        /// <summary>Achievement tag definitions or per-achievement tag assignments.</summary>
        Tags = 1 << 11,

        /// <summary>Achievement name or description overrides.</summary>
        Text = 1 << 12
    }
}
