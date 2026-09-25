using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.Showcase
{
    /// <summary>
    /// The profile values a profile widget renders after merging the manual
    /// showcase settings with the persisted provider identity.
    /// </summary>
    public sealed class ShowcaseProfileProjection
    {
        public string DisplayName { get; set; }

        public string Subtitle { get; set; }

        public string AvatarPath { get; set; }

        public string BackgroundPath { get; set; }

        public bool FromProviderIdentity { get; set; }

        /// <summary>Platform profile links, in display order.</summary>
        public IReadOnlyList<ShowcaseProfileLinkProjection> Links { get; set; } =
            Array.Empty<ShowcaseProfileLinkProjection>();
    }

    /// <summary>A resolved platform profile link: the provider it belongs to and its URL.</summary>
    public sealed class ShowcaseProfileLinkProjection
    {
        public ShowcaseProfileLinkProjection(string providerKey, string url)
        {
            ProviderKey = providerKey;
            Url = url;
        }

        public string ProviderKey { get; }

        public string Url { get; }
    }

    /// <summary>
    /// Merges manual showcase profile settings over the current user's provider
    /// identity. Manual fields win per-field when non-blank; Steam is the
    /// preferred identity source, then any identity with a usable name or avatar.
    /// </summary>
    public static class ShowcaseProfileResolver
    {
        private const string PreferredProviderKey = "Steam";

        public static ShowcaseProfileProjection Resolve(
            ShowcaseProfileSettings manual,
            IReadOnlyList<FriendIdentity> identities)
        {
            var identity = PickIdentity(identities);
            return new ShowcaseProfileProjection
            {
                DisplayName = FirstNonBlank(
                    manual?.DisplayName,
                    identity?.DisplayName,
                    identity?.ProviderNickname),
                Subtitle = TrimOrNull(manual?.Subtitle),
                AvatarPath = FirstNonBlank(
                    manual?.AvatarPath,
                    identity?.AvatarPath,
                    identity?.AvatarUrl),
                BackgroundPath = TrimOrNull(manual?.BackgroundPath),
                FromProviderIdentity = identity != null &&
                    string.IsNullOrWhiteSpace(manual?.DisplayName),
                Links = ResolveLinks(manual, identities)
            };
        }

        /// <summary>
        /// The profile's platform links: each manual entry in its saved order (its URL, or the
        /// derived one when blank; none when hidden), then every remaining derivable identity.
        /// </summary>
        public static IReadOnlyList<ShowcaseProfileLinkProjection> ResolveLinks(
            ShowcaseProfileSettings manual,
            IReadOnlyList<FriendIdentity> identities)
        {
            var derived = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var derivedOrder = new List<string>();
            foreach (var identity in identities ?? Array.Empty<FriendIdentity>())
            {
                var url = DeriveProfileUrl(identity?.ProviderKey, identity?.ExternalUserId);
                if (url != null && !derived.ContainsKey(identity.ProviderKey))
                {
                    derived[identity.ProviderKey] = url;
                    derivedOrder.Add(identity.ProviderKey);
                }
            }

            var links = new List<ShowcaseProfileLinkProjection>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in manual?.Links ?? new List<ShowcaseProfileLink>())
            {
                var key = TrimOrNull(link?.ProviderKey);
                if (key == null || !seen.Add(key) || link.Hidden)
                {
                    continue;
                }

                var url = NormalizeUrl(link.Url) ?? (derived.TryGetValue(key, out var auto) ? auto : null);
                if (url != null)
                {
                    links.Add(new ShowcaseProfileLinkProjection(key, url));
                }
            }

            foreach (var key in derivedOrder)
            {
                if (seen.Add(key))
                {
                    links.Add(new ShowcaseProfileLinkProjection(key, derived[key]));
                }
            }

            return links;
        }

        /// <summary>
        /// The public profile page for a provider's stored user id, for the providers whose
        /// current-user identity carries one (Steam's SteamID64, the RetroAchievements and
        /// Exophase usernames). Null when the provider has no derivable page.
        /// </summary>
        public static string DeriveProfileUrl(string providerKey, string externalUserId)
        {
            var id = TrimOrNull(externalUserId);
            if (id == null || string.IsNullOrWhiteSpace(providerKey))
            {
                return null;
            }

            switch (providerKey.Trim().ToUpperInvariant())
            {
                case "STEAM":
                    return id.All(char.IsDigit)
                        ? "https://steamcommunity.com/profiles/" + id
                        : null;
                case "RETROACHIEVEMENTS":
                    return "https://retroachievements.org/user/" + Uri.EscapeDataString(id);
                case "EXOPHASE":
                    // Same page ExophaseFriendsProvider links friends to.
                    return "https://www.exophase.com/user/" + Uri.EscapeDataString(id) + "/";
                default:
                    return null;
            }
        }

        /// <summary>
        /// A manual link as an absolute web URL: a bare host gets https, and anything that is
        /// not http(s) after that is dropped rather than handed to the shell.
        /// </summary>
        public static string NormalizeUrl(string value)
        {
            var text = TrimOrNull(value);
            if (text == null)
            {
                return null;
            }

            if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // Some other scheme (file:, mailto:, javascript:...) is refused outright rather
                // than rewritten into an https host.
                if (text.Contains("://") ||
                    (Uri.TryCreate(text, UriKind.Absolute, out var other) && other.Scheme.Length > 1))
                {
                    return null;
                }

                text = "https://" + text;
            }

            return Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? uri.AbsoluteUri
                : null;
        }

        private static FriendIdentity PickIdentity(IReadOnlyList<FriendIdentity> identities)
        {
            var usable = (identities ?? Array.Empty<FriendIdentity>())
                .Where(identity => identity != null && HasUsableContent(identity))
                .ToList();
            return usable.FirstOrDefault(identity =>
                    string.Equals(identity.ProviderKey, PreferredProviderKey, StringComparison.OrdinalIgnoreCase))
                ?? usable.FirstOrDefault();
        }

        private static bool HasUsableContent(FriendIdentity identity)
        {
            return !string.IsNullOrWhiteSpace(identity.DisplayName) ||
                !string.IsNullOrWhiteSpace(identity.ProviderNickname) ||
                !string.IsNullOrWhiteSpace(identity.AvatarPath) ||
                !string.IsNullOrWhiteSpace(identity.AvatarUrl);
        }

        private static string FirstNonBlank(params string[] values)
        {
            return values?.Select(TrimOrNull).FirstOrDefault(value => value != null);
        }

        private static string TrimOrNull(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
