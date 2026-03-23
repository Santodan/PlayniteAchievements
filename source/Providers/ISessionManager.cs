using PlayniteAchievements.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers
{
    /// <summary>
    /// Common interface for session managers that handle authentication for game providers.
    /// Auth state should never be kept in memory - it should be probed from the source of truth
    /// (CEF cookies, encrypted disk files, PersistedSettings) before any data provider work.
    /// </summary>
    public interface ISessionManager
    {
        /// <summary>
        /// Unique key identifying this provider (e.g., "Steam", "GOG", "Epic").
        /// </summary>
        string ProviderKey { get; }

        /// <summary>
        /// Duration for which probe results are cached before requiring a fresh probe.
        /// This prevents excessive probing while still detecting auth state changes.
        /// </summary>
        TimeSpan ProbeCacheDuration { get; }

        /// <summary>
        /// Ensures authentication is valid before data provider work.
        /// Uses cached probe results if within ProbeCacheDuration, otherwise probes fresh.
        /// Returns success if already authenticated or if authentication was completed.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The authentication probe result.</returns>
        Task<AuthProbeResult> EnsureAuthAsync(CancellationToken ct);

        /// <summary>
        /// Probes the current authentication state from the source of truth.
        /// This always performs a fresh probe, bypassing any cache.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The authentication probe result.</returns>
        Task<AuthProbeResult> ProbeAuthStateAsync(CancellationToken ct);

        /// <summary>
        /// Performs interactive authentication via WebView or browser.
        /// If forceInteractive is false, first checks if already authenticated.
        /// </summary>
        /// <param name="forceInteractive">If true, clears existing session and forces login.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <param name="progress">Optional progress reporter for auth steps.</param>
        /// <returns>The authentication result.</returns>
        Task<AuthProbeResult> AuthenticateInteractiveAsync(
            bool forceInteractive,
            CancellationToken ct,
            IProgress<AuthProgressStep> progress = null);

        /// <summary>
        /// Clears the current session, removing all stored authentication data.
        /// </summary>
        void ClearSession();

        /// <summary>
        /// Invalidates the probe cache, forcing the next EnsureAuthAsync to probe fresh.
        /// Use this when auth is detected as expired during data operations.
        /// </summary>
        void InvalidateProbeCache();
    }
}
