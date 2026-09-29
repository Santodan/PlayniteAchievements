using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// A game's stored capstones together with whether they have been stored at all, which is the
    /// pair every reader needs: an empty set means something different before and after the user
    /// has touched it.
    /// </summary>
    public struct CapstoneSet
    {
        public static readonly CapstoneSet Untouched = new CapstoneSet(false, null);

        public CapstoneSet(bool materialized, IReadOnlyList<CapstoneAssignment> assignments)
        {
            Materialized = materialized;
            Assignments = assignments;
        }

        /// <summary>
        /// True once the user has edited this game's capstones. Provider capstone flags stop
        /// applying to the game at that point.
        /// </summary>
        public bool Materialized { get; }

        public IReadOnlyList<CapstoneAssignment> Assignments { get; }
    }

    /// <summary>
    /// One of a game's capstones: an achievement that stands for finishing the category it sits in.
    /// </summary>
    /// <remarks>
    /// A provider-supplied capstone and one the user nominated are the same thing and are stored
    /// the same way. Providers seed the set; once the user edits it the whole set is written to
    /// custom data and the provider's flags no longer apply to that game, so an entry here is
    /// never a diff against provider data and there is nothing to tombstone.
    ///
    /// An assignment deliberately stores no category. Its category is its achievement's own, read
    /// when the set is resolved, which is what lets a category be renamed or re-nested without any
    /// capstone plumbing: the assignment is ApiName-keyed exactly as category membership already is.
    ///
    /// There is no scope. A game is finished when every one of its capstones is unlocked, whatever
    /// they are scoped to, so a stored scope would have changed nothing; a game holding a single
    /// capstone already has that capstone standing for the whole of it.
    /// </remarks>
    public sealed class CapstoneAssignment
    {
        public string ApiName { get; set; }

        public CapstoneAssignment Clone()
        {
            return new CapstoneAssignment { ApiName = ApiName };
        }

        public bool Matches(string apiName)
        {
            return !string.IsNullOrWhiteSpace(apiName) &&
                   string.Equals((ApiName ?? string.Empty).Trim(), apiName.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
