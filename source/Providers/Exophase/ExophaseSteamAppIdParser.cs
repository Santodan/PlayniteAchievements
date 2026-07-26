using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.Exophase
{
    internal static class ExophaseSteamAppIdParser
    {
        internal static int Extract(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return 0;
            }

            var decoded = WebUtility.HtmlDecode(html);
            foreach (var pattern in new[]
            {
                @"store\.steampowered\.com/(?:agecheck/)?app/(\d+)",
                @"steamcommunity\.com/app/(\d+)",
                @"steam/apps/(\d+)(?:/|\\)",
                @"\bdata-(?:ds-)?app(?:id|-id)\s*=\s*[""']?(\d+)"
            })
            {
                var match = Regex.Match(decoded, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (match.Success &&
                    int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId) &&
                    appId > 0)
                {
                    return appId;
                }
            }

            return 0;
        }
    }
}
