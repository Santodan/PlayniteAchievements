using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Providers.Exophase
{
    internal static class ExophaseFriendPlatformMatcher
    {
        public static bool IsSameProviderPlatform(Game game, string exophasePlatformSlug)
        {
            var gameProviderKey = ResolveProviderPlatformKey(game);
            var friendProviderKey = ResolveProviderPlatformKey(exophasePlatformSlug);
            return !string.IsNullOrWhiteSpace(gameProviderKey) &&
                   !string.IsNullOrWhiteSpace(friendProviderKey) &&
                   string.Equals(gameProviderKey, friendProviderKey, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsSameProviderPlatform(
            string sourceName,
            IEnumerable<string> platformNames,
            IEnumerable<string> platformSpecificationIds,
            string exophasePlatformSlug)
        {
            var gameProviderKey = ResolveProviderPlatformKey(sourceName, platformNames, platformSpecificationIds);
            var friendProviderKey = ResolveProviderPlatformKey(exophasePlatformSlug);
            return !string.IsNullOrWhiteSpace(gameProviderKey) &&
                   !string.IsNullOrWhiteSpace(friendProviderKey) &&
                   string.Equals(gameProviderKey, friendProviderKey, StringComparison.OrdinalIgnoreCase);
        }

        public static string ResolveProviderPlatformKey(Game game)
        {
            var slug = ResolveGamePlatformSlug(game);
            return ResolveProviderPlatformKey(slug);
        }

        public static string ResolveProviderPlatformKey(
            string sourceName,
            IEnumerable<string> platformNames,
            IEnumerable<string> platformSpecificationIds)
        {
            var slug = MapSourceToSlug(sourceName);
            if (!string.IsNullOrWhiteSpace(slug))
            {
                return ResolveProviderPlatformKey(slug);
            }

            foreach (var specificationId in platformSpecificationIds ?? Enumerable.Empty<string>())
            {
                slug = MapSpecificationIdToSlug(specificationId);
                if (!string.IsNullOrWhiteSpace(slug))
                {
                    return ResolveProviderPlatformKey(slug);
                }
            }

            foreach (var platformName in platformNames ?? Enumerable.Empty<string>())
            {
                slug = MapPlatformNameToSlug(platformName);
                if (!string.IsNullOrWhiteSpace(slug))
                {
                    return ResolveProviderPlatformKey(slug);
                }
            }

            return null;
        }

        public static string ResolveProviderPlatformKey(string exophasePlatformSlug)
        {
            var slug = NormalizePlatformSlug(exophasePlatformSlug);
            if (string.IsNullOrWhiteSpace(slug))
            {
                return null;
            }

            switch (slug)
            {
                case "steam": return "Steam";
                case "gog": return "GOG";
                case "epic": return "Epic";
                case "blizzard": return "BattleNet";
                case "origin": return "EA";
                case "xbox":
                case "xbox-one":
                case "xbox-360":
                    return "Xbox";
                case "psn":
                case "ps1":
                case "ps2":
                case "ps3":
                case "ps4":
                case "ps5":
                case "psp":
                case "vita":
                    return "PSN";
                case "retro": return "RetroAchievements";
                case "android": return "GooglePlay";
                case "apple": return "Apple";
                case "ubisoft":
                case "uplay":
                    return "Ubisoft";
                default:
                    return null;
            }
        }

        /// <summary>
        /// Maps a stored PA provider key (the servicing provider recorded on each cached game) to the
        /// canonical friend platform key that <see cref="ResolveProviderPlatformKey(string)"/> produces
        /// from an Exophase platform slug. Returns null for providers that are not a
        /// friend-matchable platform (aggregator/inclusion providers such as Exophase/Manual, and unknown
        /// keys) so callers can fall back to the stored sub-platform hint. Emulator providers fold into
        /// their console family so a friend's console game still matches the user's emulated copy.
        /// </summary>
        public static string MapProviderKeyToFriendPlatformKey(string providerKey)
        {
            var key = string.IsNullOrWhiteSpace(providerKey) ? null : providerKey.Trim();
            switch (key?.ToLowerInvariant())
            {
                case "steam": return "Steam";
                case "gog": return "GOG";
                case "epic": return "Epic";
                case "battlenet": return "BattleNet";
                case "ea": return "EA";
                case "ubisoft": return "Ubisoft";
                case "psn": return "PSN";
                case "xbox": return "Xbox";
                case "retroachievements": return "RetroAchievements";
                case "googleplay": return "GooglePlay";
                case "apple": return "Apple";

                // Emulators fold into the console family they emulate.
                case "rpcs3":
                case "shadps4":
                    return "PSN";
                case "xenia":
                    return "Xbox";

                default:
                    return null;
            }
        }

        /// <summary>
        /// Canonical friend platform key for a cached current-user game, from the labels the plugin stored
        /// at scan time. Prefers the servicing <paramref name="providerKey"/> (e.g. PSN, Steam, RPCS3->PSN);
        /// for aggregator/inclusion providers whose key is not itself a platform (Exophase, Manual) it falls
        /// back to the stored sub-platform hint <paramref name="providerPlatformKey"/>, so a platform the
        /// user self-tracks through an aggregator still matches a friend's game on that platform. Returns
        /// null when neither yields a platform.
        /// </summary>
        public static string ResolveStoredGameFamilyKey(string providerKey, string providerPlatformKey)
        {
            var mapped = MapProviderKeyToFriendPlatformKey(providerKey);
            if (!string.IsNullOrWhiteSpace(mapped))
            {
                return mapped;
            }

            return ResolveProviderPlatformKey(providerPlatformKey);
        }

        /// <summary>
        /// Resolves the Exophase achievement-page endpoint segment for a platform. PSN games live
        /// under /trophies/, Ubisoft/Uplay under /challenges/, and everything else under
        /// /achievements/. Keyed on the canonical provider platform key so every caller agrees on
        /// the endpoint for a given platform family, whatever slug/token variant it arrived as.
        /// </summary>
        public static string ResolveExophaseEndpoint(string platformSlugOrProviderKey)
        {
            var providerKey = ResolveProviderPlatformKey(platformSlugOrProviderKey);
            switch (providerKey)
            {
                case "PSN": return "trophies";
                case "Ubisoft": return "challenges";
                default: return "achievements";
            }
        }

        public static string ExtractPlatformSlugFromGameSlug(string gameSlug)
        {
            if (string.IsNullOrWhiteSpace(gameSlug))
            {
                return null;
            }

            var normalized = gameSlug.Trim().ToLowerInvariant();
            foreach (var slug in KnownPlatformSlugs)
            {
                if (normalized == slug || normalized.EndsWith("-" + slug, StringComparison.Ordinal))
                {
                    return slug;
                }
            }

            return null;
        }

        public static string ExtractPlatformSlugFromFriendGameKey(string providerGameKey)
        {
            if (string.IsNullOrWhiteSpace(providerGameKey))
            {
                return null;
            }

            var parts = providerGameKey.Split(new[] { '|' }, 2);
            if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
            {
                return NormalizePlatformSlug(parts[0]);
            }

            return ExtractPlatformSlugFromGameSlug(providerGameKey);
        }

        public static string NormalizePlatformSlug(string platform)
        {
            var token = string.IsNullOrWhiteSpace(platform)
                ? null
                : platform.Trim().ToLowerInvariant();
            switch (token)
            {
                case "ea":
                case "electronic-arts":
                case "electronic arts":
                    return "origin";
                case "ubisoft-connect":
                case "ubisoft connect":
                    return "ubisoft";
                default:
                    return token;
            }
        }

        private static string ResolveGamePlatformSlug(Game game)
        {
            if (game == null)
            {
                return null;
            }

            var sourceSlug = MapSourceToSlug(game.Source?.Name);
            if (!string.IsNullOrWhiteSpace(sourceSlug))
            {
                return sourceSlug;
            }

            foreach (var platform in game.Platforms ?? Enumerable.Empty<Platform>())
            {
                var slug = MapSpecificationIdToSlug(platform?.SpecificationId) ??
                           MapPlatformNameToSlug(platform?.Name);
                if (!string.IsNullOrWhiteSpace(slug))
                {
                    return slug;
                }
            }

            return null;
        }

        private static string MapSourceToSlug(string sourceName)
        {
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                return null;
            }

            var name = sourceName.Trim().ToLowerInvariant();
            if (name.Contains("steam")) return "steam";
            if (name.Contains("gog") || name.Contains("good old games")) return "gog";
            if (name.Contains("epic")) return "epic";
            if (name.Contains("battle.net") || name.Contains("battlenet") || ContainsDelimitedToken(name, "blizzard")) return "blizzard";
            if (name.Contains("origin") || name.Contains("electronic arts") || name.Contains("ea app") || ContainsDelimitedToken(name, "ea")) return "origin";
            if (name.Contains("google play") || name.Contains("googleplay") || name.Contains("android") || ContainsDelimitedToken(name, "android")) return "android";
            if (name.Contains("apple arcade") || name.Contains("app store") || ContainsDelimitedToken(name, "ios") || ContainsDelimitedToken(name, "apple")) return "apple";
            if (name.Contains("ubisoft") || name.Contains("uplay") || name.Contains("ubisoft connect")) return "ubisoft";

            return null;
        }

        private static string MapSpecificationIdToSlug(string specificationId)
        {
            if (string.IsNullOrWhiteSpace(specificationId))
            {
                return null;
            }

            var id = specificationId.Trim().ToLowerInvariant();
            if (id.StartsWith("sony_playstation", StringComparison.Ordinal) || id == "sony_vita") return "psn";
            if (id.Contains("360")) return "xbox-360";
            if (id.StartsWith("xbox", StringComparison.Ordinal)) return "xbox";

            return null;
        }

        private static string MapPlatformNameToSlug(string platformName)
        {
            if (string.IsNullOrWhiteSpace(platformName))
            {
                return null;
            }

            var name = platformName.Trim().ToLowerInvariant();
            var sourceLikeSlug = MapSourceToSlug(name);
            if (!string.IsNullOrWhiteSpace(sourceLikeSlug))
            {
                return sourceLikeSlug;
            }

            if (name.Contains("playstation") || name.Contains("psn") ||
                name.Contains("ps1") || name.Contains("ps2") || name.Contains("ps3") ||
                name.Contains("ps4") || name.Contains("ps5") || name.Contains("vita"))
            {
                return "psn";
            }

            if (name.Contains("xbox 360") || name.Contains("xbox360")) return "xbox-360";
            if (name.Contains("xbox")) return "xbox";
            if (name.Contains("retro") || name.Contains("retroachievements")) return "retro";
            if (name.Contains("android") || name.Contains("google play") || name.Contains("googleplay")) return "android";
            if (name.Contains("apple arcade") || name.Contains("app store") || name.Contains("ios") || ContainsDelimitedToken(name, "apple")) return "apple";

            return null;
        }

        private static bool ContainsDelimitedToken(string value, string token)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            return value
                .Split(TokenDelimiters, StringSplitOptions.RemoveEmptyEntries)
                .Any(part => string.Equals(part, token, StringComparison.OrdinalIgnoreCase));
        }

        private static readonly char[] TokenDelimiters =
            { ' ', '-', '_', '.', ':', '/', '\\', '|', '(', ')', '[', ']' };

        private static readonly string[] KnownPlatformSlugs =
        {
            "xbox-360",
            "xbox-one",
            "steam",
            "gog",
            "epic",
            "blizzard",
            "origin",
            "xbox",
            "psn",
            "ps1",
            "ps2",
            "ps3",
            "ps4",
            "ps5",
            "psp",
            "vita",
            "retro",
            "android",
            "apple",
            "ubisoft",
            "uplay"
        };
    }
}
