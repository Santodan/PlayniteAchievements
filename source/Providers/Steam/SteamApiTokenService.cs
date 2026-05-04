using System;
using System.Threading;
using System.Threading.Tasks;
using PlayniteAchievements.Providers.Settings;
using Playnite.SDK;

namespace PlayniteAchievements.Providers.Steam
{
    /// <summary>
    /// Resolves the best available Steam API token for schema fetching.
    /// Priority: manual developer key (if set) → active session web API token → null.
    /// </summary>
    public sealed class SteamApiTokenService
    {
        private readonly ILogger _logger;

        /// <summary>
        /// Delegate set by <see cref="SteamDataProvider"/> once the Steam HTTP client is ready.
        /// Resolves the session web API token from the authenticated Steam session.
        /// </summary>
        public Func<CancellationToken, Task<string>> GetSessionTokenAsync { get; set; }

        public SteamApiTokenService(ILogger logger = null)
        {
            _logger = logger;
        }

        /// <summary>
        /// Returns the best available API token, or <c>null</c> if none is configured.
        /// </summary>
        public async Task<string> ResolveAsync(CancellationToken ct)
        {
            // Manual developer key takes explicit priority — user may have a key that
            // works for ISteamUserStats/GetSchemaForGame which a session token may not.
            var manualKey = ProviderRegistry.Settings<SteamSettings>().SteamWebApiKey?.Trim();
            if (!string.IsNullOrWhiteSpace(manualKey))
            {
                _logger?.Debug("[SteamApiTokenService] Using manually configured Steam Web API key.");
                return manualKey;
            }

            // Fall back to session web API token when Steam is authenticated in Playnite.
            var steamUserId = ProviderRegistry.Settings<SteamSettings>().SteamUserId?.Trim();
            if (!string.IsNullOrWhiteSpace(steamUserId) && GetSessionTokenAsync != null)
            {
                try
                {
                    var sessionToken = await GetSessionTokenAsync(ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(sessionToken))
                    {
                        _logger?.Debug("[SteamApiTokenService] Using Steam session web API token.");
                        return sessionToken;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "[SteamApiTokenService] Failed to resolve session token; falling back to anonymous schema fetch.");
                }
            }

            return null;
        }
    }
}
