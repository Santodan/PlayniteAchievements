using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Exophase
{
    public sealed class ExophaseFriendSettings
    {
        public string Username { get; set; }
        public string DisplayName { get; set; }
        public string AvatarUrl { get; set; }
        public string AvatarPath { get; set; }
        public List<string> SelectedPlatforms { get; set; } = new List<string>();
        public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
        public DateTime? LastRefreshedUtc { get; set; }
        public DateTime? LastProbedUtc { get; set; }
        public string LastProbeStatus { get; set; }
        public string LastError { get; set; }

        [JsonIgnore]
        public string PlatformSummary => SelectedPlatforms == null || SelectedPlatforms.Count == 0
            ? string.Empty
            : string.Join(", ", SelectedPlatforms);
    }

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
        private List<ExophaseFriendSettings> _friends = new List<ExophaseFriendSettings>();
        private Dictionary<string, Guid> _friendGameMappings = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

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
        /// Valid values: "psn", "xbox", "gog", "epic", "blizzard", "origin", "retro", "android", "apple", "ubisoft".
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
        /// Manually configured public Exophase friends.
        /// </summary>
        public List<ExophaseFriendSettings> Friends
        {
            get => _friends;
            set => SetValue(ref _friends, NormalizeFriends(value));
        }

        /// <summary>
        /// Global mapping from Exophase friend game keys ("platform|slug") to Playnite game ids.
        /// </summary>
        public Dictionary<string, Guid> FriendGameMappings
        {
            get => _friendGameMappings;
            set => SetValue(ref _friendGameMappings, NormalizeFriendGameMappings(value));
        }

        public bool AddOrUpdateFriend(string username)
        {
            var normalized = NormalizeUsername(username);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            var friends = NormalizeFriends(Friends);
            var existing = friends.FirstOrDefault(friend =>
                string.Equals(friend.Username, normalized, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Username = normalized;
                Friends = friends;
                return false;
            }

            friends.Add(new ExophaseFriendSettings
            {
                Username = normalized,
                DisplayName = normalized,
                SelectedPlatforms = new List<string>(),
                AddedUtc = DateTime.UtcNow
            });
            Friends = friends;
            return true;
        }

        public bool RemoveFriend(string username)
        {
            var normalized = NormalizeUsername(username);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            var friends = NormalizeFriends(Friends);
            var removed = friends.RemoveAll(friend =>
                string.Equals(friend?.Username, normalized, StringComparison.OrdinalIgnoreCase));
            Friends = friends;
            return removed > 0;
        }

        public ExophaseFriendSettings GetFriend(string username)
        {
            var normalized = NormalizeUsername(username);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            return Friends?.FirstOrDefault(friend =>
                string.Equals(friend?.Username, normalized, StringComparison.OrdinalIgnoreCase));
        }

        public static HashSet<string> CreateDefaultManagedProviders()
        {
            return new HashSet<string>(DefaultManagedProviderTokens, StringComparer.OrdinalIgnoreCase);
        }

        public override void DeserializeFromJson(string json)
        {
            base.DeserializeFromJson(json);
            ManagedProviders = _managedProviders;
            Friends = _friends;
            FriendGameMappings = _friendGameMappings;
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

        private static List<ExophaseFriendSettings> NormalizeFriends(IEnumerable<ExophaseFriendSettings> friends)
        {
            var normalized = new List<ExophaseFriendSettings>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var friend in friends ?? Enumerable.Empty<ExophaseFriendSettings>())
            {
                var username = NormalizeUsername(friend?.Username);
                if (string.IsNullOrWhiteSpace(username) || !seen.Add(username))
                {
                    continue;
                }

                normalized.Add(new ExophaseFriendSettings
                {
                    Username = username,
                    DisplayName = string.IsNullOrWhiteSpace(friend.DisplayName) ? username : friend.DisplayName.Trim(),
                    AvatarUrl = string.IsNullOrWhiteSpace(friend.AvatarUrl) ? null : friend.AvatarUrl.Trim(),
                    AvatarPath = string.IsNullOrWhiteSpace(friend.AvatarPath) ? null : friend.AvatarPath.Trim(),
                    SelectedPlatforms = NormalizePlatformList(friend.SelectedPlatforms),
                    AddedUtc = friend.AddedUtc == default(DateTime) ? DateTime.UtcNow : friend.AddedUtc,
                    LastRefreshedUtc = friend.LastRefreshedUtc,
                    LastProbedUtc = friend.LastProbedUtc,
                    LastProbeStatus = friend.LastProbeStatus,
                    LastError = friend.LastError
                });
            }

            return normalized;
        }

        private static List<string> NormalizePlatformList(IEnumerable<string> platforms)
        {
            return (platforms ?? Enumerable.Empty<string>())
                .Where(platform => !string.IsNullOrWhiteSpace(platform))
                .Select(platform => platform.Trim().ToLowerInvariant())
                .Where(platform => !string.Equals(platform, "steam", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(platform => platform, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static Dictionary<string, Guid> NormalizeFriendGameMappings(IDictionary<string, Guid> mappings)
        {
            var normalized = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in mappings ?? new Dictionary<string, Guid>())
            {
                var key = NormalizeFriendGameMappingKey(pair.Key);
                if (!string.IsNullOrWhiteSpace(key) && pair.Value != Guid.Empty)
                {
                    normalized[key] = pair.Value;
                }
            }

            return normalized;
        }

        public static string NormalizeFriendGameMappingKey(string key)
        {
            return string.IsNullOrWhiteSpace(key) ? null : key.Trim().ToLowerInvariant();
        }

        public static string NormalizeUsername(string username)
        {
            return string.IsNullOrWhiteSpace(username) ? null : username.Trim();
        }
    }
}
