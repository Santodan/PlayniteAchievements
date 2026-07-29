using Playnite.SDK;
using Playnite.SDK.Events;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.GameJolt
{
    /// <summary>
    /// Cookie-based authentication for GameJolt. GameJolt has no OAuth path for third parties; the user
    /// logs in through a WebView and the session is carried by cookies. The logged-in username is scraped
    /// from the post-login page and persisted so refresh can build the per-user trophy endpoint.
    /// </summary>
    public sealed class GameJoltSessionManager : ISessionManager
    {
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        private static readonly TimeSpan InteractiveAuthTimeout = TimeSpan.FromMinutes(3);

        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly GameJoltCookieSnapshotStore _cookieSnapshotStore;
        private readonly GameJoltApiClient _probeClient;

        private (bool Success, string Username) _authResult;
        private List<HttpCookie> _capturedCookies;
        private int _authCheckInProgress;

        public string ProviderKey => "GameJolt";

        internal GameJoltCookieSnapshotStore CookieSnapshotStore => _cookieSnapshotStore;

        public bool IsAuthenticated =>
            !string.IsNullOrWhiteSpace(ProviderRegistry.Settings<GameJoltSettings>().UserId);

        public string Username => ProviderRegistry.Settings<GameJoltSettings>().UserId;

        public GameJoltSessionManager(IPlayniteAPI api, ILogger logger, string pluginUserDataPath)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger;
            _cookieSnapshotStore = new GameJoltCookieSnapshotStore(
                pluginUserDataPath ?? throw new ArgumentNullException(nameof(pluginUserDataPath)),
                logger);
            _probeClient = new GameJoltApiClient(_api, _logger, _cookieSnapshotStore);
        }

        /// <summary>
        /// Confirms the stored session by resolving the persisted username's profile through the
        /// cookie-authenticated site-api. A transient/failed fetch preserves the snapshot for retry.
        /// </summary>
        public async Task<AuthProbeResult> ProbeAuthStateAsync(CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                var storedUsername = ProviderRegistry.Settings<GameJoltSettings>().UserId;
                if (string.IsNullOrWhiteSpace(storedUsername))
                {
                    return AuthProbeResult.NotAuthenticated();
                }

                var resolved = await _probeClient.GetProfileUsernameAsync(storedUsername, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    PersistUsername(resolved);
                    return AuthProbeResult.AlreadyAuthenticated(resolved);
                }

                _logger?.Warn("[GameJoltAuth] Probe could not resolve stored profile - preserving snapshot for retry.");
                return AuthProbeResult.NotAuthenticated();
            }
            catch (OperationCanceledException)
            {
                return AuthProbeResult.Cancelled();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[GameJoltAuth] Auth probe failed with exception - preserving snapshot for retry.");
                return AuthProbeResult.ProbeFailed();
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

                _authResult = (false, null);
                _capturedCookies = null;

                var loginTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = _api.MainView.UIDispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var username = LoginInteractively();
                        loginTcs.TrySetResult(username ?? string.Empty);
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
                    _logger?.Warn("[GameJoltAuth] Interactive login timed out.");
                    progress?.Report(AuthProgressStep.Failed);
                    return AuthProbeResult.TimedOut(windowOpened);
                }

                var extractedUsername = await loginTcs.Task.ConfigureAwait(false);
                progress?.Report(AuthProgressStep.VerifyingSession);

                if (string.IsNullOrWhiteSpace(extractedUsername))
                {
                    _logger?.Warn("[GameJoltAuth] Interactive login failed or was cancelled.");
                    progress?.Report(AuthProgressStep.Failed);
                    return AuthProbeResult.Cancelled(windowOpened);
                }

                if (_capturedCookies != null && _capturedCookies.Count > 0)
                {
                    _cookieSnapshotStore.Save(_capturedCookies);
                }

                PersistUsername(extractedUsername);
                progress?.Report(AuthProgressStep.Completed);
                return AuthProbeResult.Authenticated(extractedUsername, windowOpened: windowOpened);
            }
            catch (OperationCanceledException)
            {
                _logger?.Info("[GameJoltAuth] Authentication was cancelled or timed out.");
                progress?.Report(AuthProgressStep.Failed);
                return AuthProbeResult.TimedOut(windowOpened);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[GameJoltAuth] Authentication failed with exception.");
                progress?.Report(AuthProgressStep.Failed);
                return AuthProbeResult.Failed(windowOpened);
            }
        }

        public void ClearSession()
        {
            _logger?.Info("[GameJoltAuth] Clearing session.");
            _authResult = (false, null);
            _capturedCookies = null;
            _cookieSnapshotStore.Delete();

            var settings = ProviderRegistry.Settings<GameJoltSettings>();
            settings.UserId = null;
            ProviderRegistry.Write(settings, persistToDisk: true);

            _api.DeleteDomainCookies(_logger, "[GameJoltAuth]", GameJoltApiClient.CookieDomains);
        }

        private void PersistUsername(string username)
        {
            var settings = ProviderRegistry.Settings<GameJoltSettings>();
            settings.UserId = username?.Trim();
            ProviderRegistry.Write(settings, persistToDisk: true);
        }

        /// <summary>
        /// Opens the login dialog and blocks until the user logs in (redirect off the login page) or the
        /// dialog is closed. Runs on the UI thread. Returns the scraped username on success.
        /// </summary>
        private string LoginInteractively()
        {
            IWebView view = null;
            try
            {
                view = _api.WebViews.CreateView(new WebViewSettings
                {
                    WindowWidth = 580,
                    WindowHeight = 700,
                    // GameJolt's login captcha rejects the default CEF user agent.
                    UserAgent = UserAgent
                });

                foreach (var domain in GameJoltApiClient.CookieDomains)
                {
                    view.DeleteDomainCookies(domain);
                }

                view.LoadingChanged += CloseWhenLoggedIn;
                view.Navigate(GameJoltApiClient.UrlLogin);
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

        private void CloseWhenLoggedIn(object sender, WebViewLoadingChangedEventArgs e)
        {
            try
            {
                if (e.IsLoading)
                {
                    return;
                }

                var view = (IWebView)sender;
                var address = view.GetCurrentAddress();

                if (IsLoginPageUrl(address) || !IsGameJoltHomeUrl(address))
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _authCheckInProgress, 1, 0) != 0)
                {
                    return;
                }

                try
                {
                    var username = GameJoltTrophyMapper.ExtractUsernameFromHtml(view.GetPageSource());
                    if (!string.IsNullOrWhiteSpace(username))
                    {
                        _capturedCookies = GameJoltCookieSnapshotStore.FilterGameJoltCookies(view.GetCookies());
                        _authResult = (true, username);
                        try
                        {
                            view.Close();
                        }
                        catch
                        {
                            // View may already be disposed.
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _authCheckInProgress, 0);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[GameJoltAuth] Failed to check authentication status.");
            }
        }

        private static bool IsLoginPageUrl(string url)
        {
            return !string.IsNullOrWhiteSpace(url) &&
                   url.IndexOf("/login", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsGameJoltHomeUrl(string url)
        {
            return !string.IsNullOrWhiteSpace(url) &&
                   url.IndexOf("gamejolt.com", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
