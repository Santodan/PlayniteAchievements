using Playnite.SDK;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Riot
{
    /// <summary>
    /// Supplies a player's challenge state. The web API is the shipped implementation; the League
    /// client's local LCU endpoints are a second candidate that needs no API key, which is why the
    /// scanner and mapper depend on this interface rather than on <see cref="RiotApiClient"/>.
    /// </summary>
    internal interface IRiotChallengeSource
    {
        Task<RiotPlayerChallengeState> GetPlayerStateAsync(CancellationToken cancel);
    }

    /// <summary>
    /// Reads challenge state from the Riot web API using the user's own key. Resolves the PUUID
    /// once and caches it in settings so a refresh costs two calls instead of three.
    /// </summary>
    internal sealed class RiotWebChallengeSource : IRiotChallengeSource
    {
        private readonly ILogger _logger;
        private readonly RiotApiClient _client;
        private readonly RiotSettings _settings;
        private readonly System.Action _persistSettings;

        public RiotWebChallengeSource(
            ILogger logger,
            RiotApiClient client,
            RiotSettings settings,
            System.Action persistSettings)
        {
            _logger = logger;
            _client = client;
            _settings = settings;
            _persistSettings = persistSettings;
        }

        public async Task<RiotPlayerChallengeState> GetPlayerStateAsync(CancellationToken cancel)
        {
            var platform = _settings.PlatformRegion;
            var apiKey = _settings.RiotApiKey;

            var puuid = await ResolvePuuidAsync(platform, apiKey, cancel).ConfigureAwait(false);

            var playerJson = await _client
                .GetPlayerDataJsonAsync(platform, puuid, apiKey, cancel)
                .ConfigureAwait(false);
            var playerData = RiotChallengeMapper.ParsePlayerData(playerJson);

            var percentiles = await LoadPercentilesAsync(platform, apiKey, cancel).ConfigureAwait(false);

            return new RiotPlayerChallengeState
            {
                PlayerKey = puuid,
                Challenges = playerData?.Challenges ?? new List<RiotChallengeInfoDto>(),
                LevelPercentiles = percentiles
            };
        }

        /// <summary>
        /// Returns the cached PUUID when one is stored, otherwise resolves it from the Riot ID and
        /// caches it. The settings view clears the cache whenever the Riot ID or region changes.
        /// </summary>
        private async Task<string> ResolvePuuidAsync(string platform, string apiKey, CancellationToken cancel)
        {
            if (!string.IsNullOrWhiteSpace(_settings.Puuid))
            {
                return _settings.Puuid.Trim();
            }

            var account = await _client
                .GetAccountByRiotIdAsync(platform, _settings.RiotGameName, _settings.RiotTagLine, apiKey, cancel)
                .ConfigureAwait(false);

            _settings.Puuid = account.Puuid;
            _persistSettings?.Invoke();
            _logger?.Info($"[Riot] Resolved PUUID for '{_settings.RiotId}' on {platform}.");

            return account.Puuid;
        }

        /// <summary>
        /// Percentiles are decoration, not data: a failure here costs rarity on locked challenges
        /// but must not fail the refresh.
        /// </summary>
        private async Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, double>>> LoadPercentilesAsync(
            string platform,
            string apiKey,
            CancellationToken cancel)
        {
            try
            {
                var json = await _client.GetPercentilesJsonAsync(platform, apiKey, cancel).ConfigureAwait(false);
                return RiotChallengeMapper.ParsePercentiles(json);
            }
            catch (System.OperationCanceledException)
            {
                throw;
            }
            catch (RiotAuthorizationException)
            {
                throw;
            }
            catch (System.Exception ex)
            {
                _logger?.Warn(ex, "[Riot] Could not load challenge percentiles; locked challenges will have no rarity.");
                return new Dictionary<long, IReadOnlyDictionary<string, double>>();
            }
        }
    }
}
