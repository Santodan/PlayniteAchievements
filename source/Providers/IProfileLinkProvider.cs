using System;

namespace PlayniteAchievements.Providers
{
    /// <summary>
    /// A provider whose platform has public user profile pages, which the showcase profile card
    /// links to. Each provider owns its address shape, so a user adding a link only types their
    /// name on that platform (a full link is taken as-is instead).
    /// </summary>
    public interface IProfileLinkProvider
    {
        /// <summary>
        /// The profile address with <c>{0}</c> where the user name goes, for example
        /// "https://psnprofiles.com/{0}". Shown to the user as the shape of the link.
        /// </summary>
        string ProfileUrlPattern { get; }

        /// <summary>The profile page for a user name or id on this platform, or null when blank.</summary>
        string BuildProfileUrl(string user);

        /// <summary>
        /// The signed-in user's name or id from this provider's stored settings, or null when
        /// the settings do not identify the user.
        /// </summary>
        string GetCurrentUserProfileName();
    }

    /// <summary>Shared formatting for <see cref="IProfileLinkProvider.BuildProfileUrl"/>.</summary>
    public static class ProfileLinkUrls
    {
        public static string Format(string pattern, string user)
        {
            var trimmed = user?.Trim();
            if (string.IsNullOrEmpty(trimmed) || string.IsNullOrWhiteSpace(pattern))
            {
                return null;
            }

            return string.Format(pattern, Uri.EscapeDataString(trimmed.TrimStart('@')));
        }
    }
}
