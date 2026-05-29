using PlayniteAchievements.Providers.Steam.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Common;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Data;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.Steam
{
    /// <summary>
    /// Steam session manager that probes authentication state from CEF cookies.
    /// Auth state is never cached in memory - always probed from the source of truth.
    /// </summary>
    public sealed class SteamSessionManager : ISessionManager
    {
        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;

        private const string CommunityEditInfoUrl = "https://steamcommunity.com/my/edit/info";

        // Temporary state for interactive login dialog coordination
        private SteamWebAuthSession _authResult;
        private IWebView _interactiveLoginView;
        private Action _clearInMemoryAuthState;

        public string ProviderKey => "Steam";

        /// <summary>
        /// Snapshot of the last successful probe.
        /// ProbeAuthStateAsync remains the authoritative auth check.
        /// </summary>
        public bool IsAuthenticated =>
            !string.IsNullOrWhiteSpace(ProviderRegistry.Settings<SteamSettings>().SteamUserId);

        public SteamSessionManager(
            IPlayniteAPI api,
            ILogger logger)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        internal void SetClearInMemoryAuthState(Action clearInMemoryAuthState)
        {
            _clearInMemoryAuthState = clearInMemoryAuthState;
        }

        private void PersistSteamUserId(string steamId)
        {
            var normalizedSteamId = string.IsNullOrWhiteSpace(steamId)
                ? null
                : steamId.Trim();
            var steamSettings = ProviderRegistry.Settings<SteamSettings>();

            if (string.Equals(steamSettings.SteamUserId, normalizedSteamId, StringComparison.Ordinal))
            {
                return;
            }

            steamSettings.SteamUserId = normalizedSteamId;
            ProviderRegistry.Write(steamSettings, persistToDisk: true);
        }

        // ---------------------------------------------------------------------
        // ISessionManager Implementation
        // ---------------------------------------------------------------------

        public async Task<AuthProbeResult> ProbeAuthStateAsync(CancellationToken ct)
        {
            using (PerfScope.Start(_logger, "Steam.ProbeAuthStateAsync", thresholdMs: 50))
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    var session = await ResolveWebAuthSessionAsync(ct).ConfigureAwait(false);
                    if (session?.IsComplete == true)
                    {
                        PersistSteamUserId(session.SteamId64);
                        return AuthProbeResult.AlreadyAuthenticated(session.SteamId64);
                    }

                    if (!string.IsNullOrWhiteSpace(session?.SteamId64))
                    {
                        PersistSteamUserId(session.SteamId64);
                    }
                    else if (session?.HasSteamSessionCookies != true)
                    {
                        PersistSteamUserId(null);
                    }

                    return AuthProbeResult.NotAuthenticated();
                }
                catch (OperationCanceledException)
                {
                    return AuthProbeResult.Cancelled();
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "[SteamAuth] Probe failed with exception.");
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

                var loginTcs = new TaskCompletionSource<SteamWebAuthSession>(TaskCreationOptions.RunContinuationsAsynchronously);

                _ = _api.MainView.UIDispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var result = LoginInteractively();
                        loginTcs.TrySetResult(result ?? SteamWebAuthSession.Empty());
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
                    Task.Delay(TimeSpan.FromMinutes(3), ct)).ConfigureAwait(false);

                if (completed != loginTcs.Task)
                {
                    CloseInteractiveLoginView();
                    await Task.WhenAny(loginTcs.Task, Task.Delay(3000)).ConfigureAwait(false);
                    progress?.Report(AuthProgressStep.Failed);
                    return AuthProbeResult.TimedOut(windowOpened);
                }

                var session = await loginTcs.Task.ConfigureAwait(false);

                progress?.Report(AuthProgressStep.VerifyingSession);
                if (session?.IsComplete != true)
                {
                    // Fallback: dialog may have been manually closed after successful login
                    session = await ResolveWebAuthSessionAsync(ct).ConfigureAwait(false);
                }

                if (session?.IsComplete != true)
                {
                    if (!string.IsNullOrWhiteSpace(session?.SteamId64))
                    {
                        PersistSteamUserId(session.SteamId64);
                    }

                    progress?.Report(AuthProgressStep.Failed);
                    return AuthProbeResult.Cancelled(windowOpened);
                }

                PersistSteamUserId(session.SteamId64);
                progress?.Report(AuthProgressStep.Completed);

                return AuthProbeResult.Authenticated(session.SteamId64, windowOpened: windowOpened);
            }
            catch (OperationCanceledException)
            {
                progress?.Report(AuthProgressStep.Failed);
                return AuthProbeResult.TimedOut(windowOpened);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[SteamAuth] Interactive authentication failed.");
                progress?.Report(AuthProgressStep.Failed);
                return AuthProbeResult.Failed(windowOpened);
            }
        }

        /// <summary>
        /// Clears the session by clearing Steam cookies from CEF.
        /// </summary>
        public void ClearSession()
        {
            _authResult = null;
            CloseInteractiveLoginView();
            _clearInMemoryAuthState?.Invoke();
            ClearSteamCookiesFromCef(_api, _logger);
            PersistSteamUserId(null);
        }

        // ---------------------------------------------------------------------
        // Internal methods for SteamHttpClient
        // ---------------------------------------------------------------------

        /// <summary>
        /// Gets a Steam page using CEF WebView.
        /// </summary>
        public async Task<(string FinalUrl, string Html)> GetSteamPageAsyncCef(string url, CancellationToken ct)
        {
            return await InvokeOnUiAsync(async () =>
            {
                var finalUrl = url;
                var html = string.Empty;
                try
                {
                    ct.ThrowIfCancellationRequested();
                    using (var view = _api.WebViews.CreateOffscreenView())
                    {
                        await view.NavigateAndWaitAsync(url, timeoutMs: 15000);
                        finalUrl = view.GetCurrentAddress();

                        // Wait for JavaScript to render dynamic content
                        await Task.Delay(2000, ct);

                        html = await view.GetPageSourceAsync();
                    }
                }
                catch (TimeoutException ex)
                {
                    _logger?.Warn(ex, $"Offscreen navigation timed out for {url}");
                    html = string.Empty;
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, $"Offscreen navigation failed for {url}");
                    html = string.Empty;
                }

                return (finalUrl, html);
            }).ConfigureAwait(false);
        }

        // ---------------------------------------------------------------------
        // Private Helper Methods
        // ---------------------------------------------------------------------

        private Task<T> InvokeOnUiAsync<T>(Func<Task<T>> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            var dispatcher = _api?.MainView?.UIDispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                return action();
            }

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    var result = await action().ConfigureAwait(true);
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }));

            return tcs.Task;
        }

        internal Task<SteamWebAuthSession> ResolveWebAuthSessionAsync(CancellationToken ct)
        {
            return InvokeOnUiAsync(async () =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    using (var view = _api.WebViews.CreateOffscreenView())
                    {
                        _logger?.Debug($"[SteamAuth] Navigating to {CommunityEditInfoUrl} to resolve web auth session...");
                        await view.NavigateAndWaitAsync(CommunityEditInfoUrl, timeoutMs: 15000).ConfigureAwait(true);
                        await Task.Delay(500, ct).ConfigureAwait(true);
                        return ResolveWebAuthSessionFromView(view);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, "[SteamAuth] Failed to resolve Steam web auth session.");
                    return SteamWebAuthSession.Empty();
                }
            });
        }

        private SteamWebAuthSession ResolveWebAuthSessionFromView(IWebView view)
        {
            if (view == null)
            {
                return SteamWebAuthSession.Empty();
            }

            var cookies = view.GetCookies();
            var hasSessionCookies = HasSteamSessionCookies(cookies);
            var cookieSteamId = TryExtractSteamId64FromCookies(cookies);
            string source = null;

            try
            {
                source = view.GetPageSource();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[SteamAuth] Failed to read Steam WebView page source.");
            }

            return SteamWebAuthParser.Parse(source, cookieSteamId, hasSessionCookies);
        }

        internal async Task<bool> WarmStoreSessionAsync(CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            await _api.MainView.UIDispatcher.InvokeAsync(async () =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    using (var view = _api.WebViews.CreateOffscreenView())
                    {
                        const string targetUrl = "https://store.steampowered.com/";

                        await view.NavigateAndWaitAsync(targetUrl, timeoutMs: 15000);

                        // Give the browser a moment to commit cookies and any store-side session state.
                        await Task.Delay(1000, ct);

                        var cookies = view.GetCookies();
                        var hasStoreCookies = cookies?.Any(c =>
                            c != null &&
                            !string.IsNullOrWhiteSpace(c.Domain) &&
                            IsSteamDomain(c.Domain) &&
                            c.Domain.IndexOf("steampowered.com", StringComparison.OrdinalIgnoreCase) >= 0) == true;

                        tcs.TrySetResult(hasStoreCookies);
                    }
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetCanceled();
                }
                catch (TimeoutException ex)
                {
                    _logger?.Warn(ex, "[SteamAuth] Store warmup navigation timed out.");
                    tcs.TrySetResult(false);
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, "[SteamAuth] Store warmup navigation failed.");
                    tcs.TrySetResult(false);
                }
            });

            return await tcs.Task.ConfigureAwait(false);
        }

        /// <summary>
        /// Synchronous login method matching Steam library plugin pattern.
        /// </summary>
        private SteamWebAuthSession LoginInteractively()
        {
            _authResult = null;

            IWebView view = null;
            try
            {
                view = _api.WebViews.CreateView(1000, 800);
                _interactiveLoginView = view;
                view.DeleteDomainCookies(".steamcommunity.com");
                view.DeleteDomainCookies("steamcommunity.com");
                view.DeleteDomainCookies(".store.steampowered.com");
                view.DeleteDomainCookies("store.steampowered.com");
                view.DeleteDomainCookies(".steampowered.com");
                view.DeleteDomainCookies("steampowered.com");
                view.DeleteDomainCookies(".login.steampowered.com");
                view.DeleteDomainCookies("login.steampowered.com");
                view.DeleteDomainCookies(".help.steampowered.com");
                view.DeleteDomainCookies("help.steampowered.com");

                view.LoadingChanged += CloseWhenLoggedIn;
                view.Navigate(CommunityEditInfoUrl);

                view.OpenDialog();

                return _authResult;
            }
            finally
            {
                if (view != null)
                {
                    view.LoadingChanged -= CloseWhenLoggedIn;
                    if (ReferenceEquals(_interactiveLoginView, view))
                    {
                        _interactiveLoginView = null;
                    }

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
                    return;

                await Task.Delay(250).ConfigureAwait(true);
                var session = ResolveWebAuthSessionFromView(view);
                if (session.IsComplete)
                {
                    _authResult = session;
                    view.Close();
                    return;
                }

                if (!IsCommunityEditInfoUrl(address))
                {
                    view.Navigate(CommunityEditInfoUrl);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[SteamAuth] Failed to check authentication status");
            }
        }

        private void CloseInteractiveLoginView()
        {
            var view = _interactiveLoginView;
            if (view == null)
            {
                return;
            }

            try
            {
                var dispatcher = _api?.MainView?.UIDispatcher;
                if (dispatcher == null || dispatcher.CheckAccess())
                {
                    view.Close();
                }
                else
                {
                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { view.Close(); } catch { }
                    }));
                }
            }
            catch
            {
            }
        }

        private bool IsLoginPageUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;

            return url.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0
                || url.IndexOf("openid", StringComparison.OrdinalIgnoreCase) >= 0
                || url.IndexOf("login.steampowered.com", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsCommunityEditInfoUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            return url.IndexOf("steamcommunity.com", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   url.IndexOf("/edit/info", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsSteamDomain(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain)) return false;
            var d = domain.Trim().TrimStart('.');
            return d.EndsWith("steamcommunity.com", StringComparison.OrdinalIgnoreCase) ||
                   d.EndsWith("steampowered.com", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsExpiredCookie(dynamic cookie)
        {
            try
            {
                DateTime? expires = cookie?.Expires;
                if (!expires.HasValue || expires.Value == DateTime.MinValue)
                {
                    return false;
                }

                return expires.Value.ToUniversalTime() <= DateTime.UtcNow;
            }
            catch
            {
                return false;
            }
        }

        public static string TryExtractSteamId64FromSteamLoginSecure(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(value);
            }
            catch
            {
                decoded = value;
            }

            var m = Regex.Match(decoded, @"(?<id>\d{17})");
            return m.Success ? m.Groups["id"].Value : null;
        }

        private static string TryExtractSteamId64FromCookies(IEnumerable<HttpCookie> cookies)
        {
            var authCookie = cookies?.FirstOrDefault(c =>
                c != null &&
                !string.IsNullOrWhiteSpace(c.Domain) &&
                IsSteamDomain(c.Domain) &&
                c.Name.Equals("steamLoginSecure", StringComparison.OrdinalIgnoreCase));

            return authCookie == null
                ? null
                : TryExtractSteamId64FromSteamLoginSecure(authCookie.Value);
        }

        private static bool HasSteamSessionCookies(IEnumerable<HttpCookie> cookies)
        {
            return cookies?.Any(c =>
                c != null &&
                !string.IsNullOrWhiteSpace(c.Domain) &&
                IsSteamDomain(c.Domain) &&
                SteamSessionCookieNames.Any(n => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase))) == true;
        }

        private static T InvokeOnUi<T>(IPlayniteAPI api, Func<T> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            var dispatcher = api?.MainView?.UIDispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                return action();
            }

            return dispatcher.Invoke(action);
        }

        private static void InvokeOnUi(IPlayniteAPI api, Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));

            var dispatcher = api?.MainView?.UIDispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.Invoke(action);
        }

        // ---------------------------------------------------------------------
        // Cookie Management (for SteamHttpClient)
        // ---------------------------------------------------------------------

        private static readonly string[] SteamSessionCookieNames = { "steamLoginSecure", "sessionid" };
        private static readonly Uri CommunityBase = new Uri("https://steamcommunity.com/");
        private static readonly Uri StoreBase = new Uri("https://store.steampowered.com/");

        public static bool HasSteamSessionCookies(IPlayniteAPI api, ILogger logger)
        {
            try
            {
                return InvokeOnUi(api, () =>
                {
                    using (var view = api.WebViews.CreateOffscreenView())
                    {
                        return HasSteamSessionCookies(view.GetCookies());
                    }
                });
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "Failed to check Steam session cookies.");
                return false;
            }
        }

        public static void LoadCefCookiesIntoJar(
            IPlayniteAPI api,
            ILogger logger,
            CookieContainer cookieJar)
        {
            try
            {
                InvokeOnUi(api, () =>
                {
                    using (var view = api.WebViews.CreateOffscreenView())
                    {
                        var cookies = view.GetCookies();
                        if (cookies == null)
                        {
                            return;
                        }

                        var steamCookies = cookies
                            .Where(c => c != null && !string.IsNullOrWhiteSpace(c.Domain))
                            .Where(c => IsSteamDomain(c.Domain))
                            .ToList();

                        foreach (var c in steamCookies)
                        {
                            try
                            {
                                var domain = c.Domain.TrimStart('.');
                                var path = string.IsNullOrWhiteSpace(c.Path) ? "/" : c.Path;
                                var uri = GetAddUriForDomain(domain);
                                var cookie = new Cookie(c.Name, SanitizeCookieValue(c.Value), path)
                                {
                                    Domain = uri.Host,
                                    Secure = c.Secure,
                                    HttpOnly = c.HttpOnly
                                };

                                if (c.Expires.HasValue && c.Expires.Value > DateTime.MinValue)
                                {
                                    var expires = c.Expires.Value;
                                    cookie.Expires = expires.Kind == DateTimeKind.Utc ? expires : expires.ToUniversalTime();
                                }
                                cookieJar.Add(uri, cookie);
                            }
                            catch (Exception ex)
                            {
                                logger?.Debug($"Failed to add cookie '{c.Name}' to jar: {ex.Message}");
                            }
                        }

                        AddSteamTimezoneCookie(cookieJar, logger);
                    }
                });
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "Failed to load cookies from CEF into jar");
            }
        }

        private static string SanitizeCookieValue(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            return value.Replace(",", "%2C");
        }

        private static void AddSteamTimezoneCookie(CookieContainer cookieJar, ILogger logger)
        {
            try
            {
                var tzCookie = new Cookie(
                    "timezoneOffset",
                    SanitizeCookieValue(SteamTimeParser.GetSteamTimezoneOffsetCookieValue()),
                    "/")
                {
                    Domain = CommunityBase.Host
                };
                cookieJar.Add(CommunityBase, tzCookie);
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "Failed to set timezoneOffset cookie");
            }
        }

        private static Uri GetAddUriForDomain(string cookieDomain)
        {
            var d = (cookieDomain ?? "").Trim().TrimStart('.');
            if (string.IsNullOrWhiteSpace(d))
                return CommunityBase;

            try
            {
                return new Uri("https://" + d);
            }
            catch
            {
                return CommunityBase;
            }
        }

        public static void ClearSteamCookiesFromCef(IPlayniteAPI api, ILogger logger)
        {
            try
            {
                InvokeOnUi(api, () =>
                {
                    using (var view = api.WebViews.CreateOffscreenView())
                    {
                        // Use both regex and explicit host deletion because CEF host/path handling
                        // can vary across Playnite/WebView versions.
                        view.DeleteDomainCookiesRegex(@"(^|\.)steamcommunity\.com$");
                        view.DeleteDomainCookiesRegex(@"(^|\.)steampowered\.com$");
                        view.DeleteDomainCookies("steamcommunity.com");
                        view.DeleteDomainCookies(".steamcommunity.com");
                        view.DeleteDomainCookies("steampowered.com");
                        view.DeleteDomainCookies(".steampowered.com");
                        view.DeleteDomainCookies("store.steampowered.com");
                        view.DeleteDomainCookies(".store.steampowered.com");
                        view.DeleteDomainCookies("login.steampowered.com");
                        view.DeleteDomainCookies(".login.steampowered.com");
                        // Explicit host and dotted-domain clears are more reliable across CEF cookie stores.
                        view.DeleteDomainCookies(".help.steampowered.com");
                        view.DeleteDomainCookies("api.steampowered.com");
                        view.DeleteDomainCookies(".api.steampowered.com");
                    }
                });
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "Failed to clear Steam cookies from CEF.");
            }
        }
    }
}
