using System;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.Steam
{
    internal sealed class SteamWebAuthSession
    {
        public SteamWebAuthSession(string steamId64, string webApiToken, bool hasSteamSessionCookies = false)
        {
            SteamId64 = NormalizeSteamId64(steamId64);
            WebApiToken = Normalize(webApiToken);
            HasSteamSessionCookies = hasSteamSessionCookies;
        }

        public string SteamId64 { get; }

        public string WebApiToken { get; }

        public bool HasSteamSessionCookies { get; }

        public bool HasSteamId => !string.IsNullOrWhiteSpace(SteamId64);

        public bool HasWebApiToken => !string.IsNullOrWhiteSpace(WebApiToken);

        public bool IsComplete => HasSteamId && HasWebApiToken;

        public static SteamWebAuthSession Empty(bool hasSteamSessionCookies = false)
            => new SteamWebAuthSession(null, null, hasSteamSessionCookies);

        internal static string NormalizeSteamId64(string value)
        {
            var normalized = Normalize(value);
            return !string.IsNullOrWhiteSpace(normalized) &&
                   Regex.IsMatch(normalized, @"^\d{17}$")
                ? normalized
                : null;
        }

        private static string Normalize(string value)
            => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    internal static class SteamWebAuthParser
    {
        private static readonly Regex SteamIdPattern =
            new Regex(@"g_steamID\s*=\s*""(?<id>[0-9]{17})""", RegexOptions.IgnoreCase);

        private static readonly Regex JsonSteamIdPattern =
            new Regex(@"""steamid""\s*:\s*""(?<id>[0-9]{17})""", RegexOptions.IgnoreCase);

        private static readonly Regex EncodedTokenPattern =
            new Regex(@"&quot;webapi_token&quot;:&quot;(?<token>[^&]+)&quot;", RegexOptions.IgnoreCase);

        private static readonly Regex JsonTokenPattern =
            new Regex(@"""webapi_token""\s*:\s*""(?<token>[^""]+)""", RegexOptions.IgnoreCase);

        public static SteamWebAuthSession Parse(
            string source,
            string cookieSteamId64 = null,
            bool hasSteamSessionCookies = false)
        {
            var steamId = SteamWebAuthSession.NormalizeSteamId64(cookieSteamId64)
                ?? ExtractSteamId64(source);
            var token = ExtractWebApiToken(source);
            return new SteamWebAuthSession(steamId, token, hasSteamSessionCookies);
        }

        public static string ExtractSteamId64(string source)
        {
            var match = MatchFirst(source, SteamIdPattern, JsonSteamIdPattern);
            return match == null
                ? null
                : SteamWebAuthSession.NormalizeSteamId64(match.Groups["id"].Value);
        }

        public static string ExtractWebApiToken(string source)
        {
            var match = MatchFirst(source, EncodedTokenPattern, JsonTokenPattern);
            var token = match?.Groups["token"].Value?.Trim();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }

        private static Match MatchFirst(string source, params Regex[] patterns)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return null;
            }

            foreach (var pattern in patterns)
            {
                var match = pattern.Match(source);
                if (match.Success)
                {
                    return match;
                }
            }

            return null;
        }
    }
}
