using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Riot
{
    /// <summary>
    /// Fetches challenge definitions, localized text and token art paths from CommunityDragon.
    /// This source needs no API key, so challenge names and icons stay available even when the
    /// user's Riot key has lapsed.
    /// </summary>
    internal sealed class RiotChallengeMetadataClient : IDisposable
    {
        private const string UrlTemplate =
            "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/{0}/v1/challenges.json";

        /// <summary>CommunityDragon's fallback locale directory, which serves English text.</summary>
        private const string DefaultLocale = "default";

        private readonly ILogger _logger;
        private readonly HttpClient _http;
        private readonly HttpClientHandler _handler;
        private readonly string _cacheDirectory;

        public RiotChallengeMetadataClient(ILogger logger, string pluginUserDataPath)
        {
            _logger = logger;
            _cacheDirectory = string.IsNullOrWhiteSpace(pluginUserDataPath)
                ? null
                : Path.Combine(pluginUserDataPath, "riot");

            _handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                UseCookies = false
            };

            _http = new HttpClient(_handler)
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
        }

        public void Dispose()
        {
            _http?.Dispose();
            _handler?.Dispose();
        }

        /// <summary>
        /// Downloads the challenge definitions for the given global language, refreshing the local
        /// copy on every call. Riot changes challenges with each patch and there is no version to
        /// compare against, so the network copy always wins; the on-disk copy is a fallback for when
        /// CommunityDragon is unreachable.
        /// </summary>
        public async Task<CDragonChallengeFile> GetChallengesAsync(string globalLanguage, CancellationToken cancel)
        {
            var locale = MapGlobalLanguageToCDragonLocale(globalLanguage);
            var url = string.Format(UrlTemplate, locale);

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");

                    using (var response = await _http.SendAsync(request, cancel).ConfigureAwait(false))
                    {
                        // A locale directory can disappear between patches; English is always present.
                        if (response.StatusCode == HttpStatusCode.NotFound &&
                            !string.Equals(locale, DefaultLocale, StringComparison.OrdinalIgnoreCase))
                        {
                            _logger?.Warn($"[Riot] CommunityDragon has no '{locale}' challenge data. Falling back to '{DefaultLocale}'.");
                            return await GetChallengesAsync(null, cancel).ConfigureAwait(false);
                        }

                        response.EnsureSuccessStatusCode();
                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                        var parsed = RiotChallengeMapper.ParseMetadata(json);
                        if (parsed?.Challenges != null && parsed.Challenges.Count > 0)
                        {
                            SaveCache(locale, json);
                            return parsed;
                        }

                        _logger?.Warn("[Riot] CommunityDragon returned no challenge definitions.");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[Riot] Failed to download challenge definitions for locale '{locale}'.");
            }

            return LoadCache(locale) ?? LoadCache(DefaultLocale);
        }

        private string GetCachePath(string locale)
            => _cacheDirectory == null ? null : Path.Combine(_cacheDirectory, $"challenges-{locale}.json");

        private void SaveCache(string locale, string json)
        {
            var path = GetCachePath(locale);
            if (path == null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(_cacheDirectory);
                File.WriteAllText(path, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[Riot] Could not cache challenge definitions to '{path}'.");
            }
        }

        private CDragonChallengeFile LoadCache(string locale)
        {
            var path = GetCachePath(locale);
            if (path == null || !File.Exists(path))
            {
                return null;
            }

            try
            {
                var parsed = RiotChallengeMapper.ParseMetadata(File.ReadAllText(path, Encoding.UTF8));
                if (parsed?.Challenges != null && parsed.Challenges.Count > 0)
                {
                    _logger?.Info($"[Riot] Using cached challenge definitions for locale '{locale}'.");
                    return parsed;
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[Riot] Cached challenge definitions at '{path}' could not be read.");
            }

            return null;
        }

        private static readonly Dictionary<string, string> LocaleByGlobalLanguage =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["english"] = DefaultLocale,
                ["german"] = "de_de",
                ["french"] = "fr_fr",
                ["spanish"] = "es_es",
                ["latam"] = "es_mx",
                ["italian"] = "it_it",
                ["portuguese"] = "pt_br",
                ["brazilian"] = "pt_br",
                ["brazilianportuguese"] = "pt_br",
                ["russian"] = "ru_ru",
                ["polish"] = "pl_pl",
                ["hungarian"] = "hu_hu",
                ["czech"] = "cs_cz",
                ["romanian"] = "ro_ro",
                ["turkish"] = "tr_tr",
                ["greek"] = "el_gr",
                ["thai"] = "th_th",
                ["vietnamese"] = "vi_vn",
                ["japanese"] = "ja_jp",
                ["korean"] = "ko_kr",
                ["koreana"] = "ko_kr",
                ["schinese"] = "zh_cn",
                ["tchinese"] = "zh_tw"
            };

        /// <summary>
        /// Maps the plugin's global language to a CommunityDragon locale directory. League ships
        /// fewer locales than the plugin offers, so anything unmapped falls back to English.
        /// </summary>
        internal static string MapGlobalLanguageToCDragonLocale(string globalLanguage)
        {
            var trimmed = (globalLanguage ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return DefaultLocale;
            }

            if (LocaleByGlobalLanguage.TryGetValue(trimmed, out var mapped))
            {
                return mapped;
            }

            // Already a locale code such as "de-DE" or "de_DE".
            var normalized = trimmed.Replace('-', '_').ToLowerInvariant();
            return normalized.Length == 5 && normalized[2] == '_' ? normalized : DefaultLocale;
        }
    }
}
