using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// Which achievements the editor lists: one game's, as the Manage Achievements window shows
    /// them, or the library's, narrowed by the library editor's Game filter.
    /// </summary>
    /// <remarks>
    /// Immutable. The features that write a game's whole list from its rows (reordering, adding and
    /// deleting authored achievements, linking files, importing and exporting, the custom platform,
    /// manual tracking, the auto capstone and the resets) need every row of one game, so they are
    /// offered only while <see cref="SingleGameId"/> names one.
    /// </remarks>
    public sealed class EditorScope
    {
        private EditorScope(Guid? fixedGameId, IReadOnlyList<Guid> filterGameIds)
        {
            FixedGameId = fixedGameId;
            FilterGameIds = filterGameIds;
        }

        /// <summary>The Manage Achievements window's game.</summary>
        public static EditorScope ForGame(Guid gameId) => new EditorScope(gameId, null);

        /// <summary>Every game's achievements, or those of <paramref name="gameIds"/> when given and not empty.</summary>
        public static EditorScope Library(IEnumerable<Guid> gameIds = null)
        {
            var list = gameIds?.Where(id => id != Guid.Empty).Distinct().ToList();
            return new EditorScope(null, list?.Count > 0 ? list : null);
        }

        public bool IsLibrary => !FixedGameId.HasValue;

        /// <summary>The game of a per-game editor; null for the library editor.</summary>
        public Guid? FixedGameId { get; }

        /// <summary>The library editor's picked games; null for every game, and for a per-game editor.</summary>
        public IReadOnlyList<Guid> FilterGameIds { get; }

        /// <summary>The one game the editor lists, when it lists exactly one.</summary>
        public Guid? SingleGameId => FixedGameId ?? (FilterGameIds?.Count == 1 ? FilterGameIds[0] : (Guid?)null);

        /// <summary>Whether the single-game features are on: the editor lists exactly one game.</summary>
        public bool AllowsSingleGameFeatures => SingleGameId.HasValue;
    }
}
