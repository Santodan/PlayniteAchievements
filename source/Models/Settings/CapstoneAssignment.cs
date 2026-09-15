using System;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// One of a game's capstones: an achievement that stands for finishing something, and how much
    /// it stands for.
    /// </summary>
    /// <remarks>
    /// A provider-supplied capstone and one the user nominated are the same thing and are stored
    /// the same way. Providers seed the set; once the user edits it the whole set is written to
    /// custom data and the provider's flags no longer apply to that game, so an entry here is
    /// never a diff against provider data and there is nothing to tombstone.
    ///
    /// A category-scoped capstone deliberately stores no category. Its category is its
    /// achievement's own, read when the set is resolved, which is what lets a category be renamed
    /// or re-nested without any capstone plumbing: the assignment is ApiName-keyed exactly as
    /// category membership already is.
    /// </remarks>
    public sealed class CapstoneAssignment
    {
        public string ApiName { get; set; }

        /// <summary>
        /// True when this capstone stands for the whole game rather than one category. A game-wide
        /// capstone still claims its own achievement's category; it additionally fills every
        /// category that has no capstone of its own.
        /// </summary>
        public bool IsGameWide { get; set; }

        public CapstoneAssignment Clone()
        {
            return new CapstoneAssignment
            {
                ApiName = ApiName,
                IsGameWide = IsGameWide
            };
        }

        public bool Matches(string apiName)
        {
            return !string.IsNullOrWhiteSpace(apiName) &&
                   string.Equals((ApiName ?? string.Empty).Trim(), apiName.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
