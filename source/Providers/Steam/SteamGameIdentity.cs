using Playnite.SDK.Models;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.Globalization;

namespace PlayniteAchievements.Providers.Steam
{
    internal static class SteamGameIdentity
    {
        internal static readonly Guid SteamPluginId = Guid.Parse("CB91DFC9-B977-43BF-8E70-55F46E410FAB");

        internal static bool TryGetSteamAppId(Game game, out int appId)
        {
            appId = 0;
            if (game == null)
            {
                return false;
            }

            if (GameCustomDataLookup.TryGetSteamAppIdOverride(game.Id, out appId))
            {
                return true;
            }

            if (game.PluginId != SteamPluginId)
            {
                return false;
            }

            return TryGetPositiveId(game.GameId, out appId);
        }

        internal static bool TryGetPositiveId(string value, out int id)
        {
            return int.TryParse(
                       (value ?? string.Empty).Trim(),
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out id) &&
                   id > 0;
        }
    }
}
