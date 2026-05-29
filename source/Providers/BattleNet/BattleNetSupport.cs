using System;
using System.Collections.Generic;
using Playnite.SDK.Models;

namespace PlayniteAchievements.Providers.BattleNet
{
    internal static class BattleNetGameSupport
    {
        public static readonly Guid BattleNetPluginId = Guid.Parse("E3C26A3D-D695-4CB7-A769-5FF7612C7EDD");
        // Temporarily disabled until SC2 support is validated.
        internal const bool IsSc2Enabled = false;

        public static bool IsSupported(Game game, BattleNetSettings settings)
        {
            if (game == null || game.PluginId != BattleNetPluginId)
            {
                return false;
            }

            return IsWowGame(game) || (IsSc2Enabled && IsSc2Game(game) && HasConfiguredSc2(settings));
        }

        public static bool IsWowGame(Game game)
        {
            var name = game?.Name;
            return !string.IsNullOrWhiteSpace(name) &&
                (name.Equals("wow", StringComparison.OrdinalIgnoreCase) ||
                 name.IndexOf("world of warcraft", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static bool IsSc2Game(Game game)
        {
            var name = game?.Name;
            return !string.IsNullOrWhiteSpace(name) &&
                (name.Equals("sc2", StringComparison.OrdinalIgnoreCase) ||
                 name.IndexOf("starcraft ii", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 name.IndexOf("starcraft 2", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static bool HasConfiguredSc2(BattleNetSettings settings)
        {
            return HasApiCredentials(settings) &&
                settings.Sc2RegionId > 0 &&
                settings.Sc2RealmId > 0 &&
                settings.Sc2ProfileId > 0;
        }

        public static bool HasApiCredentials(BattleNetSettings settings)
        {
            return settings != null &&
                !string.IsNullOrWhiteSpace(settings.BattleNetClientId) &&
                !string.IsNullOrWhiteSpace(settings.BattleNetClientSecret);
        }
    }

    internal static class BattleNetLocaleMapper
    {
        private const string DefaultWebLocale = "en-us";

        private static readonly Dictionary<string, string> LocaleAliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "english", "en-us" },
                { "german", "de-de" },
                { "french", "fr-fr" },
                { "spanish", "es-es" },
                { "latam", "es-mx" },
                { "italian", "it-it" },
                { "brazilian", "pt-br" },
                { "brazilianportuguese", "pt-br" },
                { "russian", "ru-ru" },
                { "polish", "pl-pl" },
                { "japanese", "ja-jp" },
                { "koreana", "ko-kr" },
                { "korean", "ko-kr" },
                { "schinese", "zh-cn" },
                { "tchinese", "zh-tw" }
            };

        private static readonly HashSet<string> SupportedWebLocales =
            new HashSet<string>(LocaleAliases.Values, StringComparer.OrdinalIgnoreCase);

        public static string ToWowWebLocale(string globalLanguage)
        {
            return ResolveWebLocale(globalLanguage);
        }

        public static string ToApiLocale(string globalLanguage)
        {
            var parts = ResolveWebLocale(globalLanguage).Split('-');
            return parts[0].ToLowerInvariant() + "_" + parts[1].ToUpperInvariant();
        }

        private static string ResolveWebLocale(string globalLanguage)
        {
            if (string.IsNullOrWhiteSpace(globalLanguage))
            {
                return DefaultWebLocale;
            }

            var value = globalLanguage.Trim();
            if (LocaleAliases.TryGetValue(value, out var mapped))
            {
                return mapped;
            }

            var locale = NormalizeExplicitWebLocale(value);
            return SupportedWebLocales.Contains(locale) ? locale : DefaultWebLocale;
        }

        private static string NormalizeExplicitWebLocale(string locale)
        {
            var parts = locale.Replace('_', '-').Split('-');
            if (parts.Length != 2 ||
                parts[0].Length != 2 ||
                parts[1].Length < 2 ||
                parts[1].Length > 4)
            {
                return DefaultWebLocale;
            }

            return parts[0].ToLowerInvariant() + "-" + parts[1].ToLowerInvariant();
        }
    }
}
