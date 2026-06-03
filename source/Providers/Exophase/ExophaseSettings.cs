using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Exophase
{
    /// <summary>
    /// Exophase provider settings. Authentication is handled via session manager.
    /// </summary>
    public class ExophaseSettings : ProviderSettingsBase
    {
        private static readonly string[] DefaultManagedProviderTokens =
        {
            "android",
            "apple",
            "ubisoft"
        };

        private string _userId;
        private HashSet<string> _managedProviders = CreateDefaultManagedProviders();
        private HashSet<Guid> _includedGames = new HashSet<Guid>();
        private Dictionary<Guid, string> _slugOverrides = new Dictionary<Guid, string>();
        private bool _enableActiveMonitoring = false;
        private int _monitoringIntervalSeconds = 300;

        /// <inheritdoc />
        public override string ProviderKey => "Exophase";

        /// <summary>
        /// Exophase user ID (username).
        /// </summary>
        public string UserId
        {
            get => _userId;
            set => SetValue(ref _userId, value);
        }

        /// <summary>
        /// Provider/platform tokens that Exophase should automatically claim.
        /// Games matching these tokens will use Exophase instead of modern providers.
        /// Valid values: "steam", "psn", "xbox", "gog", "epic", "blizzard", "origin", "retro", "android", "apple", "ubisoft".
        /// </summary>
        public HashSet<string> ManagedProviders
        {
            get => _managedProviders;
            set => SetValue(
                ref _managedProviders,
                NormalizeManagedProviders(value));
        }

        /// <summary>
        /// Individual game IDs that should use Exophase even if their provider/platform token is not in managed providers.
        /// Allows per-game override for platforms not globally enabled.
        /// </summary>
        [JsonIgnore]
        public HashSet<Guid> IncludedGames
        {
            get => _includedGames;
            set => SetValue(ref _includedGames, value ?? new HashSet<Guid>());
        }

        /// <summary>
        /// Per-game Exophase slug overrides.
        /// Key is Playnite Game ID, value is the Exophase game slug (e.g., "game-name-gog").
        /// When set, this slug is used directly instead of auto-detection.
        /// </summary>
        [JsonIgnore]
        public Dictionary<Guid, string> SlugOverrides
        {
            get => _slugOverrides;
            set => SetValue(ref _slugOverrides, value ?? new Dictionary<Guid, string>());
        }

        /// <summary>
        /// When true, polls the Exophase API during gameplay to detect newly unlocked achievements
        /// and shows in-app overlay notifications (uses the same notification style configured on
        /// the Achievement Notifications tab).
        /// </summary>
        public bool EnableActiveMonitoring
        {
            get => _enableActiveMonitoring;
            set => SetValue(ref _enableActiveMonitoring, value);
        }

        /// <summary>
        /// How often (in minutes) to poll the Exophase API while a monitored game is running.
        /// Minimum 5 minutes to avoid hitting Exophase rate limits.
        /// </summary>
        public int MonitoringIntervalSeconds
        {
            get => _monitoringIntervalSeconds;
            set => SetValue(ref _monitoringIntervalSeconds, Math.Max(30, Math.Min(3600, value)));
        }

        public static HashSet<string> CreateDefaultManagedProviders()
        {
            return new HashSet<string>(DefaultManagedProviderTokens, StringComparer.OrdinalIgnoreCase);
        }

        public override void DeserializeFromJson(string json)
        {
            base.DeserializeFromJson(json);
            ManagedProviders = _managedProviders;
        }

        private static HashSet<string> NormalizeManagedProviders(IEnumerable<string> providers)
        {
            var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (providers == null)
            {
                return normalized;
            }

            foreach (var provider in providers)
            {
                if (string.IsNullOrWhiteSpace(provider))
                {
                    continue;
                }

                var token = provider.Trim();
                if (string.Equals(token, "ea", StringComparison.OrdinalIgnoreCase))
                {
                    token = "origin";
                }

                normalized.Add(token);
            }

            return normalized;
        }
    }
}
