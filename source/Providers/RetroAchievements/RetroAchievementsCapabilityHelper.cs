using Playnite.SDK.Models;

namespace PlayniteAchievements.Providers.RetroAchievements
{
    internal static class RetroAchievementsCapabilityHelper
    {
        public static bool HasConfiguredCredentials(RetroAchievementsSettings settings)
        {
            return settings != null &&
                   !string.IsNullOrWhiteSpace(settings.RaUsername) &&
                   !string.IsNullOrWhiteSpace(settings.RaWebApiKey);
        }

        public static bool HasPlatformMetadata(Game game)
        {
            if (game?.Platforms == null || game.Platforms.Count == 0)
            {
                return false;
            }

            foreach (var platform in game.Platforms)
            {
                if (platform == null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(platform.SpecificationId) ||
                    !string.IsNullOrWhiteSpace(platform.Name))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool CanSetOverride(Game game)
        {
            if (game == null)
            {
                return false;
            }

            return !HasPlatformMetadata(game) ||
                   RaConsoleIdResolver.TryResolve(game, out _);
        }

        public static bool CanUsePlatformlessNameFallback(Game game, RetroAchievementsSettings settings)
        {
            return settings?.EnableRaNameFallback == true &&
                   !string.IsNullOrWhiteSpace(game?.Name) &&
                   !HasPlatformMetadata(game);
        }

        public static bool CanUseNameFallback(Game game, RetroAchievementsSettings settings, bool hasResolvedConsole)
        {
            if (settings?.EnableRaNameFallback != true || string.IsNullOrWhiteSpace(game?.Name))
            {
                return false;
            }

            return hasResolvedConsole || !HasPlatformMetadata(game);
        }
    }
}
