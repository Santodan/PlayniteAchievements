using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Events;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.BattleNet.Models;

namespace PlayniteAchievements.Providers.BattleNet
{
    public sealed class BattleNetSessionManager : ISessionManager
    {
        private const string WowProfileScope = "wow.profile";
        private static readonly TimeSpan InteractiveAuthTimeout = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan TokenExpiryBuffer = TimeSpan.FromMinutes(2);

        private readonly IPlayniteAPI _api;
        private readonly BattleNetApiClient _apiClient;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _tokenSemaphore = new SemaphoreSlim(1, 1);

        public string ProviderKey => "BattleNet";

        public bool IsAuthenticated
        {
            get
            {
                var settings = ProviderRegistry.Settings<BattleNetSettings>();
                return HasFreshToken(settings);
            }
        }

        public BattleNetSessionManager(IPlayniteAPI api, BattleNetApiClient apiClient, ILogger logger)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _logger = logger;
        }

        public async Task<string> GetAccessTokenAsync(CancellationToken ct)
        {
            var settings = ProviderRegistry.Settings<BattleNetSettings>();
            if (HasFreshToken(settings))
            {
                return settings.BattleNetAccessToken;
            }

            await _tokenSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                settings = ProviderRegistry.Settings<BattleNetSettings>();
                if (HasFreshToken(settings))
                {
                    return settings.BattleNetAccessToken;
                }

                ClearTokenState(settings, persistToDisk: true);
            }
            finally
            {
                _tokenSemaphore.Release();
            }

