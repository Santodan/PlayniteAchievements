using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using Playnite.SDK;
using Playnite.SDK.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Exophase
{
    /// <summary>
    /// Cookie-based authentication client for Exophase.
    /// Uses Playnite's IWebView API for browser-based authentication.
    /// Exophase does not use OAuth tokens; session is maintained via cookies.
    /// Auth state is always probed from the source of truth before any provider work.
    /// </summary>
    public sealed class ExophaseSessionManager : ISessionManager
    {
        private const string UrlLogin = "https://www.exophase.com/login";
        private const string UrlAccount = "https://www.exophase.com/account";
        private static readonly TimeSpan InteractiveAuthTimeout = TimeSpan.FromMinutes(3);

        private (bool Success, string Username) _authResult;
        private int _authCheckInProgress;
        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly ExophaseCookieSnapshotStore _cookieSnapshotStore;

        public string ProviderKey => "Exophase";

        /// <summary>
        /// Exposes the cookie snapshot store for use by the API client.
        /// </summary>
        internal ExophaseCookieSnapshotStore CookieSnapshotStore => _cookieSnapshotStore;

        /// <summary>
        /// Cached auth state from the last successful live probe.
        /// Provider work must still call ProbeAuthStateAsync before fetching data.
        /// </summary>
        public bool IsAuthenticated =>
            !string.IsNullOrWhiteSpace(ProviderRegistry.Settings<ExophaseSettings>().UserId);

        /// <summary>
        /// Gets the current username if authenticated.
        /// </summary>
        public string Username => ProviderRegistry.Settings<ExophaseSettings>().UserId;

        public ExophaseSessionManager(
            IPlayniteAPI api,
            ILogger logger,
            string pluginUserDataPath)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger;
            _cookieSnapshotStore = new ExophaseCookieSnapshotStore(
                pluginUserDataPath ?? throw new ArgumentNullException(nameof(pluginUserDataPath)),
                logger);
        }

        // ---------------------------------------------------------------------
        // ISessionManager Implementation
        // ---------------------------------------------------------------------

        /// <summary>
        /// Probes the current authentication state using a live /account verification.
        /// A saved cookie snapshot may be restored before the probe when available.
        /// </summary>
        public async Task<AuthProbeResult> ProbeAuthStateAsync(CancellationToken ct)
        {
            using (PerfScope.Start(_logger, "Exophase.ProbeAuthStateAsync", thresholdMs: 50))
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    var accountProbe = await ProbeAccountSessionAsync(ct, allowSnapshotRestore: true).ConfigureAwait(false);

                    _logger?.Info($"[ExophaseAuth] Account probe result: IsAuthenticated={accountProbe.IsAuthenticated}, " +
                        $"Username='{accountProbe.Username ?? "null"}'");

                    if (accountProbe.IsAuthenticated)
                    {
                        var exophaseSettings = ProviderRegistry.Settings<ExophaseSettings>();
                        var previousUserId = exophaseSettings.UserId;
                        exophaseSettings.UserId = accountProbe.Username;
                        ProviderRegistry.Write(exophaseSettings, persistToDisk: true);
                        _logger?.Info($"[ExophaseAuth] Auth verified for user '{accountProbe.Username}'");
                        return AuthProbeResult.AlreadyAuthenticated(accountProbe.Username);
                    }

                    // Only destroy auth state when the server explicitly redirected
                    // to login. Transient failures (network, timeout, CEF issues)
                    // should preserve the snapshot for the next attempt.
                    if (accountProbe.WasRedirectedToLogin)
                    {
                        _logger?.Warn("[ExophaseAuth] Server redirected to login - session expired, clearing auth state");
                        DeleteSavedCookieSnapshot();
                        ClearPersistedUserId();
                    }
                    else
                    {
                        _logger?.Warn("[ExophaseAuth] Probe failed without login redirect - preserving snapshot for retry");
                    }

                    return AuthProbeResult.NotAuthenticated();
                }
                catch (OperationCanceledException)
                {
                    _logger?.Warn("[ExophaseAuth] Probe cancelled - preserving snapshot for retry");
                    return AuthProbeResult.Cancelled();
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "[ExophaseAuth] Auth probe failed with exception - preserving snapshot for retry");
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
                    // Fallback: dialog may have been manually closed after successful login.
                    var accountProbe = await ProbeAccountSessionAsync(ct, allowSnapshotRestore: false).ConfigureAwait(false);
                    extractedUsername = accountProbe.Username;
                }

                if (string.IsNullOrWhiteSpace(extractedUsername))
                {
                    _logger?.Warn("[ExophaseAuth] Interactive login failed or was cancelled.");
                    progress?.Report(AuthProgressStep.Failed);
                    return AuthProbeResult.Cancelled(windowOpened);
                }

                var saveSettings = ProviderRegistry.Settings<ExophaseSettings>();
                saveSettings.UserId = extractedUsername;
                ProviderRegistry.Write(saveSettings, persistToDisk: true);
                await SaveCurrentCookiesSnapshotAsync(ct).ConfigureAwait(false);

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
        /// Clears the session by clearing cookies.
        /// </summary>
        public void ClearSession()
        {
            _logger?.Info("[ExophaseAuth] Clearing session.");
            _authResult = (false, null);
            _cookieSnapshotStore.Delete();

            // Clear persisted user ID
            var clearSettings = ProviderRegistry.Settings<ExophaseSettings>();
            clearSettings.UserId = null;
            ProviderRegistry.Write(clearSettings, persistToDisk: true);

            // Clear cookies from CEF (all possible Exophase domains)
            try
            {
                _api.MainView.UIDispatcher.Invoke(() =>
                {
                    using (var view = _api.WebViews.CreateOffscreenView())
                    {
                        view.DeleteDomainCookies(".exophase.com");
                        view.DeleteDomainCookies("exophase.com");
                        view.DeleteDomainCookies(".www.exophase.com");
                        view.DeleteDomainCookies("www.exophase.com");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[ExophaseAuth] Failed to clear Exophase cookies from CEF.");
            }
        }

        // ---------------------------------------------------------------------
        // Private Helper Methods
        // ---------------------------------------------------------------------

        private async Task<ExophaseAccountProbeResult> ProbeAccountSessionAsync(CancellationToken ct, bool allowSnapshotRestore)
        {
            using (PerfScope.Start(_logger, "Exophase.ProbeAccountSessionAsync", thresholdMs: 50))
            {
                ct.ThrowIfCancellationRequested();

                // Always load snapshot first - snapshot is the source of truth, not CEF
                List<HttpCookie> snapshotCookies = null;
                var snapshotLoaded = allowSnapshotRestore && _cookieSnapshotStore.TryLoad(out snapshotCookies);

                var dispatchOperation = _api.MainView.UIDispatcher.InvokeAsync(async () =>
                {
                    // If we have a snapshot, restore it first and probe
                    if (snapshotLoaded && snapshotCookies != null && snapshotCookies.Count > 0)
                    {
                        using (var view = _api.WebViews.CreateOffscreenView())
                        {
                            await ReplaceExophaseCookiesAsync(view, snapshotCookies, ct);
                            return await VerifyAccountSessionAsync(view, ct);
                        }
                    }

                    // No snapshot available - try CEF cookies as fallback
                    using (var fallbackView = _api.WebViews.CreateOffscreenView())
                    {
                        return await VerifyAccountSessionAsync(fallbackView, ct);
                    }
                });

                var resultTask = await dispatchOperation.Task.ConfigureAwait(false);
                var result = await resultTask.ConfigureAwait(false);

                // Merge fresh probe cookies with snapshot to avoid degradation.
                // The server may not re-include all auth cookies in its response,
                // so we preserve snapshot cookies absent from the probe result.
                if (result?.IsAuthenticated == true && result.Cookies?.Count > 0)
                {
                    var mergedCookies = snapshotLoaded && snapshotCookies?.Count > 0
                        ? MergeCookieSets(snapshotCookies, result.Cookies)
                        : result.Cookies;
                    _cookieSnapshotStore.Save(mergedCookies);
                }

                return result ?? new ExophaseAccountProbeResult();
            }
        }

        private async Task<ExophaseAccountProbeResult> VerifyAccountSessionAsync(
            IWebView view,
            CancellationToken ct)
        {
            var result = new ExophaseAccountProbeResult();

            try
            {
                await view.NavigateAndWaitAsync(UrlAccount, timeoutMs: 10000);
                await Task.Delay(1000, ct);

                result.FinalUrl = view.GetCurrentAddress();
                result.Cookies = ExophaseCookieSnapshotStore.FilterExophaseCookies(view.GetCookies());

                if (IsLoginPageUrl(result.FinalUrl))
                {
                    result.WasRedirectedToLogin = true;
                    return result;
                }

                var html = await view.GetPageSourceAsync();
                result.Username = ExtractUsernameFromHtml(html);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[ExophaseAuth] Failed to check account page.");
            }

            return result;
        }

        private async Task ReplaceExophaseCookiesAsync(
            IWebView view,
            IReadOnlyList<HttpCookie> cookies,
            CancellationToken ct)
        {
            view.DeleteDomainCookies(".exophase.com");
            view.DeleteDomainCookies("exophase.com");
            view.DeleteDomainCookies(".www.exophase.com");
            view.DeleteDomainCookies("www.exophase.com");

            foreach (var cookie in cookies ?? Enumerable.Empty<HttpCookie>())
            {
                ct.ThrowIfCancellationRequested();

                if (cookie == null || string.IsNullOrWhiteSpace(cookie.Name))
                {
                    continue;
                }

                var cookieCopy = CloneCookie(cookie);
                view.SetCookies(BuildCookieOriginUrl(cookieCopy), cookieCopy);
            }

            await Task.Delay(250, ct);
        }

        private async Task SaveCurrentCookiesSnapshotAsync(CancellationToken ct)
        {
            var currentCookies = await CaptureCurrentCookiesAsync(ct).ConfigureAwait(false);
            if (currentCookies.Count == 0)
            {
                return;
            }

            _cookieSnapshotStore.Save(currentCookies);
        }

        private async Task<List<HttpCookie>> CaptureCurrentCookiesAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var dispatchOperation = _api.MainView.UIDispatcher.InvokeAsync(() =>
            {
                using (var view = _api.WebViews.CreateOffscreenView())
                {
                    return ExophaseCookieSnapshotStore.FilterExophaseCookies(view.GetCookies());
                }
            });

            return await dispatchOperation.Task.ConfigureAwait(false);
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
                    return;
                }

                if (Interlocked.CompareExchange(ref _authCheckInProgress, 1, 0) != 0)
                    return;

                var extractedUsername = await WaitForAuthenticatedUserAsync(CancellationToken.None).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(extractedUsername))
                {
                    _authResult = (true, extractedUsername);
                    _ = _api.MainView.UIDispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            view.Close();
                        }
                        catch
                        {
                            // View may already be disposed
                        }
                    }));
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
            var result = await ExophaseApiClient.PollAsync(
                async ct2 =>
                {
                    try
                    {
                        return await ProbeAccountSessionAsync(ct2, allowSnapshotRestore: false).ConfigureAwait(false);
                    }
                    catch
                    {
                        return new ExophaseAccountProbeResult();
                    }
                },
                probe => probe.IsAuthenticated,
                maxAttempts: 8,
                delayMs: 500,
                ct).ConfigureAwait(false);

            return result.IsAuthenticated ? result.Username : null;
        }

        private static bool IsLoginPageUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            return url.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static HttpCookie CloneCookie(HttpCookie cookie)
        {
            if (cookie == null)
            {
                return null;
            }

            return new HttpCookie
            {
                Name = cookie.Name,
                Value = cookie.Value,
                Domain = cookie.Domain,
                Path = string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path,
                Expires = cookie.Expires,
                Secure = cookie.Secure,
                HttpOnly = cookie.HttpOnly,
                SameSite = cookie.SameSite,
                Priority = cookie.Priority
            };
        }

        /// <summary>
        /// Merges fresh probe cookies with an existing snapshot, preferring probe values
        /// but preserving snapshot cookies absent from the probe result.
        /// Prevents progressive cookie degradation across restarts.
        /// </summary>
        private List<HttpCookie> MergeCookieSets(
            IReadOnlyList<HttpCookie> snapshotCookies,
            IReadOnlyList<HttpCookie> freshCookies)
        {
            if (snapshotCookies == null || snapshotCookies.Count == 0)
            {
                return freshCookies?.ToList() ?? new List<HttpCookie>();
            }

            if (freshCookies == null || freshCookies.Count == 0)
            {
                return snapshotCookies.ToList();
            }

            // Index fresh cookies by normalized "Name|Domain" for O(1) lookup.
            var freshKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in freshCookies)
            {
                if (c != null && !string.IsNullOrWhiteSpace(c.Name))
                {
                    freshKeys.Add(CookieKey(c.Name, c.Domain));
                }
            }

            // Start with all fresh cookies.
            var merged = new List<HttpCookie>(freshCookies.Count + snapshotCookies.Count);
            foreach (var cookie in freshCookies)
            {
                if (cookie != null)
                {
                    merged.Add(CloneCookie(cookie));
                }
            }

            // Preserve snapshot cookies that have no matching fresh cookie.
            var preservedNames = new List<string>();
            foreach (var cookie in snapshotCookies)
            {
                if (cookie == null || string.IsNullOrWhiteSpace(cookie.Name))
                {
                    continue;
                }

                if (!freshKeys.Contains(CookieKey(cookie.Name, cookie.Domain)))
                {
                    merged.Add(CloneCookie(cookie));
                    preservedNames.Add(cookie.Name);
                }
            }

            if (preservedNames.Count > 0)
            {
                _logger?.Debug($"[ExophaseAuth] Merge preserved {preservedNames.Count} snapshot-only cookie(s): {string.Join(", ", preservedNames)}");
            }

            return merged;
        }

        private static string CookieKey(string name, string domain)
        {
            var normalizedDomain = string.IsNullOrWhiteSpace(domain)
                ? ""
                : domain.Trim().TrimStart('.').ToLowerInvariant();
            return $"{name ?? ""}|{normalizedDomain}";
        }

        private static string BuildCookieOriginUrl(HttpCookie cookie)
        {
            var domain = (cookie?.Domain ?? string.Empty).Trim().TrimStart('.');
            if (string.IsNullOrWhiteSpace(domain))
            {
                domain = "www.exophase.com";
            }

            return "https://" + domain;
        }

        private void ClearPersistedUserId()
        {
            var settings = ProviderRegistry.Settings<ExophaseSettings>();
            if (settings == null || string.IsNullOrWhiteSpace(settings.UserId))
            {
                return;
            }

            settings.UserId = null;
            ProviderRegistry.Write(settings, persistToDisk: true);
        }

        private void DeleteSavedCookieSnapshot()
        {
            if (!_cookieSnapshotStore.Exists)
            {
                return;
            }

            _cookieSnapshotStore.Delete();
        }

        private sealed class ExophaseAccountProbeResult
        {
            public string Username { get; set; }

            public string FinalUrl { get; set; }

            public List<HttpCookie> Cookies { get; set; } = new List<HttpCookie>();
            public bool IsAuthenticated => !string.IsNullOrWhiteSpace(Username);

            /// <summary>
            /// True when the server redirected to the login page, indicating
            /// the session has definitively expired (vs a transient failure).
            /// </summary>
            public bool WasRedirectedToLogin { get; set; }
        }
    }
}
