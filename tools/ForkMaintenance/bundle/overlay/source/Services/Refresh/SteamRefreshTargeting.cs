using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Steam;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services
{
    internal static class SteamRefreshTargeting
    {
        internal const string SteamProviderKey = "Steam";
        internal const string SteamFamilySharingSourceName = "Steam Family Sharing";

        public static bool HasSteamProvider(IEnumerable<IDataProvider> providers)
        {
            return (providers ?? Enumerable.Empty<IDataProvider>()).Any(provider =>
                provider != null &&
                string.Equals(provider.ProviderKey, SteamProviderKey, StringComparison.OrdinalIgnoreCase));
        }

        public static bool HasSteamProvider(IEnumerable<string> providerKeys)
        {
            return (providerKeys ?? Enumerable.Empty<string>()).Any(providerKey =>
                !string.IsNullOrWhiteSpace(providerKey) &&
                string.Equals(providerKey.Trim(), SteamProviderKey, StringComparison.OrdinalIgnoreCase));
        }

        public static bool Matches(Game game, SteamRefreshTargetMode mode)
        {
            if (game == null || mode == SteamRefreshTargetMode.All || game.PluginId != SteamDataProvider.SteamPluginId)
            {
                return true;
            }

            var isFamilyShared = IsFamilyShared(game);
            switch (mode)
            {
                case SteamRefreshTargetMode.OwnedOnly:
                    return !isFamilyShared;

                case SteamRefreshTargetMode.FamilySharedOnly:
                    return isFamilyShared;

                default:
                    return true;
            }
        }

        public static IEnumerable<Game> Apply(IEnumerable<Game> games, SteamRefreshTargetMode mode, bool hasSteamProvider)
        {
            if (!hasSteamProvider || mode == SteamRefreshTargetMode.All)
            {
                return games ?? Enumerable.Empty<Game>();
            }

            return (games ?? Enumerable.Empty<Game>()).Where(game => Matches(game, mode));
        }

        public static bool IsFamilyShared(Game game)
        {
            var sourceName = game?.Source?.Name?.Trim();
            return string.Equals(sourceName, SteamFamilySharingSourceName, StringComparison.OrdinalIgnoreCase);
        }
    }
}