using Playnite.SDK;
using Playnite.SDK.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Exophase
{
    /// <summary>
    /// Client for the public JSON API on api.exophase.com (reverse-engineered; see the front end's
    /// /public/ endpoints). All requests go through the shared <see cref="ExophaseApiClient"/> WebView
    /// fetch path so the Cloudflare clearance cookie in the offscreen browser profile is applied; the
    /// public read endpoints do not require Exophase login cookies.
    /// </summary>
    internal sealed class ExophasePublicApiClient
    {
        private const string ApiBaseUrl = "https://api.exophase.com";
        private const int PlayerGamesPageSize = 50;

        // Runaway guard: 200 pages = a 10,000-game library, far beyond any real profile.
        private const int MaxPlayerGamesPages = 200;

        private readonly ExophaseApiClient _apiClient;
        private readonly ILogger _logger;

        public ExophasePublicApiClient(ExophaseApiClient apiClient, ILogger logger)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _logger = logger;
        }

        /// <summary>
        /// Fetches the player's entire library across all services via the paginated games-list
        /// endpoint (50 games per page, 1-based pages). Returns null on failure so callers can fall
        /// back; a mid-pagination failure is also null rather than a silently truncated library.
        /// </summary>
        public async Task<List<ExophasePlayerGame>> GetPlayerGamesAsync(long playerProfileId, CancellationToken ct)
        {
            if (playerProfileId <= 0)
            {
                return null;
            }

            var gamesByMasterId = new Dictionary<long, ExophasePlayerGame>();
            for (var page = 1; page <= MaxPlayerGamesPages; page++)
            {
                ct.ThrowIfCancellationRequested();

                var url = $"{ApiBaseUrl}/public/player/{playerProfileId}/games" +
                    $"?page={page}&environment=&sort=1&showHidden=0&me=0&query=";
                var json = await _apiClient.FetchJsonViaWebViewAsync(url, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    _logger?.Warn($"[Exophase] Player games page {page} returned no JSON for profile {playerProfileId}.");
                    return null;
                }

                ExophasePlayerGamesResponse response;
                try
                {
                    response = Serialization.FromJson<ExophasePlayerGamesResponse>(json);
                }
                catch (Exception ex)
                {
                    _logger?.Warn($"[Exophase] Failed to parse player games page {page} for profile {playerProfileId}: {ex.Message}");
                    return null;
                }

                if (response == null || !response.Success)
                {
                    // A page beyond the last returns success:false; on page 1 that means the
                    // profile id is wrong or the endpoint failed.
                    if (page == 1)
                    {
                        _logger?.Warn($"[Exophase] Player games endpoint returned success=false on page 1 for profile {playerProfileId}.");
                        return null;
                    }

                    break;
                }

                var pageGames = response.Games ?? new List<ExophasePlayerGame>();
                foreach (var game in pageGames)
                {
                    if (game == null || game.MasterId <= 0)
                    {
                        continue;
                    }

                    gamesByMasterId[game.MasterId] = game;
                }

                if (pageGames.Count < PlayerGamesPageSize)
                {
                    break;
                }
            }

            _logger?.Debug($"[Exophase] Player games: profile {playerProfileId} -> {gamesByMasterId.Count} unique game(s).");
            return gamesByMasterId.Values.ToList();
        }

        /// <summary>
        /// Fetches the awards a player has earned for one game (earned only; locked awards are
        /// absent). The response carries stable award ids, the platform-native achievement key
        /// (canonical_id) and exact unix unlock timestamps. Returns null on failure; an empty list
        /// is a legitimate zero-unlock result.
        /// </summary>
        public async Task<List<ExophaseEarnedAward>> GetEarnedAwardsAsync(long masterPlayerId, long masterId, CancellationToken ct)
        {
            if (masterPlayerId <= 0 || masterId <= 0)
            {
                return null;
            }

            var url = $"{ApiBaseUrl}/public/player/{masterPlayerId}/game/{masterId}/earned?last=0";
            var json = await _apiClient.FetchJsonViaWebViewAsync(url, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            ExophaseEarnedResponse response;
            try
            {
                response = Serialization.FromJson<ExophaseEarnedResponse>(json);
            }
            catch (Exception ex)
            {
                _logger?.Warn($"[Exophase] Failed to parse earned awards for player {masterPlayerId}, game {masterId}: {ex.Message}");
                return null;
            }

            if (response == null || !response.Success)
            {
                return null;
            }

            return response.List ?? new List<ExophaseEarnedAward>();
        }

        /// <summary>
        /// Resolves a profile's identity from one rendered profile-page fetch: the numeric
        /// playerProfileId the games-list endpoint takes (not the username) plus the rich SSR
        /// games blob (the only source of the game-level Steam appid, recent 50 games only).
        /// The rendered HTML is returned so callers can reuse it for profile metadata parsing.
        /// </summary>
        public async Task<ExophaseProfileIdentity> ResolveProfileIdentityAsync(string username, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                return null;
            }

            var url = $"https://www.exophase.com/user/{Uri.EscapeDataString(username.Trim())}/";
            var fetched = await _apiClient.FetchProfilePageWithGlobalsAsync(url, ct).ConfigureAwait(false);
            if (fetched == null)
            {
                return null;
            }

            var identity = new ExophaseProfileIdentity { Html = fetched.Html };

            if (long.TryParse(fetched.PlayerProfileId?.Trim(), out var profileId) && profileId > 0)
            {
                identity.PlayerProfileId = profileId;
            }

            identity.SsrGames = TryParseSsrGames(fetched.PlayerGamesJson);
            return identity;
        }

        // The SSR blob is an object { featured_image, next, games: [...] } (verified live);
        // tolerate a bare array as a fallback shape.
        private List<ExophasePlayerGame> TryParseSsrGames(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                var trimmed = json.Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                {
                    return Serialization.FromJson<List<ExophasePlayerGame>>(trimmed);
                }

                var blob = Serialization.FromJson<ExophaseSsrPlayerGamesBlob>(trimmed);
                return blob?.Games;
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[Exophase] Failed to parse SSR playerGames blob: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Extracts the game slug from a games-list row's endpoint_awards URL.
        /// </summary>
        public static string GetGameSlug(ExophasePlayerGame game)
        {
            return ExophaseApiClient.ExtractSlugFromUrl(game?.Meta?.EndpointAwards);
        }

        /// <summary>
        /// Extracts the per-service player id from the endpoint_awards #fragment — the same value
        /// the profile page's game links carry, which scopes an achievement page to the player.
        /// </summary>
        public static string GetContextId(ExophasePlayerGame game)
        {
            var endpoint = game?.Meta?.EndpointAwards;
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return null;
            }

            var hashIndex = endpoint.LastIndexOf('#');
            if (hashIndex < 0 || hashIndex >= endpoint.Length - 1)
            {
                return null;
            }

            var fragment = endpoint.Substring(hashIndex + 1).Trim();
            return fragment.Length > 0 && fragment.All(char.IsDigit) ? fragment : null;
        }

        /// <summary>
        /// Converts a games-list row's playtime to whole minutes, preferring the structured
        /// playtimeUnits over the localized display string.
        /// </summary>
        public static int GetPlaytimeMinutes(ExophasePlayerGame game)
        {
            var units = game?.PlaytimeUnits;
            if (units == null)
            {
                return 0;
            }

            var minutes = (int)Math.Round(units.Hours * 60) + units.Minutes;
            return Math.Max(0, minutes);
        }

        /// <summary>
        /// Converts a unix-seconds field to a UTC timestamp; 0 means "none" on this API.
        /// </summary>
        public static DateTime? FromUnixSeconds(long value)
        {
            if (value <= 0)
            {
                return null;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(value).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        /// <summary>
        /// Resolves the Steam appid for an SSR-blob game row: meta.canonical_id is the appid as a
        /// string for Steam environments; the Steam Store link in meta.links is the fallback source.
        /// Returns 0 when unresolved (including for API rows, which never carry an appid).
        /// </summary>
        public static int GetSteamAppId(ExophasePlayerGame game)
        {
            var meta = game?.Meta;
            if (meta == null ||
                !string.Equals(meta.EnvironmentSlug, "steam", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (int.TryParse(meta.CanonicalId?.Trim(), out var appId) && appId > 0)
            {
                return appId;
            }

            foreach (var link in meta.Links ?? new List<ExophasePlayerGameLink>())
            {
                var endpoint = link?.Endpoint;
                if (string.IsNullOrWhiteSpace(endpoint))
                {
                    continue;
                }

                var extracted = ExophaseSteamAppIdParser.Extract(endpoint);
                if (extracted > 0)
                {
                    return extracted;
                }
            }

            return 0;
        }

        /// <summary>
        /// Image URLs from the API and SSR blob are host-relative m.exophase.com paths; absolute
        /// URLs pass through unchanged.
        /// </summary>
        public static string NormalizeImageUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            var trimmed = url.Trim();
            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                return "https:" + trimmed;
            }

            return "https://m.exophase.com/" + trimmed.TrimStart('/');
        }
    }

    #region Public API Response Models

    [DataContract]
    public sealed class ExophasePlayerGamesResponse
    {
        [DataMember(Name = "success")]
        public bool Success { get; set; }

        [DataMember(Name = "games")]
        public List<ExophasePlayerGame> Games { get; set; }

        [DataMember(Name = "next")]
        public long? Next { get; set; }
    }

    /// <summary>
    /// Shape of the server-rendered window.playerGames global (verified live):
    /// { featured_image, next, games: [...] } holding the 50 most-recently-played games with
    /// rich meta (the only source of the game-level Steam appid).
    /// </summary>
    [DataContract]
    public sealed class ExophaseSsrPlayerGamesBlob
    {
        [DataMember(Name = "next")]
        public long? Next { get; set; }

        [DataMember(Name = "games")]
        public List<ExophasePlayerGame> Games { get; set; }
    }

    [DataContract]
    public sealed class ExophasePlayerGame
    {
        [DataMember(Name = "master_id")]
        public long MasterId { get; set; }

        [DataMember(Name = "master_playerid")]
        public long MasterPlayerId { get; set; }

        [DataMember(Name = "earned_awards")]
        public int? EarnedAwards { get; set; }

        [DataMember(Name = "total_awards")]
        public int? TotalAwards { get; set; }

        [DataMember(Name = "percent")]
        public double? Percent { get; set; }

        [DataMember(Name = "playtime")]
        public string Playtime { get; set; }

        [DataMember(Name = "playtimeUnits")]
        public ExophasePlaytimeUnits PlaytimeUnits { get; set; }

        [DataMember(Name = "lastplayed_utc")]
        public long LastPlayedUtc { get; set; }

        [DataMember(Name = "completion_date_utc")]
        public long CompletionDateUtc { get; set; }

        [DataMember(Name = "firstplayed")]
        public long FirstPlayed { get; set; }

        [DataMember(Name = "resource_standard")]
        public string ResourceStandard { get; set; }

        [DataMember(Name = "resource_small")]
        public string ResourceSmall { get; set; }

        [DataMember(Name = "resource_tile")]
        public string ResourceTile { get; set; }

        [DataMember(Name = "meta")]
        public ExophasePlayerGameMeta Meta { get; set; }
    }

    [DataContract]
    public sealed class ExophasePlaytimeUnits
    {
        [DataMember(Name = "hours")]
        public double Hours { get; set; }

        [DataMember(Name = "minutes")]
        public int Minutes { get; set; }
    }

    [DataContract]
    public sealed class ExophasePlayerGameMeta
    {
        [DataMember(Name = "environment_slug")]
        public string EnvironmentSlug { get; set; }

        [DataMember(Name = "environment_name")]
        public string EnvironmentName { get; set; }

        [DataMember(Name = "title")]
        public string Title { get; set; }

        [DataMember(Name = "platforms")]
        public List<ExophasePlatform> Platforms { get; set; }

        // Absolute ".../game/{slug}/achievements|trophies|challenges/#{master_playerid}" URL;
        // null for titles with no achievements (demos/tools).
        [DataMember(Name = "endpoint_awards")]
        public string EndpointAwards { get; set; }

        // Present only in the SSR window.playerGames blob, never in the paginated API response.
        // For Steam environments this is the Steam appid as a string.
        [DataMember(Name = "canonical_id")]
        public string CanonicalId { get; set; }

        // SSR blob only; the Steam Store link's endpoint embeds the appid as a fallback source.
        [DataMember(Name = "links")]
        public List<ExophasePlayerGameLink> Links { get; set; }
    }

    [DataContract]
    public sealed class ExophasePlayerGameLink
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "endpoint")]
        public string Endpoint { get; set; }
    }

    [DataContract]
    public sealed class ExophaseEarnedResponse
    {
        [DataMember(Name = "success")]
        public bool Success { get; set; }

        [DataMember(Name = "master_gameid")]
        public string MasterGameId { get; set; }

        [DataMember(Name = "list")]
        public List<ExophaseEarnedAward> List { get; set; }
    }

    [DataContract]
    public sealed class ExophaseEarnedAward
    {
        // Live responses use camelCase for this one field (unlike the rest of the payload).
        [DataMember(Name = "masterAwardId")]
        public long MasterAwardId { get; set; }

        [DataMember(Name = "awardid")]
        public long AwardId { get; set; }

        // Observed equal to MasterAwardId in live responses; falls back when either is absent.
        public long EffectiveAwardId => MasterAwardId != 0 ? MasterAwardId : AwardId;

        // The platform's native achievement key (e.g. the Steam apiname); locale-independent.
        [DataMember(Name = "canonical_id")]
        public string CanonicalId { get; set; }

        // Unix seconds of the exact unlock time.
        [DataMember(Name = "timestamp")]
        public long Timestamp { get; set; }

        [DataMember(Name = "earned")]
        public string Earned { get; set; }

        [DataMember(Name = "slug")]
        public string Slug { get; set; }

        // "/achievement/{game-slug}/{n}-{award-slug}" where {n} is an internal page id,
        // NOT the award id — do not parse ids out of this.
        [DataMember(Name = "endpoint")]
        public string Endpoint { get; set; }

        [DataMember(Name = "icons")]
        public ExophaseEarnedAwardIcons Icons { get; set; }
    }

    [DataContract]
    public sealed class ExophaseEarnedAwardIcons
    {
        [DataMember(Name = "s")]
        public string Small { get; set; }

        [DataMember(Name = "m")]
        public string Medium { get; set; }

        [DataMember(Name = "l")]
        public string Large { get; set; }

        [DataMember(Name = "o")]
        public string Original { get; set; }

        [DataMember(Name = "t")]
        public string Tile { get; set; }
    }

    /// <summary>
    /// A profile's resolved identity: the numeric top-level profile id plus the rendered page HTML
    /// (for metadata parsing) and the SSR games blob (rich meta for the recent 50 games).
    /// </summary>
    internal sealed class ExophaseProfileIdentity
    {
        public long PlayerProfileId { get; set; }

        public string Html { get; set; }

        public List<ExophasePlayerGame> SsrGames { get; set; }
    }

    #endregion
}
