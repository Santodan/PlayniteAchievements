using Newtonsoft.Json;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.EA.Models;
using Playnite.SDK;
using Playnite.SDK.Events;
using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.EA
{
    public sealed class EASessionManager : ISessionManager
    {
        private const string UrlToken =
            "https://accounts.ea.com/connect/auth?client_id=ORIGIN_JS_SDK&response_type=token&redirect_uri=nucleus:rest&prompt=none";

        private const string UrlLogin = "https://www.ea.com/login";
        private const string GraphQlEndpoint = "https://service-aggregation-layer.juno.ea.com/graphql";
        private const string IdentityQuery = @"query { me { player { pd psd displayName } } }";

        private static readonly TimeSpan InteractiveAuthTimeout = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan TokenExpiryBuffer = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan FallbackTokenCacheDuration = TimeSpan.FromMinutes(25);

        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _tokenSemaphore = new SemaphoreSlim(1, 1);

        private int _authCheckInProgress;
        private (bool Success, string PlayerSubId) _authResult;

        private string _cachedAccessToken;
        private DateTime _cachedTokenExpiryUtc = DateTime.MinValue;

        public string ProviderKey => "EA";

        public bool IsAuthenticated =>
            !string.IsNullOrWhiteSpace(ProviderRegistry.Settings<EASettings>().PlayerSubId);

        public EASessionManager(IPlayniteAPI api, ILogger logger, HttpClient httpClient)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        }

        public string GetPlayerSubId() => ProviderRegistry.Settings<EASettings>().PlayerSubId;

        public string GetPlayerId() => ProviderRegistry.Settings<EASettings>().PlayerId;

        public async Task<string> GetAccessTokenAsync(CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(_cachedAccessToken) && DateTime.UtcNow < _cachedTokenExpiryUtc)
            {
                return _cachedAccessToken;
            }

            await _tokenSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!string.IsNullOrWhiteSpace(_cachedAccessToken) && DateTime.UtcNow < _cachedTokenExpiryUtc)
                {
                    return _cachedAccessToken;
                }

                var token = await ProbeTokenUrlAsync(timeoutMs: 10000, ct: ct).ConfigureAwait(false);
                if (token != null && token.IsLoggedIn)
                {
                    SetCachedToken(token);
                    return _cachedAccessToken;
                }
            }
            finally
            {
                _tokenSemaphore.Release();
            }

            throw new EaAuthRequiredException("EA authentication required. Please login.");
        }

        public async Task<AuthProbeResult> ProbeAuthStateAsync(CancellationToken ct)
        {
            using (PerfScope.Start(_logger, "EA.ProbeAuthStateAsync", thresholdMs: 50))
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    var token = await ProbeTokenUrlAsync(timeoutMs: 10000, ct: ct).ConfigureAwait(false);
                    if (token != null && token.IsLoggedIn)
                    {
                        SetCachedToken(token);
                        var identity = await FetchIdentityAsync(token.AccessToken, ct).ConfigureAwait(false);
                        if (identity != null && !string.IsNullOrWhiteSpace(identity.Psd))
                        {
                            PersistIdentity(identity);
                            return AuthProbeResult.AlreadyAuthenticated(identity.Psd, GetCachedTokenExpiryOrNull());
                        }

                        return AuthProbeResult.ProbeFailed();
                    }

                    ClearSettings();
                    return AuthProbeResult.NotAuthenticated();
                }
                catch (OperationCanceledException)
                {
                    return AuthProbeResult.Cancelled();
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "[EAAuth] Probe failed with exception.");
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

                _logger?.Info("[EAAuth] Starting interactive authentication.");

                if (!forceInteractive)
                {
                    var existingResult = await ProbeAuthStateAsync(ct).ConfigureAwait(false);
                    if (existingResult.IsSuccess)
                    {
                        _logger?.Info("[EAAuth] Already authenticated.");
                        progress?.Report(AuthProgressStep.Completed);
                        return existingResult;
                    }
                }
                else
                {
                    ClearSession();
                }

                _logger?.Info("[EAAuth] Opening login dialog.");
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
                    _logger?.Warn("[EAAuth] Interactive login timed out.");
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
                    _logger?.Warn("[EAAuth] Interactive login failed or was cancelled.");
                    progress?.Report(AuthProgressStep.Failed);
                    return AuthProbeResult.Cancelled(windowOpened);
                }

                _logger?.Info("[EAAuth] Interactive login succeeded.");
                progress?.Report(AuthProgressStep.Completed);
                return AuthProbeResult.Authenticated(
                    extractedId,
                    expiresUtc: GetCachedTokenExpiryOrNull(),
                    windowOpened: windowOpened);
            }
            catch (OperationCanceledException)
            {
                _logger?.Info("[EAAuth] Authentication was cancelled or timed out.");
                progress?.Report(AuthProgressStep.Failed);
                return AuthProbeResult.TimedOut(windowOpened);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[EAAuth] Authentication failed with exception.");
                progress?.Report(AuthProgressStep.Failed);
                return AuthProbeResult.Failed(windowOpened);
            }
        }

        public void ClearSession()
        {
            _logger?.Info("[EAAuth] Clearing session.");
            _authResult = (false, null);
            _cachedAccessToken = null;
            _cachedTokenExpiryUtc = DateTime.MinValue;

            ClearSettings();

            _api.DeleteDomainCookies(_logger, "[EAAuth]", "ea.com", ".ea.com", "accounts.ea.com");
        }

        private void ClearSettings()
        {
            var settings = ProviderRegistry.Settings<EASettings>();
            if (!string.IsNullOrWhiteSpace(settings.PlayerSubId))
            {
                settings.PlayerId = null;
                settings.PlayerSubId = null;
                settings.DisplayName = null;
                ProviderRegistry.Write(settings, persistToDisk: true);
            }
        }

        private async Task<EaTokenResponse> ProbeTokenUrlAsync(int timeoutMs = 10000, CancellationToken ct = default)
        {
            using (PerfScope.Start(_logger, "EA.ProbeTokenUrlAsync", thresholdMs: 50))
            {
                ct.ThrowIfCancellationRequested();
                var result = await _api.WithOffscreenViewAsync(async view =>
                {
                    try
                    {
                        await view.NavigateAndWaitAsync(UrlToken, timeoutMs: timeoutMs);
                        var responseText = await view.GetPageTextAsync();
                        if (!string.IsNullOrWhiteSpace(responseText))
                        {
                            return JsonConvert.DeserializeObject<EaTokenResponse>(responseText);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, "[EAAuth] Token URL probe failed.");
                    }

                    return null;
                }).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return result;
            }
        }

        private async Task<EaPlayerIdentity> FetchIdentityAsync(string accessToken, CancellationToken ct)
        {
            var body = JsonConvert.SerializeObject(new { query = IdentityQuery });
            using (var request = new HttpRequestMessage(HttpMethod.Post, GraphQlEndpoint))
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false))
                {
                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger?.Warn($"[EAAuth] Identity query failed with HTTP {(int)response.StatusCode}: {json}");
                        return null;
                    }

                    var result = JsonConvert.DeserializeObject<GraphQlIdentityResponse>(json);
                    var player = result?.Data?.Me?.Player;
                    if (player == null || string.IsNullOrWhiteSpace(player.Psd))
                    {
                        _logger?.Warn("[EAAuth] Identity query returned no player data.");
                        return null;
                    }

                    return new EaPlayerIdentity
                    {
                        Pd = player.Pd,
                        Psd = player.Psd,
                        DisplayName = player.DisplayName
                    };
                }
            }
        }

        private string LoginInteractively()
        {
            _authResult = (false, null);
            IWebView view = null;

            try
            {
                view = _api.WebViews.CreateView(1000, 800);
                view.DeleteDomainCookies("ea.com");
                view.DeleteDomainCookies(".ea.com");
                view.DeleteDomainCookies("accounts.ea.com");

                view.LoadingChanged += CloseWhenLoggedIn;
                view.Navigate(UrlLogin);
                view.OpenDialog();

                return _authResult.Success ? _authResult.PlayerSubId : null;
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
                {
                    return;
                }

                var view = (IWebView)sender;
                var address = view.GetCurrentAddress();

                if (string.IsNullOrWhiteSpace(address))
                {
                    return;
                }

                var isLoginPage = address.IndexOf("login", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                  address.IndexOf("ea.com/login", StringComparison.OrdinalIgnoreCase) >= 0;

                if (isLoginPage)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _authCheckInProgress, 1, 0) != 0)
                {
                    return;
                }

                _logger?.Debug($"[EAAuth] Navigation to: {address}");

                var extractedId = await WaitForAuthenticatedAsync(CancellationToken.None).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(extractedId))
                {
                    _authResult = (true, extractedId);
                    _logger?.Info($"[EAAuth] Authenticated as player sub ID: {extractedId}");
                    _ = _api.MainView.UIDispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            view.Close();
                        }
                        catch (Exception closeEx)
                        {
                            _logger?.Debug(closeEx, "[EAAuth] Failed to close login dialog.");
                        }
                    }));
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[EAAuth] Failed to check authentication status");
            }
            finally
            {
                Interlocked.Exchange(ref _authCheckInProgress, 0);
            }
        }

        private async Task<string> WaitForAuthenticatedAsync(CancellationToken ct)
        {
            const int attempts = 8;
            const int delayMs = 500;

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var token = await ProbeTokenUrlAsync(timeoutMs: 6000, ct: ct).ConfigureAwait(false);
                    if (token != null && token.IsLoggedIn)
                    {
                        SetCachedToken(token);
                        var identity = await FetchIdentityAsync(token.AccessToken, ct).ConfigureAwait(false);
                        if (identity != null && !string.IsNullOrWhiteSpace(identity.Psd))
                        {
                            PersistIdentity(identity);
                            return identity.Psd;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "[EAAuth] Waiting for authentication failed, retrying.");
                }

                if (attempt < attempts)
                {
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
            }

            return null;
        }

        private void PersistIdentity(EaPlayerIdentity identity)
        {
            if (identity == null || string.IsNullOrWhiteSpace(identity.Psd))
            {
                return;
            }

            var settings = ProviderRegistry.Settings<EASettings>();
            var changed =
                !string.Equals(settings.PlayerId, identity.Pd, StringComparison.Ordinal) ||
                !string.Equals(settings.PlayerSubId, identity.Psd, StringComparison.Ordinal) ||
                !string.Equals(settings.DisplayName, identity.DisplayName, StringComparison.Ordinal);

            settings.PlayerId = identity.Pd;
            settings.PlayerSubId = identity.Psd;
            settings.DisplayName = identity.DisplayName;

            if (changed)
            {
                ProviderRegistry.Write(settings, persistToDisk: true);
                _logger?.Info($"[EAAuth] Identity persisted: pd={identity.Pd}, psd={identity.Psd}, name={identity.DisplayName}");
            }
        }

        private void SetCachedToken(EaTokenResponse token)
        {
            if (token == null || !token.IsLoggedIn)
            {
                return;
            }

            _cachedAccessToken = token.AccessToken;
            _cachedTokenExpiryUtc = GetTokenExpiryUtc(token);
        }

        private static DateTime GetTokenExpiryUtc(EaTokenResponse token)
        {
            if (token != null &&
                int.TryParse(token.ExpiresIn, NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiresInSeconds) &&
                expiresInSeconds > 0)
            {
                var ttl = TimeSpan.FromSeconds(expiresInSeconds);
                if (ttl > TokenExpiryBuffer)
                {
                    ttl -= TokenExpiryBuffer;
                }

                return DateTime.UtcNow.Add(ttl);
            }

            return DateTime.UtcNow.Add(FallbackTokenCacheDuration);
        }

        private DateTime? GetCachedTokenExpiryOrNull()
        {
            return _cachedTokenExpiryUtc > DateTime.MinValue
                ? (DateTime?)_cachedTokenExpiryUtc
                : null;
        }

        private sealed class GraphQlIdentityResponse
        {
            [JsonProperty("data")]
            public IdentityData Data { get; set; }
        }

        private sealed class IdentityData
        {
            [JsonProperty("me")]
            public IdentityMe Me { get; set; }
        }

        private sealed class IdentityMe
        {
            [JsonProperty("player")]
            public EaPlayerIdentity Player { get; set; }
        }
    }

    internal sealed class EaPlayerIdentity
    {
        [JsonProperty("pd")]
        public string Pd { get; set; }

        [JsonProperty("psd")]
        public string Psd { get; set; }

        [JsonProperty("displayName")]
        public string DisplayName { get; set; }
    }
}
