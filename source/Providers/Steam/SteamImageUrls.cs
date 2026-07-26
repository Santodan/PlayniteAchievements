using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Steam
{
    /// <summary>
    /// Builds Steam CDN image URLs that are derivable purely from an app id.
    /// Used to supply cover and icon art for provider-only (unowned) friend games,
    /// which have no Playnite library entry to resolve images from.
    /// </summary>
    internal static class SteamImageUrls
    {
        private const string CdnHost = "https://cdn.akamai.steamstatic.com/steam/apps";
        private const string StoreAppDetailsBase = "https://store.steampowered.com/api/appdetails";
        private static readonly HttpClient StoreHttp = CreateStoreHttpClient();

        public static string Cover(int appId)
        {
            return appId > 0
                ? $"{CdnHost}/{appId}/library_600x900.jpg"
                : null;
        }

        public static string Icon(int appId)
        {
            return appId > 0
                ? $"{CdnHost}/{appId}/capsule_231x87.jpg"
                : null;
        }

        public static string Header(int appId)
        {
            return appId > 0
                ? $"{CdnHost}/{appId}/header.jpg"
                : null;
        }

        public static string LibraryHero(int appId)
        {
            return appId > 0
                ? $"{CdnHost}/{appId}/library_hero.jpg"
                : null;
        }

        public static async Task<SteamStoreImageUrls> GetStoreFallbackAsync(
            int appId,
            CancellationToken cancel,
            ILogger logger = null)
        {
            if (appId <= 0)
            {
                return null;
            }

            try
            {
                var appIdText = appId.ToString(CultureInfo.InvariantCulture);
                var url = $"{StoreAppDetailsBase}?appids={appIdText}&filters=basic";
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                using (var response = await StoreHttp.SendAsync(request, cancel).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return null;
                    }

                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    return ParseStoreFallback(appId, json);
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex)
            {
                logger?.Debug(ex, $"Steam appdetails image lookup timed out for appId={appId}.");
                return null;
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, $"Steam appdetails image lookup failed for appId={appId}.");
                return null;
            }
        }

        internal static SteamStoreImageUrls ParseStoreFallback(int appId, string json)
        {
            if (appId <= 0 || string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                var root = JsonConvert.DeserializeObject<Dictionary<string, SteamStoreAppDetailsResponse>>(json);
                if (root == null ||
                    !root.TryGetValue(appId.ToString(CultureInfo.InvariantCulture), out var response) ||
                    response?.Success != true ||
                    response.Data == null)
                {
                    return null;
                }

                var data = response.Data;
                var iconUrl = FirstValidImageUrl(data.CapsuleImage, data.CapsuleImageV5, data.HeaderImage);
                var coverUrl = FirstValidImageUrl(data.HeaderImage, data.CapsuleImage, data.CapsuleImageV5);
                if (string.IsNullOrWhiteSpace(iconUrl) && string.IsNullOrWhiteSpace(coverUrl))
                {
                    return null;
                }

                return new SteamStoreImageUrls
                {
                    IconUrl = iconUrl,
                    CoverUrl = coverUrl
                };
            }
            catch
            {
                return null;
            }
        }

        private static HttpClient CreateStoreHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) PlayniteAchievements/1.0");
            return client;
        }

        private static string FirstValidImageUrl(params string[] urls)
        {
            return urls?
                .Select(NormalizeImageUrl)
                .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
        }

        private static string NormalizeImageUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            var trimmed = url.Trim();
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                return null;
            }

            return trimmed;
        }

        private sealed class SteamStoreAppDetailsResponse
        {
            [JsonProperty("success")]
            public bool Success { get; set; }

            [JsonProperty("data")]
            public SteamStoreAppDetailsData Data { get; set; }
        }

        private sealed class SteamStoreAppDetailsData
        {
            [JsonProperty("header_image")]
            public string HeaderImage { get; set; }

            [JsonProperty("capsule_image")]
            public string CapsuleImage { get; set; }

            [JsonProperty("capsule_imagev5")]
            public string CapsuleImageV5 { get; set; }
        }
    }

    internal sealed class SteamStoreImageUrls
    {
        public string IconUrl { get; set; }

        public string CoverUrl { get; set; }
    }
}
