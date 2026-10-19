using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace PlayniteAchievements.Providers.PSN
{
    internal sealed class PsnRuntimeHooks
    {
        public Func<string, CancellationToken, Task<PsnBootstrapAttempt>> BootstrapFromNpssoAsync { get; set; }

        public Action ClearBrowserCookies { get; set; }

        public Func<CancellationToken, Task<CookieContainer>> LoginInteractiveAsync { get; set; }

        public Func<CookieContainer, CancellationToken, Task<bool>> ProbeSessionAsync { get; set; }

        public Func<CookieContainer, CancellationToken, Task<MobileTokens>> RequestMobileTokensAsync { get; set; }

        public Func<DateTime> UtcNow { get; set; }
    }

    internal sealed class PsnBootstrapAttempt
    {
        public PsnBootstrapAttempt(AuthOutcome outcome, CookieContainer cookies = null, string finalAddress = null)
        {
            Outcome = outcome;
            Cookies = cookies;
            FinalAddress = finalAddress ?? string.Empty;
        }

        public CookieContainer Cookies { get; }

        public string FinalAddress { get; }

        public AuthOutcome Outcome { get; }

        public bool IsSuccess =>
            Outcome == AuthOutcome.AlreadyAuthenticated ||
            Outcome == AuthOutcome.Authenticated;
    }

    public sealed class PsnSessionManager : ISessionManager
    {
        private enum AuthMode
        {
            PassiveProbe,
            ExplicitValidation,
            ProviderAccess
        }

        private sealed class ValidationCooldown
        {
            public string Npsso { get; set; }

            public AuthProbeResult Result { get; set; }

            public DateTime UntilUtc { get; set; }
        }

        private sealed class PsnTransientAuthException : Exception
        {
            public PsnTransientAuthException(string message, bool isTimeout)
                : base(message)
            {
                IsTimeout = isTimeout;
            }

            public PsnTransientAuthException(string message, Exception innerException, bool isTimeout)
                : base(message, innerException)
            {
                IsTimeout = isTimeout;
            }

            public bool IsTimeout { get; }
        }

        private static readonly TimeSpan InteractiveAuthTimeout = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan MobileTokenExpiryBuffer = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan NpssoFailureCooldown = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan GameListProbeTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan MobileRequestTimeout = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan NpssoNavigationTimeout = TimeSpan.FromSeconds(15);

        private const string LoginUrl = @"https://web.np.playstation.com/api/session/v1/signin?redirect_uri=https://io.playstation.com/central/auth/login%3FpostSignInURL=https://www.playstation.com/home%26cancelURL=https://www.playstation.com/home&smcid=web:pdc";
        private const string MobileCodeUrl = "https://ca.account.sony.com/api/authz/v3/oauth/authorize?access_type=offline&client_id=09515159-7237-4370-9b40-3806e67c0891&redirect_uri=com.scee.psxandroid.scecompcall%3A%2F%2Fredirect&response_type=code&scope=psn%3Amobile.v2.core%20psn%3Aclientapp";
        private const string MobileTokenUrl = "https://ca.account.sony.com/api/authz/v3/oauth/token";
        private const string MobileTokenAuth = "MDk1MTUxNTktNzIzNy00MzcwLTliNDAtMzgwNmU2N2MwODkxOnVjUGprYTV0bnRCMktxc1A=";
        private const string GameListProbeUrl = "https://web.np.playstation.com/api/graphql/v1/op?operationName=getPurchasedGameList&variables=%7B%22isActive%22%3Atrue%2C%22platform%22%3A%5B%22ps3%22%2C%22ps4%22%2C%22ps5%22%5D%2C%22start%22%3A0%2C%22size%22%3A1%2C%22subscriptionService%22%3A%22NONE%22%7D&extensions=%7B%22persistedQuery%22%3A%7B%22version%22%3A1%2C%22sha256Hash%22%3A%222c045408b0a4d0264bb5a3edfed4efd49fb4749cf8d216be9043768adff905e2%22%7D%7D";

        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly PsnRuntimeHooks _hooks;
        private readonly SemaphoreSlim _authGate = new SemaphoreSlim(1, 1);
        private readonly string _legacyTokenPath;
        private readonly string _tokenPath;
        private int _sessionStateVersion;
        private readonly object _validationLock = new object();

        private MobileTokens _mobileToken;
        private DateTime _mobileTokenAcquiredUtc = DateTime.MinValue;
        private DateTime _mobileTokenExpiryUtc = DateTime.MinValue;
        private ValidationCooldown _validationCooldown;

        public string ProviderKey => "PSN";

        public bool IsAuthenticated => File.Exists(_tokenPath);

        public PsnSessionManager(
            IPlayniteAPI api,
            ILogger logger,
            string pluginUserDataPath)
            : this(
                api ?? throw new ArgumentNullException(nameof(api)),
                logger,
                pluginUserDataPath,
                api?.Paths?.ExtensionsDataPath,
                null)
        {
        }

        internal PsnSessionManager(
            IPlayniteAPI api,
            ILogger logger,
            string pluginUserDataPath,
            string extensionsDataPath,
            PsnRuntimeHooks hooks = null)
        {
            if (string.IsNullOrWhiteSpace(pluginUserDataPath))
            {
                throw new ArgumentException("Plugin user data path is required.", nameof(pluginUserDataPath));
            }

            _api = api;
            _logger = logger;
            _hooks = hooks ?? new PsnRuntimeHooks();
            _tokenPath = Path.Combine(pluginUserDataPath, "psn", "cookies.bin");
            _legacyTokenPath = string.IsNullOrWhiteSpace(extensionsDataPath)
                ? string.Empty
                : Path.Combine(extensionsDataPath, "PlayniteAchievements", "psn_cookies.bin");

            MigrateTokenFile();
        }

        public Task<AuthProbeResult> ProbeAuthStateAsync(CancellationToken ct)
        {
            return RunAuthCheckAsync(AuthMode.PassiveProbe, ct, "PassiveProbe");
        }

        internal async Task<AuthProbeResult> ValidateNpssoAsync(
            CancellationToken ct,
            string triggerSource = "ExplicitValidation")
        {
            var npsso = GetConfiguredNpsso();
            if (string.IsNullOrWhiteSpace(npsso))
            {
                ResetNpssoValidationState();
                return AuthProbeResult.NotAuthenticated();
            }

            if (TryGetValidationCooldown(npsso, out var cachedResult))
            {
                _logger?.Info($"[PSNAuth] Validation cooldown reused for trigger={triggerSource}.");
                return cachedResult;
            }

            var result = await RunAuthCheckAsync(
                AuthMode.ExplicitValidation,
                ct,
                triggerSource ?? "ExplicitValidation").ConfigureAwait(false);

            if (result.IsSuccess)
            {
                ResetNpssoValidationState();
            }
            else
            {
                StoreValidationCooldown(npsso, result);
            }

            return result;
        }

        public async Task<AuthProbeResult> AuthenticateInteractiveAsync(
            bool forceInteractive,
            CancellationToken ct,
            IProgress<AuthProgressStep> progress = null)
        {
            var windowOpened = false;
            var sessionStateVersion = CaptureSessionStateVersion();

            try
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(AuthProgressStep.CheckingExistingSession);

                if (!forceInteractive)
                {
                    var existing = await ProbeAuthStateAsync(ct).ConfigureAwait(false);
                    if (existing.IsSuccess)
                    {
                        progress?.Report(AuthProgressStep.Completed);
                        return existing;
                    }
                }
                else
                {
                    ClearSession();
                    sessionStateVersion = CaptureSessionStateVersion();
                }

                progress?.Report(AuthProgressStep.OpeningLoginWindow);
                windowOpened = true;

                var cookies = await LoginInteractiveAsync(ct).ConfigureAwait(false);
                if (!WriteCookiesToDisk(cookies, sessionStateVersion))
                {
                    progress?.Report(AuthProgressStep.Failed);
                    return AuthProbeResult.Cancelled(windowOpened);
                }

                ResetMobileTokenState();
                progress?.Report(AuthProgressStep.WaitingForUserLogin);
                var deadlineUtc = UtcNow().Add(InteractiveAuthTimeout);

                while (UtcNow() < deadlineUtc)
                {
                    ct.ThrowIfCancellationRequested();

                    var result = await ProbeAuthStateAsync(ct).ConfigureAwait(false);
                    if (result.IsSuccess)
                    {
                        progress?.Report(AuthProgressStep.Completed);
                        return AuthProbeResult.Authenticated(windowOpened: true);
                    }

                    if (result.Outcome == AuthOutcome.TimedOut ||
                        result.Outcome == AuthOutcome.ProbeFailed)
                    {
                        progress?.Report(AuthProgressStep.Failed);
                        return WithWindowOpened(result);
                    }

                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }

                progress?.Report(AuthProgressStep.Failed);
                return AuthProbeResult.TimedOut(windowOpened: true);
            }
            catch (OperationCanceledException)
            {
                return AuthProbeResult.TimedOut(windowOpened);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[PSNAuth] Interactive auth failed.");
                progress?.Report(AuthProgressStep.Failed);
                return AuthProbeResult.Failed(windowOpened);
            }
        }

        public void ClearSession()
        {
            _logger?.Info("[PSNAuth] Clearing session.");
            Interlocked.Increment(ref _sessionStateVersion);
            ResetMobileTokenState();
            ResetNpssoValidationState();

            try
            {
                ClearBrowserCookies();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[PSNAuth] Failed to clear PSN cookies.");
            }

            try
            {
                if (File.Exists(_tokenPath))
                {
                    File.Delete(_tokenPath);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[PSNAuth] Failed to delete token file.");
            }
        }

        public void InvalidateAccessToken()
        {
            _logger?.Debug("[PSNAuth] Invalidating access token.");
            ResetMobileTokenState();
        }

        internal void ResetNpssoValidationState()
        {
            lock (_validationLock)
            {
                _validationCooldown = null;
            }
        }

        public async Task<string> GetAccessTokenAsync(CancellationToken ct, bool forceRefresh = false)
        {
            using (PerfScope.Start(_logger, "PSN.GetAccessTokenAsync", thresholdMs: 50))
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    await _authGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var sessionStateVersion = CaptureSessionStateVersion();
                        if (!forceRefresh && HasValidMobileToken() && IsSessionStateCurrent(sessionStateVersion))
                        {
                            return _mobileToken.access_token;
                        }

                        var sessionResult = await EnsureSessionAsync(
                            AuthMode.ProviderAccess,
                            ct,
                            forceRefresh ? "ProviderAccess.ForceRefresh" : "ProviderAccess",
                            sessionStateVersion).ConfigureAwait(false);
                        if (!sessionResult.IsSuccess)
                        {
                            ResetMobileTokenState();
                            throw new PsnAuthRequiredException("PlayStation authentication required. Please login.");
                        }

                        if (!forceRefresh && HasValidMobileToken() && IsSessionStateCurrent(sessionStateVersion))
                        {
                            return _mobileToken.access_token;
                        }

                        var cookieContainer = ReadCookiesFromDisk();
                        if (cookieContainer.Count == 0)
                        {
                            ResetMobileTokenState();
                            throw new PsnAuthRequiredException("PlayStation authentication required. Please login.");
                        }

                        var mobileToken = await RequestMobileTokensAsync(cookieContainer, ct).ConfigureAwait(false);
                        if (mobileToken == null || string.IsNullOrWhiteSpace(mobileToken.access_token))
                        {
                            ResetMobileTokenState();
                            throw new PsnAuthRequiredException("PlayStation authentication required. Please login.");
                        }

                        if (!IsSessionStateCurrent(sessionStateVersion))
                        {
                            ResetMobileTokenState();
                            throw new PsnAuthRequiredException("PlayStation authentication required. Please login.");
                        }

                        SetMobileToken(mobileToken);
                        if (!IsSessionStateCurrent(sessionStateVersion))
                        {
                            ResetMobileTokenState();
                            throw new PsnAuthRequiredException("PlayStation authentication required. Please login.");
                        }

                        return mobileToken.access_token;
                    }
                    finally
                    {
                        _authGate.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (PsnAuthRequiredException)
                {
                    throw;
                }
                catch (PsnTransientAuthException ex)
                {
                    _logger?.Warn($"[PSNAuth] Provider access failed temporarily: {ex.Message}");
                    throw new PsnAuthRequiredException("PlayStation authentication required. Please login.");
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "[PSNAuth] Failed to get PSN access token.");
                    throw new PsnAuthRequiredException("PlayStation authentication required. Please login.");
                }
            }
        }

        private async Task<AuthProbeResult> RunAuthCheckAsync(AuthMode mode, CancellationToken ct, string triggerSource)
        {
            using (PerfScope.Start(_logger, "PSN.RunAuthCheckAsync", thresholdMs: 50))
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    await _authGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var result = await EnsureSessionAsync(
                            mode,
                            ct,
                            triggerSource,
                            CaptureSessionStateVersion()).ConfigureAwait(false);
                        _logger?.Info($"[PSNAuth] Completed trigger={triggerSource}, mode={mode}, outcome={result.Outcome}.");
                        return result;
                    }
                    finally
                    {
                        _authGate.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    return AuthProbeResult.Cancelled();
                }
                catch (PsnTransientAuthException ex)
                {
                    _logger?.Warn($"[PSNAuth] Transient failure during {triggerSource}: {ex.Message}");
                    return ex.IsTimeout
                        ? AuthProbeResult.TimedOut()
                        : AuthProbeResult.ProbeFailed();
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, $"[PSNAuth] Authentication failed during {triggerSource}.");
                    return AuthProbeResult.ProbeFailed();
                }
            }
        }

        private async Task<AuthProbeResult> EnsureSessionAsync(
            AuthMode mode,
            CancellationToken ct,
            string triggerSource,
            int sessionStateVersion)
        {
            var npsso = GetConfiguredNpsso();
            var allowBootstrap = mode != AuthMode.PassiveProbe && !string.IsNullOrWhiteSpace(npsso);

            _logger?.Info(
                $"[PSNAuth] Session check trigger={triggerSource}, mode={mode}, hasCookieFile={File.Exists(_tokenPath)}, allowBootstrap={allowBootstrap}.");

            if (mode == AuthMode.ExplicitValidation)
            {
                return allowBootstrap
                    ? await BootstrapAndProbeAsync(npsso, ct, sessionStateVersion).ConfigureAwait(false)
                    : AuthProbeResult.NotAuthenticated();
            }

            if (!IsSessionStateCurrent(sessionStateVersion))
            {
                return AuthProbeResult.NotAuthenticated();
            }

            var cookieContainer = ReadCookiesFromDisk();
            if (cookieContainer.Count == 0)
            {
                if (mode == AuthMode.PassiveProbe)
                {
                    return AuthProbeResult.NotAuthenticated();
                }

                return allowBootstrap
                    ? await BootstrapAndProbeAsync(npsso, ct, sessionStateVersion).ConfigureAwait(false)
                    : AuthProbeResult.NotAuthenticated();
            }

            if (await ProbeSessionAsync(cookieContainer, ct).ConfigureAwait(false))
            {
                if (!IsSessionStateCurrent(sessionStateVersion))
                {
                    return AuthProbeResult.NotAuthenticated();
                }

                // The store cookies can outlive Sony's account sign-in, which every trophy request's
                // token comes from, so a session that cannot mint a token is not signed in.
                if (HasValidMobileToken() || await TryMintMobileTokenAsync(cookieContainer, sessionStateVersion, ct).ConfigureAwait(false))
                {
                    return AuthProbeResult.AlreadyAuthenticated();
                }

                _logger?.Info("[PSNAuth] Store session is valid but the account sign-in has expired.");
                return allowBootstrap
                    ? await BootstrapAndProbeAsync(npsso, ct, sessionStateVersion).ConfigureAwait(false)
                    : AuthProbeResult.NotAuthenticated();
            }

            return allowBootstrap
                ? await BootstrapAndProbeAsync(npsso, ct, sessionStateVersion).ConfigureAwait(false)
                : AuthProbeResult.NotAuthenticated();
        }

        private async Task<bool> TryMintMobileTokenAsync(
            CookieContainer cookieContainer,
            int sessionStateVersion,
            CancellationToken ct)
        {
            var mobileToken = await RequestMobileTokensAsync(cookieContainer, ct).ConfigureAwait(false);
            if (mobileToken == null || string.IsNullOrWhiteSpace(mobileToken.access_token) ||
                !IsSessionStateCurrent(sessionStateVersion))
            {
                return false;
            }

            SetMobileToken(mobileToken);
            return true;
        }

        private async Task<AuthProbeResult> BootstrapAndProbeAsync(
            string npsso,
            CancellationToken ct,
            int sessionStateVersion)
        {
            ct.ThrowIfCancellationRequested();
            var bootstrap = await BootstrapFromNpssoAsync(npsso, ct).ConfigureAwait(false);
            if (!bootstrap.IsSuccess)
            {
                return CreateProbeResult(bootstrap.Outcome);
            }

            ct.ThrowIfCancellationRequested();
            if (!WriteCookiesToDisk(bootstrap.Cookies, sessionStateVersion))
            {
                return IsSessionStateCurrent(sessionStateVersion)
                    ? AuthProbeResult.ProbeFailed()
                    : AuthProbeResult.NotAuthenticated();
            }

            ct.ThrowIfCancellationRequested();
            if (!IsSessionStateCurrent(sessionStateVersion))
            {
                return AuthProbeResult.NotAuthenticated();
            }

            ResetMobileTokenState();
            var isAuthenticated = await ProbeSessionAsync(bootstrap.Cookies, ct).ConfigureAwait(false);
            if (!IsSessionStateCurrent(sessionStateVersion))
            {
                return AuthProbeResult.NotAuthenticated();
            }

            return isAuthenticated
                ? AuthProbeResult.Authenticated()
                : AuthProbeResult.NotAuthenticated();
        }

        private async Task<PsnBootstrapAttempt> BootstrapFromNpssoAsync(string npsso, CancellationToken ct)
        {
            if (_hooks.BootstrapFromNpssoAsync != null)
            {
                return await _hooks.BootstrapFromNpssoAsync(npsso, ct).ConfigureAwait(false);
            }

            if (_api == null)
            {
                return new PsnBootstrapAttempt(AuthOutcome.ProbeFailed);
            }

            return await InvokeOnUiThreadAsync(async () =>
            {
                using (var view = _api.WebViews.CreateOffscreenView())
                {
                    ClearSonyCookies(view);

                    var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    EventHandler<Playnite.SDK.Events.WebViewLoadingChangedEventArgs> loadingChanged = (s, e) =>
                    {
                        var address = SafeGetCurrentAddress(view);
                        if (address.StartsWith("https://www.playstation.com/", StringComparison.OrdinalIgnoreCase))
                        {
                            completion.TrySetResult(address);
                        }
                    };

                    using (ct.Register(() => completion.TrySetCanceled()))
                    {
                        view.LoadingChanged += loadingChanged;
                        try
                        {
                            view.SetCookies("https://ca.account.sony.com", new HttpCookie
                            {
                                Domain = "ca.account.sony.com",
                                Name = "npsso",
                                Path = "/",
                                Value = npsso
                            });
                            view.Navigate(LoginUrl);

                            var completed = await Task.WhenAny(
                                completion.Task,
                                Task.Delay(NpssoNavigationTimeout)).ConfigureAwait(false);
                            if (completed != completion.Task)
                            {
                                var finalAddress = SafeGetCurrentAddress(view);
                                return LooksLikeAuthFailureAddress(finalAddress)
                                    ? new PsnBootstrapAttempt(AuthOutcome.NotAuthenticated, finalAddress: finalAddress)
                                    : new PsnBootstrapAttempt(AuthOutcome.TimedOut, finalAddress: finalAddress);
                            }

                            var address = await completion.Task.ConfigureAwait(false);
                            var cookies = BuildCookieContainer(view.GetCookies());
                            return cookies.Count > 0
                                ? new PsnBootstrapAttempt(AuthOutcome.Authenticated, cookies, address)
                                : new PsnBootstrapAttempt(AuthOutcome.NotAuthenticated, finalAddress: address);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger?.Error(ex, "[PSNAuth] NPSSO bootstrap failed.");
                            return new PsnBootstrapAttempt(
                                AuthOutcome.ProbeFailed,
                                finalAddress: SafeGetCurrentAddress(view));
                        }
                        finally
                        {
                            view.LoadingChanged -= loadingChanged;
                        }
                    }
                }
            }).ConfigureAwait(false);
        }

        private void ClearBrowserCookies()
        {
            if (_hooks.ClearBrowserCookies != null)
            {
                _hooks.ClearBrowserCookies();
                return;
            }

            if (_api == null)
            {
                return;
            }

            InvokeOnUiThread(() =>
            {
                using (var view = _api.WebViews.CreateOffscreenView())
                {
                    ClearSonyCookies(view);
                }
            });
        }

        private async Task<CookieContainer> LoginInteractiveAsync(CancellationToken ct)
        {
            if (_hooks.LoginInteractiveAsync != null)
            {
                return await _hooks.LoginInteractiveAsync(ct).ConfigureAwait(false);
            }

            if (_api == null)
            {
                return null;
            }

            return await InvokeOnUiThreadAsync(() =>
            {
                ct.ThrowIfCancellationRequested();

                var loggedIn = false;
                var settings = new WebViewSettings
                {
                    WindowHeight = 700,
                    WindowWidth = 580,
                    UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/108.0.0.0 Safari/537.36"
                };

                using (var view = _api.WebViews.CreateView(settings))
                {
                    EventHandler<Playnite.SDK.Events.WebViewLoadingChangedEventArgs> loadingChanged = (s, e) =>
                    {
                        var address = SafeGetCurrentAddress(view);
                        if (address.StartsWith("https://www.playstation.com/", StringComparison.OrdinalIgnoreCase))
                        {
                            loggedIn = true;
                            view.Close();
                        }
                    };

                    view.LoadingChanged += loadingChanged;
                    try
                    {
                        ClearSonyCookies(view);
                        view.Navigate(LoginUrl);
                        view.OpenDialog();
                        return loggedIn ? BuildCookieContainer(view.GetCookies()) : null;
                    }
                    finally
                    {
                        view.LoadingChanged -= loadingChanged;
                    }
                }
            }).ConfigureAwait(false);
        }

        private async Task<bool> ProbeSessionAsync(CookieContainer cookieContainer, CancellationToken ct)
        {
            if (_hooks.ProbeSessionAsync != null)
            {
                return await _hooks.ProbeSessionAsync(cookieContainer, ct).ConfigureAwait(false);
            }

            try
            {
                using (var handler = new HttpClientHandler { CookieContainer = cookieContainer })
                using (var httpClient = new HttpClient(handler))
                {
                    httpClient.DefaultRequestHeaders.TryAddWithoutValidation("x-apollo-operation-name", "pn_psn");
                    httpClient.Timeout = GameListProbeTimeout;

                    using (var response = await httpClient.GetAsync(GameListProbeUrl, ct).ConfigureAwait(false))
                    {
                        if (IsTransientStatus(response.StatusCode))
                        {
                            throw new PsnTransientAuthException(
                                "PSN session probe returned a transient status.",
                                isTimeout: false);
                        }

                        return response.IsSuccessStatusCode;
                    }
                }
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new PsnTransientAuthException("PSN session probe timed out.", ex, isTimeout: true);
            }
            catch (HttpRequestException ex)
            {
                throw new PsnTransientAuthException("PSN session probe failed.", ex, isTimeout: false);
            }
        }

        private async Task<MobileTokens> RequestMobileTokensAsync(CookieContainer cookieContainer, CancellationToken ct)
        {
            if (_hooks.RequestMobileTokensAsync != null)
            {
                return await _hooks.RequestMobileTokensAsync(cookieContainer, ct).ConfigureAwait(false);
            }

            using (var handler = new HttpClientHandler { CookieContainer = cookieContainer, AllowAutoRedirect = false })
            using (var httpClient = new HttpClient(handler))
            {
                httpClient.Timeout = MobileRequestTimeout;

                string mobileCode;
                try
                {
                    using (var response = await httpClient.GetAsync(MobileCodeUrl, ct).ConfigureAwait(false))
                    {
                        if (response.StatusCode != HttpStatusCode.Redirect)
                        {
                            if (IsTransientStatus(response.StatusCode))
                            {
                                throw new PsnTransientAuthException(
                                    "PSN mobile code request returned a transient status.",
                                    isTimeout: false);
                            }

                            _logger?.Debug($"[PSNAuth] Mobile code request returned {(int)response.StatusCode} instead of a redirect.");
                            return null;
                        }

                        var location = response.Headers.Location;
                        mobileCode = location == null ? null : GetQueryParam(location.Query, "code");
                        if (string.IsNullOrWhiteSpace(mobileCode))
                        {
                            // Sony redirects with an error instead of a code when its sign-in has lapsed.
                            _logger?.Debug($"[PSNAuth] Mobile code redirect carried no code (error={GetQueryParam(location?.Query, "error") ?? "none"}).");
                            return null;
                        }
                    }
                }
                catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    throw new PsnTransientAuthException("PSN mobile code request timed out.", ex, isTimeout: true);
                }
                catch (HttpRequestException ex)
                {
                    throw new PsnTransientAuthException("PSN mobile code request failed.", ex, isTimeout: false);
                }

                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Post, MobileTokenUrl))
                    {
                        request.Content = new FormUrlEncodedContent(new[]
                        {
                            new KeyValuePair<string, string>("code", mobileCode),
                            new KeyValuePair<string, string>("redirect_uri", "com.scee.psxandroid.scecompcall://redirect"),
                            new KeyValuePair<string, string>("grant_type", "authorization_code"),
                            new KeyValuePair<string, string>("token_format", "jwt")
                        });
                        request.Headers.Authorization =
                            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", MobileTokenAuth);

                        using (var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false))
                        {
                            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (!response.IsSuccessStatusCode)
                            {
                                if (IsTransientStatus(response.StatusCode))
                                {
                                    throw new PsnTransientAuthException(
                                        "PSN token request returned a transient status.",
                                        isTimeout: false);
                                }

                                _logger?.Debug($"[PSNAuth] Token request failed: {response.StatusCode} - {content}");
                                return null;
                            }

                            return Newtonsoft.Json.JsonConvert.DeserializeObject<MobileTokens>(content);
                        }
                    }
                }
                catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    throw new PsnTransientAuthException("PSN token request timed out.", ex, isTimeout: true);
                }
                catch (HttpRequestException ex)
                {
                    throw new PsnTransientAuthException("PSN token request failed.", ex, isTimeout: false);
                }
            }
        }

        private void MigrateTokenFile()
        {
            if (string.IsNullOrWhiteSpace(_legacyTokenPath) ||
                !File.Exists(_legacyTokenPath) ||
                File.Exists(_tokenPath))
            {
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(_tokenPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.Move(_legacyTokenPath, _tokenPath);

                var oldDir = Path.GetDirectoryName(_legacyTokenPath);
                if (Directory.Exists(oldDir) && Directory.GetFiles(oldDir).Length == 0)
                {
                    Directory.Delete(oldDir);
                }

                _logger?.Info("[PSNAuth] Migrated token file to new location.");
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[PSNAuth] Failed to migrate token file.");
            }
        }

        private string GetConfiguredNpsso()
        {
            return (ProviderRegistry.Settings<PsnSettings>()?.Npsso ?? string.Empty).Trim();
        }

        private int CaptureSessionStateVersion()
        {
            return Volatile.Read(ref _sessionStateVersion);
        }

        private bool IsSessionStateCurrent(int sessionStateVersion)
        {
            return sessionStateVersion == Volatile.Read(ref _sessionStateVersion);
        }

        private bool TryGetValidationCooldown(string npsso, out AuthProbeResult result)
        {
            lock (_validationLock)
            {
                var now = UtcNow();
                if (_validationCooldown != null && now >= _validationCooldown.UntilUtc)
                {
                    _validationCooldown = null;
                }

                if (_validationCooldown != null &&
                    string.Equals(_validationCooldown.Npsso, npsso, StringComparison.Ordinal))
                {
                    result = _validationCooldown.Result;
                    return true;
                }

                result = null;
                return false;
            }
        }

        private void StoreValidationCooldown(string npsso, AuthProbeResult result)
        {
            if (string.IsNullOrWhiteSpace(npsso) || result == null || result.IsSuccess)
            {
                return;
            }

            lock (_validationLock)
            {
                _validationCooldown = new ValidationCooldown
                {
                    Npsso = npsso,
                    Result = result,
                    UntilUtc = UtcNow().Add(NpssoFailureCooldown)
                };
            }
        }

        private static AuthProbeResult CreateProbeResult(AuthOutcome outcome)
        {
            switch (outcome)
            {
                case AuthOutcome.Authenticated:
                    return AuthProbeResult.Authenticated();
                case AuthOutcome.AlreadyAuthenticated:
                    return AuthProbeResult.AlreadyAuthenticated();
                case AuthOutcome.TimedOut:
                    return AuthProbeResult.TimedOut();
                case AuthOutcome.Cancelled:
                    return AuthProbeResult.Cancelled();
                case AuthOutcome.Failed:
                    return AuthProbeResult.Failed();
                case AuthOutcome.ProbeFailed:
                    return AuthProbeResult.ProbeFailed();
                default:
                    return AuthProbeResult.NotAuthenticated();
            }
        }

        private static AuthProbeResult WithWindowOpened(AuthProbeResult result)
        {
            switch (result?.Outcome)
            {
                case AuthOutcome.Authenticated:
                    return AuthProbeResult.Authenticated(windowOpened: true);
                case AuthOutcome.AlreadyAuthenticated:
                    return AuthProbeResult.AlreadyAuthenticated();
                case AuthOutcome.TimedOut:
                    return AuthProbeResult.TimedOut(windowOpened: true);
                case AuthOutcome.Cancelled:
                    return AuthProbeResult.Cancelled(windowOpened: true);
                case AuthOutcome.Failed:
                    return AuthProbeResult.Failed(windowOpened: true);
                case AuthOutcome.ProbeFailed:
                    return AuthProbeResult.ProbeFailed();
                default:
                    return AuthProbeResult.NotAuthenticated();
            }
        }

        private CookieContainer ReadCookiesFromDisk()
        {
            try
            {
                if (!File.Exists(_tokenPath))
                {
                    return new CookieContainer();
                }

                using (var stream = File.Open(_tokenPath, FileMode.Open))
                {
                    var formatter = new BinaryFormatter();
                    return (CookieContainer)formatter.Deserialize(stream);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[PSNAuth] Failed to read cookies from disk.");
                return new CookieContainer();
            }
        }

        private bool WriteCookiesToDisk(CookieContainer cookieJar, int sessionStateVersion)
        {
            if (cookieJar == null ||
                cookieJar.Count == 0 ||
                !IsSessionStateCurrent(sessionStateVersion))
            {
                return false;
            }

            try
            {
                var directory = Path.GetDirectoryName(_tokenPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (File.Exists(_tokenPath))
                {
                    File.Delete(_tokenPath);
                }

                using (var stream = File.Create(_tokenPath))
                {
                    var formatter = new BinaryFormatter();
                    formatter.Serialize(stream, cookieJar);
                }

                if (!IsSessionStateCurrent(sessionStateVersion))
                {
                    TryDeleteTokenFile();
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[PSNAuth] Failed to write cookies to disk.");
                return false;
            }
        }

        private void TryDeleteTokenFile()
        {
            try
            {
                if (File.Exists(_tokenPath))
                {
                    File.Delete(_tokenPath);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[PSNAuth] Failed to delete token file.");
            }
        }

        private void SetMobileToken(MobileTokens mobileToken)
        {
            _mobileToken = mobileToken;
            _mobileTokenAcquiredUtc = UtcNow();

            var expiresInSeconds = Math.Max(0, mobileToken.expires_in);
            _mobileTokenExpiryUtc = expiresInSeconds > 0
                ? _mobileTokenAcquiredUtc.AddSeconds(expiresInSeconds)
                : _mobileTokenAcquiredUtc;
        }

        private bool HasValidMobileToken()
        {
            if (_mobileToken == null ||
                string.IsNullOrWhiteSpace(_mobileToken.access_token) ||
                _mobileTokenExpiryUtc == DateTime.MinValue)
            {
                return false;
            }

            var tokenLifetime = _mobileTokenExpiryUtc - _mobileTokenAcquiredUtc;
            var buffer = tokenLifetime > MobileTokenExpiryBuffer
                ? MobileTokenExpiryBuffer
                : TimeSpan.Zero;
            return UtcNow() < _mobileTokenExpiryUtc - buffer;
        }

        private void ResetMobileTokenState()
        {
            _mobileToken = null;
            _mobileTokenAcquiredUtc = DateTime.MinValue;
            _mobileTokenExpiryUtc = DateTime.MinValue;
        }

        private Task<T> InvokeOnUiThreadAsync<T>(Func<Task<T>> action)
        {
            var dispatcher = _api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            if (dispatcher.CheckAccess())
            {
                return action();
            }

            return dispatcher.InvokeAsync(action).Task.Unwrap();
        }

        private Task<T> InvokeOnUiThreadAsync<T>(Func<T> action)
        {
            var dispatcher = _api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            if (dispatcher.CheckAccess())
            {
                return Task.FromResult(action());
            }

            return dispatcher.InvokeAsync(action).Task;
        }

        private void InvokeOnUiThread(Action action)
        {
            var dispatcher = _api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            if (dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.Invoke(action);
        }

        private DateTime UtcNow()
        {
            return _hooks.UtcNow?.Invoke() ?? DateTime.UtcNow;
        }

        private static CookieContainer BuildCookieContainer(List<HttpCookie> cookies)
        {
            var cookieContainer = new CookieContainer();
            foreach (var cookie in cookies ?? new List<HttpCookie>())
            {
                try
                {
                    if (cookie.Domain == ".playstation.com")
                    {
                        cookieContainer.Add(
                            new Uri("https://web.np.playstation.com"),
                            new Cookie(cookie.Name, cookie.Value));
                    }

                    if (cookie.Domain == ".ca.account.sony.com" || cookie.Domain == "ca.account.sony.com")
                    {
                        cookieContainer.Add(
                            new Uri("https://ca.account.sony.com"),
                            new Cookie(cookie.Name, cookie.Value));
                    }

                    if (cookie.Domain == ".sony.com")
                    {
                        cookieContainer.Add(
                            new Uri("https://ca.account.sony.com"),
                            new Cookie(cookie.Name, cookie.Value));
                    }
                }
                catch
                {
                }
            }

            return cookieContainer;
        }

        private static void ClearSonyCookies(IWebView view)
        {
            view.DeleteDomainCookies(".sony.com");
            view.DeleteDomainCookies(".ca.account.sony.com");
            view.DeleteDomainCookies("ca.account.sony.com");
            view.DeleteDomainCookies(".playstation.com");
            view.DeleteDomainCookies("io.playstation.com");
        }

        private static string SafeGetCurrentAddress(IWebView view)
        {
            try
            {
                return view.GetCurrentAddress() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool LooksLikeAuthFailureAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return false;
            }

            return address.IndexOf("account.sony.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   address.IndexOf("/signin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   address.IndexOf("login", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsTransientStatus(HttpStatusCode statusCode)
        {
            var numeric = (int)statusCode;
            return statusCode == (HttpStatusCode)429 || numeric >= 500;
        }

        private static string GetQueryParam(string query, string key)
        {
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            if (query.StartsWith("?", StringComparison.Ordinal))
            {
                query = query.Substring(1);
            }

            var pairs = query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                var parts = pair.Split(new[] { '=' }, 2);
                if (parts.Length == 2 && string.Equals(parts[0], key, StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(parts[1]);
                }
            }

            return null;
        }
    }
}
