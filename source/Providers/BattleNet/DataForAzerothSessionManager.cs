using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Events;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.BattleNet
{
    /// <summary>
    /// Signs in to Data for Azeroth, the site that supplies World of Warcraft rarity percentages.
    ///
    /// The site logs in through Battle.net OAuth and keeps its session as a JSON web token in the
    /// page's local storage under "token.jwt", identifying the account by the token's subject claim
    /// and treating it as valid for about a week from its issued-at claim. This manager drives that
    /// the same way the other providers drive theirs: open the site's login in a browser window,
    /// wait for a real identity to appear, persist it, and verify it live on demand. Signing in also
    /// clears the human-check the site puts in front of its whole origin, which is what lets
    /// <see cref="DataForAzerothClient"/> read rarity at all.
    ///
    /// It is deliberately NOT exposed through <see cref="IDataProvider.AuthSession"/>. The refresh
    /// pipeline drops providers whose AuthSession does not probe clean, so surfacing it would stop
    /// World of Warcraft and StarCraft II achievements syncing whenever this optional third-party
    /// account is not signed in, when all it withholds is a rarity percentage.
    /// </summary>
    public sealed class DataForAzerothSessionManager : ISessionManager
    {
        private static readonly TimeSpan InteractiveAuthTimeout = TimeSpan.FromMinutes(3);
        private const string LogPrefix = "[BattleNet/DFA]";

        /// <summary>Where the site keeps its session token. Read only, never written by the plugin.</summary>
        private const string SessionTokenScript = "(function(){try{return window.localStorage.getItem('token.jwt')||'';}catch(e){return '';}})()";

        private const string ClearSessionTokenScript = "(function(){try{window.localStorage.removeItem('token.jwt');return 'ok';}catch(e){return '';}})()";

        /// <summary>
        /// The site treats a token as good for this long after its issued-at claim, so the same window
        /// is used here rather than inventing one.
        /// </summary>
        private static readonly TimeSpan SessionLifetime = TimeSpan.FromMilliseconds(601_200_000);

        private readonly IPlayniteAPI _api;
        private readonly BattleNetApiClient _apiClient;
        private readonly ILogger _logger;

        private int _authCheckInProgress;
        private (bool Success, string UserId) _authResult;

        public DataForAzerothSessionManager(IPlayniteAPI api, BattleNetApiClient apiClient, ILogger logger)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _logger = logger;
        }

        /// <summary>
        /// Informational only: this manager is never registered in the provider auth pipeline, so the
        /// key is not used to resolve settings or localized provider names. It must not collide with
        /// <see cref="BattleNetSessionManager"/>'s "BattleNet".
        /// </summary>
        public string ProviderKey => "DataForAzeroth";

        /// <summary>
        /// Cheap settings-only check, matching the other providers. Provider work still probes.
        /// </summary>
        public bool IsAuthenticated =>
            !string.IsNullOrWhiteSpace(ProviderRegistry.Settings<BattleNetSettings>().DataForAzerothUserId);

        public string UserId => ProviderRegistry.Settings<BattleNetSettings>().DataForAzerothUserId;

        private DataForAzerothClient Client => _apiClient.DataForAzeroth;

        /// <summary>
        /// Reads the site session out of the browser and reports who is signed in. Opens no window.
        /// </summary>
        public async Task<AuthProbeResult> ProbeAuthStateAsync(CancellationToken ct)
        {
            using (PerfScope.Start(_logger, "DataForAzeroth.ProbeAuthStateAsync", thresholdMs: 50))
            {
                try
                {
                    var session = await ReadSessionAsync(ct).ConfigureAwait(false);
                    if (session.IsValid)
                    {
                        PersistUserId(session.UserId);

                        // Being signed in is what the user controls, so it decides the verdict. The
                        // site can still withhold data from a signed-in visitor, which is worth saying
                        // out loud rather than reporting a sign-in problem the user cannot act on.
                        var client = Client;
                        if (client != null)
                        {
                            var served = await client.ProbeGateAsync(ct).ConfigureAwait(false);
                            if (served != DataForAzerothStatus.Ok)
                            {
                                _logger?.Warn(
                                    $"{LogPrefix} Signed in as {session.UserId}, but the site is not serving data " +
                                    $"({served}). WoW rarity will stay unavailable until it does.");
                            }
                        }

                        return AuthProbeResult.AlreadyAuthenticated(session.UserId, session.ExpiresUtc);
                    }

                    if (session.IsExpired)
                    {
                        _logger?.Info($"{LogPrefix} The stored site session has expired; a fresh sign-in is needed.");
                    }

                    ClearPersistedUserId();
                    return AuthProbeResult.NotAuthenticated();
                }
                catch (OperationCanceledException)
                {
                    return AuthProbeResult.Cancelled();
                }
                catch (Exception ex)
                {
                    // Could not reach a verdict, which is not the same as signed out: the persisted
                    // identity is preserved for a retry, the way the other providers handle it.
                    _logger?.Debug(ex, $"{LogPrefix} Session probe failed.");
                    return AuthProbeResult.Create(AuthOutcome.ProbeFailed, "LOCPlayAch_Auth_TemporaryFailure");
                }
            }
        }

        /// <summary>
        /// Opens the site's login in a browser window. Only ever reached from a settings button.
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
                }

                progress?.Report(AuthProgressStep.OpeningLoginWindow);

                var loginTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = _api.MainView.UIDispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        loginTcs.TrySetResult(LoginInteractively() ?? string.Empty);
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
                    _logger?.Warn($"{LogPrefix} Interactive sign-in timed out.");
                    progress?.Report(AuthProgressStep.Failed);
                    return AuthProbeResult.TimedOut(windowOpened);
                }

                var userId = await loginTcs.Task.ConfigureAwait(false);

                progress?.Report(AuthProgressStep.VerifyingSession);
                if (string.IsNullOrWhiteSpace(userId))
                {
                    // The window may have been closed by hand after a successful sign-in, so the
                    // browser is the authority rather than the dialog's outcome.
                    var session = await ReadSessionAsync(ct).ConfigureAwait(false);
                    userId = session.IsValid ? session.UserId : null;
                }

                if (string.IsNullOrWhiteSpace(userId))
                {
                    _logger?.Warn($"{LogPrefix} Sign-in did not complete.");
                    progress?.Report(AuthProgressStep.Failed);
                    return AuthProbeResult.Cancelled(windowOpened);
                }

                PersistUserId(userId);

                // Signing in also clears the site's human check, so let the next refresh try again
                // instead of waiting out the backoff from the last refusal.
                _apiClient.InvalidateDataForAzerothRarityCache();

                _logger?.Info($"{LogPrefix} Signed in; WoW rarity will fill in on the next refresh.");
                progress?.Report(AuthProgressStep.Completed);
                return AuthProbeResult.Authenticated(userId, windowOpened: windowOpened);
            }
            catch (OperationCanceledException)
            {
                progress?.Report(AuthProgressStep.Failed);
                return AuthProbeResult.TimedOut(windowOpened);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"{LogPrefix} Sign-in failed.");
                progress?.Report(AuthProgressStep.Failed);
                return AuthProbeResult.Failed(windowOpened);
            }
        }

        /// <summary>
        /// Signs out: drops the site's session token, its cookies, and the persisted identity.
        /// </summary>
        public void ClearSession()
        {
            _logger?.Info($"{LogPrefix} Signing out.");
            _authResult = (false, null);
            ClearPersistedUserId();

            try
            {
                _api.MainView.UIDispatcher.Invoke(() =>
                {
                    using (var view = _api.WebViews.CreateOffscreenView())
                    {
                        view.NavigateAndWaitAsync(DataForAzerothClient.BaseUrl).GetAwaiter().GetResult();
                        view.EvaluateScriptAsync(ClearSessionTokenScript).GetAwaiter().GetResult();
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"{LogPrefix} Failed to clear the site session token.");
            }

            _api.DeleteDomainCookies(_logger, LogPrefix, DataForAzerothClient.CookieDomains);
            _apiClient.InvalidateDataForAzerothRarityCache();
        }

        private string LoginInteractively()
        {
            _authResult = (false, null);
            IWebView view = null;

            try
            {
                view = _api.WebViews.CreateView(1000, 800);
                view.LoadingChanged += CloseWhenSignedIn;
                view.Navigate(DataForAzerothClient.BaseUrl);
                view.OpenDialog();

                return _authResult.Success ? _authResult.UserId : null;
            }
            finally
            {
                if (view != null)
                {
                    view.LoadingChanged -= CloseWhenSignedIn;
                    view.Dispose();
                }
            }
        }

        /// <summary>
        /// Closes the window once the site has issued a session. The site is a single-page app whose
        /// sign-in ends in a redirect back to itself, so each completed load is a chance to look.
        /// </summary>
        private async void CloseWhenSignedIn(object sender, WebViewLoadingChangedEventArgs e)
        {
            try
            {
                if (e.IsLoading)
                {
                    return;
                }

                var view = (IWebView)sender;
                if (Interlocked.CompareExchange(ref _authCheckInProgress, 1, 0) != 0)
                {
                    return;
                }

                // The token is written by the app after its redirect resolves, so give it a moment
                // rather than deciding on the first load event.
                var session = await AsyncPoll.UntilAsync(
                    ct => ReadSessionFromViewAsync(view, ct),
                    probed => probed.IsValid,
                    maxAttempts: 8,
                    delayMs: 500,
                    CancellationToken.None).ConfigureAwait(false);

                if (!session.IsValid)
                {
                    return;
                }

                _authResult = (true, session.UserId);
                _ = _api.MainView.UIDispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        view.Close();
                    }
                    catch
                    {
                        // The user may have closed it already.
                    }
                }));
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"{LogPrefix} Failed to check the sign-in state.");
            }
            finally
            {
                Interlocked.Exchange(ref _authCheckInProgress, 0);
            }
        }

        private async Task<SiteSession> ReadSessionAsync(CancellationToken ct)
        {
            var operation = _api.MainView.UIDispatcher.InvokeAsync(async () =>
            {
                using (var view = _api.WebViews.CreateOffscreenView())
                {
                    // Local storage is origin scoped, so the view has to be on the site to read it.
                    await view.NavigateAndWaitAsync(DataForAzerothClient.BaseUrl).ConfigureAwait(false);
                    return await ReadSessionFromViewAsync(view, ct).ConfigureAwait(false);
                }
            });

            var task = await operation.Task.ConfigureAwait(false);
            return await task.ConfigureAwait(false);
        }

        private async Task<SiteSession> ReadSessionFromViewAsync(IWebView view, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var evaluated = await view.EvaluateScriptAsync(SessionTokenScript).ConfigureAwait(false);
                if (evaluated?.Success != true)
                {
                    return SiteSession.None;
                }

                return ParseSession(evaluated.Result as string);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"{LogPrefix} Could not read the site session token.");
                return SiteSession.None;
            }
        }

        /// <summary>
        /// Reads the subject and issued-at claims out of the site's session token. Only the claims are
        /// read; the token itself is never logged or persisted, since it is a live credential.
        /// </summary>
        internal static SiteSession ParseSession(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return SiteSession.None;
            }

            var parts = token.Split('.');
            if (parts.Length < 2)
            {
                return SiteSession.None;
            }

            try
            {
                var payload = JObject.Parse(Encoding.UTF8.GetString(DecodeBase64Url(parts[1])));
                var subject = payload.Value<string>("sub");
                var issuedAt = payload.Value<long?>("iat");
                if (string.IsNullOrWhiteSpace(subject) || !issuedAt.HasValue)
                {
                    return SiteSession.None;
                }

                var expires = DateTimeOffset.FromUnixTimeSeconds(issuedAt.Value).UtcDateTime.Add(SessionLifetime);
                return new SiteSession(subject, expires);
            }
            catch (Exception)
            {
                return SiteSession.None;
            }
        }

        private static byte[] DecodeBase64Url(string value)
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            switch (normalized.Length % 4)
            {
                case 2: normalized += "=="; break;
                case 3: normalized += "="; break;
            }

            return Convert.FromBase64String(normalized);
        }

        private void PersistUserId(string userId)
        {
            var settings = ProviderRegistry.Settings<BattleNetSettings>();
            if (string.Equals(settings.DataForAzerothUserId, userId, StringComparison.Ordinal))
            {
                return;
            }

            settings.DataForAzerothUserId = userId;
            ProviderRegistry.Write(settings, persistToDisk: true);
        }

        private void ClearPersistedUserId()
        {
            var settings = ProviderRegistry.Settings<BattleNetSettings>();
            if (string.IsNullOrWhiteSpace(settings.DataForAzerothUserId))
            {
                return;
            }

            settings.DataForAzerothUserId = null;
            ProviderRegistry.Write(settings, persistToDisk: true);
        }

        /// <summary>The site session as the plugin understands it: who, and until when.</summary>
        internal struct SiteSession
        {
            public SiteSession(string userId, DateTime expiresUtc)
            {
                UserId = userId;
                ExpiresUtc = expiresUtc;
            }

            public static SiteSession None => new SiteSession(null, DateTime.MinValue);

            public string UserId { get; }

            public DateTime ExpiresUtc { get; }

            public bool IsValid => !string.IsNullOrWhiteSpace(UserId) && ExpiresUtc > DateTime.UtcNow;

            public bool IsExpired => !string.IsNullOrWhiteSpace(UserId) && ExpiresUtc <= DateTime.UtcNow;
        }
    }
}
