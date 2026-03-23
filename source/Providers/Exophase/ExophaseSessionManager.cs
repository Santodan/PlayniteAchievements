using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Models;
using Playnite.SDK;
using Playnite.SDK.Events;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Exophase
{
    /// <summary>
    /// Cookie-based authentication client for Exophase.
    /// Uses Playnite's IWebView API for browser-based authentication.
    /// Exophase does not use OAuth tokens; session is maintained via cookies.
    /// Auth state is never cached in memory - always probed from the source of truth.
    /// </summary>
    public sealed class ExophaseSessionManager : ISessionManager, IExophaseTokenProvider
    {
        private const string UrlLogin = "https://www.exophase.com/login";
        private const string UrlAccount = "https://www.exophase.com/account";
        private static readonly TimeSpan InteractiveAuthTimeout = TimeSpan.FromMinutes(3);

        private (bool Success, string Username) _authResult;
        private int _authCheckInProgress;
        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly AuthProbeCache _probeCache;

        public string ProviderKey => "Exophase";

        public TimeSpan ProbeCacheDuration => AuthProbeCache.ProviderCacheDurations.Exophase;

        public ExophaseSessionManager(
            IPlayniteAPI api,
            ILogger logger,
            PlayniteAchievementsSettings settings,
            AuthProbeCache probeCache)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger;
            _settings = settings;
            _probeCache = probeCache ?? throw new ArgumentNullException(nameof(probeCache));
        }

        /// <summary>
        /// Backward-compatible constructor that creates a default AuthProbeCache.
        /// </summary>
        public ExophaseSessionManager(
            IPlayniteAPI api,
            ILogger logger,
            PlayniteAchievementsSettings settings)
            : this(api, logger, settings, new AuthProbeCache(logger))
        {
        }

        // ---------------------------------------------------------------------
        // IExophaseTokenProvider (backward compatibility)
        // ---------------------------------------------------------------------

        /// <summary>
        /// Checks if currently authenticated based on cached probe result.
        /// This probes from source of truth if cache is invalid.
        /// </summary>
        public bool IsAuthenticated
        {
            get
            {
                if (_probeCache.IsCacheValid(ProviderKey, ProbeCacheDuration))
                {
                    return _probeCache.TryGetCachedUserId(ProviderKey, ProbeCacheDuration, out _);
                }
                // Probe synchronously (this is a property getter, so we do a quick check)
                return HasExophaseSessionCookies(_api, _logger);
            }
        }

        /// <summary>
        /// Gets the current username if authenticated.
        /// Probes from source of truth if not cached.
        /// </summary>
        public string Username
        {
            get
            {
                if (_probeCache.TryGetCachedUserId(ProviderKey, ProbeCacheDuration, out var userId))
                {
                    return userId;
                }
                return _settings?.Persisted?.ExophaseUserId;
            }
        }

        // ---------------------------------------------------------------------
        // ISessionManager Implementation
        // ---------------------------------------------------------------------

        /// <summary>
        /// Ensures authentication is valid before data provider work.
        /// Uses cached probe results if within ProbeCacheDuration, otherwise probes fresh.
        /// </summary>
        public async Task<AuthProbeResult> EnsureAuthAsync(CancellationToken ct)
        {
            // Check if we have a valid cached result
            if (_probeCache.IsCacheValid(ProviderKey, ProbeCacheDuration))
            {
                if (_probeCache.TryGetCachedUserId(ProviderKey, ProbeCacheDuration, out var cachedUserId))
                {
                    return AuthProbeResult.AlreadyAuthenticated(cachedUserId);
                }
            }

            // Cache expired or invalid - perform fresh probe
            return await ProbeAuthStateAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Probes the current authentication state from CEF cookies.
        /// This always performs a fresh probe, bypassing any cache.
        /// </summary>
        public async Task<AuthProbeResult> ProbeAuthStateAsync(CancellationToken ct)
        {
            using (PerfScope.Start(_logger, "Exophase.ProbeAuthStateAsync", thresholdMs: 50))
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    // Fast path: check persisted username AND cookies exist
                    var persistedUsername = _settings?.Persisted?.ExophaseUserId;
                    var hasCookies = HasExophaseSessionCookies(_api, _logger);

                    if (!string.IsNullOrWhiteSpace(persistedUsername) && hasCookies)
                    {
                        _probeCache.RecordProbe(ProviderKey, true, persistedUsername);
                        _logger?.Debug("[ExophaseAuth] Restored from persisted settings + cookie check.");
                        return AuthProbeResult.AlreadyAuthenticated(persistedUsername);
                    }

                    // Do full verification by navigating to account page
                    var extractedUsername = await QuickAuthCheckAsync(ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(extractedUsername))
                    {
                        _probeCache.RecordProbe(ProviderKey, true, extractedUsername);
                        if (_settings?.Persisted != null)
                        {
                            _settings.Persisted.ExophaseUserId = extractedUsername;
                        }
                        return AuthProbeResult.AlreadyAuthenticated(extractedUsername);
                    }

                    // Verification failed, clear any stale persisted state
                    if (_settings?.Persisted != null)
                    {
                        _settings.Persisted.ExophaseUserId = null;
                    }
                    _probeCache.RecordProbe(ProviderKey, false);

                    return AuthProbeResult.NotAuthenticated();
                }
                catch (OperationCanceledException)
                {
                    return AuthProbeResult.Cancelled();
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "[ExophaseAuth] Probe failed with exception.");
                    _probeCache.RecordProbe(ProviderKey, false);
                    return AuthProbeResult.ProbeFailed();
                }
            }
        }

        /// <summary>
        /// Performs interactive authentication via WebView.
        /// </summary>
        public async Task<AuthProbeResult> AuthenticateInteractiveAsync(
            bool forceInteractive,
            CancellationToken ct,
            IProgress<AuthProgressStep> progress = null)
        {
            var windowOpened = false;

            try
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(AuthProgressStep.CheckingExistingSession);

                _logger?.Info("[ExophaseAuth] Starting interactive authentication.");

                if (!forceInteractive)
                {
                    var existingResult = await ProbeAuthStateAsync(ct).ConfigureAwait(false);
                    if (existingResult.IsSuccess)
                    {
                        _logger?.Info("[ExophaseAuth] Already authenticated.");
                        progress?.Report(AuthProgressStep.Completed);
                        return existingResult;
                    }
                }
                else
                {
                    ClearSession();
                }

                _logger?.Info("[ExophaseAuth] Opening login dialog.");
                progress?.Report(AuthProgressStep.OpeningLoginWindow);

                var loginTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

                _ = _api.MainView.UIDispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var result = LoginInteractively();
                        loginTcs.TrySetResult(result ?? "");
                    }
                    catch (Exception ex)
                    {
                        loginTcs.TrySetException(ex);
                    }
                }));
                windowOpened = true;

                progress?.Report(AuthProgressStep.WaitingForUserLogin);
                var completed = await Task.WhenAny(
                    loginTcs.Task,
                    Task.Delay(InteractiveAuthTimeout, ct)).ConfigureAwait(false);

                if (completed != loginTcs.Task)
                {
                    _logger?.Warn("[ExophaseAuth] Interactive login timed out.");
                    progress?.Report(AuthProgressStep.Failed);
                    return AuthProbeResult.TimedOut(windowOpened);
                }

                var extractedUsername = await loginTcs.Task.ConfigureAwait(false);

                progress?.Report(AuthProgressStep.VerifyingSession);
                if (string.IsNullOrWhiteSpace(extractedUsername))
                {
                    // Fallback: dialog may have been manually closed after successful login
                    extractedUsername = await QuickAuthCheckAsync(ct).ConfigureAwait(false);
                }

                if (string.IsNullOrWhiteSpace(extractedUsername))
                {
                    _logger?.Warn("[ExophaseAuth] Interactive login failed or was cancelled.");
                    progress?.Report(AuthProgressStep.Failed);
                    return AuthProbeResult.Cancelled(windowOpened);
                }

                _probeCache.RecordProbe(ProviderKey, true, extractedUsername);
                if (_settings?.Persisted != null)
                {
                    _settings.Persisted.ExophaseUserId = extractedUsername;
                }

                _logger?.Info("[ExophaseAuth] Interactive login succeeded.");
                progress?.Report(AuthProgressStep.Completed);
                return AuthProbeResult.Authenticated(extractedUsername, windowOpened: windowOpened);
            }
            catch (OperationCanceledException)
            {
                _logger?.Info("[ExophaseAuth] Authentication was cancelled or timed out.");
                progress?.Report(AuthProgressStep.Failed);
                return AuthProbeResult.TimedOut(windowOpened);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[ExophaseAuth] Authentication failed with exception.");
                progress?.Report(AuthProgressStep.Failed);
                return AuthProbeResult.Failed(windowOpened);
            }
        }

        /// <summary>
        /// Clears the session by clearing cookies and invalidating cache.
        /// </summary>
        public void ClearSession()
        {
            _logger?.Info("[ExophaseAuth] Clearing session.");
            _authResult = (false, null);

            // Invalidate cache
            _probeCache.Invalidate(ProviderKey);

            // Clear persisted user ID
            if (_settings?.Persisted != null)
            {
                _settings.Persisted.ExophaseUserId = null;
            }

            // Clear cookies from CEF
            try
            {
                _api.MainView.UIDispatcher.Invoke(() =>
                {
                    using (var view = _api.WebViews.CreateOffscreenView())
                    {
                        view.DeleteDomainCookies(".exophase.com");
                        view.DeleteDomainCookies("exophase.com");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[ExophaseAuth] Failed to clear Exophase cookies from CEF.");
            }
        }

        public void InvalidateProbeCache()
        {
            _probeCache.Invalidate(ProviderKey);
        }

        // ---------------------------------------------------------------------
        // Private Helper Methods
        // ---------------------------------------------------------------------

        /// <summary>
        /// Checks if any exophase.com session cookies exist.
        /// </summary>
        public static bool HasExophaseSessionCookies(IPlayniteAPI api, ILogger logger)
        {
            try
            {
                using (var view = api.WebViews.CreateOffscreenView())
                {
                    var cookies = view.GetCookies();
                    if (cookies == null)
                        return false;

                    return cookies.Any(c =>
                        c != null &&
                        !string.IsNullOrWhiteSpace(c.Domain) &&
                        c.Domain.IndexOf("exophase.com", StringComparison.OrdinalIgnoreCase) >= 0);
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "[ExophaseAuth] Failed to check session cookies.");
                return false;
            }
        }

        /// <summary>
        /// Quick check using offscreen view to verify authentication.
        /// </summary>
        private async Task<string> QuickAuthCheckAsync(CancellationToken ct)
        {
            using (PerfScope.Start(_logger, "Exophase.QuickAuthCheckAsync", thresholdMs: 50))
            {
                ct.ThrowIfCancellationRequested();

                var dispatchOperation = _api.MainView.UIDispatcher.InvokeAsync(async () =>
                {
                    using (var view = _api.WebViews.CreateOffscreenView())
                    {
                        try
                        {
                            // First check if we have Exophase session cookies
                            var cookies = view.GetCookies();
                            var hasExophaseCookies = cookies?.Any(c =>
                                c != null &&
                                !string.IsNullOrWhiteSpace(c.Domain) &&
                                c.Domain.IndexOf("exophase.com", StringComparison.OrdinalIgnoreCase) >= 0) == true;

                            _logger?.Debug($"[ExophaseAuth] Has exophase.com cookies: {hasExophaseCookies}");

                            // Navigate to account page to verify session
                            await view.NavigateAndWaitAsync(UrlAccount, timeoutMs: 10000);
                            var currentUrl = view.GetCurrentAddress();

                            _logger?.Debug($"[ExophaseAuth] After navigation, current URL: {currentUrl}");

                            // If redirected to login page, not authenticated
                            if (IsLoginPageUrl(currentUrl))
                            {
                                _logger?.Debug("[ExophaseAuth] Account page redirected to login, not authenticated.");
                                return null;
                            }

                            // We're on the account page, extract username
                            var html = await view.GetPageSourceAsync();
                            var username = ExtractUsernameFromHtml(html);
                            if (!string.IsNullOrWhiteSpace(username))
                            {
                                _logger?.Debug($"[ExophaseAuth] Extracted username: {username}");
                                return username;
                            }

                            _logger?.Debug("[ExophaseAuth] Could not extract username from account page HTML.");
                        }
                        catch (Exception ex)
                        {
                            _logger?.Debug(ex, "[ExophaseAuth] Failed to check account page.");
                        }
                    }
                    return null;
                });

                var responseTask = await dispatchOperation.Task.ConfigureAwait(false);
                return await responseTask.ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Extracts username from the account page HTML.
        /// </summary>
        private string ExtractUsernameFromHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return null;
            }

            // Pattern: window.me = { username: 'jdd056', ... }
            var meIndex = html.IndexOf("window.me = {", StringComparison.OrdinalIgnoreCase);
            if (meIndex >= 0)
            {
                var usernameKeyIndex = html.IndexOf("username:", meIndex, StringComparison.OrdinalIgnoreCase);
                if (usernameKeyIndex >= 0 && usernameKeyIndex < meIndex + 500)
                {
                    var startQuote = html.IndexOf('\'', usernameKeyIndex);
                    if (startQuote < 0)
                        startQuote = html.IndexOf('"', usernameKeyIndex);

                    if (startQuote >= 0 && startQuote < usernameKeyIndex + 50)
                    {
                        var quoteChar = html[startQuote];
                        var endQuote = html.IndexOf(quoteChar, startQuote + 1);
                        if (endQuote > startQuote)
                        {
                            return html.Substring(startQuote + 1, endQuote - startQuote - 1);
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Synchronous login method matching GOG session manager pattern.
        /// </summary>
        private string LoginInteractively()
        {
            _authResult = (false, null);
            IWebView view = null;

            try
            {
                view = _api.WebViews.CreateView(580, 700);
                view.DeleteDomainCookies(".exophase.com");
                view.DeleteDomainCookies("exophase.com");

                view.LoadingChanged += CloseWhenLoggedIn;
                view.Navigate(UrlLogin);

                view.OpenDialog();

                return _authResult.Success ? _authResult.Username : null;
            }
            finally
            {
                if (view != null)
                {
                    view.LoadingChanged -= CloseWhenLoggedIn;
                    view.Dispose();
                }
            }
        }

        private async void CloseWhenLoggedIn(object sender, WebViewLoadingChangedEventArgs e)
        {
            try
            {
                if (e.IsLoading)
                    return;

                var view = (IWebView)sender;
                var address = view.GetCurrentAddress();

                if (IsLoginPageUrl(address))
                {
                    _logger?.Debug("[ExophaseAuth] Still on login page, waiting...");
                    return;
                }

                if (Interlocked.CompareExchange(ref _authCheckInProgress, 1, 0) != 0)
                    return;

                _logger?.Debug($"[ExophaseAuth] Navigation to: {address}");

                var extractedUsername = await WaitForAuthenticatedUserAsync(CancellationToken.None).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(extractedUsername))
                {
                    _authResult = (true, extractedUsername);
                    _logger?.Info($"[ExophaseAuth] Authenticated as user: {extractedUsername}");
                    _ = _api.MainView.UIDispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            view.Close();
                        }
                        catch (Exception closeEx)
                        {
                            _logger?.Debug(closeEx, "[ExophaseAuth] Failed to close login dialog.");
                        }
                    }));
                }
                else
                {
                    _logger?.Debug("[ExophaseAuth] Session not authenticated yet after navigation.");
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[ExophaseAuth] Failed to check authentication status");
            }
            finally
            {
                Interlocked.Exchange(ref _authCheckInProgress, 0);
            }
        }

        private async Task<string> WaitForAuthenticatedUserAsync(CancellationToken ct)
        {
            const int attempts = 8;
            const int delayMs = 500;

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var extractedUsername = await QuickAuthCheckAsync(ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(extractedUsername))
                    {
                        return extractedUsername;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "[ExophaseAuth] Waiting for authenticated user failed, retrying.");
                }

                if (attempt < attempts)
                {
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
            }

            return null;
        }

        private static bool IsLoginPageUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            return url.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ---------------------------------------------------------------------
        // Legacy Type Conversion
        // ---------------------------------------------------------------------

        private ExophaseAuthResult ConvertToLegacyResult(AuthProbeResult result)
        {
            var outcome = result.Outcome switch
            {
                AuthOutcome.AlreadyAuthenticated => ExophaseAuthOutcome.AlreadyAuthenticated,
                AuthOutcome.Authenticated => ExophaseAuthOutcome.Authenticated,
                AuthOutcome.NotAuthenticated => ExophaseAuthOutcome.NotAuthenticated,
                AuthOutcome.Cancelled => ExophaseAuthOutcome.Cancelled,
                AuthOutcome.TimedOut => ExophaseAuthOutcome.TimedOut,
                AuthOutcome.Failed => ExophaseAuthOutcome.Failed,
                AuthOutcome.ProbeFailed => ExophaseAuthOutcome.ProbeFailed,
                _ => ExophaseAuthOutcome.Failed
            };

            return ExophaseAuthResult.Create(
                outcome,
                result.MessageKey,
                result.UserId,
                result.WindowOpened);
        }
    }
}
