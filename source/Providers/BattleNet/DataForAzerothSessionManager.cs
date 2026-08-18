using Playnite.SDK;
using Playnite.SDK.Events;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.BattleNet
{
    /// <summary>
    /// Manages the Data for Azeroth site session: the bot check the site puts in front of its whole
    /// origin, which only a person can clear. Opens the site in a browser window so the user can tick
    /// the checkbox (and sign in, which the site says stops the check reappearing), then leaves the
    /// resulting cookie in the shared browser store for <see cref="DataForAzerothClient"/> to replay.
    ///
    /// It implements <see cref="ISessionManager"/> for the shape - probe, interactive, clear - that the
    /// settings view is already written against, but it is deliberately NOT exposed through
    /// <see cref="IDataProvider.AuthSession"/>. Doing so would put a third-party fan site's checkbox in
    /// front of the refresh pipeline, which drops unauthenticated providers from the run: an uncleared
    /// check would stop World of Warcraft and StarCraft II achievements syncing entirely, when all that
    /// is actually unavailable is a rarity percentage.
    /// </summary>
    public sealed class DataForAzerothSessionManager : ISessionManager
    {
        private static readonly TimeSpan InteractiveAuthTimeout = TimeSpan.FromMinutes(3);
        private const string LogPrefix = "[BattleNet/DFA]";

        private readonly IPlayniteAPI _api;
        private readonly BattleNetApiClient _apiClient;
        private readonly ILogger _logger;

        private int _verifyInProgress;
        private bool _checkCleared;

        public DataForAzerothSessionManager(IPlayniteAPI api, BattleNetApiClient apiClient, ILogger logger)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _logger = logger;
        }

        /// <summary>
        /// Informational only. This manager is never registered in the provider auth pipeline, so the
        /// key is not used to look up settings or localized provider names; it must not collide with
        /// <see cref="BattleNetSessionManager"/>'s "BattleNet".
        /// </summary>
        public string ProviderKey => "DataForAzeroth";

        private DataForAzerothClient Client => _apiClient.DataForAzeroth;

        /// <summary>
        /// Asks the site whether it will serve data right now. Runs through the same client and cookie
        /// path a refresh uses, so a cleared verdict means the replay genuinely works rather than that
        /// the browser store merely looks healthy.
        /// </summary>
        public async Task<AuthProbeResult> ProbeAuthStateAsync(CancellationToken ct)
        {
            var client = Client;
            if (client == null)
            {
                return AuthProbeResult.NotAuthenticated();
            }

            using (PerfScope.Start(_logger, "DataForAzeroth.ProbeAuthStateAsync", thresholdMs: 50))
            {
                try
                {
                    var status = await client.ProbeGateAsync(ct).ConfigureAwait(false);
                    switch (status)
                    {
                        case DataForAzerothStatus.Ok:
                            _checkCleared = true;
                            return AuthProbeResult.AlreadyAuthenticated();
                        case DataForAzerothStatus.Gated:
                            _checkCleared = false;
                            return AuthProbeResult.NotAuthenticated();
                        default:
                            // Unreachable or broken is not the same as "the user must act", so the
                            // last known state is left alone rather than reported as uncleared.
                            return AuthProbeResult.Create(
                                AuthOutcome.ProbeFailed,
                                "LOCPlayAch_Auth_TemporaryFailure");
                    }
                }
                catch (OperationCanceledException)
                {
                    return AuthProbeResult.Cancelled();
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"{LogPrefix} Gate probe threw.");
                    return AuthProbeResult.Create(AuthOutcome.ProbeFailed, "LOCPlayAch_Auth_TemporaryFailure");
                }
            }
        }

        /// <summary>
        /// Opens the site so the user can clear its check. Only ever reached from a button in settings.
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

                progress?.Report(AuthProgressStep.OpeningLoginWindow);

                var verifyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = _api.MainView.UIDispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        verifyTcs.TrySetResult(VerifyInteractively());
                    }
                    catch (Exception ex)
                    {
                        verifyTcs.TrySetException(ex);
                    }
                }));
                windowOpened = true;

                progress?.Report(AuthProgressStep.WaitingForUserLogin);
                var completed = await Task.WhenAny(
                    verifyTcs.Task,
                    Task.Delay(InteractiveAuthTimeout, ct)).ConfigureAwait(false);

                if (completed != verifyTcs.Task)
                {
                    _logger?.Warn($"{LogPrefix} The verify window was left open; giving up on this attempt.");
                    progress?.Report(AuthProgressStep.Failed);
                    return AuthProbeResult.TimedOut(windowOpened);
                }

                await verifyTcs.Task.ConfigureAwait(false);

                // The window closing is a hint, not proof: the user may have ticked the box and closed
                // it by hand, or dismissed it untouched. A live probe is the only authority.
                progress?.Report(AuthProgressStep.VerifyingSession);
                Client?.ResetGateState();
                _apiClient.InvalidateDataForAzerothRarityCache();

                var confirmed = await ProbeAuthStateAsync(ct).ConfigureAwait(false);
                if (confirmed.IsSuccess)
                {
                    _logger?.Info($"{LogPrefix} The site check is cleared; WoW rarity will fill in on the next refresh.");
                    progress?.Report(AuthProgressStep.Completed);
                    return AuthProbeResult.Authenticated(null, windowOpened: windowOpened);
                }

                progress?.Report(AuthProgressStep.Failed);
                return AuthProbeResult.Cancelled(windowOpened);
            }
            catch (OperationCanceledException)
            {
                progress?.Report(AuthProgressStep.Failed);
                return AuthProbeResult.TimedOut(windowOpened);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"{LogPrefix} Clearing the site check failed.");
                progress?.Report(AuthProgressStep.Failed);
                return AuthProbeResult.Failed(windowOpened);
            }
        }

        /// <summary>
        /// Forgets the cleared check by deleting the site's cookies. Cookies only: the user's Data for
        /// Azeroth login lives in the page's local storage, which is left untouched, so this does not
        /// sign them out of the site.
        /// </summary>
        public void ClearSession()
        {
            _logger?.Info($"{LogPrefix} Clearing the stored site check.");
            _checkCleared = false;
            _api.DeleteDomainCookies(_logger, LogPrefix, DataForAzerothClient.CookieDomains);
            Client?.ResetGateState();
            _apiClient.InvalidateDataForAzerothRarityCache();
        }

        /// <summary>
        /// Last known state, for UI that must not perform a live request. Provider work always probes.
        /// </summary>
        public bool IsCheckCleared => _checkCleared;

        private bool VerifyInteractively()
        {
            _checkCleared = false;
            IWebView view = null;

            try
            {
                view = _api.WebViews.CreateView(760, 640);
                view.LoadingChanged += CloseWhenCheckCleared;
                view.Navigate(DataForAzerothClient.BaseUrl);
                view.OpenDialog();
                return _checkCleared;
            }
            finally
            {
                if (view != null)
                {
                    view.LoadingChanged -= CloseWhenCheckCleared;
                    view.Dispose();
                }
            }
        }

        /// <summary>
        /// Closes the window once the site starts serving data again. The interstitial writes its
        /// cookie and reloads, so the load that follows the user's tick is the moment the check has
        /// actually lifted; polling the site rather than watching for a cookie means this still works
        /// if the gate's cookie is renamed.
        /// </summary>
        private async void CloseWhenCheckCleared(object sender, WebViewLoadingChangedEventArgs e)
        {
            try
            {
                if (e.IsLoading)
                {
                    return;
                }

                var client = Client;
                if (client == null)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _verifyInProgress, 1, 0) != 0)
                {
                    return;
                }

                var status = await AsyncPoll.UntilAsync(
                    async ct => await client.ProbeGateAsync(ct).ConfigureAwait(false),
                    result => result == DataForAzerothStatus.Ok,
                    maxAttempts: 4,
                    delayMs: 500,
                    CancellationToken.None).ConfigureAwait(false);

                if (status != DataForAzerothStatus.Ok)
                {
                    return;
                }

                _checkCleared = true;
                var view = (IWebView)sender;
                _ = _api.MainView.UIDispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        view.Close();
                    }
                    catch
                    {
                        // The view may already be gone if the user closed it themselves.
                    }
                }));
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"{LogPrefix} Failed to check whether the site check was cleared.");
            }
            finally
            {
                Interlocked.Exchange(ref _verifyInProgress, 0);
            }
        }
    }
}
