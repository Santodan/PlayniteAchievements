using System.Globalization;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.Steam
{
    /// <summary>Reads a Steam app id from what a user types or pastes.</summary>
    internal static class SteamAppIds
    {
        private static readonly Regex AppUrl = new Regex(
            @"(?:store\.steampowered\.com|steamcommunity\.com|steamdb\.info)/app/(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The app id a bare id or a store, community or SteamDB app address names; null otherwise.</summary>
        public static int? TryParse(string text)
        {
            var part = text?.Trim() ?? string.Empty;
            var address = AppUrl.Match(part);
            if (address.Success)
            {
                part = address.Groups[1].Value;
            }

            return int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) && appId > 0
                ? appId
                : (int?)null;
        }
    }
}
