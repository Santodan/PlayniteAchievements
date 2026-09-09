using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Providers.Riot
{
    /// <summary>
    /// Riot API routing. challenges-v1 is served from a platform host (na1, euw1, ...); account-v1
    /// is served from a regional host (americas, europe, asia, sea). A player's platform determines
    /// both, so settings store only the platform and the regional host is derived.
    /// </summary>
    internal static class RiotRegions
    {
        public const string DefaultPlatform = "na1";

        /// <summary>Platform routing values, each mapped to the regional host account-v1 must use.</summary>
        private static readonly Dictionary<string, string> RegionalByPlatform =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["br1"] = "americas",
                ["la1"] = "americas",
                ["la2"] = "americas",
                ["na1"] = "americas",
                ["eun1"] = "europe",
                ["euw1"] = "europe",
                ["ru"] = "europe",
                ["tr1"] = "europe",
                ["jp1"] = "asia",
                ["kr"] = "asia",
                ["oc1"] = "sea",
                ["ph2"] = "sea",
                ["sg2"] = "sea",
                ["th2"] = "sea",
                ["tw2"] = "sea",
                ["vn2"] = "sea"
            };

        /// <summary>Display labels for the settings dropdown. Region codes are Riot's, not localizable.</summary>
        private static readonly Dictionary<string, string> LabelByPlatform =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["na1"] = "NA — North America",
                ["euw1"] = "EUW — Europe West",
                ["eun1"] = "EUNE — Europe Nordic & East",
                ["kr"] = "KR — Korea",
                ["br1"] = "BR — Brazil",
                ["jp1"] = "JP — Japan",
                ["la1"] = "LAN — Latin America North",
                ["la2"] = "LAS — Latin America South",
                ["oc1"] = "OCE — Oceania",
                ["tr1"] = "TR — Türkiye",
                ["ru"] = "RU — Russia",
                ["ph2"] = "PH — Philippines",
                ["sg2"] = "SG — Singapore",
                ["th2"] = "TH — Thailand",
                ["tw2"] = "TW — Taiwan",
                ["vn2"] = "VN — Vietnam"
            };

        public static IReadOnlyList<RiotRegionChoice> Choices { get; } = LabelByPlatform
            .Select(pair => new RiotRegionChoice(pair.Key, pair.Value))
            .OrderBy(choice => choice.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        public static bool IsKnownPlatform(string platform)
            => !string.IsNullOrWhiteSpace(platform) && RegionalByPlatform.ContainsKey(platform.Trim());

        /// <summary>Normalizes user/persisted input to a known platform code, falling back to the default.</summary>
        public static string NormalizePlatform(string platform)
        {
            var trimmed = platform?.Trim();
            return IsKnownPlatform(trimmed)
                ? trimmed.ToLowerInvariant()
                : DefaultPlatform;
        }

        public static string GetPlatformHost(string platform)
            => $"https://{NormalizePlatform(platform)}.api.riotgames.com";

        public static string GetRegionalHost(string platform)
        {
            var normalized = NormalizePlatform(platform);
            var regional = RegionalByPlatform.TryGetValue(normalized, out var value) ? value : "americas";
            return $"https://{regional}.api.riotgames.com";
        }
    }

    internal sealed class RiotRegionChoice
    {
        public RiotRegionChoice(string platform, string label)
        {
            Platform = platform;
            Label = label;
        }

        public string Platform { get; }

        public string Label { get; }
    }
}