            throw new BattleNetAuthRequiredException("Battle.net authentication required. Please login.");
        }

        public async Task<AuthProbeResult> ProbeAuthStateAsync(CancellationToken ct)
        {
            try
            {
                var settings = ProviderRegistry.Settings<BattleNetSettings>();
                if (!HasOAuthSetup(settings))
                {
                    return AuthProbeResult.Create(AuthOutcome.NotAuthenticated, "LOCPlayAch_Settings_BattleNet_Status_MissingOAuthSetup");
                }

                if (!HasFreshToken(settings))
                {
                    ClearTokenState(settings, persistToDisk: true);
                    return AuthProbeResult.NotAuthenticated();
                }

                var userInfo = await _apiClient.GetUserInfoAsync(
                    GetApiRegion(settings),
                    settings.BattleNetAccessToken,
                    ct).ConfigureAwait(false);

                if (userInfo == null || string.IsNullOrWhiteSpace(userInfo.Sub))
                {
                    return AuthProbeResult.ProbeFailed();
                }

                PersistUserInfo(settings, userInfo);
                return AuthProbeResult.AlreadyAuthenticated(userInfo.Sub, settings.BattleNetTokenExpiryUtc);
            }
            catch (OperationCanceledException)
            {
                return AuthProbeResult.Cancelled();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[BattleNetAuth] Auth probe failed.");
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

                var settings = ProviderRegistry.Settings<BattleNetSettings>();
                if (!HasOAuthSetup(settings))
                {
                    return AuthProbeResult.Create(AuthOutcome.Failed, "LOCPlayAch_Settings_BattleNet_Status_MissingOAuthSetup");
                }

                if (!forceInteractive)
                {
                    var probe = await ProbeAuthStateAsync(ct).ConfigureAwait(false);
                    if (probe.IsSuccess)
                    {
                        progress?.Report(AuthProgressStep.Completed);
                        return probe;
                    }
                }
                else
                {
                    ClearTokenState(settings, persistToDisk: true);
                }

                progress?.Report(AuthProgressStep.OpeningLoginWindow);
                var callbackUrl = await CaptureAuthorizationCallbackAsync(settings, forceInteractive, ct).ConfigureAwait(false);
                windowOpened = true;

                if (string.IsNullOrWhiteSpace(callbackUrl))
                {
                    progress?.Report(AuthProgressStep.Failed);
                    return AuthProbeResult.Cancelled(windowOpened);
                }

                var callbackUri = new Uri(callbackUrl);
                var authorizationCode = GetQueryParam(callbackUri.Query, "code");
                var error = GetQueryParam(callbackUri.Query, "error");
                if (!string.IsNullOrWhiteSpace(error))
                {
                    _logger?.Warn($"[BattleNetAuth] OAuth callback returned error: {error}");
                    progress?.Report(AuthProgressStep.Failed);
                    return AuthProbeResult.Failed(windowOpened);
                }

                if (string.IsNullOrWhiteSpace(authorizationCode))
                {
                    _logger?.Warn("[BattleNetAuth] OAuth callback did not include an authorization code.");
                    progress?.Report(AuthProgressStep.Failed);
                    return AuthProbeResult.Failed(windowOpened);
                }

                progress?.Report(AuthProgressStep.VerifyingSession);
                var token = await _apiClient.ExchangeAuthorizationCodeAsync(
                    GetApiRegion(settings),
                    settings.BattleNetClientId,
                    settings.BattleNetClientSecret,
                    authorizationCode,
                    settings.BattleNetRedirectUri,
                    ct).ConfigureAwait(false);

                PersistToken(settings, token);

                var userInfo = await _apiClient.GetUserInfoAsync(
                    GetApiRegion(settings),
                    settings.BattleNetAccessToken,
                    ct).ConfigureAwait(false);
                PersistUserInfo(settings, userInfo);

                progress?.Report(AuthProgressStep.Completed);
                return AuthProbeResult.Authenticated(userInfo?.Sub, settings.BattleNetTokenExpiryUtc, windowOpened);
            }
            catch (OperationCanceledException)
            {
                progress?.Report(AuthProgressStep.Failed);
                return AuthProbeResult.TimedOut(windowOpened);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[BattleNetAuth] Interactive authentication failed.");
                progress?.Report(AuthProgressStep.Failed);
                return AuthProbeResult.Failed(windowOpened);
            }
        }

        public void ClearSession()
        {
            var settings = ProviderRegistry.Settings<BattleNetSettings>();
            ClearTokenState(settings, persistToDisk: true);

            _api.DeleteDomainCookies(
                _logger,
                "[BattleNetAuth]",
                "battle.net",
                ".battle.net",
                "blizzard.com",
                ".blizzard.com");
        }

        private Task<string> CaptureAuthorizationCallbackAsync(
            BattleNetSettings settings,
            bool forceInteractive,
            CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var state = Guid.NewGuid().ToString("N");
            var authorizationUrl = BattleNetApiClient.BuildAuthorizationUrl(
                GetApiRegion(settings),
                settings.BattleNetClientId,
                settings.BattleNetRedirectUri,
                state,
                WowProfileScope);
            LoopbackCallbackListener loopbackListener = null;
            var callbackLock = new object();

            _ = _api.MainView.UIDispatcher.BeginInvoke(new Action(() =>
            {
                IWebView view = null;
                try
                {
                    var callback = string.Empty;
                    Action<string> captureCallback = address =>
                    {
                        if (string.IsNullOrWhiteSpace(address))
                        {
                            return;
                        }

                        lock (callbackLock)
                        {
                            if (string.IsNullOrWhiteSpace(callback))
                            {
                                callback = address;
                            }
                        }

                        try
                        {
                            _api.MainView.UIDispatcher.BeginInvoke(new Action(() =>
                            {
                                try { view?.Close(); } catch { }
                            }));
                        }
                        catch
                        {
                        }
                    };

                    loopbackListener = LoopbackCallbackListener.TryStart(
                        settings.BattleNetRedirectUri,
                        _logger,
                        ct,
                        captureCallback);

                    view = _api.WebViews.CreateView(new WebViewSettings
                    {
                        WindowHeight = 700,
                        WindowWidth = 580
                    });

                    if (forceInteractive)
                    {
                        view.DeleteDomainCookies("battle.net");
                        view.DeleteDomainCookies(".battle.net");
                        view.DeleteDomainCookies("blizzard.com");
                        view.DeleteDomainCookies(".blizzard.com");
                    }

                    EventHandler<WebViewLoadingChangedEventArgs> loadingChanged = (sender, args) =>
                    {
                        var address = SafeGetCurrentAddress(view);
                        if (string.IsNullOrWhiteSpace(address))
                        {
                            return;
                        }

                        if (IsAuthorizationCallback(address, settings.BattleNetRedirectUri))
                        {
                            captureCallback(address);
                        }
                    };

                    view.LoadingChanged += loadingChanged;
                    try
                    {
                        view.Navigate(authorizationUrl);
                        view.OpenDialog();
                    }
                    finally
                    {
                        view.LoadingChanged -= loadingChanged;
                    }

                    if (string.IsNullOrWhiteSpace(callback) && loopbackListener?.CallbackTask.IsCompleted == true)
                    {
                        callback = loopbackListener.GetCompletedCallback();
                    }

                    if (!string.IsNullOrWhiteSpace(callback))
                    {
                        var callbackUri = new Uri(callback);
                        var returnedState = GetQueryParam(callbackUri.Query, "state");
                        if (!string.Equals(state, returnedState, StringComparison.Ordinal))
                        {
                            _logger?.Warn("[BattleNetAuth] OAuth callback state did not match.");
                            callback = string.Empty;
                        }
                    }

                    tcs.TrySetResult(callback);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    loopbackListener?.Dispose();
                    view?.Dispose();
                }
            }));

            ct.Register(() => tcs.TrySetCanceled());
            return WithTimeoutAsync(tcs.Task, InteractiveAuthTimeout, ct);
        }

        private sealed class LoopbackCallbackListener : IDisposable
        {
            private readonly Uri _redirectUri;
            private readonly ILogger _logger;
            private readonly Action<string> _onCallback;
            private readonly TcpListener _listener;
            private readonly TaskCompletionSource<string> _callbackTask;
            private readonly CancellationTokenRegistration _cancellationRegistration;
            private bool _disposed;

            private LoopbackCallbackListener(Uri redirectUri, IPAddress address, ILogger logger, CancellationToken ct, Action<string> onCallback)
            {
                _redirectUri = redirectUri;
                _logger = logger;
                _onCallback = onCallback;
                _listener = new TcpListener(address, redirectUri.Port);
                _callbackTask = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _cancellationRegistration = ct.Register(() =>
                {
                    _callbackTask.TrySetCanceled();
                    StopListener();
                });
            }

            public Task<string> CallbackTask => _callbackTask.Task;

            public static LoopbackCallbackListener TryStart(
                string redirectUri,
                ILogger logger,
                CancellationToken ct,
                Action<string> onCallback)
            {
                if (!TryGetLoopbackEndpoint(redirectUri, out var uri, out var address))
                {
                    return null;
                }

                try
                {
                    var listener = new LoopbackCallbackListener(uri, address, logger, ct, onCallback);
                    listener._listener.Start();
                    _ = Task.Run(() => listener.AcceptLoopAsync());
                    logger?.Debug($"[BattleNetAuth] Listening for OAuth callback on {uri.GetLeftPart(UriPartial.Authority)}.");
                    return listener;
                }
                catch (Exception ex)
                {
                    logger?.Error(ex, $"[BattleNetAuth] Failed to start local OAuth callback listener for '{redirectUri}'.");
                    return null;
                }
            }

            public string GetCompletedCallback()
            {
                if (!_callbackTask.Task.IsCompleted ||
                    _callbackTask.Task.IsCanceled ||
                    _callbackTask.Task.IsFaulted)
                {
                    return null;
                }

                return _callbackTask.Task.Result;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _cancellationRegistration.Dispose();
                StopListener();
            }

            private async Task AcceptLoopAsync()
            {
                while (!_callbackTask.Task.IsCompleted)
                {
                    TcpClient client = null;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                        await HandleClientAsync(client).ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch (SocketException)
                    {
                        if (!_disposed)
                        {
                            _callbackTask.TrySetResult(null);
                        }

                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, "[BattleNetAuth] Failed while reading local OAuth callback.");
                    }
                    finally
                    {
                        try { client?.Close(); } catch { }
                    }
                }
            }

            private async Task HandleClientAsync(TcpClient client)
            {
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                {
                    var requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                    var callbackUrl = BuildCallbackUrl(requestLine);
                    var isCallback = IsAuthorizationCallback(callbackUrl, _redirectUri.ToString());

                    await WriteResponseAsync(stream, isCallback).ConfigureAwait(false);

                    if (isCallback)
                    {
                        _callbackTask.TrySetResult(callbackUrl);
                        _onCallback?.Invoke(callbackUrl);
                        StopListener();
                    }
                }
            }

            private string BuildCallbackUrl(string requestLine)
            {
                if (string.IsNullOrWhiteSpace(requestLine))
                {
                    return null;
                }

                var parts = requestLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    return null;
                }

                var target = parts[1];
                if (Uri.TryCreate(target, UriKind.Absolute, out var absolute))
                {
                    return absolute.ToString();
                }

                if (!target.StartsWith("/", StringComparison.Ordinal))
                {
                    return null;
                }

                return _redirectUri.GetLeftPart(UriPartial.Authority) + target;
            }

            private static async Task WriteResponseAsync(Stream stream, bool success)
            {
                var body = success
                    ? "<!doctype html><html><body><h1>Battle.net login complete</h1><p>You can close this window and return to Playnite.</p></body></html>"
                    : "<!doctype html><html><body><h1>Battle.net login callback not recognized</h1></body></html>";
                var status = success ? "200 OK" : "404 Not Found";
                var bodyBytes = Encoding.UTF8.GetBytes(body);
                var header =
                    $"HTTP/1.1 {status}\r\n" +
                    "Content-Type: text/html; charset=utf-8\r\n" +
                    $"Content-Length: {bodyBytes.Length}\r\n" +
                    "Connection: close\r\n\r\n";
                var headerBytes = Encoding.ASCII.GetBytes(header);

                await stream.WriteAsync(headerBytes, 0, headerBytes.Length).ConfigureAwait(false);
                await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }

            private static bool TryGetLoopbackEndpoint(string redirectUri, out Uri uri, out IPAddress address)
            {
                uri = null;
                address = null;

                if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out uri) ||
                    !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                    uri.IsDefaultPort)
                {
                    return false;
                }

                if (IPAddress.TryParse(uri.Host, out var parsedAddress))
                {
                    if (IPAddress.IsLoopback(parsedAddress))
                    {
                        address = parsedAddress;
                        return true;
                    }

                    return false;
                }

                if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                {
                    address = IPAddress.Loopback;
                    return true;
                }

                return false;
            }

            private void StopListener()
            {
                try { _listener.Stop(); } catch { }
            }
        }

        private static async Task<string> WithTimeoutAsync(Task<string> task, TimeSpan timeout, CancellationToken ct)
        {
            var completed = await Task.WhenAny(task, Task.Delay(timeout, ct)).ConfigureAwait(false);
            if (completed != task)
            {
                return null;
            }

            return await task.ConfigureAwait(false);
        }

        private static bool IsAuthorizationCallback(string address, string redirectUri)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(redirectUri) &&
                address.StartsWith(redirectUri, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return address.IndexOf("code=", StringComparison.OrdinalIgnoreCase) >= 0 &&
                address.IndexOf("state=", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string SafeGetCurrentAddress(IWebView view)
        {
            try
            {
                return view?.GetCurrentAddress();
            }
            catch
            {
                return null;
            }
        }

        private void PersistToken(BattleNetSettings settings, BattleNetApiTokenResponse token)
        {
            if (settings == null || string.IsNullOrWhiteSpace(token?.AccessToken))
            {
                return;
            }

            settings.BattleNetAccessToken = token.AccessToken;
            settings.BattleNetRefreshToken = token.RefreshToken;
            settings.BattleNetTokenType = string.IsNullOrWhiteSpace(token.TokenType) ? "bearer" : token.TokenType;
            settings.BattleNetTokenExpiryUtc = DateTime.UtcNow.AddSeconds(Math.Max(token.ExpiresIn, 60)).Subtract(TokenExpiryBuffer);
            ProviderRegistry.Write(settings, persistToDisk: true);
        }

        private static void PersistUserInfo(BattleNetSettings settings, BattleNetUserInfoResponse userInfo)
        {
            if (settings == null || userInfo == null)
            {
                return;
            }

            var changed =
                !string.Equals(settings.BattleNetAccountId, userInfo.Sub, StringComparison.Ordinal) ||
                !string.Equals(settings.BattleNetBattleTag, userInfo.BattleTag, StringComparison.Ordinal);

            settings.BattleNetAccountId = userInfo.Sub;
            settings.BattleNetBattleTag = userInfo.BattleTag;

            if (changed)
            {
                ProviderRegistry.Write(settings, persistToDisk: true);
            }
        }

        private static void ClearTokenState(BattleNetSettings settings, bool persistToDisk)
        {
            if (settings == null)
            {
                return;
            }

            settings.BattleNetAccessToken = null;
            settings.BattleNetRefreshToken = null;
            settings.BattleNetTokenType = null;
            settings.BattleNetTokenExpiryUtc = DateTime.MinValue;
            settings.BattleNetAccountId = null;
            settings.BattleNetBattleTag = null;

            if (persistToDisk)
            {
                ProviderRegistry.Write(settings, persistToDisk: true);
            }
        }

        private static bool HasFreshToken(BattleNetSettings settings)
        {
            return settings != null &&
                !string.IsNullOrWhiteSpace(settings.BattleNetAccessToken) &&
                settings.BattleNetTokenExpiryUtc > DateTime.UtcNow.Add(TokenExpiryBuffer);
        }

        private static bool HasOAuthSetup(BattleNetSettings settings)
        {
            return BattleNetGameSupport.HasApiCredentials(settings) &&
                !string.IsNullOrWhiteSpace(settings.BattleNetRedirectUri);
        }

        private static string GetApiRegion(BattleNetSettings settings)
        {
            return string.IsNullOrWhiteSpace(settings?.WowRegion) ? "us" : settings.WowRegion;
        }

        private static string GetQueryParam(string query, string key)
        {
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            if (query.StartsWith("?"))
            {
                query = query.Substring(1);
            }

            var pairs = query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs)
            {
                var keyValue = pair.Split(new[] { '=' }, 2);
                if (keyValue.Length == 2 && string.Equals(keyValue[0], key, StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(keyValue[1]);
                }
            }

            return null;
        }
    }

    internal sealed class BattleNetAuthRequiredException : Exception
    {
        public BattleNetAuthRequiredException(string message) : base(message) { }
    }
}
