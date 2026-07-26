using System;
using System.Text.RegularExpressions;
using PlayniteAchievements.Models;

namespace PlayniteAchievements.Providers.Steam
{
    internal sealed class SteamWebAuthSession
    {
        public SteamWebAuthSession(
            string steamId64,
            string webApiToken,
            bool hasSteamSessionCookies = false,
            AuthOutcome? transientFailureOutcome = null)
        {
            SteamId64 = NormalizeSteamId64(steamId64);
            WebApiToken = Normalize(webApiToken);
            HasSteamSessionCookies = hasSteamSessionCookies;
            TransientFailureOutcome = transientFailureOutcome;
        }

        public string SteamId64 { get; }

        public string WebApiToken { get; }

        public bool HasSteamSessionCookies { get; }

        public AuthOutcome? TransientFailureOutcome { get; }

        public bool IsTransientFailure => TransientFailureOutcome.HasValue;

        public bool HasSteamId => !string.IsNullOrWhiteSpace(SteamId64);

        public bool HasWebApiToken => !string.IsNullOrWhiteSpace(WebApiToken);

        public bool IsComplete => HasSteamId && HasWebApiToken;

        public static SteamWebAuthSession Empty(bool hasSteamSessionCookies = false)
            => new SteamWebAuthSession(null, null, hasSteamSessionCookies);

        public static SteamWebAuthSession TransientFailure(
            AuthOutcome outcome,
            SteamWebAuthSession partialSession = null)
            => new SteamWebAuthSession(
                partialSession?.SteamId64,
                partialSession?.WebApiToken,
                partialSession?.HasSteamSessionCookies ?? true,
                transientFailureOutcome: outcome);

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

        private static readonly Regex EncodedLoyaltyTokenPattern =
            new Regex(@"loyalty_webapi_token&quot;\s*:\s*&quot;(?<token>[^&]+)&quot;", RegexOptions.IgnoreCase);

        private static readonly Regex JsonLoyaltyTokenPattern =
            new Regex(@"""loyalty_webapi_token""\s*:\s*""(?<token>[^""]+)""", RegexOptions.IgnoreCase);

        private static readonly Regex AttributeLoyaltyTokenPattern =
            new Regex(@"data-loyalty_webapi_token\s*=\s*""(?<token>[^""]+)""", RegexOptions.IgnoreCase);

        public static SteamWebAuthSession Parse(
            string source,
            string cookieSteamId64 = null,
            bool hasSteamSessionCookies = false)
        {
            // The page's g_steamID reflects the account the community session actually
            // renders as; cookie-derived IDs can come from stale cookies left on other
            // Steam domains after an account switch, so they are only a fallback.
            var steamId = ExtractSteamId64(source)
                ?? SteamWebAuthSession.NormalizeSteamId64(cookieSteamId64);
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
            var match = MatchFirst(
                source,
                AttributeLoyaltyTokenPattern,
                EncodedLoyaltyTokenPattern,
                JsonLoyaltyTokenPattern,
                EncodedTokenPattern,
                JsonTokenPattern);
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
