using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Providers.Manual
{
    /// <summary>
    /// Search result from a manual achievement source (e.g., Steam Store).
    /// </summary>
    public class ManualGameSearchResult
    {
        /// <summary>
        /// Unique identifier in the source system (e.g., Steam AppID).
        /// </summary>
        public string SourceGameId { get; set; }

        /// <summary>
        /// Display name of the game.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// URL to the game's icon or capsule image.
        /// </summary>
        public string IconUrl { get; set; }

        /// <summary>
        /// Indicates if this game has achievements in the source system.
        /// </summary>
        public bool HasAchievements { get; set; }

        /// <summary>
        /// Optional platform display string (e.g., "Steam, PS4" for Exophase results).
        /// </summary>
        public string Platforms { get; set; }
    }

    /// <summary>
    /// Abstraction interface for manual achievement sources.
    /// Implementations provide search and achievement fetch capabilities for different platforms.
    /// </summary>
    public interface IManualSource
    {
        /// <summary>
        /// Unique key identifying this source (e.g., "Steam", "Exophase").
        /// </summary>
        string SourceKey { get; }

        /// <summary>
        /// Display name for this source (localized).
        /// </summary>
        string SourceName { get; }

        /// <summary>
        /// Searches for games in the source system.
        /// </summary>
        /// <param name="query">Search query (game name).</param>
        /// <param name="language">Language code for results (e.g., "english").</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>List of search results, or empty list if none found or error.</returns>
        Task<List<ManualGameSearchResult>> SearchGamesAsync(string query, string language, CancellationToken ct);

        /// <summary>
        /// Fetches achievements for a game from the source system.
        /// </summary>
        /// <param name="sourceGameId">The source game ID (e.g., Steam AppID).</param>
        /// <param name="language">Language code for achievement text.</param>
        /// <param name="sourceGameId">Output: the source game ID.</param>
        /// <param name="gameName">Output: the game name from the source system.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>List of AchievementDetail objects with Unlocked=false and UnlockTimeUtc=null, or null if error.</returns>
        Task<List<AchievementDetail>> GetAchievementsAsync(string sourceGameId, string language, CancellationToken ct);
    }
}
