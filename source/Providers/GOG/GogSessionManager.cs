using PlayniteAchievements.Providers.GOG.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Common;
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
    /// Auth state is always probed from the source of truth before any provider work.
    /// </summary>
    public sealed class GogSessionManager : ISessionManager, IGogTokenProvider
    {
        private const string UrlLogin = "https://www.gog.com/account/";
        private const string UrlAccountInfo = "https://menu.gog.com/v1/account/basic";
        private static readonly TimeSpan InteractiveAuthTimeout = TimeSpan.FromMinutes(3);

        private (bool Success, string UserId) _authResult;
        private int _authCheckInProgress;
        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;

        public string ProviderKey => "GOG";

        /// <summary>
        /// Checks if currently authenticated based on persisted UserId setting.
        /// </summary>
        public bool IsAuthenticated =>
            !string.IsNullOrWhiteSpace(ProviderRegistry.Settings<GogSettings>().UserId);

        public GogSessionManager(
            IPlayniteAPI api,
            ILogger logger,
            PlayniteAchievementsSettings settings)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        // ---------------------------------------------------------------------
        // IGogTokenProvider
        // ---------------------------------------------------------------------

        /// <summary>
        /// Gets the current access token by probing from source of truth.
        /// Throws if not authenticated.
        /// </summary>
        public string GetAccessToken()
        {
            var probeTask = ProbeAuthStateAsync(CancellationToken.None);
            probeTask.ConfigureAwait(false).GetAwaiter().GetResult();

            if (probeTask.Result.IsSuccess)
            {
                var apiResponse = CallAccountInfoApiAsync(timeoutMs: 6000)
                    .ConfigureAwait(false).GetAwaiter().GetResult();

                if (apiResponse != null && !string.IsNullOrWhiteSpace(apiResponse.ResolvedAccessToken))
                {
                    return apiResponse.ResolvedAccessToken;
                }
            }

            throw new AuthRequiredException("GOG authentication required. Please login.");
        }

        /// <summary>
        /// Gets the current user ID from provider settings.
        /// </summary>
        public string GetUserId() => ProviderRegistry.Settings<GogSettings>().UserId;

        // ---------------------------------------------------------------------
        // ISessionManager Implementation
        // ---------------------------------------------------------------------

        public async Task<AuthProbeResult> ProbeAuthStateAsync(CancellationToken ct)
        {
            using (PerfScope.Start(_logger, "GOG.ProbeAuthStateAsync", thresholdMs: 50))
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    var response = await CallAccountInfoApiAsync(timeoutMs: 6000).ConfigureAwait(false);
                    if (response != null && response.IsLoggedIn && !string.IsNullOrWhiteSpace(response.UserId))
                    {
                        var userId = response.UserId;

                        var gogSettings = ProviderRegistry.Settings<GogSettings>();
                        if (gogSettings.UserId != userId)
                        {
                            gogSettings.UserId = userId;
                            ProviderRegistry.Write(gogSettings);
                        }

                        var expiresUtc = GetTokenExpiryUtc(response);
                        return AuthProbeResult.AlreadyAuthenticated(userId, expiresUtc);
                    }

                    var gogSettingsClear = ProviderRegistry.Settings<GogSettings>();
                    if (!string.IsNullOrWhiteSpace(gogSettingsClear.UserId))
                    {
                        gogSettingsClear.UserId = null;
                        ProviderRegistry.Write(gogSettingsClear);
                    }

                    return AuthProbeResult.NotAuthenticated();
                }
                catch (OperationCanceledException)
                {
                    return AuthProbeResult.Cancelled();
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "[GogAuth] Probe failed with exception.");
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

                _logger?.Info("[GogAuth] Starting interactive authentication.");

                if (!forceInteractive)
                {
                    var existingResult = await ProbeAuthStateAsync(ct).ConfigureAwait(false);
                    if (existingResult.IsSuccess)
                    {
                        _logger?.Info("[GogAuth] Already authenticated.");
                        progress?.Report(AuthProgressStep.Completed);
                        return existingResult;
                    }
                }
                else
                {
                    ClearSession();
                }

                _logger?.Info("[GogAuth] Opening login dialog.");
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
                    _logger?.Warn("[GogAuth] Interactive login timed out.");
                    progress?.Report(AuthProgressStep.Failed);
                    return AuthProbeResult.TimedOut(windowOpened);
                }

                var extractedId = await loginTcs.Task.ConfigureAwait(false);

                progress?.Report(AuthProgressStep.VerifyingSession);
                if (string.IsNullOrWhiteSpace(extractedId))
                {
                    var probeResult = await ProbeAuthStateAsync(ct).ConfigureAwait(false);
                    if (probeResult.IsSuccess)
                    {
                        extractedId = probeResult.UserId;
                    }
                }

                if (string.IsNullOrWhiteSpace(extractedId))
                {
                    _logger?.Warn("[GogAuth] Interactive login failed or was cancelled.");
                    progress?.Report(AuthProgressStep.Failed);
                    return AuthProbeResult.Cancelled(windowOpened);
                }

                var gogSettings = ProviderRegistry.Settings<GogSettings>();
                gogSettings.UserId = extractedId;
                ProviderRegistry.Write(gogSettings);

                _logger?.Info("[GogAuth] Interactive login succeeded.");
                progress?.Report(AuthProgressStep.Completed);
                return AuthProbeResult.Authenticated(extractedId, windowOpened: windowOpened);
            }
            catch (OperationCanceledException)
            {
                _logger?.Info("[GogAuth] Authentication was cancelled or timed out.");
                progress?.Report(AuthProgressStep.Failed);
                return AuthProbeResult.TimedOut(windowOpened);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[GogAuth] Authentication failed with exception.");
                progress?.Report(AuthProgressStep.Failed);
                return AuthProbeResult.Failed(windowOpened);
            }
        }

        public void ClearSession()
        {
            _logger?.Info("[GogAuth] Clearing session.");
            _authResult = (false, null);

            var gogSettings = ProviderRegistry.Settings<GogSettings>();
            if (!string.IsNullOrWhiteSpace(gogSettings.UserId))
            {
                gogSettings.UserId = null;
                ProviderRegistry.Write(gogSettings);
            }

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

        // ---------------------------------------------------------------------
        // Private Helper Methods
        // ---------------------------------------------------------------------

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

        private static DateTime? GetTokenExpiryUtc(GogAccountInfoResponse response)
        {
            if (response == null)
                return null;

            if (response.ResolvedAccessTokenExpires > 0)
            {
                return DateTimeOffset.FromUnixTimeSeconds(response.ResolvedAccessTokenExpires).UtcDateTime;
            }

            return null;
        }

        private static bool IsLoginPageUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            return url.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0
                   || url.IndexOf("openlogin", StringComparison.OrdinalIgnoreCase) >= 0;
        }

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
                    _logger?.Debug("[GogAuth] Still on login page, waiting...");
                    return;
                }

                if (Interlocked.CompareExchange(ref _authCheckInProgress, 1, 0) != 0)
                    return;

                _logger?.Debug($"[GogAuth] Navigation to: {address}");

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

        private async Task<string> WaitForAuthenticatedUserAsync(CancellationToken ct)
        {
            const int attempts = 8;
            const int delayMs = 500;

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var response = await CallAccountInfoApiAsync(timeoutMs: 6000).ConfigureAwait(false);
                    if (response != null && response.IsLoggedIn && !string.IsNullOrWhiteSpace(response.UserId))
                    {
                        return response.UserId;
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
    }
}
