using PlayniteAchievements.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.LinkedFiles
{
    /// <summary>
    /// Which linked games a refresh reads the files of. A refresh that names games reads those;
    /// one over the library reads every linked game. Linked games need no provider for this,
    /// so a custom-only game is read by the same refreshes that would include it.
    /// </summary>
    public sealed class LinkedFilePassScope
    {
        private LinkedFilePassScope(IReadOnlyCollection<Guid> gameIds, bool respectExclusions)
        {
            GameIds = gameIds;
            RespectExclusions = respectExclusions;
        }

        /// <summary>The games to read, or null to read every linked game.</summary>
        public IReadOnlyCollection<Guid> GameIds { get; }

        public bool RespectExclusions { get; }

        public bool IsLibraryWide => GameIds == null;

        public static LinkedFilePassScope ForGames(IEnumerable<Guid> gameIds, bool respectExclusions)
        {
            return new LinkedFilePassScope(
                (gameIds ?? Enumerable.Empty<Guid>()).Where(id => id != Guid.Empty).Distinct().ToList(),
                respectExclusions);
        }

        /// <summary>
        /// The scope of a refresh request, or null when the request refreshes no current-user
        /// games (a friends-only refresh).
        /// </summary>
        public static LinkedFilePassScope FromRequest(
            RefreshModeType mode,
            Guid? singleGameId,
            RefreshOptions options,
            IEnumerable<Guid> requestGameIds)
        {
            if (mode.IsFriendRefreshMode())
            {
                return null;
            }

            if (options != null && (options.Subjects & RefreshSubjects.CurrentUser) == 0)
            {
                return null;
            }

            var named = new List<Guid>();
            if (singleGameId.HasValue && singleGameId.Value != Guid.Empty)
            {
                named.Add(singleGameId.Value);
            }

            named.AddRange(options?.PlayniteGameIds ?? Enumerable.Empty<Guid>());
            named.AddRange(requestGameIds ?? Enumerable.Empty<Guid>());

            var respect = options?.RespectUserExclusions ?? true;
            if (named.Any(id => id != Guid.Empty))
            {
                // Games named outright follow the request's own rule for explicit includes, as the
                // provider refresh does.
                var bypass = options?.ForceBypassExclusionsForExplicitIncludes ?? true;
                return ForGames(named, respect && !bypass);
            }

            return new LinkedFilePassScope(null, respect);
        }
    }
}
