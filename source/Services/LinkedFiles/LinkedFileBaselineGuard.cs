using PlayniteAchievements.Models.Settings;
using System;

namespace PlayniteAchievements.Services.LinkedFiles
{
    /// <summary>
    /// Whether a read may author the rows its file names. A schema that creates unmatched rows
    /// authors every row when the game shows none, which is what a new link is for. A link that
    /// has already read the game successfully is in a different position: the rows it would
    /// create are the ones it created before, so a game that now shows none has lost them rather
    /// than never had them, and authoring them again would duplicate the whole file onto it.
    /// </summary>
    public static class LinkedFileBaselineGuard
    {
        /// <param name="link">The game's link, for what its last read found.</param>
        /// <param name="achievementCount">Achievements the game shows, custom rows included.</param>
        /// <param name="customDefinitionCount">Custom achievements the store holds for the game.</param>
        public static bool HasLostItsAchievements(
            LinkedFileLink link,
            int achievementCount,
            int customDefinitionCount)
        {
            return achievementCount <= 0 &&
                   customDefinitionCount <= 0 &&
                   link?.LastReadUtc.HasValue == true &&
                   string.Equals(link.LastReadStatus, nameof(LinkedFileReadStatus.Ok), StringComparison.Ordinal);
        }
    }
}
