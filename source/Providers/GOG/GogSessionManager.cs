using PlayniteAchievements.Providers.GOG.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Providers.Settings;
using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Events;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.GOG
{
    /// <summary>
    /// WebView-based authentication client for GOG.
    /// Uses Playnite's IWebView API for browser-based authentication.
    /// Based on SteamSessionManager pattern and playnite-plugincommon GogApi.cs.
    /// </summary>
    public sealed class GogSessionManager : IGogTokenProvider
    {
        private const string UrlLogin = "https://www.gog.com/account/";
        private const string UrlAccountInfo = "https://menu.gog.com/v1/account/basic";
        private static readonly TimeSpan InteractiveAuthTimeout = TimeSpan.FromMinutes(3);

        private string _accessToken;
        private string _userId;
        private DateTime _tokenExpiryUtc = DateTime.MinValue;
        private bool _isSessionAuthenticated;
        private (bool Success, string UserId) _authResult;
        private int _authCheckInProgress;
        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;

        public GogSessionManager(IPlayniteAPI api, ILogger logger, PlayniteAchievementsSettings settings)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            var gogSettings = GetProviderSettings();
            if (!string.IsNullOrWhiteSpace(gogSettings.UserId))
            {
                _userId = gogSettings.UserId.Trim();
            }
        }

        private GogSettings GetProviderSettings()
        {
            return ProviderSettings.Load<GogSettings>();
        }

        private void SaveProviderSettings(GogSettings providerSettings)
        {
            providerSettings.Save();
        }

        /// <summary>
        /// Gets the current access token, throwing if expired or missing.
        /// </summary>
        public string GetAccessToken()
        {
            if (string.IsNullOrEmpty(_accessToken) || DateTime.UtcNow >= _tokenExpiryUtc)
            {
                throw new AuthRequiredException("GOG authentication required. Please login.");
            }
            return _accessToken;
        }

        /// <summary>
        /// Gets the current user ID.
        /// </summary>
        public string GetUserId() => _userId;

        /// <summary>
        /// Checks if currently authenticated based on verified web session (cookie-backed),
        /// independent of in-memory token availability.
        /// </summary>
        public bool IsAuthenticated => _isSessionAuthenticated;

        /// <summary>
        /// Runs a best-effort background probe to hydrate authentication state from current web session cookies.
        /// Safe to call at startup; failures are logged and not propagated.
        /// </summary>
        public async Task PrimeAuthenticationStateAsync(CancellationToken ct)
        {
            using (PerfScope.Start(_logger, "GOG.PrimeAuthenticationStateAsync", thresholdMs: 50))
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var result = await ProbeAuthenticationAsync(ct).ConfigureAwait(false);
                    _logger?.Debug($"[GogAuth] Startup auth probe completed with outcome={result?.Outcome}.");
                }
                catch (OperationCanceledException)
                {
                    _logger?.Debug("[GogAuth] Startup auth probe cancelled.");
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "[GogAuth] Startup auth probe failed.");
                }
            }
        }

        public async Task<GogAuthResult> ProbeAuthenticationAsync(CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                if (HasValidToken())
                {
                    return GogAuthResult.Create(
                        GogAuthOutcome.AlreadyAuthenticated,
                        "LOCPlayAch_Settings_GogAuth_AlreadyAuthenticated",
                        _userId,
                        _tokenExpiryUtc,
                        windowOpened: false);
                }

                var extractedId = await QuickAuthCheckAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(extractedId))
                {
                    return GogAuthResult.Create(
                        GogAuthOutcome.AlreadyAuthenticated,
                        "LOCPlayAch_Settings_GogAuth_AlreadyAuthenticated",
                        extractedId,
                        _tokenExpiryUtc,
                        windowOpened: false);
                }

                    _isSessionAuthenticated = false;

                return GogAuthResult.Create(
                    GogAuthOutcome.NotAuthenticated,
                    "LOCPlayAch_Settings_GogAuth_NotAuthenticated",
                    windowOpened: false);
            }
            catch (OperationCanceledException)
            {
                return GogAuthResult.Create(
                    GogAuthOutcome.Cancelled,
                    "LOCPlayAch_Settings_GogAuth_Cancelled",
                    windowOpened: false);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[GogAuth] Probe failed with exception.");
                return GogAuthResult.Create(
                    GogAuthOutcome.ProbeFailed,
                    "LOCPlayAch_Settings_GogAuth_ProbeFailed",
                    windowOpened: false);
            }
        }

        /// <summary>
        /// Main authentication entry point.
        /// </summary>
        public async Task<GogAuthResult> AuthenticateInteractiveAsync(
            bool forceInteractive,
            CancellationToken ct,
            IProgress<GogAuthProgressStep> progress = null)
        {
            var windowOpened = false;

            try
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(GogAuthProgressStep.CheckingExistingSession);

                _logger?.Info("[GogAuth] Starting interactive authentication.");

                if (!forceInteractive)
                {
                    try
                    {
                        var existingUserId = await QuickAuthCheckAsync(ct).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(existingUserId))
                        {
                            _logger?.Info("[GogAuth] Quick auth check succeeded - already authenticated.");
                            progress?.Report(GogAuthProgressStep.Completed);
                            return GogAuthResult.Create(
                                GogAuthOutcome.AlreadyAuthenticated,
                                "LOCPlayAch_Settings_GogAuth_AlreadyAuthenticated",
                                existingUserId,
                                _tokenExpiryUtc,
                                windowOpened: false);
                        }
                    }
                    catch (Exception quickCheckEx)
                    {
                        _logger?.Debug(quickCheckEx, "[GogAuth] Quick check failed before interactive login, proceeding.");
                    }
                }
                else
                {
                    ClearSession();
                }

                _logger?.Info("[GogAuth] Opening login dialog.");
                progress?.Report(GogAuthProgressStep.OpeningLoginWindow);

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

                progress?.Report(GogAuthProgressStep.WaitingForUserLogin);
                var completed = await Task.WhenAny(
                    loginTcs.Task,
                    Task.Delay(InteractiveAuthTimeout, ct)).ConfigureAwait(false);

                if (completed != loginTcs.Task)
                {
                    _logger?.Warn("[GogAuth] Interactive login timed out while waiting for dialog completion.");
                    progress?.Report(GogAuthProgressStep.Failed);
                    return GogAuthResult.Create(
                        GogAuthOutcome.TimedOut,
                        "LOCPlayAch_Settings_GogAuth_TimedOut",
                        windowOpened: windowOpened);
                }

                var extractedId = await loginTcs.Task.ConfigureAwait(false);

                progress?.Report(GogAuthProgressStep.VerifyingSession);
                if (string.IsNullOrWhiteSpace(extractedId))
                {
                    // Fallback: dialog may have been manually closed after successful login.
                    extractedId = await QuickAuthCheckAsync(ct).ConfigureAwait(false);
                }

                if (string.IsNullOrWhiteSpace(extractedId))
                {
                    _logger?.Warn("[GogAuth] Interactive login failed or was cancelled.");
                    progress?.Report(GogAuthProgressStep.Failed);
                    return GogAuthResult.Create(
                        GogAuthOutcome.Cancelled,
                        "LOCPlayAch_Settings_GogAuth_Cancelled",
                        windowOpened: windowOpened);
                }

                _userId = extractedId;
                _logger?.Info("[GogAuth] Interactive login succeeded.");
                progress?.Report(GogAuthProgressStep.Completed);
                return GogAuthResult.Create(
                    GogAuthOutcome.Authenticated,
                    "LOCPlayAch_Settings_GogAuth_Verified",
                    extractedId,
                    _tokenExpiryUtc,
                    windowOpened: windowOpened);
            }
            catch (OperationCanceledException)
            {
                _logger?.Info("[GogAuth] Authentication was cancelled or timed out.");
                progress?.Report(GogAuthProgressStep.Failed);
                return GogAuthResult.Create(
                    GogAuthOutcome.TimedOut,
                    "LOCPlayAch_Settings_GogAuth_TimedOut",
                    windowOpened: windowOpened);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[GogAuth] Authentication failed with exception.");
                progress?.Report(GogAuthProgressStep.Failed);
                return GogAuthResult.Create(
                    GogAuthOutcome.Failed,
                    "LOCPlayAch_Settings_GogAuth_Failed",
                    windowOpened: windowOpened);
            }
        }

        /// <summary>
        /// Quick check using offscreen view to see if already authenticated.
        /// </summary>
        private async Task<string> QuickAuthCheckAsync(CancellationToken ct)
        {
            using (PerfScope.Start(_logger, "GOG.QuickAuthCheckAsync", thresholdMs: 50))
            {
                // Check existing token validity
                if (HasValidToken())
                {
                    _logger?.Debug("[GogAuth] Existing token is still valid.");
                    return _userId;
                }

                ct.ThrowIfCancellationRequested();

                var responseTask = CallAccountInfoApiAsync(timeoutMs: 6000);
                var completed = await Task.WhenAny(
                    responseTask,
                    Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
                if (completed != responseTask)
                {
                    throw new OperationCanceledException(ct);
                }

                var response = await responseTask.ConfigureAwait(false);
                if (TryApplyAuthResponse(response, requireToken: true))
                {
                    _logger?.Info("[GogAuth] Quick check validated existing session.");
                    return _userId;
                }

                _logger?.Debug("[GogAuth] Quick check - not logged in.");
                return null;
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
                    var extractedId = await QuickAuthCheckAsync(ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(extractedId))
                    {
                        return extractedId;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "[GogAuth] Waiting for authenticated user failed, retrying.");
                }

                if (attempt < attempts)
                {
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
            }

            return null;
        }

        /// <summary>
        /// Calls the account info API using an offscreen WebView session.
        /// </summary>
        private async Task<GogAccountInfoResponse> CallAccountInfoApiAsync(int timeoutMs = 15000)
        {
            using (PerfScope.Start(_logger, "GOG.CallAccountInfoApiAsync", thresholdMs: 50, context: $"timeoutMs={timeoutMs}"))
            {
                var dispatchOperation = _api.MainView.UIDispatcher.InvokeAsync(async () =>
                {
                    using (var view = _api.WebViews.CreateOffscreenView())
                    {
                        await view.NavigateAndWaitAsync(UrlAccountInfo, timeoutMs: timeoutMs);
                        var responseText = await view.GetPageTextAsync();
                        if (TryParseAccountInfo(responseText, out var response))
                        {
                            return response;
                        }
                    }
                    return null;
                });

                var responseTask = await dispatchOperation.Task.ConfigureAwait(false);
                return await responseTask.ConfigureAwait(false);
            }
        }

        private bool TryApplyAuthResponse(GogAccountInfoResponse response, bool requireToken)
        {
            if (response == null || !response.IsLoggedIn)
            {
                return false;
            }

            _userId = response.UserId;
            _accessToken = response.ResolvedAccessToken;
            _isSessionAuthenticated = !string.IsNullOrWhiteSpace(_userId);

            if (_isSessionAuthenticated)
            {
                var gogSettings = GetProviderSettings();
                gogSettings.UserId = _userId;
                SaveProviderSettings(gogSettings);
            }

            if (string.IsNullOrWhiteSpace(_userId))
            {
                _logger?.Debug("[GogAuth] Account API returned logged-in state but no userId.");
                _isSessionAuthenticated = false;
                return false;
            }

            if (string.IsNullOrWhiteSpace(_accessToken))
            {
                _tokenExpiryUtc = DateTime.MinValue;
                if (requireToken)
                {
                    _logger?.Debug("[GogAuth] Account API returned logged-in state but no accessToken.");
                    return false;
                }

                return true;
            }

            // GOG returns accessTokenExpires as an absolute Unix timestamp (seconds since epoch).
            if (response.ResolvedAccessTokenExpires > 0)
            {
                _tokenExpiryUtc = DateTimeOffset.FromUnixTimeSeconds(response.ResolvedAccessTokenExpires).UtcDateTime;
            }
            else
            {
                // Fallback: assume 5-minute validity if expires value is missing or invalid.
                _tokenExpiryUtc = DateTime.UtcNow.AddSeconds(300);
            }

            return true;
        }

        /// <summary>
        /// Parses the account info response from JSON text.
        /// </summary>
        private bool TryParseAccountInfo(string json, out GogAccountInfoResponse response)
        {
            response = null;
            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                response = Serialization.FromJson<GogAccountInfoResponse>(json);
                return response != null && response.IsLoggedIn;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[GogAuth] Failed to parse account info response.");
                return false;
            }
        }

        /// <summary>
        /// Checks if URL is a login page.
        /// </summary>
        private static bool IsLoginPageUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            return url.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0
                   || url.IndexOf("openlogin", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Clears the session by resetting stored tokens.
        /// </summary>
        public void ClearSession()
        {
            _logger?.Info("[GogAuth] Clearing session.");
            _accessToken = null;
            _userId = null;
            _tokenExpiryUtc = DateTime.MinValue;
            _isSessionAuthenticated = false;
            _authResult = (false, null);

            var gogSettings = GetProviderSettings();
            gogSettings.UserId = null;
            SaveProviderSettings(gogSettings);

            // Also clear cookies from CEF
            try
            {
                _api.MainView.UIDispatcher.Invoke(() =>
                {
                    using (var view = _api.WebViews.CreateOffscreenView())
                    {
                        view.DeleteDomainCookies(".gog.com");
                        view.DeleteDomainCookies("gog.com");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[GogAuth] Failed to clear GOG cookies from CEF.");
            }
        }

        /// <summary>
        /// Synchronous login method matching Steam library plugin pattern.
        /// Blocks until CloseWhenLoggedIn closes the view.
        /// </summary>
        private string LoginInteractively()
        {
            _authResult = (false, null);
            IWebView view = null;

            try
            {
                view = _api.WebViews.CreateView(580, 700);
                view.DeleteDomainCookies(".gog.com");
                view.DeleteDomainCookies("gog.com");

                view.LoadingChanged += CloseWhenLoggedIn;
                view.Navigate(UrlLogin);

                // This blocks until CloseWhenLoggedIn calls view.Close()
                view.OpenDialog();

                return _authResult.Success ? _authResult.UserId : null;
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

        /// <summary>
        /// Event handler that auto-closes the WebView when auth is detected.
        /// </summary>
        private async void CloseWhenLoggedIn(object sender, WebViewLoadingChangedEventArgs e)
        {
            try
            {
                if (e.IsLoading)
                    return;

                var view = (IWebView)sender;
                var address = view.GetCurrentAddress();

                // Skip if still on login page
                if (IsLoginPageUrl(address))
                {
                    _logger?.Debug("[GogAuth] Still on login page, waiting...");
                    return;
                }

                if (Interlocked.CompareExchange(ref _authCheckInProgress, 1, 0) != 0)
                    return;

                _logger?.Debug($"[GogAuth] Navigation to: {address}");

                // GOG does not reliably redirect after login and can commit cookies with a short delay.
                // Poll auth for a short window so successful login auto-closes reliably.
                var extractedId = await WaitForAuthenticatedUserAsync(CancellationToken.None).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(extractedId))
                {
                    _authResult = (true, extractedId);
                    _logger?.Info($"[GogAuth] Authenticated as user: {extractedId}");
                    _ = _api.MainView.UIDispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            view.Close();
                        }
                        catch (Exception closeEx)
                        {
                            _logger?.Debug(closeEx, "[GogAuth] Failed to close login dialog.");
                        }
                    }));
                }
                else
                {
                    _logger?.Debug("[GogAuth] Session not authenticated yet after navigation.");
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[GogAuth] Failed to check authentication status");
            }
            finally
            {
                Interlocked.Exchange(ref _authCheckInProgress, 0);
            }
        }

        private bool HasValidToken()
        {
            return !string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiryUtc;
        }
    }

}
