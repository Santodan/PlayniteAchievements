using System;

namespace PlayniteAchievements.Models.Achievements
{
    /// <summary>
    /// An achievement row a user override can be applied to.
    /// </summary>
    /// <remarks>
    /// Exists so the override application lives in one place. The hydrated achievement list and the
    /// summary rows read from SQL are different types built by different paths, and each used to
    /// carry its own copy of "apply the user's overrides". The copies drifted: the summary one was
    /// missing the hidden override, the manual-link guard on the unlock timestamp, and the cleared
    /// timestamp, so the same edit showed in one surface and not the other.
    ///
    /// Deliberately not on this interface: <c>Category</c> and <c>CategoryType</c>, because
    /// hydration resolves those into its own path normalization pass rather than assigning them
    /// directly; the note, which is assigned unconditionally rather than overridden; and unlock
    /// status and rarity, which stay provider-owned.
    /// </remarks>
    public interface IAchievementOverrideTarget
    {
        string DisplayName { get; set; }

        string Description { get; set; }

        int? Points { get; set; }

        string TrophyType { get; set; }

        bool Hidden { get; set; }

        DateTime? UnlockTimeUtc { get; set; }

        /// <summary>
        /// Read-only here: an override may correct an unlocked row's timestamp but must never move
        /// unlock status itself, which would change unlocked counts and completion and look like a
        /// real unlock to the in-game monitor.
        /// </summary>
        bool Unlocked { get; }
    }
}
