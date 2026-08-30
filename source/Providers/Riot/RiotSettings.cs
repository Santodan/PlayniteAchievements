using Newtonsoft.Json;
using PlayniteAchievements.Providers.Settings;
using System;

namespace PlayniteAchievements.Providers.Riot
{
    /// <summary>
    /// Riot provider settings. Riot has no keyless per-player endpoint, so the user supplies their
    /// own API key from developer.riotgames.com; the plugin never ships one.
    /// </summary>
    public class RiotSettings : ProviderSettingsBase
    {
        private string _riotGameName;
        private string _riotTagLine;
        private string _platformRegion = RiotRegions.DefaultPlatform;
        private string _apiKey;
        private string _puuid;

        /// <inheritdoc />
        public override string ProviderKey => "Riot";

        /// <summary>The name half of the Riot ID, before the '#'.</summary>
        public string RiotGameName
        {
            get => _riotGameName;
            set => SetValue(ref _riotGameName, value);
        }

        /// <summary>The tag half of the Riot ID, after the '#', stored without the '#'.</summary>
        public string RiotTagLine
        {
            get => _riotTagLine;
            set => SetValue(ref _riotTagLine, NormalizeTagLine(value));
        }

        /// <summary>Platform routing value (na1, euw1, ...). The regional host is derived from it.</summary>
        public string PlatformRegion
        {
            get => _platformRegion;
            set => SetValue(ref _platformRegion, RiotRegions.NormalizePlatform(value));
        }

        /// <summary>
        /// The user's own Riot API key. Stored in the plugin settings file the same way the
        /// RetroAchievements web API key is.
        /// </summary>
        public string RiotApiKey
        {
            get => _apiKey;
            set => SetValue(ref _apiKey, value);
        }

        /// <summary>
        /// PUUID resolved from the Riot ID, cached so a refresh costs one call instead of two.
        /// Cleared whenever the Riot ID or region changes.
        /// </summary>
        public string Puuid
        {
            get => _puuid;
            set => SetValue(ref _puuid, value);
        }

        /// <summary>True when every field a refresh needs has a value.</summary>
        [JsonIgnore]
        public bool HasCredentials =>
            !string.IsNullOrWhiteSpace(RiotGameName) &&
            !string.IsNullOrWhiteSpace(RiotTagLine) &&
            !string.IsNullOrWhiteSpace(RiotApiKey);

        /// <summary>The Riot ID in its canonical "name#tag" form, or null when incomplete.</summary>
        [JsonIgnore]
        public string RiotId =>
            string.IsNullOrWhiteSpace(RiotGameName) || string.IsNullOrWhiteSpace(RiotTagLine)
                ? null
                : $"{RiotGameName.Trim()}#{RiotTagLine.Trim()}";

        private static string NormalizeTagLine(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            return trimmed.StartsWith("#", StringComparison.Ordinal) ? trimmed.Substring(1) : trimmed;
        }
    }
}
