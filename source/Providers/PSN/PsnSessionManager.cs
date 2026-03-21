using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Settings;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.PSN
{
    public sealed class PsnSessionManager
    {
        private static readonly TimeSpan InteractiveAuthTimeout = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan CachedTokenLifetime = TimeSpan.FromMinutes(45);
        private static readonly TimeSpan MobileTokenExpiryBuffer = TimeSpan.FromMinutes(5);

        private const string LoginUrl = @"https://web.np.playstation.com/api/session/v1/signin?redirect_uri=https://io.playstation.com/central/auth/login%3FpostSignInURL=https://www.playstation.com/home%26cancelURL=https://www.playstation.com/home&smcid=web:pdc";
        private const string MobileCodeUrl = "https://ca.account.sony.com/api/authz/v3/oauth/authorize?access_type=offline&client_id=09515159-7237-4370-9b40-3806e67c0891&redirect_uri=com.scee.psxandroid.scecompcall%3A%2F%2Fredirect&response_type=code&scope=psn%3Amobile.v2.core%20psn%3Aclientapp";
        private const string MobileTokenUrl = "https://ca.account.sony.com/api/authz/v3/oauth/token";
        private const string MobileTokenAuth = "MDk1MTUxNTktNzIzNy00MzcwLTliNDAtMzgwNmU2N2MwODkxOnVjUGprYTV0bnRCMktxc1A=";
        private const string GameListProbeUrl = "https://web.np.playstation.com/api/graphql/v1/op?operationName=getPurchasedGameList&variables=%7B%22isActive%22%3Atrue%2C%22platform%22%3A%5B%22ps3%22%2C%22ps4%22%2C%22ps5%22%5D%2C%22start%22%3A0%2C%22size%22%3A1%2C%22subscriptionService%22%3A%22NONE%22%7D&extensions=%7B%22persistedQuery%22%3A%7B%22version%22%3A1%2C%22sha256Hash%22%3A%222c045408b0a4d0264bb5a3edfed4efd49fb4749cf8d216be9043768adff905e2%22%7D%7D";

        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _tokenSemaphore = new SemaphoreSlim(1, 1);
        private readonly string _tokenPath;

        private readonly PersistedSettings _settings;
        private MobileTokens _mobileToken;
        private DateTime _mobileTokenAcquiredUtc = DateTime.MinValue;
        private DateTime _mobileTokenExpiryUtc = DateTime.MinValue;

        private string _accessToken;
        private DateTime _tokenAcquiredUtc = DateTime.MinValue;
        private bool _isSessionAuthenticated;

        public PsnSessionManager(IPlayniteAPI api, ILogger logger, PersistedSettings settings)
        {
            if (api == null) throw new ArgumentNullException(nameof(api));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            _api = api;
            _logger = logger;
            _settings = settings;
            _tokenPath = Path.Combine(api.Paths.ExtensionsDataPath, "PlayniteAchievements", "psn_cookies.bin");
        }

        public bool IsAuthenticated => _isSessionAuthenticated;

        public async Task PrimeAuthenticationStateAsync(CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var result = await ProbeAuthenticationAsync(ct).ConfigureAwait(false);
                _logger?.Debug($"[PSNAch] Startup auth probe completed with outcome={result?.Outcome}.");
            }
            catch (OperationCanceledException)
            {
                _logger?.Debug("[PSNAch] Startup auth probe cancelled.");
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[PSNAch] Startup auth probe failed.");
            }
        }

        public async Task<string> GetAccessTokenAsync(CancellationToken ct, bool forceRefresh = false)
        {
            ct.ThrowIfCancellationRequested();

            var token = await TryAcquireTokenAsync(ct, forceRefresh).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }

            throw new PsnAuthRequiredException("PlayStation authentication required. Please login.");
        }

        public async Task<PsnAuthResult> ProbeAuthenticationAsync(CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();

                var token = await TryAcquireTokenAsync(ct, forceRefresh: false).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    return PsnAuthResult.Create(
                        PsnAuthOutcome.AlreadyAuthenticated,
                        "LOCPlayAch_Settings_PsnAuth_AlreadyAuthenticated",
                        windowOpened: false);
                }

                return PsnAuthResult.Create(
                    PsnAuthOutcome.NotAuthenticated,
                    "LOCPlayAch_Settings_PsnAuth_NotAuthenticated",
                    windowOpened: false);
            }
            catch (OperationCanceledException)
            {
                return PsnAuthResult.Create(
                    PsnAuthOutcome.Cancelled,
                    "LOCPlayAch_Settings_PsnAuth_Cancelled",
                    windowOpened: false);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[PSNAch] Probe failed with exception.");
                return PsnAuthResult.Create(
                    PsnAuthOutcome.ProbeFailed,
                    "LOCPlayAch_Settings_PsnAuth_ProbeFailed",
                    windowOpened: false);
            }
        }

        public async Task<PsnAuthResult> AuthenticateInteractiveAsync(
            bool forceInteractive,
            CancellationToken ct,
            IProgress<PsnAuthProgressStep> progress = null)
        {
            var windowOpened = false;

            try
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(PsnAuthProgressStep.CheckingExistingSession);

                if (!forceInteractive)
                {
                    var existingToken = await TryAcquireTokenAsync(ct, forceRefresh: false).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(existingToken))
                    {
                        progress?.Report(PsnAuthProgressStep.Completed);
                        return PsnAuthResult.Create(
                            PsnAuthOutcome.AlreadyAuthenticated,
                            "LOCPlayAch_Settings_PsnAuth_AlreadyAuthenticated",
                            windowOpened: false);
                    }
                }

                progress?.Report(PsnAuthProgressStep.OpeningLoginWindow);
                windowOpened = await LoginAsync(ct).ConfigureAwait(false);

                if (!windowOpened)
                {
                    progress?.Report(PsnAuthProgressStep.Failed);
                    return PsnAuthResult.Create(
                        PsnAuthOutcome.Failed,
                        "LOCPlayAch_Settings_PsnAuth_WindowNotOpened",
                        windowOpened: false);
                }

                progress?.Report(PsnAuthProgressStep.WaitingForUserLogin);
                var deadlineUtc = DateTime.UtcNow.Add(InteractiveAuthTimeout);

                while (DateTime.UtcNow < deadlineUtc)
                {
                    ct.ThrowIfCancellationRequested();

                    var token = await TryAcquireTokenAsync(ct, forceRefresh: true).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        progress?.Report(PsnAuthProgressStep.Completed);
                        return PsnAuthResult.Create(
                            PsnAuthOutcome.Authenticated,
                            "LOCPlayAch_Settings_PsnAuth_Verified",
                            windowOpened: true);
                    }

                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }

                progress?.Report(PsnAuthProgressStep.Failed);
                return PsnAuthResult.Create(
                    PsnAuthOutcome.TimedOut,
                    "LOCPlayAch_Settings_PsnAuth_TimedOut",
                    windowOpened: true);
            }
            catch (OperationCanceledException)
            {
                return PsnAuthResult.Create(
                    PsnAuthOutcome.Cancelled,
                    "LOCPlayAch_Settings_PsnAuth_Cancelled",
                    windowOpened: windowOpened);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[PSNAch] Interactive auth failed.");
                progress?.Report(PsnAuthProgressStep.Failed);
                return PsnAuthResult.Create(
                    PsnAuthOutcome.Failed,
                    "LOCPlayAch_Settings_PsnAuth_Failed",
                    windowOpened: windowOpened);
            }
        }

        public void ClearSession()
        {
            try
            {
                ClearAuthentication();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[PSNAch] Failed to clear PSN auth state.");
            }

            SetCachedToken(null);
        }

        internal void InvalidateAccessToken()
        {
            _logger?.Debug("[PSNAch] Invalidating cached PSN token state.");
            SetCachedToken(null);
        }

        private async Task<bool> LoginAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var loggedIn = false;

            try
            {
                // Delete existing token file if exists
                if (File.Exists(_tokenPath))
                {
                    File.Delete(_tokenPath);
                }

                var webViewSettings = new WebViewSettings
                {
                    WindowHeight = 700,
                    WindowWidth = 580,
                    UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/108.0.0.0 Safari/537.36"
                };

                using (var view = _api.WebViews.CreateView(webViewSettings))
                {
                    view.LoadingChanged += (s, e) =>
                    {
                        var address = view.GetCurrentAddress();
                        if (address.StartsWith(@"https://www.playstation.com/"))
                        {
                            loggedIn = true;
                            view.Close();
                        }
                    };

                    // Clear existing cookies
                    view.DeleteDomainCookies(".sony.com");
                    view.DeleteDomainCookies(".ca.account.sony.com");
                    view.DeleteDomainCookies("ca.account.sony.com");
                    view.DeleteDomainCookies(".playstation.com");
                    view.DeleteDomainCookies("io.playstation.com");

                    view.Navigate(LoginUrl);
                    view.OpenDialog();
                }

                if (!loggedIn)
                {
                    return false;
                }

                // Dump cookies to disk
                await Task.Run(() => DumpCookies()).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[PSNAch] Login failed.");
                return false;
            }
        }

        private async Task<bool> CheckAuthenticationAsync()
        {
            try
            {
                var npsso = ProviderSettings.Load<PsnSettings>().Npsso;
                var hasTokenFile = File.Exists(_tokenPath);

                _logger?.Debug($"[PSNAch] CheckAuthentication: hasTokenFile={hasTokenFile}, npsso length={npsso?.Length ?? 0}");

                if (!hasTokenFile && string.IsNullOrWhiteSpace(npsso))
                {
                    _logger?.Debug("[PSNAch] No token file or NPSSO configured.");
                    return false;
                }

                // If we have NPSSO but no token file, try NPSSO authentication first
                if (!hasTokenFile && !string.IsNullOrWhiteSpace(npsso))
                {
                    _logger?.Debug("[PSNAch] No token file, trying NPSSO authentication");
                    TryRefreshCookies(npsso);
                }

                // Check if user is logged in
                var isLoggedIn = await GetIsUserLoggedInAsync().ConfigureAwait(false);
                _logger?.Debug($"[PSNAch] GetIsUserLoggedIn result: {isLoggedIn}");

                if (!isLoggedIn)
                {
                    // Try refreshing with NPSSO if we haven't already
                    if (hasTokenFile && !string.IsNullOrWhiteSpace(npsso))
                    {
                        _logger?.Debug("[PSNAch] Token file exists but not logged in, trying NPSSO refresh");
                        TryRefreshCookies(npsso);
                        isLoggedIn = await GetIsUserLoggedInAsync().ConfigureAwait(false);
                        _logger?.Debug($"[PSNAch] After NPSSO refresh, isLoggedIn: {isLoggedIn}");
                    }

                    if (!isLoggedIn)
                    {
                        return false;
                    }
                }

                // Get mobile token if needed
                if (!HasValidMobileToken())
                {
                    _logger?.Debug(
                        $"[PSNAch] Mobile token missing or stale (hasToken={!string.IsNullOrWhiteSpace(_mobileToken?.access_token)}, expiryUtc={_mobileTokenExpiryUtc:O}); requesting new token.");
                    if (!await GetMobileTokenAsync().ConfigureAwait(false))
                    {
                        _logger?.Debug("[PSNAch] Failed to get mobile token");
                        return false;
                    }

                    _logger?.Debug($"[PSNAch] Mobile token reissued; valid until {_mobileTokenExpiryUtc:O}.");
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[PSNAch] CheckAuthentication failed.");
                return false;
            }
        }

        private void ClearAuthentication()
        {
            try
            {
                if (File.Exists(_tokenPath))
                {
                    File.Delete(_tokenPath);
                }
                ResetMobileTokenState();
                _logger?.Info("[PSNAch] Authentication cleared.");
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[PSNAch] Failed to clear authentication.");
            }
        }

        private async Task<bool> GetIsUserLoggedInAsync()
        {
            try
            {
                var cookieContainer = ReadCookiesFromDisk();
                if (cookieContainer == null || cookieContainer.Count == 0)
                {
                    return false;
                }

                using (var handler = new HttpClientHandler { CookieContainer = cookieContainer })
                using (var httpClient = new HttpClient(handler))
                {
                    httpClient.DefaultRequestHeaders.TryAddWithoutValidation("x-apollo-operation-name", "pn_psn");
                    httpClient.Timeout = TimeSpan.FromSeconds(10);
                    var response = await httpClient.GetAsync(GameListProbeUrl).ConfigureAwait(false);
                    return response.IsSuccessStatusCode;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[PSNAch] GetIsUserLoggedIn check failed.");
                return false;
            }
        }

        private async Task<bool> GetMobileTokenAsync()
        {
            var cookieContainer = ReadCookiesFromDisk();
            if (cookieContainer == null || cookieContainer.Count == 0)
            {
                return false;
            }

            using (var handler = new HttpClientHandler { CookieContainer = cookieContainer, AllowAutoRedirect = false })
            using (var httpClient = new HttpClient(handler))
            {
                string mobileCode;
                try
                {
                    var mobileCodeResponse = await httpClient.GetAsync(MobileCodeUrl).ConfigureAwait(false);
                    if (mobileCodeResponse.StatusCode != HttpStatusCode.Redirect)
                    {
                        _logger?.Debug($"[PSNAch] Mobile code request returned {mobileCodeResponse.StatusCode}, expected redirect.");
                        return false;
                    }

                    var location = mobileCodeResponse.Headers.Location;
                    if (location == null)
                    {
                        return false;
                    }

                    mobileCode = GetQueryParam(location.Query, "code");
                    if (string.IsNullOrWhiteSpace(mobileCode))
                    {
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "[PSNAch] Failed to get mobile code.");
                    return false;
                }

                try
                {
                    var requestMessage = new HttpRequestMessage(new HttpMethod("POST"), MobileTokenUrl);
                    var formContent = new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("code", mobileCode),
                        new KeyValuePair<string, string>("redirect_uri", "com.scee.psxandroid.scecompcall://redirect"),
                        new KeyValuePair<string, string>("grant_type", "authorization_code"),
                        new KeyValuePair<string, string>("token_format", "jwt")
                    };
                    requestMessage.Content = new FormUrlEncodedContent(formContent);
                    requestMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", MobileTokenAuth);

                    var tokenResponse = await httpClient.SendAsync(requestMessage).ConfigureAwait(false);
                    var responseContent = await tokenResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!tokenResponse.IsSuccessStatusCode)
                    {
                        _logger?.Debug($"[PSNAch] Token request failed: {tokenResponse.StatusCode} - {responseContent}");
                        return false;
                    }

                    _mobileToken = Newtonsoft.Json.JsonConvert.DeserializeObject<MobileTokens>(responseContent);
                    if (_mobileToken == null || string.IsNullOrWhiteSpace(_mobileToken.access_token))
                    {
                        ResetMobileTokenState();
                        return false;
                    }

                    _mobileTokenAcquiredUtc = DateTime.UtcNow;
                    var expiresInSeconds = Math.Max(0, _mobileToken.expires_in);
                    _mobileTokenExpiryUtc = expiresInSeconds > 0
                        ? _mobileTokenAcquiredUtc.AddSeconds(expiresInSeconds)
                        : _mobileTokenAcquiredUtc;
                    return true;
                }
                catch (Exception ex)
                {
                    ResetMobileTokenState();
                    _logger?.Error(ex, "[PSNAch] Failed to exchange mobile code for token.");
                    return false;
                }
            }
        }

        private void TryRefreshCookies(string npsso)
        {
            try
            {
                _logger?.Debug($"[PSNAch] Attempting to refresh cookies with NPSSO (length={npsso?.Length ?? 0})");

                using (var webView = _api.WebViews.CreateOffscreenView())
                {
                    // Set the NPSSO cookie using Playnite SDK's HttpCookie type
                    var npssoCookie = new HttpCookie
                    {
                        Domain = "ca.account.sony.com",
                        Value = npsso,
                        Name = "npsso",
                        Path = "/"
                    };
                    webView.SetCookies("https://ca.account.sony.com", npssoCookie);

                    // Navigate to login - this should auto-authenticate with the NPSSO cookie
                    webView.NavigateAndWait(LoginUrl);

                    var finalAddress = webView.GetCurrentAddress();
                    _logger?.Debug($"[PSNAch] After NPSSO navigation, final address: {finalAddress}");
                }

                // Dump the cookies that were set during authentication
                DumpCookies();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[PSNAch] Failed to refresh cookies with NPSSO.");
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
                _logger?.Debug(ex, "[PSNAch] Failed to read cookies from disk.");
                return new CookieContainer();
            }
        }

        private void WriteCookiesToDisk(CookieContainer cookieJar)
        {
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
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[PSNAch] Failed to write cookies to disk.");
            }
        }

        private void DumpCookies()
        {
            try
            {
                using (var view = _api.WebViews.CreateOffscreenView())
                {
                    var cookies = view.GetCookies();
                    _logger?.Debug($"[PSNAch] DumpCookies: found {cookies?.Count ?? 0} cookies");

                    var cookieContainer = new CookieContainer();

                    foreach (var cookie in cookies)
                    {
                        _logger?.Debug($"[PSNAch] Cookie: {cookie.Name}@{cookie.Domain} (value length={cookie.Value?.Length ?? 0})");
                        try
                        {
                            if (cookie.Domain == ".playstation.com")
                            {
                                cookieContainer.Add(new Uri("https://web.np.playstation.com"), new Cookie(cookie.Name, cookie.Value));
                            }
                            if (cookie.Domain == ".ca.account.sony.com" || cookie.Domain == "ca.account.sony.com")
                            {
                                cookieContainer.Add(new Uri("https://ca.account.sony.com"), new Cookie(cookie.Name, cookie.Value));
                            }
                            if (cookie.Domain == ".sony.com")
                            {
                                cookieContainer.Add(new Uri("https://ca.account.sony.com"), new Cookie(cookie.Name, cookie.Value));
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.Debug(ex, $"[PSNAch] Failed to add cookie: {cookie.Name}@{cookie.Domain}");
                        }
                    }

                    _logger?.Debug($"[PSNAch] Cookie container has {cookieContainer.Count} cookies");
                    WriteCookiesToDisk(cookieContainer);
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[PSNAch] Failed to dump cookies.");
            }
        }

        private async Task<string> TryAcquireTokenAsync(CancellationToken ct, bool forceRefresh)
        {
            if (!forceRefresh && HasFreshCachedToken())
            {
                return _accessToken;
            }

            await _tokenSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!forceRefresh && HasFreshCachedToken())
                {
                    return _accessToken;
                }

                if (!await CheckAuthenticationAsync().ConfigureAwait(false))
                {
                    SetCachedToken(null);
                    return null;
                }

                if (!HasValidMobileToken())
                {
                    _logger?.Debug("[PSNAch] Authentication check completed but mobile token is still invalid.");
                    SetCachedToken(null);
                    return null;
                }

                var token = _mobileToken?.access_token;
                if (string.IsNullOrWhiteSpace(token))
                {
                    SetCachedToken(null);
                    return null;
                }

                SetCachedToken(token);
                return token;
            }
            finally
            {
                _tokenSemaphore.Release();
            }
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
            var effectiveBuffer = tokenLifetime > MobileTokenExpiryBuffer
                ? MobileTokenExpiryBuffer
                : TimeSpan.Zero;
            return DateTime.UtcNow < _mobileTokenExpiryUtc - effectiveBuffer;
        }

        private void ResetMobileTokenState()
        {
            _mobileToken = null;
            _mobileTokenAcquiredUtc = DateTime.MinValue;
            _mobileTokenExpiryUtc = DateTime.MinValue;
        }

        private static string GetQueryParam(string query, string key)
        {
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            // Remove leading '?' if present
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

        private bool HasFreshCachedToken()
        {
            return !string.IsNullOrWhiteSpace(_accessToken) &&
                   (DateTime.UtcNow - _tokenAcquiredUtc) < CachedTokenLifetime;
        }

        private void SetCachedToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                _accessToken = null;
                _tokenAcquiredUtc = DateTime.MinValue;
                _isSessionAuthenticated = false;
                ResetMobileTokenState();
                return;
            }

            _accessToken = token;
            _tokenAcquiredUtc = DateTime.UtcNow;
            _isSessionAuthenticated = true;
        }
    }
}
