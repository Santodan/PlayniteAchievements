using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Models.Settings
{
    public enum FriendSettingsSource
    {
        AutoDiscovered,
        Manual
    }

    public sealed class FriendSettingsEntry
    {
        public string ProviderKey { get; set; }

        public string ExternalUserId { get; set; }

        public string DisplayName { get; set; }

        public string Nickname { get; set; }

        public string AvatarUrl { get; set; }

        public string AvatarPath { get; set; }

        public FriendSettingsSource Source { get; set; } = FriendSettingsSource.AutoDiscovered;

        public bool IsIgnored { get; set; }

        public List<string> SelectedPlatforms { get; set; } = new List<string>();

        public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

        public DateTime? LastRefreshedUtc { get; set; }

        public DateTime? LastProbedUtc { get; set; }

        public string LastProbeStatus { get; set; }

        public string LastError { get; set; }

        [JsonIgnore]
        public bool IsManual => Source == FriendSettingsSource.Manual;

        public FriendSettingsEntry Clone()
        {
            return new FriendSettingsEntry
            {
                ProviderKey = ProviderKey,
                ExternalUserId = ExternalUserId,
                DisplayName = DisplayName,
                Nickname = Nickname,
                AvatarUrl = AvatarUrl,
                AvatarPath = AvatarPath,
                Source = Source,
                IsIgnored = IsIgnored,
                SelectedPlatforms = SelectedPlatforms?.ToList() ?? new List<string>(),
                AddedUtc = AddedUtc,
                LastRefreshedUtc = LastRefreshedUtc,
                LastProbedUtc = LastProbedUtc,
                LastProbeStatus = LastProbeStatus,
                LastError = LastError
            };
        }

        public FriendSettingsEntry Normalize()
        {
            ProviderKey = NormalizeToken(ProviderKey);
            ExternalUserId = NormalizeToken(ExternalUserId);
            DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? ExternalUserId : DisplayName.Trim();
            Nickname = NormalizeNullable(Nickname);
            AvatarUrl = NormalizeNullable(AvatarUrl);
            AvatarPath = NormalizeNullable(AvatarPath);
            SelectedPlatforms = NormalizePlatformList(SelectedPlatforms);
            if (AddedUtc == default(DateTime))
            {
                AddedUtc = DateTime.UtcNow;
            }

            if (AddedUtc.Kind == DateTimeKind.Local)
            {
                AddedUtc = AddedUtc.ToUniversalTime();
            }

            LastProbeStatus = NormalizeNullable(LastProbeStatus);
            LastError = NormalizeNullable(LastError);
            return this;
        }

        internal static List<string> NormalizePlatformList(IEnumerable<string> platforms)
        {
            return (platforms ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static string BuildKey(string providerKey, string externalUserId)
        {
            providerKey = NormalizeToken(providerKey);
            externalUserId = NormalizeToken(externalUserId);
            return string.IsNullOrWhiteSpace(providerKey) || string.IsNullOrWhiteSpace(externalUserId)
                ? null
                : providerKey.ToLowerInvariant() + "|" + externalUserId.ToLowerInvariant();
        }

        private static string NormalizeToken(string value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static string NormalizeNullable(string value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public sealed class FriendAccountRef
    {
        public string ProviderKey { get; set; }

        public string ExternalUserId { get; set; }

        [JsonIgnore]
        public string Key => BuildKey(ProviderKey, ExternalUserId);

        public FriendAccountRef Clone()
        {
            return new FriendAccountRef
            {
                ProviderKey = ProviderKey,
                ExternalUserId = ExternalUserId
            };
        }

        public FriendAccountRef Normalize()
        {
            ProviderKey = NormalizeToken(ProviderKey);
            ExternalUserId = NormalizeToken(ExternalUserId);
            return this;
        }

        public static FriendAccountRef From(string providerKey, string externalUserId)
        {
            return new FriendAccountRef
            {
                ProviderKey = providerKey,
                ExternalUserId = externalUserId
            }.Normalize();
        }

        public static string BuildKey(string providerKey, string externalUserId)
        {
            providerKey = NormalizeToken(providerKey);
            externalUserId = NormalizeToken(externalUserId);
            return string.IsNullOrWhiteSpace(providerKey) || string.IsNullOrWhiteSpace(externalUserId)
                ? null
                : providerKey.ToLowerInvariant() + "|" + externalUserId.ToLowerInvariant();
        }

        public bool Matches(string providerKey, string externalUserId)
        {
            return string.Equals(Key, BuildKey(providerKey, externalUserId), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeToken(string value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public sealed class FriendMergeGroup
    {
        public string Id { get; set; }

        public string Nickname { get; set; }

        public FriendAccountRef AvatarAccount { get; set; }

        public List<FriendAccountRef> Members { get; set; } = new List<FriendAccountRef>();

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        [JsonIgnore]
        public bool IsValid => Members != null && Members.Count >= 2;

        public FriendMergeGroup Clone()
        {
            return new FriendMergeGroup
            {
                Id = Id,
                Nickname = Nickname,
                AvatarAccount = AvatarAccount?.Clone(),
                Members = Members?.Select(member => member?.Clone()).Where(member => member != null).ToList()
                          ?? new List<FriendAccountRef>(),
                CreatedUtc = CreatedUtc
            };
        }

        public FriendMergeGroup Normalize()
        {
            Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim();
            Nickname = string.IsNullOrWhiteSpace(Nickname) ? null : Nickname.Trim();
            AvatarAccount = AvatarAccount?.Clone()?.Normalize();
            Members = NormalizeMembers(Members);
            if (AvatarAccount != null && !Members.Any(member => member.Matches(AvatarAccount.ProviderKey, AvatarAccount.ExternalUserId)))
            {
                AvatarAccount = Members.FirstOrDefault()?.Clone();
            }

            if (CreatedUtc == default(DateTime))
            {
                CreatedUtc = DateTime.UtcNow;
            }

            if (CreatedUtc.Kind == DateTimeKind.Local)
            {
                CreatedUtc = CreatedUtc.ToUniversalTime();
            }

            return this;
        }

        public bool Contains(string providerKey, string externalUserId)
        {
            return Members?.Any(member => member?.Matches(providerKey, externalUserId) == true) == true;
        }

        private static List<FriendAccountRef> NormalizeMembers(IEnumerable<FriendAccountRef> members)
        {
            var result = new List<FriendAccountRef>();
            var seenAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in members ?? Enumerable.Empty<FriendAccountRef>())
            {
                var normalized = member?.Clone()?.Normalize();
                var key = normalized?.Key;
                if (string.IsNullOrWhiteSpace(key) ||
                    !seenAccounts.Add(key) ||
                    !seenProviders.Add(normalized.ProviderKey))
                {
                    continue;
                }

                result.Add(normalized);
            }

            return result
                .OrderBy(member => member.ProviderKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(member => member.ExternalUserId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
