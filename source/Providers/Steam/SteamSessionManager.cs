using PlayniteAchievements.Providers.Steam.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Refresh;
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
    public sealed class SteamSessionManager : ISessionManager, IRefreshAuthArtifactSource
    {
        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;

        private const string CommunityEditInfoUrl = "https://steamcommunity.com/my/edit/info";

        // Temporary state for interactive login dialog coordination
        private SteamWebAuthSession _authResult;
        private SteamWebAuthSession _lastProbeSession;
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
            _offscreenViews = new OffscreenViewLeaseSource(_api, _logger);
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
                    _lastProbeSession = session;
                    if (session?.IsComplete == true)
                    {
                        PersistSteamUserId(session.SteamId64);
                        return AuthProbeResult.AlreadyAuthenticated(session.SteamId64);
                    }

                    if (session?.IsTransientFailure == true)
                    {
                        var result = AuthProbeResult.Create(
                            session.TransientFailureOutcome.Value,
                            "LOCPlayAch_Common_NotAuthenticated",
                            userId: session.SteamId64);
                        _logger?.Warn(
                            $"[SteamAuth] Steam web auth could not be verified ({result.Outcome}); keeping persisted Steam identity unchanged.");
                        return result;
                    }

                    // Only clear the persisted ID when no session cookies remain at all.
                    // An incomplete session (ID without a web API token) must not update
                    // the persisted identity: it can carry a stale account's ID.
                    if (session != null &&
                        !session.IsTransientFailure &&
                        session.HasSteamSessionCookies != true)
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

        public object GetRefreshAuthArtifact(AuthProbeResult probeResult)
        {
            return probeResult?.IsSuccess == true
                ? _lastProbeSession
                : null;
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
        // Scan-scoped offscreen view lease
        // ---------------------------------------------------------------------

        private readonly OffscreenViewLeaseSource _offscreenViews;

        /// <summary>
        /// Holds one shared offscreen CEF view open until the returned lease is disposed.
        /// While a lease is active, page fetches, auth probes, and cookie reads reuse the
        /// shared view instead of creating and disposing a view per call, bounding CEF
        /// memory during long scans.
        /// </summary>
        public IDisposable BeginOffscreenViewLease() => _offscreenViews.BeginLease();

        /// <summary>
        /// The shared offscreen view source, for callers that fetch non-Steam pages through
        /// the same leased view (e.g. SteamHunters enrichment during a scan).
        /// </summary>
        internal OffscreenViewLeaseSource OffscreenViews => _offscreenViews;

        // ---------------------------------------------------------------------
        // Internal methods for SteamHttpClient
        // ---------------------------------------------------------------------

        /// <summary>
        /// Gets a Steam page using an offscreen CEF WebView. The modern community pages server-render
        /// their full data (owned games, achievement progress) into inline window.SSR scripts that are
        /// present as soon as navigation completes, so a single page-source read after a short settle
        /// delay captures everything - no scrolling or content-count polling is required.
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
                    await _offscreenViews.WithNavigableViewAsync(async view =>
                    {
                        await view.NavigateAndWaitAsync(url, timeoutMs: 15000);
                        finalUrl = view.GetCurrentAddress();
                        await Task.Delay(2000, ct).ConfigureAwait(true);
                        html = await view.GetPageSourceAsync().ConfigureAwait(true);
                        return true;
                    }, ct).ConfigureAwait(true);
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
                    return await _offscreenViews.WithNavigableViewAsync(async view =>
                    {
                        _logger?.Debug($"[SteamAuth] Navigating to {CommunityEditInfoUrl} to resolve web auth session...");
                        try
                        {
                            await view.NavigateAndWaitAsync(CommunityEditInfoUrl, timeoutMs: 15000).ConfigureAwait(true);
                        }
                        catch (TimeoutException ex)
                        {
                            SteamWebAuthSession session = null;
                            try
                            {
                                session = ResolveWebAuthSessionFromView(view);
                            }
                            catch (Exception inspectEx)
                            {
                                _logger?.Debug(inspectEx, "[SteamAuth] Failed to inspect timed-out Steam WebView.");
                            }

                            if (session?.IsComplete == true)
                            {
                                _logger?.Warn(
                                    "[SteamAuth] Steam WebView navigation timed out, but a complete auth session was available from the page/cookies.");
                                return session;
                            }

                            _logger?.Warn(ex, "[SteamAuth] Steam WebView navigation timed out before auth could be verified.");
                            return SteamWebAuthSession.TransientFailure(
                                AuthOutcome.TimedOut,
                                session);
                        }

                        await Task.Delay(500, ct).ConfigureAwait(true);
                        return ResolveWebAuthSessionFromView(view);
                    }, ct).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (TimeoutException ex)
                {
                    _logger?.Warn(ex, "[SteamAuth] Steam web auth session resolution timed out before the WebView could be inspected.");
                    return SteamWebAuthSession.TransientFailure(AuthOutcome.TimedOut);
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, "[SteamAuth] Failed to resolve Steam web auth session.");
                    return SteamWebAuthSession.TransientFailure(AuthOutcome.ProbeFailed);
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

            var session = SteamWebAuthParser.Parse(source, cookieSteamId, hasSessionCookies);
            ClearStaleSteamPoweredCookies(view, cookies, session.SteamId64);
            return session;
        }

        /// <summary>
        /// Deletes steampowered.com cookies whose steamLoginSecure identifies a different
        /// account than the resolved community session. Such cookies survive an account
        /// switch performed through another login flow (e.g. the Steam library extension)
        /// and would otherwise keep authenticating requests as the previous account.
        /// </summary>
        private void ClearStaleSteamPoweredCookies(
            IWebView view,
            IEnumerable<HttpCookie> cookies,
            string resolvedSteamId64)
        {
            if (view == null || cookies == null || string.IsNullOrWhiteSpace(resolvedSteamId64))
            {
                return;
            }

            var staleDomains = cookies
                .Where(c => c != null &&
                            !string.IsNullOrWhiteSpace(c.Domain) &&
                            IsSteamPoweredDomain(c.Domain) &&
                            c.Name.Equals("steamLoginSecure", StringComparison.OrdinalIgnoreCase))
                .Select(c => new
                {
                    c.Domain,
                    SteamId = TryExtractSteamId64FromSteamLoginSecure(c.Value)
                })
                .Where(c => !string.IsNullOrWhiteSpace(c.SteamId) &&
                            !string.Equals(c.SteamId, resolvedSteamId64, StringComparison.Ordinal))
                .Select(c => c.Domain.Trim().TrimStart('.'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var domain in staleDomains)
            {
                try
                {
                    _logger?.Info($"[SteamAuth] Clearing stale Steam cookies on '{domain}' (login cookie belongs to a different account than the active session).");
                    view.DeleteDomainCookies("." + domain);
                    view.DeleteDomainCookies(domain);
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"[SteamAuth] Failed to clear stale Steam cookies on '{domain}'.");
                }
            }
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

        private static bool IsSteamCommunityDomain(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain)) return false;
            var d = domain.Trim().TrimStart('.');
            return d.EndsWith("steamcommunity.com", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSteamPoweredDomain(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain)) return false;
            var d = domain.Trim().TrimStart('.');
            return d.EndsWith("steampowered.com", StringComparison.OrdinalIgnoreCase);
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
            // Only the steamcommunity.com login cookie identifies the session the
            // community scrapes run as. Cookies on steampowered.com domains can belong
            // to a previously logged-in account and must not determine identity.
            var authCookie = cookies?.FirstOrDefault(c =>
                c != null &&
                !string.IsNullOrWhiteSpace(c.Domain) &&
                IsSteamCommunityDomain(c.Domain) &&
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

        /// <summary>
        /// Copies Steam cookies from the CEF browser into the given HTTP cookie jar,
        /// reusing the scan-leased offscreen view when one is active.
        /// </summary>
        public void LoadCefCookiesIntoJar(CookieContainer cookieJar)
        {
            try
            {
                InvokeOnUi(_api, () =>
                {
                    var (view, owned) = _offscreenViews.AcquireView();
                    try
                    {
                        LoadCefCookiesIntoJarFromView(view, _logger, cookieJar);
                    }
                    finally
                    {
                        _offscreenViews.ReleaseView(view, owned, faulted: false);
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to load cookies from CEF into jar");
            }
        }

        private static void LoadCefCookiesIntoJarFromView(
            IWebView view,
            ILogger logger,
            CookieContainer cookieJar)
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
                        // Explicit host and dotted-domain clears are more reliable across CEF cookie stores.
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
