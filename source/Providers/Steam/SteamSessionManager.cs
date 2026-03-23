using PlayniteAchievements.Providers.Steam.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Services;
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
        private readonly PlayniteAchievementsSettings _settings;
        private readonly AuthProbeCache _probeCache;

        // Temporary state for interactive login dialog coordination
        private (bool Success, string SteamId) _authResult;

        public string ProviderKey => "Steam";

        public TimeSpan ProbeCacheDuration => AuthProbeCache.ProviderCacheDurations.Steam;

        /// <summary>
        /// Checks if currently authenticated based on cached probe result.
        /// </summary>
        public bool IsAuthenticated
        {
            get
            {
                if (_probeCache.IsCacheValid(ProviderKey, ProbeCacheDuration))
                {
                    return _probeCache.TryGetCachedUserId(ProviderKey, ProbeCacheDuration, out _);
                }
                return !string.IsNullOrWhiteSpace(ProviderRegistry.Settings<SteamSettings>().SteamUserId);
            }
        }

        public SteamSessionManager(
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
        public SteamSessionManager(
            IPlayniteAPI api,
            ILogger logger,
            PlayniteAchievementsSettings settings)
            : this(api, logger, settings, new AuthProbeCache(logger))
        {
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
            using (PerfScope.Start(_logger, "Steam.ProbeAuthStateAsync", thresholdMs: 50))
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    // First try quick hydration from existing CEF cookies
                    var steamId = ProbeSteamIdFromCefCookies();
                    if (!string.IsNullOrWhiteSpace(steamId))
                    {
                        _probeCache.RecordProbe(ProviderKey, true, steamId);
                        var steamSettings = ProviderRegistry.Settings<SteamSettings>();
                        steamSettings.SteamUserId = steamId;
                        ProviderRegistry.Write(steamSettings);
                        return AuthProbeResult.AlreadyAuthenticated(steamId);
                    }

                    // Try navigation to refresh cookies
                    steamId = await RefreshCookiesHeadlessAsync(ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(steamId))
                    {
                        _probeCache.RecordProbe(ProviderKey, true, steamId);
                        var steamSettings = ProviderRegistry.Settings<SteamSettings>();
                        steamSettings.SteamUserId = steamId;
                        ProviderRegistry.Write(steamSettings);
                        return AuthProbeResult.AlreadyAuthenticated(steamId);
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
                    _logger?.Error(ex, "[SteamAuth] Probe failed with exception.");
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
                    Task.Delay(TimeSpan.FromMinutes(3), ct)).ConfigureAwait(false);

                if (completed != loginTcs.Task)
                {
                    progress?.Report(AuthProgressStep.Failed);
                    return AuthProbeResult.TimedOut(windowOpened);
                }

                var extractedId = await loginTcs.Task.ConfigureAwait(false);

                progress?.Report(AuthProgressStep.VerifyingSession);
                if (string.IsNullOrWhiteSpace(extractedId))
                {
                    // Fallback: dialog may have been manually closed after successful login
                    extractedId = ProbeSteamIdFromCefCookies();
                }

                if (string.IsNullOrWhiteSpace(extractedId))
                {
                    progress?.Report(AuthProgressStep.Failed);
                    return AuthProbeResult.Cancelled(windowOpened);
                }

                _probeCache.RecordProbe(ProviderKey, true, extractedId);
                var steamSettings = ProviderRegistry.Settings<SteamSettings>();
                steamSettings.SteamUserId = extractedId;
                ProviderRegistry.Write(steamSettings);
                progress?.Report(AuthProgressStep.Completed);

                return AuthProbeResult.Authenticated(extractedId, windowOpened: windowOpened);
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
            ClearSteamCookiesFromCef(_api, _logger);
            _probeCache.Invalidate(ProviderKey);
            var steamSettings = ProviderRegistry.Settings<SteamSettings>();
            steamSettings.SteamUserId = null;
            ProviderRegistry.Write(steamSettings);
        }

        public void InvalidateProbeCache()
        {
            _probeCache.Invalidate(ProviderKey);
        }

        /// <summary>
        /// Gets the cached Steam ID64 if available from cache or persisted settings.
        /// Returns null if not authenticated.
        /// </summary>
        public string GetCachedSteamId64()
        {
            if (_probeCache.TryGetCachedUserId(ProviderKey, ProbeCacheDuration, out var cachedId))
            {
                return cachedId;
            }
            return ProviderRegistry.Settings<SteamSettings>().SteamUserId;
        }

        // ---------------------------------------------------------------------
        // Internal methods for SteamHttpClient
        // ---------------------------------------------------------------------

        /// <summary>
        /// Gets a Steam page using CEF WebView.
        /// </summary>
        public async Task<(string FinalUrl, string Html)> GetSteamPageAsyncCef(string url, CancellationToken ct)
        {
            string finalUrl = url;
            string html = "";
            var tcs = new TaskCompletionSource<(string, string)>();

            await _api.MainView.UIDispatcher.InvokeAsync(async () =>
            {
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
                        tcs.TrySetResult((finalUrl, html));
                    }
                }
                catch (TimeoutException ex)
                {
                    _logger?.Warn(ex, $"Offscreen navigation timed out for {url}");
                    tcs.TrySetResult((finalUrl, ""));
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, $"Offscreen navigation failed for {url}");
                    tcs.TrySetResult((finalUrl, ""));
                }
            });

            return await tcs.Task;
        }

        // ---------------------------------------------------------------------
        // Private Helper Methods
        // ---------------------------------------------------------------------

        /// <summary>
        /// Probes SteamID64 directly from current CEF cookies without navigation.
        /// </summary>
        private string ProbeSteamIdFromCefCookies()
        {
            try
            {
                using (var view = _api.WebViews.CreateOffscreenView())
                {
                    var cookies = view.GetCookies();
                    if (cookies == null)
                    {
                        return null;
                    }

                    var authCookie = cookies.FirstOrDefault(c =>
                        c != null &&
                        !string.IsNullOrWhiteSpace(c.Domain) &&
                        IsSteamDomain(c.Domain) &&
                        c.Name.Equals("steamLoginSecure", StringComparison.OrdinalIgnoreCase));
                    if (authCookie == null)
                    {
                        return null;
                    }

                    return TryExtractSteamId64FromSteamLoginSecure(authCookie.Value);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to probe SteamID64 from CEF cookies.");
                return null;
            }
        }

        /// <summary>
        /// Refreshes cookies by navigating to Steam Community and extracting the SteamID.
        /// </summary>
        private async Task<string> RefreshCookiesHeadlessAsync(CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<string>();

            await _api.MainView.UIDispatcher.InvokeAsync(async () =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    using (var view = _api.WebViews.CreateOffscreenView())
                    {
                        var targetUrl = "https://steamcommunity.com/";

                        _logger?.Debug($"[SteamAuth] Navigating to {targetUrl} to refresh session...");
                        await view.NavigateAndWaitAsync(targetUrl, timeoutMs: 15000);

                        // Give the browser a moment to commit cookies
                        await Task.Delay(1000, ct);

                        var cookies = view.GetCookies();
                        var authCookie = cookies?.FirstOrDefault(c =>
                            c.Name.Equals("steamLoginSecure", StringComparison.OrdinalIgnoreCase));

                        if (authCookie != null)
                        {
                            var extractedId = TryExtractSteamId64FromSteamLoginSecure(authCookie.Value);
                            tcs.TrySetResult(extractedId);
                            return;
                        }

                        tcs.TrySetResult(null);
                    }
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetResult(null);
                }
                catch (TimeoutException ex)
                {
                    _logger?.Warn(ex, "[SteamAuth] Offscreen navigation timed out.");
                    tcs.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "[SteamAuth] Offscreen navigation failed.");
                    tcs.TrySetResult(null);
                }
            });

            return await tcs.Task;
        }

        /// <summary>
        /// Synchronous login method matching Steam library plugin pattern.
        /// </summary>
        private string LoginInteractively()
        {
            _authResult = (false, null);

            IWebView view = null;
            try
            {
                view = _api.WebViews.CreateView(1000, 800);
                view.DeleteDomainCookies(".steamcommunity.com");
                view.DeleteDomainCookies("steamcommunity.com");
                view.DeleteDomainCookies(".steampowered.com");
                view.DeleteDomainCookies("steampowered.com");

                view.LoadingChanged += CloseWhenLoggedIn;
                view.Navigate("https://steamcommunity.com/login/home/?goto=" +
                              Uri.EscapeDataString("https://steamcommunity.com/my/"));

                view.OpenDialog();

                return _authResult.Success ? _authResult.SteamId : null;
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
                    return;

                var cookies = await Task.Run(() => view.GetCookies()).ConfigureAwait(false);
                var authCookie = cookies?.FirstOrDefault(c =>
                    c.Name.Equals("steamLoginSecure", StringComparison.OrdinalIgnoreCase));

                if (authCookie != null)
                {
                    var extractedId = TryExtractSteamId64FromSteamLoginSecure(authCookie.Value);
                    if (!string.IsNullOrWhiteSpace(extractedId))
                    {
                        _authResult = (true, extractedId);
                        _ = _api.MainView.UIDispatcher.BeginInvoke(new Action(() => view.Close()));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[SteamAuth] Failed to check authentication status");
            }
        }

        private bool IsLoginPageUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;

            return url.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0
                || url.IndexOf("openid", StringComparison.OrdinalIgnoreCase) >= 0
                || url.IndexOf("login.steampowered.com", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsSteamDomain(string domain)
        {
            if (string.IsNullOrWhiteSpace(domain)) return false;
            var d = domain.Trim().TrimStart('.');
            return d.EndsWith("steamcommunity.com", StringComparison.OrdinalIgnoreCase) ||
                   d.EndsWith("steampowered.com", StringComparison.OrdinalIgnoreCase);
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
                using (var view = api.WebViews.CreateOffscreenView())
                {
                    var cookies = view.GetCookies();
                    if (cookies == null)
                        return false;

                    return cookies.Any(c =>
                        c != null &&
                        !string.IsNullOrWhiteSpace(c.Domain) &&
                        IsSteamDomain(c.Domain) &&
                        SteamSessionCookieNames.Any(n => string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase)));
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "Failed to check Steam session cookies.");
                return false;
            }
        }

        public static Uri GetAddUriForDomain(string cookieDomain)
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

        public void LoadCefCookiesIntoJar(IPlayniteAPI api, CookieContainer cookieJar, ILogger logger)
        {
            try
            {
                using (var view = api.WebViews.CreateOffscreenView())
                {
                    var cookies = view.GetCookies();
                    if (cookies == null)
                        return;

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
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "Failed to load cookies from CEF into jar");
            }
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

        private static string SanitizeCookieValue(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            return value.Replace(",", "%2C");
        }

        public static void ClearSteamCookiesFromCef(IPlayniteAPI api, ILogger logger)
        {
            try
            {
                api.MainView.UIDispatcher.Invoke(() =>
                {
                    using (var view = api.WebViews.CreateOffscreenView(new WebViewSettings()))
                    {
                        view.DeleteDomainCookiesRegex(@"(^|\.)steamcommunity\.com$");
                        view.DeleteDomainCookiesRegex(@"(^|\.)steampowered\.com$");
                        view.DeleteDomainCookies("store.steampowered.com");
                        view.DeleteDomainCookies("login.steampowered.com");
                        view.DeleteDomainCookies("help.steampowered.com");
                    }
                });
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "Failed to clear Steam cookies from CEF.");
            }
        }

        // ---------------------------------------------------------------------
        // Internal methods for SteamHttpClient
        // ---------------------------------------------------------------------

        /// <summary>
        /// Gets whether a session refresh is needed.
        /// </summary>
        public bool NeedsRefresh => !_probeCache.IsCacheValid(ProviderKey, ProbeCacheDuration);
    }
}
