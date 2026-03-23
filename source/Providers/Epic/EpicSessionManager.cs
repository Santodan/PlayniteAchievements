using Newtonsoft.Json;
using PlayniteAchievements.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Services;
using Playnite.SDK;
using Playnite.SDK.Events;
using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace PlayniteAchievements.Providers.Epic
{
    /// <summary>
    /// Epic Games session manager that probes authentication state from EpicSettings.
    /// Auth state is never cached in memory - always probed from the source of truth.
    /// </summary>
    public sealed class EpicSessionManager : ISessionManager, IEpicSessionProvider
    {
        private const string UrlLogin = "https://www.epicgames.com/id/login?responseType=code";
        private const string UrlAccount = "https://www.epicgames.com/account/personal";
        private const string UrlAuthCode = "https://www.epicgames.com/id/api/redirect?clientId=34a02cf8f4414e29b15921876da36f9a&responseType=code";
        private const string UrlAccountAuth = "https://account-public-service-prod03.ol.epicgames.com/account/api/oauth/token";
        private const string AuthEncodedString = "MzRhMDJjZjhmNDQxNGUyOWIxNTkyMTg3NmRhMzZmOWE6ZGFhZmJjY2M3Mzc3NDUwMzlkZmZlNTNkOTRmYzc2Y2Y=";
        private const string LoginCodePattern = "localhost\\/launcher\\/authorized\\?code=([^&\\s\\\"'<>]+)";
        private const string QueryCodePattern = "(?:\\?|&|\\b)code=([^&\\s\\\"'<>]+)";
        private const string LooseCodePattern = "\\bcode\\s*[:=]\\s*([A-Za-z0-9_\\-\\.\\~\\%]+)";

        private const int TokenExpiryBufferMinutes = 5;
        private static readonly TimeSpan InteractiveAuthTimeout = TimeSpan.FromMinutes(3);

        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly AuthProbeCache _probeCache;
        private readonly SemaphoreSlim _tokenRefreshSemaphore = new SemaphoreSlim(1, 1);

        // Temporary state for interactive login dialog coordination only
        private int _authCheckInProgress;
        private (bool Success, string AccountId) _authResult;

        public string ProviderKey => "Epic";

        public TimeSpan ProbeCacheDuration => AuthProbeCache.ProviderCacheDurations.Epic;

        public EpicSessionManager(
            IPlayniteAPI api,
            ILogger logger,
            PlayniteAchievementsSettings settings,
            AuthProbeCache probeCache)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _probeCache = probeCache ?? throw new ArgumentNullException(nameof(probeCache));
        }

        /// <summary>
        /// Backward-compatible constructor that creates a default AuthProbeCache.
        /// </summary>
        public EpicSessionManager(
            IPlayniteAPI api,
            ILogger logger,
            PlayniteAchievementsSettings settings)
            : this(api, logger, settings, new AuthProbeCache(logger))
        {
        }

        /// <summary>
        /// Checks if currently authenticated based on cached probe result or token validity.
        /// </summary>
        public bool IsAuthenticated
        {
            get
            {
                if (_probeCache.IsCacheValid(ProviderKey, ProbeCacheDuration))
                {
                    return _probeCache.TryGetCachedUserId(ProviderKey, ProbeCacheDuration, out _);
                }
                return HasValidAccessToken();
            }
        }

        public string GetAccountId() => GetEpicSettings().AccountId?.Trim();

        // ---------------------------------------------------------------------
        // IEpicSessionProvider
        // ---------------------------------------------------------------------

        public async Task<string> GetAccessTokenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (HasValidAccessToken())
            {
                return GetEpicSettings().AccessToken;
            }

            var refreshToken = GetRefreshToken();
            var refreshExpiry = GetRefreshTokenExpiryUtc();
            if (!string.IsNullOrWhiteSpace(refreshToken) && DateTime.UtcNow < refreshExpiry)
            {
                await _tokenRefreshSemaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (HasValidAccessToken())
                    {
                        return GetEpicSettings().AccessToken;
                    }

                    await RenewTokensAsync(refreshToken, ct).ConfigureAwait(false);
                    if (HasValidAccessToken())
                    {
                        return GetEpicSettings().AccessToken;
                    }
                }
                finally
                {
                    _tokenRefreshSemaphore.Release();
                }
            }

            throw new EpicAuthRequiredException("Epic access token unavailable. Please authenticate.");
        }

        public async Task<bool> TryRefreshTokenAsync(CancellationToken ct)
        {
            var refreshToken = GetRefreshToken();
            var refreshExpiry = GetRefreshTokenExpiryUtc();

            if (string.IsNullOrWhiteSpace(refreshToken) || DateTime.UtcNow >= refreshExpiry)
            {
                _logger?.Debug("[EpicAuth] Cannot refresh token - no valid refresh token available.");
                return false;
            }

            await _tokenRefreshSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (HasValidAccessToken())
                {
                    return true;
                }

                try
                {
                    await RenewTokensAsync(refreshToken, ct).ConfigureAwait(false);
                    var success = HasValidAccessToken();
                    if (success)
                    {
                        _logger?.Info("[EpicAuth] Token refresh succeeded.");
                    }
                    else
                    {
                        _logger?.Warn("[EpicAuth] Token refresh completed but no valid access token.");
                    }
                    return success;
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "[EpicAuth] Token refresh failed.");
                    return false;
                }
            }
            finally
            {
                _tokenRefreshSemaphore.Release();
            }
        }

        // ---------------------------------------------------------------------
        // ISessionManager Implementation
        // ---------------------------------------------------------------------

        public async Task<AuthProbeResult> EnsureAuthAsync(CancellationToken ct)
        {
            if (_probeCache.IsCacheValid(ProviderKey, ProbeCacheDuration))
            {
                if (_probeCache.TryGetCachedUserId(ProviderKey, ProbeCacheDuration, out var cachedUserId))
                {
                    return AuthProbeResult.AlreadyAuthenticated(cachedUserId);
                }
            }

            return await ProbeAuthStateAsync(ct).ConfigureAwait(false);
        }

        public async Task<AuthProbeResult> ProbeAuthStateAsync(CancellationToken ct)
        {
            using (PerfScope.Start(_logger, "Epic.ProbeAuthStateAsync", thresholdMs: 50))
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    // Check if we have a valid access token
                    if (HasValidAccessToken())
                    {
                        var accountId = GetEpicSettings().AccountId?.Trim();
                        _probeCache.RecordProbe(ProviderKey, true, accountId);
                        return AuthProbeResult.AlreadyAuthenticated(accountId, GetTokenExpiryUtc());
                    }

                    // Try refresh token if access token is expired
                    if (HasValidRefreshToken())
                    {
                        try
                        {
                            await RenewTokensAsync(GetRefreshToken(), ct).ConfigureAwait(false);
                        }
                        catch (Exception renewEx)
                        {
                            _logger?.Debug(renewEx, "[EpicAuth] Refresh token probe failed.");
                        }

                        if (HasValidAccessToken())
                        {
                            var accountId = GetEpicSettings().AccountId?.Trim();
                            _probeCache.RecordProbe(ProviderKey, true, accountId);
                            return AuthProbeResult.AlreadyAuthenticated(accountId, GetTokenExpiryUtc());
                        }
                    }

                    // Best-effort: attempt non-interactive auth-code retrieval from existing logged-in browser session.
                    var authCode = await TryGetAuthorizationCodeFromSessionAsync(ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(authCode))
                    {
                        await AuthenticateUsingAuthCodeAsync(authCode, ct).ConfigureAwait(false);
                        if (HasValidAccessToken())
                        {
                            var accountId = GetEpicSettings().AccountId?.Trim();
                            _probeCache.RecordProbe(ProviderKey, true, accountId);
                            return AuthProbeResult.AlreadyAuthenticated(accountId, GetTokenExpiryUtc());
                        }
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
                    _logger?.Error(ex, "[EpicAuth] Probe failed with exception.");
                    _probeCache.RecordProbe(ProviderKey, false);
                    return AuthProbeResult.ProbeFailed();
                }
            }
        }

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

                if (!forceInteractive)
                {
                    var existingResult = await ProbeAuthStateAsync(ct).ConfigureAwait(false);
                    if (existingResult.IsSuccess)
                    {
                        progress?.Report(AuthProgressStep.Completed);
                        return existingResult;
                    }
                }
                else
                {
                    ClearSession();
                }

                progress?.Report(AuthProgressStep.OpeningLoginWindow);

                var loginTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = _api.MainView.UIDispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var result = LoginByFlow(EpicAuthFlow.AutoFallback);
                        loginTcs.TrySetResult(result ?? string.Empty);
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
                    progress?.Report(AuthProgressStep.Failed);
                    return AuthProbeResult.TimedOut(windowOpened);
                }

                var extracted = await loginTcs.Task.ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(extracted))
                {
                    progress?.Report(AuthProgressStep.VerifyingSession);
                    extracted = await TryGetAuthorizationCodeFromSessionAsync(ct).ConfigureAwait(false);
                }

                if (string.IsNullOrWhiteSpace(extracted))
                {
                    progress?.Report(AuthProgressStep.Failed);
                    return AuthProbeResult.Cancelled(windowOpened);
                }

                await AuthenticateUsingAuthCodeAsync(extracted, ct).ConfigureAwait(false);
                if (!HasValidAccessToken())
                {
                    progress?.Report(AuthProgressStep.Failed);
                    return AuthProbeResult.Failed(windowOpened);
                }

                var accountId = GetEpicSettings().AccountId?.Trim();
                _probeCache.RecordProbe(ProviderKey, true, accountId);
                progress?.Report(AuthProgressStep.Completed);

                return AuthProbeResult.Authenticated(accountId, windowOpened: windowOpened, expiresUtc: GetTokenExpiryUtc());
            }
            catch (OperationCanceledException)
            {
                progress?.Report(AuthProgressStep.Failed);
                return AuthProbeResult.TimedOut(windowOpened);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[EpicAuth] Authentication failed with exception.");
                progress?.Report(AuthProgressStep.Failed);
                return AuthProbeResult.Failed(windowOpened);
            }
        }

        public void ClearSession()
        {
            _logger?.Info("[EpicAuth] Clearing session.");

            _probeCache.Invalidate(ProviderKey);

            // Clear cookies from CEF
            try
            {
                _api.MainView.UIDispatcher.Invoke(() =>
                {
                    using (var view = _api.WebViews.CreateOffscreenView())
                    {
                        view.DeleteDomainCookies("epicgames.com");
                        view.DeleteDomainCookies(".epicgames.com");
                        view.DeleteDomainCookies(".store.epicgames.com");
                        view.DeleteDomainCookies("account-public-service-prod03.ol.epicgames.com");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[EpicAuth] Failed to clear Epic cookies.");
            }

            // Clear persisted tokens
            var epicSettings = GetEpicSettings();
            epicSettings.AccountId = null;
            epicSettings.AccessToken = null;
            epicSettings.RefreshToken = null;
            epicSettings.TokenType = null;
            epicSettings.TokenExpiryUtc = DateTime.MinValue;
            epicSettings.RefreshTokenExpiryUtc = DateTime.MinValue;
            ProviderRegistry.Write(epicSettings);
        }

        public void InvalidateProbeCache()
        {
            _probeCache.Invalidate(ProviderKey);
        }

        // ---------------------------------------------------------------------
        // Private Helper Methods
        // ---------------------------------------------------------------------

        private EpicSettings GetEpicSettings() => ProviderRegistry.Settings<EpicSettings>();

        private bool HasValidAccessToken()
        {
            var settings = GetEpicSettings();
            return !string.IsNullOrWhiteSpace(settings.AccessToken) &&
                   DateTime.UtcNow < settings.TokenExpiryUtc.AddMinutes(-TokenExpiryBufferMinutes);
        }

        private bool HasValidRefreshToken()
        {
            var refreshToken = GetRefreshToken();
            var refreshExpiry = GetRefreshTokenExpiryUtc();
            return !string.IsNullOrWhiteSpace(refreshToken) && DateTime.UtcNow < refreshExpiry;
        }

        private string GetRefreshToken() => GetEpicSettings().RefreshToken;

        private DateTime GetTokenExpiryUtc() => GetEpicSettings().TokenExpiryUtc;

        private DateTime GetRefreshTokenExpiryUtc() => GetEpicSettings().RefreshTokenExpiryUtc;

        private string LoginByFlow(EpicAuthFlow authFlow)
        {
            switch (authFlow)
            {
                case EpicAuthFlow.EmbeddedWindow:
                    return LoginInteractively();
                case EpicAuthFlow.SystemBrowser:
                    return LoginViaSystemBrowser();
                case EpicAuthFlow.AutoFallback:
                default:
                    return LoginWithFallback();
            }
        }

        private string LoginWithFallback()
        {
            var code = LoginInteractively();
            if (!string.IsNullOrWhiteSpace(code))
            {
                return code;
            }

            _logger?.Warn("[EpicAuth] Embedded login did not produce an auth code. Falling back to system browser flow.");
            return LoginViaSystemBrowser();
        }

        private string LoginInteractively()
        {
            _authResult = (false, null);

            IWebView view = null;
            try
            {
                view = _api.WebViews.CreateView(new WebViewSettings
                {
                    WindowWidth = 1000,
                    WindowHeight = 800,
                    UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) EpicGamesLauncher"
                });

                view.LoadingChanged += CloseWhenLoggedIn;
                view.Navigate(UrlLogin);
                view.OpenDialog();

                return _authResult.Success ? _authResult.AccountId : null;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[EpicAuth] Embedded login flow failed.");
                return null;
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

        private string LoginViaSystemBrowser()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = UrlAuthCode,
                    UseShellExecute = true
                });

                var prompt = ResourceProvider.GetString("LOCPlayAch_Settings_EpicAuth_BrowserPrompt");
                var title = ResourceProvider.GetString("LOCPlayAch_Settings_EpicConnection");

                if (string.IsNullOrWhiteSpace(prompt))
                {
                    prompt = "Sign in to Epic in your browser, then paste the final redirected URL (or the code value) here.";
                }

                var input = _api.Dialogs.SelectString(prompt, title, string.Empty);
                if (input == null || !input.Result)
                {
                    return null;
                }

                var selected = input.SelectedString?.Trim();
                if (string.IsNullOrWhiteSpace(selected))
                {
                    return null;
                }

                var extracted = TryExtractAuthorizationCode(selected);
                return string.IsNullOrWhiteSpace(extracted) ? selected : extracted;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[EpicAuth] Failed to launch system browser login flow.");
                return null;
            }
        }

        /// <summary>
        /// Alternative authentication flow that shows instructions first, then opens browser
        /// and prompts user to paste the authorization code directly.
        /// </summary>
        public async Task<AuthProbeResult> LoginAlternativeAsync(CancellationToken ct)
        {
            var windowOpened = false;

            try
            {
                ct.ThrowIfCancellationRequested();

                // Show instructions dialog first
                var instructionsTitle = ResourceProvider.GetString("LOCPlayAch_Settings_EpicConnection");
                var instructionsMessage = ResourceProvider.GetString("LOCPlayAch_Settings_Epic_AltAuthInstructions");
                if (string.IsNullOrWhiteSpace(instructionsMessage))
                {
                    instructionsMessage = "1. Login to Epic in your default browser when it opens\n2. After logging in, find the \"authorizationCode\" value on the page\n3. Copy and paste that value in the next dialog";
                }

                var instructionsResult = _api.Dialogs.ShowMessage(
                    instructionsMessage,
                    instructionsTitle,
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Information);

                if (instructionsResult != MessageBoxResult.OK)
                {
                    return AuthProbeResult.Cancelled(windowOpened);
                }

                // Open browser to auth URL
                windowOpened = true;
                Process.Start(new ProcessStartInfo
                {
                    FileName = UrlAuthCode,
                    UseShellExecute = true
                });

                // Prompt for authorization code
                var codePrompt = ResourceProvider.GetString("LOCPlayAch_Settings_Epic_AltAuthCodePrompt");
                if (string.IsNullOrWhiteSpace(codePrompt))
                {
                    codePrompt = "Paste the authorizationCode value here:";
                }

                var input = _api.Dialogs.SelectString(codePrompt, instructionsTitle, string.Empty);
                if (input == null || !input.Result)
                {
                    return AuthProbeResult.Cancelled(windowOpened);
                }

                var authCode = input.SelectedString?.Trim();
                if (string.IsNullOrWhiteSpace(authCode))
                {
                    return AuthProbeResult.Cancelled(windowOpened);
                }

                // Try to extract code from URL if user pasted the full URL
                var extracted = TryExtractAuthorizationCode(authCode);
                authCode = string.IsNullOrWhiteSpace(extracted) ? authCode : extracted;

                // Exchange code for tokens
                await AuthenticateUsingAuthCodeAsync(authCode, ct).ConfigureAwait(false);

                if (!HasValidAccessToken())
                {
                    return AuthProbeResult.Failed(windowOpened);
                }

                var accountId = GetEpicSettings().AccountId?.Trim();
                _probeCache.RecordProbe(ProviderKey, true, accountId);

                return AuthProbeResult.Authenticated(accountId, windowOpened: windowOpened);
            }
            catch (OperationCanceledException)
            {
                return AuthProbeResult.TimedOut(windowOpened);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[EpicAuth] Alternative login failed with exception.");
                return AuthProbeResult.Failed(windowOpened);
            }
        }

        private async void CloseWhenLoggedIn(object sender, WebViewLoadingChangedEventArgs e)
        {
            try
            {
                if (e.IsLoading)
                {
                    return;
                }

                // Prevent concurrent checks
                if (Interlocked.CompareExchange(ref _authCheckInProgress, 1, 0) != 0)
                {
                    return;
                }

                var view = (IWebView)sender;

                // Poll for authorization code with retry
                var code = await WaitForAuthorizationCodeAsync(view, CancellationToken.None).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(code))
                {
                    _authResult = (true, code);
                    _logger?.Info("[EpicAuth] Authorization code extracted, closing login dialog.");
                    _ = _api.MainView.UIDispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            view.Close();
                        }
                        catch (Exception closeEx)
                        {
                            _logger?.Debug(closeEx, "[EpicAuth] Failed to close login dialog.");
                        }
                    }));
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[EpicAuth] CloseWhenLoggedIn failed.");
            }
            finally
            {
                Interlocked.Exchange(ref _authCheckInProgress, 0);
            }
        }

        private async Task<string> WaitForAuthorizationCodeAsync(IWebView view, CancellationToken ct)
        {
            const int attempts = 8;
            const int delayMs = 500;

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    // Check for redirect indicator before attempting extraction.
                    var pageText = await view.GetPageTextAsync().ConfigureAwait(false);
                    if (string.IsNullOrEmpty(pageText) || !pageText.Contains("localhost"))
                    {
                        if (attempt < attempts)
                        {
                            await Task.Delay(delayMs, ct).ConfigureAwait(false);
                        }
                        continue;
                    }

                    var source = await view.GetPageSourceAsync().ConfigureAwait(false);
                    var code = TryExtractAuthorizationCode(source);
                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        return code;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"[EpicAuth] Waiting for authorization code failed on attempt {attempt}, retrying.");
                }

                if (attempt < attempts)
                {
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
            }

            return null;
        }

        private async Task<string> TryGetAuthorizationCodeFromSessionAsync(CancellationToken ct)
        {
            using (PerfScope.Start(_logger, "Epic.TryGetAuthorizationCodeFromSessionAsync", thresholdMs: 50))
            {
                var dispatchOperation = _api.MainView.UIDispatcher.InvokeAsync(async () =>
                {
                    using (var view = _api.WebViews.CreateOffscreenView())
                    {
                        try
                        {
                            ct.ThrowIfCancellationRequested();
                            await view.NavigateAndWaitAsync(UrlAuthCode, timeoutMs: 12000);

                            var source = await view.GetPageSourceAsync().ConfigureAwait(false);
                            var code = TryExtractAuthorizationCode(source);
                            if (!string.IsNullOrWhiteSpace(code))
                            {
                                return code;
                            }

                            var text = await view.GetPageTextAsync().ConfigureAwait(false);
                            return TryExtractAuthorizationCode(text);
                        }
                        catch (Exception ex)
                        {
                            _logger?.Debug(ex, "[EpicAuth] Offscreen auth-code probe failed.");
                            return null;
                        }
                    }
                });

                var operationTask = await dispatchOperation.Task.ConfigureAwait(false);
                return await operationTask.ConfigureAwait(false);
            }
        }

        private static string TryExtractAuthorizationCode(string htmlOrText)
        {
            if (string.IsNullOrWhiteSpace(htmlOrText))
            {
                return null;
            }

            var candidate = htmlOrText.Trim();
            for (int i = 0; i < 4; i++)
            {
                var extracted = ExtractCodeCandidate(candidate);
                if (!string.IsNullOrWhiteSpace(extracted))
                {
                    return extracted;
                }

                string decoded;
                try
                {
                    decoded = Uri.UnescapeDataString(candidate);
                }
                catch
                {
                    break;
                }

                if (string.Equals(decoded, candidate, StringComparison.Ordinal))
                {
                    break;
                }

                candidate = decoded;
            }

            return null;
        }

        private static string ExtractCodeCandidate(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var queryMatch = Regex.Match(value, QueryCodePattern, RegexOptions.IgnoreCase);
            if (queryMatch.Success && queryMatch.Groups.Count > 1)
            {
                var extracted = queryMatch.Groups[1].Value;
                return NormalizeCode(extracted);
            }

            var legacyMatch = Regex.Match(value, LoginCodePattern, RegexOptions.IgnoreCase);
            if (!legacyMatch.Success || legacyMatch.Groups.Count < 2)
            {
                var looseMatch = Regex.Match(value, LooseCodePattern, RegexOptions.IgnoreCase);
                if (looseMatch.Success && looseMatch.Groups.Count > 1)
                {
                    return NormalizeCode(looseMatch.Groups[1].Value);
                }

                return null;
            }

            return NormalizeCode(legacyMatch.Groups[1].Value);
        }

        private static string NormalizeCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            try
            {
                return Uri.UnescapeDataString(value.Trim());
            }
            catch
            {
                return value.Trim();
            }
        }

        private async Task AuthenticateUsingAuthCodeAsync(string authorizationCode, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(authorizationCode))
            {
                throw new EpicAuthRequiredException("Epic authorization code is missing.");
            }

            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Clear();
                httpClient.DefaultRequestHeaders.Add("Authorization", "basic " + AuthEncodedString);

                using (var content = new StringContent(
                    $"grant_type=authorization_code&code={authorizationCode}&token_type=eg1"))
                {
                    content.Headers.Clear();
                    content.Headers.Add("Content-Type", "application/x-www-form-urlencoded");
                    var response = await httpClient.PostAsync(UrlAccountAuth, content, ct).ConfigureAwait(false);
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new EpicAuthRequiredException($"Epic token exchange failed with HTTP {(int)response.StatusCode}.");
                    }

                    ApplyTokenResponse(body);
                }
            }
        }

        private async Task RenewTokensAsync(string refreshToken, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                throw new EpicAuthRequiredException("Epic refresh token is missing.");
            }

            using (var httpClient = new HttpClient())
            {
                httpClient.DefaultRequestHeaders.Clear();
                httpClient.DefaultRequestHeaders.Add("Authorization", "basic " + AuthEncodedString);

                using (var content = new StringContent(
                    $"grant_type=refresh_token&refresh_token={refreshToken}&token_type=eg1"))
                {
                    content.Headers.Clear();
                    content.Headers.Add("Content-Type", "application/x-www-form-urlencoded");
                    var response = await httpClient.PostAsync(UrlAccountAuth, content, ct).ConfigureAwait(false);
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new EpicAuthRequiredException($"Epic token refresh failed with HTTP {(int)response.StatusCode}.");
                    }

                    ApplyTokenResponse(body);
                }
            }
        }

        private void ApplyTokenResponse(string responseJson)
        {
            if (string.IsNullOrWhiteSpace(responseJson))
            {
                throw new EpicAuthRequiredException("Epic token endpoint returned an empty response.");
            }

            OauthResponse payload;
            try
            {
                payload = JsonConvert.DeserializeObject<OauthResponse>(responseJson);
            }
            catch (Exception ex)
            {
                throw new EpicAuthRequiredException("Epic token response could not be parsed: " + ex.Message);
            }

            if (payload == null || string.IsNullOrWhiteSpace(payload.access_token) || string.IsNullOrWhiteSpace(payload.account_id))
            {
                throw new EpicAuthRequiredException("Epic token response was missing required fields.");
            }

            // Save to provider settings - the source of truth in this worktree
            var epicSettings = GetEpicSettings();
            epicSettings.AccountId = payload.account_id?.Trim();
            epicSettings.AccessToken = payload.access_token;
            epicSettings.RefreshToken = payload.refresh_token;
            epicSettings.TokenType = string.IsNullOrWhiteSpace(payload.token_type) ? "bearer" : payload.token_type.Trim();
            epicSettings.TokenExpiryUtc = NormalizeUtc(payload.expires_at, payload.expires_in);
            epicSettings.RefreshTokenExpiryUtc = NormalizeUtc(payload.refresh_expires_at, payload.refresh_expires_in);
            ProviderRegistry.Write(epicSettings);

            // Update cache
            _probeCache.RecordProbe(ProviderKey, true, payload.account_id?.Trim());
        }

        private static DateTime NormalizeUtc(DateTime? explicitUtc, int? expiresInSeconds)
        {
            if (explicitUtc.HasValue && explicitUtc.Value != DateTime.MinValue)
            {
                return explicitUtc.Value.Kind == DateTimeKind.Utc
                    ? explicitUtc.Value
                    : explicitUtc.Value.ToUniversalTime();
            }

            if (expiresInSeconds.HasValue && expiresInSeconds.Value > 0)
            {
                return DateTime.UtcNow.AddSeconds(expiresInSeconds.Value);
            }

            return DateTime.UtcNow.AddHours(1);
        }

        private sealed class OauthResponse
        {
            [JsonProperty("account_id")]
            public string account_id { get; set; }

            [JsonProperty("token_type")]
            public string token_type { get; set; }

            [JsonProperty("access_token")]
            public string access_token { get; set; }

            [JsonProperty("expires_at")]
            public DateTime? expires_at { get; set; }

            [JsonProperty("expires_in")]
            public int? expires_in { get; set; }

            [JsonProperty("refresh_token")]
            public string refresh_token { get; set; }

            [JsonProperty("refresh_expires_at")]
            public DateTime? refresh_expires_at { get; set; }

            [JsonProperty("refresh_expires")]
            public int? refresh_expires_in { get; set; }
        }
    }
}
