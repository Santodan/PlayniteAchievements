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
                Links = ResolveLinks(manual)
            };
        }

        /// <summary>
        /// Turns a platform user name into that platform's profile address, or null when the
        /// platform has no profile pages. Assigned at plugin startup from the providers
        /// (<c>IProfileLinkProvider</c>), which own the address shapes.
        /// </summary>
        public static Func<string, string, string> ProfileUrlBuilder { get; set; }

        /// <summary>
        /// The signed-in user's name on each enabled platform that stores one, in provider order.
        /// Assigned at plugin startup from the providers' settings.
        /// </summary>
        public static Func<IReadOnlyList<KeyValuePair<string, string>>> CurrentUserProfileNames { get; set; }

        /// <summary>
        /// The profile's platform links. Before the user has saved any (<c>Links</c> null) there
        /// is one for every platform that knows the signed-in user's name; afterwards they are
        /// exactly the saved list, in its order. A saved entry's value is a user name, built into
        /// the platform's address, or a full link used as-is; a blank value falls back to the
        /// signed-in user's stored name, and an entry that resolves to nothing is dropped.
        /// </summary>
        public static IReadOnlyList<ShowcaseProfileLinkProjection> ResolveLinks(ShowcaseProfileSettings manual)
        {
            var currentNames = ReadCurrentUserProfileNames();
            var links = new List<ShowcaseProfileLinkProjection>();
            if (manual?.Links == null)
            {
                foreach (var pair in currentNames)
                {
                    AddLink(links, pair.Key, BuildLinkUrl(pair.Key, pair.Value));
                }

                return links;
            }

            foreach (var link in manual.Links)
            {
                var key = TrimOrNull(link?.ProviderKey);
                if (key == null)
                {
                    continue;
                }

                var value = TrimOrNull(link.Value) ?? currentNames.FirstOrDefault(pair =>
                    string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
                AddLink(links, key, BuildLinkUrl(key, value));
            }

            return links;
        }

        /// <summary>
        /// A link value's address: a full link (a scheme, or a host with a path) normalized to
        /// http(s), otherwise a user name built through the platform's address shape.
        /// </summary>
        public static string BuildLinkUrl(string providerKey, string value)
        {
            var text = TrimOrNull(value);
            if (text == null)
            {
                return null;
            }

            if (LooksLikeLink(text))
            {
                return NormalizeUrl(text);
            }

            return NormalizeUrl(ProfileUrlBuilder?.Invoke(providerKey, text));
        }

        // Suffixes that mark a bare host ("mysite.com") as a link. A fixed list rather than any
        // dotted word, because some platforms (Epic) allow periods in user names: "john.smith"
        // stays a name, and a name that does end in one of these needs its full profile link.
        private static readonly HashSet<string> LinkSuffixes = new HashSet<string>(
            new[]
            {
                "com", "net", "org", "io", "gg", "tv", "me", "co", "app", "dev", "info", "xyz",
                "uk", "us", "ca", "au", "de", "fr", "jp", "eu", "ru", "br", "nl", "es", "it"
            },
            StringComparer.OrdinalIgnoreCase);

        // User names never carry a scheme, a "www." prefix, or a slash, so any of those marks a
        // pasted address, as does a bare host ending in a known suffix.
        private static bool LooksLikeLink(string text)
        {
            if (text.Contains("://") || text.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (text.Any(char.IsWhiteSpace))
            {
                return false;
            }

            if (text.Contains("/") && text.Contains("."))
            {
                return true;
            }

            var lastDot = text.LastIndexOf('.');
            return lastDot > 0 &&
                   text.All(ch => char.IsLetterOrDigit(ch) || ch == '.' || ch == '-') &&
                   LinkSuffixes.Contains(text.Substring(lastDot + 1));
        }

        private static void AddLink(List<ShowcaseProfileLinkProjection> links, string providerKey, string url)
        {
            if (url != null)
            {
                links.Add(new ShowcaseProfileLinkProjection(providerKey, url));
            }
        }

        private static IReadOnlyList<KeyValuePair<string, string>> ReadCurrentUserProfileNames()
        {
            try
            {
                return CurrentUserProfileNames?.Invoke() ?? Array.Empty<KeyValuePair<string, string>>();
            }
            catch
            {
                return Array.Empty<KeyValuePair<string, string>>();
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
