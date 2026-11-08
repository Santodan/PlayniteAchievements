using System;

namespace PlayniteAchievements.Models.Achievements
{
    /// <summary>
    /// A row whose unlock time a user override can correct: the hydrated achievement list and the
    /// summary rows read from SQL. Every other overridden field reaches a row through its custom
    /// layer or the resolved definitions view.
    /// </summary>
    public interface IAchievementOverrideTarget
    {
        DateTime? UnlockTimeUtc { get; set; }

        /// <summary>
        /// Read-only here: an override may correct an unlocked row's timestamp but must never move
        /// unlock status itself, which would change unlocked counts and completion and look like a
        /// real unlock to the in-game monitor.
        /// </summary>
        bool Unlocked { get; }
    }
}
