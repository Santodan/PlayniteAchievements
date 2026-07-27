using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Steam
{
    internal sealed class SteamHuntersApiClient
    {
        private const string BaseUrl = "https://steamhunters.com/api";

        private readonly Func<string, CancellationToken, Task<string>> _fetchPageText;
        private readonly ILogger _logger;

        public SteamHuntersApiClient(
            Func<string, CancellationToken, Task<string>> fetchPageText,
            ILogger logger)
        {
            _fetchPageText = fetchPageText ?? throw new ArgumentNullException(nameof(fetchPageText));
            _logger = logger;
        }

        /// <summary>
        /// Fetches the achievement groups through an offscreen CEF view (the Steam scan's
        /// shared leased view while a scan or friend refresh holds the lease).
        /// steamhunters.com tarpits the .NET HTTP stack's TLS fingerprint, so the browser is
        /// the only reliable transport; the JSON endpoint renders as a plain-text document,
        /// so the page text is the raw JSON body. Returns null on failure.
        /// </summary>
        public async Task<SteamHuntersAchievementGroupsResponse> GetAchievementGroupsAsync(
            int appId,
            CancellationToken cancel)
        {
            if (appId <= 0)
            {
                return null;
            }

            var url = BaseUrl + "/GetAchievementGroups/v1?appId=" +
                appId.ToString(CultureInfo.InvariantCulture);

            var body = await _fetchPageText(url, cancel).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                _logger?.Warn($"[SteamHunters] Group request returned no content for appId={appId}.");
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<SteamHuntersAchievementGroupsResponse>(body);
            }
            catch (JsonException ex)
            {
                _logger?.Warn(ex, $"[SteamHunters] Failed to parse achievement groups for appId={appId} (length={body.Length}).");
                return null;
            }
        }
    }

    internal sealed class SteamHuntersAchievementGroupsResponse
    {
        public string GroupBy { get; set; }

        public List<SteamHuntersAchievementGroup> Groups { get; set; } =
            new List<SteamHuntersAchievementGroup>();
    }

    internal sealed class SteamHuntersAchievementGroup
    {
        public string Name { get; set; }

        public int? DlcAppId { get; set; }

        public string DlcAppName { get; set; }

        public List<string> AchievementApiNames { get; set; } = new List<string>();
    }
}
