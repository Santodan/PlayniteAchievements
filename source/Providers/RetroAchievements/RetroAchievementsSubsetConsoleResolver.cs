using PlayniteAchievements.Providers.RetroAchievements.Models;

namespace PlayniteAchievements.Providers.RetroAchievements
{
    internal static class RetroAchievementsSubsetConsoleResolver
    {
        public static int? Resolve(RaGameInfoUserProgress gameInfo, int? fallbackConsoleId)
        {
            if (gameInfo?.ConsoleId > 0)
            {
                return gameInfo.ConsoleId;
            }

            return fallbackConsoleId.HasValue && fallbackConsoleId.Value > 0
                ? fallbackConsoleId
                : null;
        }
    }
}
