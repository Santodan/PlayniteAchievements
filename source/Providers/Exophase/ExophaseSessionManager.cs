using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using Playnite.SDK;
using Playnite.SDK.Events;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Exophase
{
    /// <summary>
    /// Cookie-based authentication client for Exophase.
    /// Uses Playnite's IWebView API for browser-based authentication.
    /// Exophase does not use OAuth tokens; session is maintained via cookies.
    /// </summary>
    public sealed class ExophaseSessionManager : IExophaseTokenProvider
    {
        private const string UrlLogin = "https://www.exophase.com/login";
        private const string UrlAccount = "https://www.exophase.com/account";
        private static readonly TimeSpan InteractiveAuthTimeout = TimeSpan.FromMinutes(3);

        private bool _isSessionAuthenticated;
        private string _username;
        private (bool Success, string Username) _authResult;
        private int _authCheckInProgress;
        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;

        public ExophaseSessionManager(IPlayniteAPI api, ILogger logger)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger;
        }

        /// <summary>
        /// Checks if currently authenticated based on verified web session.
        /// </summary>
        public bool IsAuthenticated => _isSessionAuthenticated;

        /// <summary>
        /// Gets the current username if authenticated.
        /// </summary>
        public string Username => _username;

        /// <summary>
        /// Runs a best-effort background probe to hydrate authentication state from current web session cookies.
        /// Safe to call at startup; failures are logged and not propagated.
        /// </summary>
        public async Task PrimeAuthenticationStateAsync(CancellationToken ct)
        {
            using (PerfScope.Start(_logger, "Exophase.PrimeAuthenticationStateAsync", thresholdMs: 50))
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var result = await ProbeAuthenticationAsync(ct).ConfigureAwait(false);
                    _logger?.Debug($"[ExophaseAuth] Startup auth probe completed with outcome={result?.Outcome}.");
                }
                catch (OperationCanceledException)
                {
                    _logger?.Debug("[ExophaseAuth] Startup auth probe cancelled.");
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "[ExophaseAuth] Startup auth probe failed.");
                }
            }
        }

        public async Task<ExophaseAuthResult> ProbeAuthenticationAsync(CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                if (_isSessionAuthenticated && !string.IsNullOrWhiteSpace(_username))
                {
                    return ExophaseAuthResult.Create(
                        ExophaseAuthOutcome.AlreadyAuthenticated,
                        "LOCPlayAch_Settings_ExophaseAuth_AlreadyAuthenticated",
                        _username,
                        windowOpened: false);
                }

                var extractedUsername = await QuickAuthCheckAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(extractedUsername))
                {
                    return ExophaseAuthResult.Create(
                        ExophaseAuthOutcome.AlreadyAuthenticated,
                        "LOCPlayAch_Settings_ExophaseAuth_AlreadyAuthenticated",
                        extractedUsername,
                        windowOpened: false);
                }

                _isSessionAuthenticated = false;

                return ExophaseAuthResult.Create(
                    ExophaseAuthOutcome.NotAuthenticated,
                    "LOCPlayAch_Settings_ExophaseAuth_NotAuthenticated",
                    windowOpened: false);
            }
            catch (OperationCanceledException)
            {
                return ExophaseAuthResult.Create(
                    ExophaseAuthOutcome.Cancelled,
                    "LOCPlayAch_Settings_ExophaseAuth_Cancelled",
                    windowOpened: false);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[ExophaseAuth] Probe failed with exception.");
                return ExophaseAuthResult.Create(
                    ExophaseAuthOutcome.ProbeFailed,
                    "LOCPlayAch_Settings_ExophaseAuth_ProbeFailed",
                    windowOpened: false);
            }
        }

        /// <summary>
        /// Main authentication entry point.
        /// </summary>
        public async Task<ExophaseAuthResult> AuthenticateInteractiveAsync(
            bool forceInteractive,
            CancellationToken ct,
            IProgress<ExophaseAuthProgressStep> progress = null)
        {
            var windowOpened = false;

            try
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(ExophaseAuthProgressStep.CheckingExistingSession);

                _logger?.Info("[ExophaseAuth] Starting interactive authentication.");

                if (!forceInteractive)
                {
                    try
                    {
                        var existingUsername = await QuickAuthCheckAsync(ct).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(existingUsername))
                        {
                            _logger?.Info("[ExophaseAuth] Quick auth check succeeded - already authenticated.");
                            progress?.Report(ExophaseAuthProgressStep.Completed);
                            return ExophaseAuthResult.Create(
                                ExophaseAuthOutcome.AlreadyAuthenticated,
                                "LOCPlayAch_Settings_ExophaseAuth_AlreadyAuthenticated",
                                existingUsername,
                                windowOpened: false);
                        }
                    }
                    catch (Exception quickCheckEx)
                    {
                        _logger?.Debug(quickCheckEx, "[ExophaseAuth] Quick check failed before interactive login, proceeding.");
                    }
                }
                else
                {
                    ClearSession();
                }

                _logger?.Info("[ExophaseAuth] Opening login dialog.");
                progress?.Report(ExophaseAuthProgressStep.OpeningLoginWindow);

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

                progress?.Report(ExophaseAuthProgressStep.WaitingForUserLogin);
                var completed = await Task.WhenAny(
                    loginTcs.Task,
                    Task.Delay(InteractiveAuthTimeout, ct)).ConfigureAwait(false);

                if (completed != loginTcs.Task)
                {
                    _logger?.Warn("[ExophaseAuth] Interactive login timed out while waiting for dialog completion.");
                    progress?.Report(ExophaseAuthProgressStep.Failed);
                    return ExophaseAuthResult.Create(
                        ExophaseAuthOutcome.TimedOut,
                        "LOCPlayAch_Settings_ExophaseAuth_TimedOut",
                        windowOpened: windowOpened);
                }

                var extractedUsername = await loginTcs.Task.ConfigureAwait(false);

                progress?.Report(ExophaseAuthProgressStep.VerifyingSession);
                if (string.IsNullOrWhiteSpace(extractedUsername))
                {
                    // Fallback: dialog may have been manually closed after successful login.
                    extractedUsername = await QuickAuthCheckAsync(ct).ConfigureAwait(false);
                }

                if (string.IsNullOrWhiteSpace(extractedUsername))
                {
                    _logger?.Warn("[ExophaseAuth] Interactive login failed or was cancelled.");
                    progress?.Report(ExophaseAuthProgressStep.Failed);
                    return ExophaseAuthResult.Create(
                        ExophaseAuthOutcome.Cancelled,
                        "LOCPlayAch_Settings_ExophaseAuth_Cancelled",
                        windowOpened: windowOpened);
                }

                _username = extractedUsername;
                _logger?.Info("[ExophaseAuth] Interactive login succeeded.");
                progress?.Report(ExophaseAuthProgressStep.Completed);
                return ExophaseAuthResult.Create(
                    ExophaseAuthOutcome.Authenticated,
                    "LOCPlayAch_Settings_ExophaseAuth_Verified",
                    extractedUsername,
                    windowOpened: windowOpened);
            }
            catch (OperationCanceledException)
            {
                _logger?.Info("[ExophaseAuth] Authentication was cancelled or timed out.");
                progress?.Report(ExophaseAuthProgressStep.Failed);
                return ExophaseAuthResult.Create(
                    ExophaseAuthOutcome.TimedOut,
                    "LOCPlayAch_Settings_ExophaseAuth_TimedOut",
                    windowOpened: windowOpened);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[ExophaseAuth] Authentication failed with exception.");
                progress?.Report(ExophaseAuthProgressStep.Failed);
                return ExophaseAuthResult.Create(
                    ExophaseAuthOutcome.Failed,
                    "LOCPlayAch_Settings_ExophaseAuth_Failed",
                    windowOpened: windowOpened);
            }
        }

        /// <summary>
        /// Quick check using offscreen view to see if already authenticated.
        /// If account page redirects to login, user is not authenticated.
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
                            await view.NavigateAndWaitAsync(UrlAccount, timeoutMs: 10000);
                            var currentUrl = view.GetCurrentAddress();

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
                                _isSessionAuthenticated = true;
                                _username = username;
                                return username;
                            }
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
        /// Exophase embeds user info in window.me JavaScript object.
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
        /// Clears the session by resetting stored state and clearing cookies.
        /// </summary>
        public void ClearSession()
        {
            _logger?.Info("[ExophaseAuth] Clearing session.");
            _username = null;
            _isSessionAuthenticated = false;
            _authResult = (false, null);

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

        /// <summary>
        /// Synchronous login method matching GOG session manager pattern.
        /// Blocks until CloseWhenLoggedIn closes the view.
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

                // This blocks until CloseWhenLoggedIn calls view.Close()
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
                    _logger?.Debug("[ExophaseAuth] Still on login page, waiting...");
                    return;
                }

                if (Interlocked.CompareExchange(ref _authCheckInProgress, 1, 0) != 0)
                    return;

                _logger?.Debug($"[ExophaseAuth] Navigation to: {address}");

                // Exophase may redirect to various pages after login.
                // Poll for authentication like GOG does.
                var extractedUsername = await WaitForAuthenticatedUserAsync(CancellationToken.None).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(extractedUsername))
                {
                    _authResult = (true, extractedUsername);
                    _isSessionAuthenticated = true;
                    _username = extractedUsername;
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

        /// <summary>
        /// Checks if URL is a login page.
        /// </summary>
        private static bool IsLoginPageUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            return url.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
