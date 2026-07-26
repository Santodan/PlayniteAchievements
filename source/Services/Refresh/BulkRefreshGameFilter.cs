using System.Collections.Generic;
using System.Linq;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.Refresh
{
    internal static class BulkRefreshGameFilter
    {
        public static bool ShouldIncludeHiddenGames(PersistedSettings settings)
        {
            return settings?.IncludeHiddenGamesInBulkScans ?? true;
        }

        public static bool ShouldIncludeGame(Game game, PersistedSettings settings)
        {
            return game != null && (ShouldIncludeHiddenGames(settings) || !game.Hidden);
        }

        public static IEnumerable<Game> ApplyHiddenFilter(IEnumerable<Game> games, PersistedSettings settings)
        {
            if (games == null)
            {
                return Enumerable.Empty<Game>();
            }

            return ShouldIncludeHiddenGames(settings)
                ? games.Where(game => game != null)
                : games.Where(game => game != null && !game.Hidden);
        }
    }
}
