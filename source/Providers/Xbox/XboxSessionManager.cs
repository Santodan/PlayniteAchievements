using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK.Data;
using PlayniteAchievements.Providers.Xbox.Models;

namespace PlayniteAchievements.Providers.Xbox
{
    /// <summary>
    /// Xbox Live session manager that probes authentication state from encrypted disk files.
    /// Auth state is never cached in memory - always probed from the source of truth.
    /// </summary>
    public sealed class XboxSessionManager : ISessionManager
    {
        private static readonly TimeSpan InteractiveAuthTimeout = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan CachedTokenLifetime = TimeSpan.FromMinutes(45);

        private const string ClientId = "38cd2fa8-66fd-4760-afb2-405eb65d5b0c";
        private const string RedirectUri = "https://login.live.com/oauth20_desktop.srf";
        private const string Scope = "Xboxlive.signin Xboxlive.offline_access";

        private const string LoginUrlFormat = @"https://login.live.com/oauth20_authorize.srf?client_id={0}&response_type=code&approval_prompt=auto&scope={1}&redirect_uri={2}";

        private const string TokenUrl = "https://login.live.com/oauth20_token.srf";
        private const string XboxAuthUrl = "https://user.auth.xboxlive.com/user/authenticate";
        private const string XstsAuthUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
        private const string ProfileUrl = "https://profile.xboxlive.com/users/batch/profile/settings";

        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _tokenSemaphore = new SemaphoreSlim(1, 1);
        private readonly string _liveTokensPath;
        private readonly string _xstsTokensPath;
        private readonly AuthProbeCache _probeCache;

        // Cached authorization data for the lifetime of a single operation
        private AuthorizationData _cachedAuthData;
        private DateTime _tokenAcquiredUtc = DateTime.MinValue;

        public string ProviderKey => "Xbox";

        public TimeSpan ProbeCacheDuration => AuthProbeCache.ProviderCacheDurations.Xbox;

        public XboxSessionManager(
            IPlayniteAPI api,
            ILogger logger,
            PersistedSettings settings,
            AuthProbeCache probeCache,
            string pluginUserDataPath)
        {
            if (api == null) throw new ArgumentNullException(nameof(api));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            _api = api;
            _logger = logger;
            _probeCache = probeCache ?? throw new ArgumentNullException(nameof(probeCache));

            // Use pluginUserDataPath for token storage (consistent with RA and other providers)
            var basePath = Path.Combine(pluginUserDataPath ?? string.Empty, "xbox");
            _liveTokensPath = Path.Combine(basePath, "live.json");
            _xstsTokensPath = Path.Combine(basePath, "xsts.json");

            // Migrate from old location if needed
            MigrateTokenFiles();
        }

        /// <summary>
        /// Backward-compatible constructor that creates a default AuthProbeCache.
        /// </summary>
        public XboxSessionManager(
            IPlayniteAPI api,
            ILogger logger,
            PersistedSettings settings,
            string pluginUserDataPath)
            : this(api, logger, settings, new AuthProbeCache(logger), pluginUserDataPath)
        {
        }

        /// <summary>
        /// Migrates token files from old location to new plugin data location.
        /// </summary>
        private void MigrateTokenFiles()
        {
            var oldBasePath = Path.Combine(_api.Paths.ExtensionsDataPath, "PlayniteAchievements");
            var oldLivePath = Path.Combine(oldBasePath, "xbox_live.json");
            var oldXstsPath = Path.Combine(oldBasePath, "xbox_xsts.json");

            try
            {
                var newDir = Path.GetDirectoryName(_liveTokensPath);
                if (!Directory.Exists(newDir))
                {
                    Directory.CreateDirectory(newDir);
                }

                if (File.Exists(oldLivePath) && !File.Exists(_liveTokensPath))
                {
                    File.Move(oldLivePath, _liveTokensPath);
                    _logger?.Info("[XboxAuth] Migrated Live tokens to new location.");
                }

                if (File.Exists(oldXstsPath) && !File.Exists(_xstsTokensPath))
                {
                    File.Move(oldXstsPath, _xstsTokensPath);
                    _logger?.Info("[XboxAuth] Migrated XSTS tokens to new location.");
                }

                // Try to clean up old directory if empty
                if (Directory.Exists(oldBasePath) && Directory.GetFiles(oldBasePath).Length == 0)
                {
                    Directory.Delete(oldBasePath);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[XboxAuth] Failed to migrate token files.");
            }
        }

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
                return File.Exists(_xstsTokensPath);
            }
        }

        // ---------------------------------------------------------------------
        // ISessionManager Implementation
        // ---------------------------------------------------------------------

        /// <summary>
        /// Ensures authentication is valid before data provider work.
        /// Uses cached probe results if within ProbeCacheDuration, otherwise probes fresh.
        /// </summary>
        public async Task<AuthProbeResult> EnsureAuthAsync(CancellationToken ct)
        {
            // Check if we have a valid cached result
            if (_probeCache.IsCacheValid(ProviderKey, ProbeCacheDuration))
            {
                if (_probeCache.TryGetCachedUserId(ProviderKey, ProbeCacheDuration, out var cachedUserId))
                {
                    return AuthProbeResult.AlreadyAuthenticated(cachedUserId);
                }
            }

            // Cache expired or invalid - perform fresh probe
            return await ProbeAuthStateAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Probes the current authentication state from encrypted disk files.
        /// This always performs a fresh probe, bypassing any cache.
        /// </summary>
        public async Task<AuthProbeResult> ProbeAuthStateAsync(CancellationToken ct)
        {
            using (PerfScope.Start(_logger, "Xbox.ProbeAuthStateAsync", thresholdMs: 50))
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    var authData = await TryAcquireAuthorizationAsync(ct, forceRefresh: false).ConfigureAwait(false);
                    if (authData != null)
                    {
                        var userId = authData.DisplayClaims?.xui?[0]?.xid?.ToString();
                        _probeCache.RecordProbe(ProviderKey, true, userId);
                        return AuthProbeResult.AlreadyAuthenticated(userId);
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
                    _logger?.Error(ex, "[XboxAuth] Probe failed with exception.");
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
                windowOpened = await LoginAsync(ct).ConfigureAwait(false);

                if (!windowOpened)
                {
                    progress?.Report(AuthProgressStep.Failed);
                    return AuthProbeResult.Failed(windowOpened: false);
                }

                progress?.Report(AuthProgressStep.WaitingForUserLogin);
                var deadlineUtc = DateTime.UtcNow.Add(InteractiveAuthTimeout);

                while (DateTime.UtcNow < deadlineUtc)
                {
                    ct.ThrowIfCancellationRequested();

                    var authData = await TryAcquireAuthorizationAsync(ct, forceRefresh: true).ConfigureAwait(false);
                    if (authData != null)
                    {
                        var userId = authData.DisplayClaims?.xui?[0]?.xid?.ToString();
                        _probeCache.RecordProbe(ProviderKey, true, userId);
                        progress?.Report(AuthProgressStep.Completed);
                        return AuthProbeResult.Authenticated(userId, windowOpened: windowOpened);
                    }

                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }

                progress?.Report(AuthProgressStep.Failed);
                return AuthProbeResult.TimedOut(windowOpened);
            }
            catch (OperationCanceledException)
            {
                return AuthProbeResult.TimedOut(windowOpened);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[XboxAuth] Interactive auth failed.");
                progress?.Report(AuthProgressStep.Failed);
                return AuthProbeResult.Failed(windowOpened);
            }
        }

        /// <summary>
        /// Clears the session by deleting token files and invalidating cache.
        /// </summary>
        public void ClearSession()
        {
            _logger?.Info("[XboxAuth] Clearing session.");

            // Invalidate cache
            _probeCache.Invalidate(ProviderKey);

            // Clear cached auth data
            _cachedAuthData = null;
            _tokenAcquiredUtc = DateTime.MinValue;

            // Delete token files
            try
            {
                if (File.Exists(_liveTokensPath))
                {
                    File.Delete(_liveTokensPath);
                }
                if (File.Exists(_xstsTokensPath))
                {
                    File.Delete(_xstsTokensPath);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[XboxAuth] Failed to delete token files.");
            }
        }

        public void InvalidateProbeCache()
        {
            _probeCache.Invalidate(ProviderKey);
        }

        // ---------------------------------------------------------------------
        // Authorization Provider Methods
        // ---------------------------------------------------------------------

        /// <summary>
        /// Gets the authorization data needed for API calls.
        /// </summary>
        public async Task<AuthorizationData> GetAuthorizationAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var authData = await TryAcquireAuthorizationAsync(ct, forceRefresh: false).ConfigureAwait(false);
            if (authData != null)
            {
                return authData;
            }

            throw new XboxAuthRequiredException("Xbox authentication required. Please login.");
        }

        // ---------------------------------------------------------------------
        // Private Helper Methods
        // ---------------------------------------------------------------------

        private async Task<bool> LoginAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var callbackUrl = string.Empty;

            try
            {
                // Delete existing token files
                if (File.Exists(_liveTokensPath))
                {
                    File.Delete(_liveTokensPath);
                }

                if (File.Exists(_xstsTokensPath))
                {
                    File.Delete(_xstsTokensPath);
                }

                var loginUrl = string.Format(LoginUrlFormat, ClientId, Uri.EscapeDataString(Scope), Uri.EscapeDataString(RedirectUri));

                var webViewSettings = new WebViewSettings
                {
                    WindowHeight = 560,
                    WindowWidth = 490
                };

                using (var view = _api.WebViews.CreateView(webViewSettings))
                {
                    view.LoadingChanged += (s, e) =>
                    {
                        var url = view.GetCurrentAddress();
                        if (url.Contains("code="))
                        {
                            callbackUrl = url;
                            view.Close();
                        }
                    };

                    // Clear existing cookies
                    view.DeleteDomainCookies(".live.com");
                    view.DeleteDomainCookies(".login.live.com");
                    view.DeleteDomainCookies("live.com");
                    view.DeleteDomainCookies("login.live.com");
                    view.DeleteDomainCookies(".xboxlive.com");
                    view.DeleteDomainCookies(".xbox.com");
                    view.DeleteDomainCookies(".microsoft.com");
                    view.Navigate(loginUrl);
                    view.OpenDialog();
                }

                if (string.IsNullOrEmpty(callbackUrl))
                {
                    return false;
                }

                // Extract authorization code from callback URL
                var rediUri = new Uri(callbackUrl);
                var authorizationCode = GetQueryParam(rediUri.Query, "code");

                if (string.IsNullOrEmpty(authorizationCode))
                {
                    _logger?.Warn("[XboxAch] No authorization code in callback URL.");
                    return false;
                }

                // Exchange authorization code for tokens
                var tokenResponse = await RequestOAuthTokenAsync(authorizationCode).ConfigureAwait(false);

                var liveTokens = new LiveTokens
                {
                    access_token = tokenResponse.access_token,
                    refresh_token = tokenResponse.refresh_token,
                    expires_in = tokenResponse.expires_in,
                    token_type = tokenResponse.token_type,
                    user_id = tokenResponse.user_id,
                    CreationDate = DateTime.Now
                };

                // Save tokens encrypted
                SaveLiveTokens(liveTokens);

                // Authenticate with Xbox Live
                await AuthenticateXboxLiveAsync(liveTokens.access_token).ConfigureAwait(false);

                return true;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[XboxAch] Login failed.");
                return false;
            }
        }

        private async Task<LiveTokens> RequestOAuthTokenAsync(string authorizationCode)
        {
            var requestData = BuildFormData(new Dictionary<string, string>
            {
                { "grant_type", "authorization_code" },
                { "code", authorizationCode }
            });
            return await ExecuteTokenRequestAsync(requestData).ConfigureAwait(false);
        }

        private async Task<LiveTokens> RefreshOAuthTokenAsync(string refreshToken)
        {
            var requestData = BuildFormData(new Dictionary<string, string>
            {
                { "grant_type", "refresh_token" },
                { "refresh_token", refreshToken }
            });
            return await ExecuteTokenRequestAsync(requestData).ConfigureAwait(false);
        }

        private async Task<LiveTokens> ExecuteTokenRequestAsync(string formData)
        {
            using (var client = new HttpClient())
            {
                var response = await client.PostAsync(
                    TokenUrl,
                    new StringContent(formData, Encoding.ASCII, "application/x-www-form-urlencoded")).ConfigureAwait(false);

                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return Serialization.FromJson<LiveTokens>(content);
            }
        }

        private string BuildFormData(Dictionary<string, string> data)
        {
            var allData = new Dictionary<string, string>(data)
            {
                { "scope", Scope },
                { "client_id", ClientId },
                { "redirect_uri", RedirectUri }
            };

            var parts = new List<string>();
            foreach (var kvp in allData)
            {
                parts.Add($"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}");
            }
            return string.Join("&", parts);
        }

        private async Task AuthenticateXboxLiveAsync(string accessToken)
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("x-xbl-contract-version", "1");

                // Authenticate with Xbox Live
                var authRequest = new XboxAuthRequest
                {
                    Properties = { RpsTicket = $"d={accessToken}" }
                };

                var authContent = new StringContent(
                    Serialization.ToJson(authRequest),
                    Encoding.UTF8,
                    "application/json");

                var authResponse = await client.PostAsync(XboxAuthUrl, authContent).ConfigureAwait(false);
                var authResponseContent = await authResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!authResponse.IsSuccessStatusCode)
                {
                    _logger?.Error($"[XboxAch] Xbox auth failed: {authResponse.StatusCode} - {authResponseContent}");
                    throw new Exception("Xbox Live authentication failed.");
                }

                var authTokens = Serialization.FromJson<AuthorizationData>(authResponseContent);

                // Authorize with XSTS
                var xstsRequest = new XstsRequest
                {
                    Properties = { UserTokens = new List<string> { authTokens.Token } }
                };

                var xstsContent = new StringContent(
                    Serialization.ToJson(xstsRequest),
                    Encoding.UTF8,
                    "application/json");

                var xstsResponse = await client.PostAsync(XstsAuthUrl, xstsContent).ConfigureAwait(false);
                var xstsResponseContent = await xstsResponse.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!xstsResponse.IsSuccessStatusCode)
                {
                    // Check for XSTS error
                    var xstsError = Serialization.FromJson<XstsResponse>(xstsResponseContent);
                    var errorCode = xstsError?.XErr?.XErr ?? 0;
                    var errorMessage = xstsError?.XErr?.Message ?? "Unknown error";

                    _logger?.Error($"[XboxAch] XSTS authorization failed: {errorCode} - {errorMessage}");

                    if (errorCode == 2148916233)
                    {
                        throw new Exception("The account doesn't have an Xbox account.");
                    }
                    else if (errorCode == 2148916235)
                    {
                        throw new Exception("Xbox Live is not available in your region.");
                    }
                    else if (errorCode == 2148916236 || errorCode == 2148916237)
                    {
                        throw new Exception("Adult verification required. Please login to Xbox.com to verify your account.");
                    }
                    else if (errorCode == 2148916238)
                    {
                        throw new Exception("The account belongs to a minor without Xbox profile.");
                    }

                    throw new Exception($"XSTS authorization failed: {errorMessage}");
                }

                // Save XSTS tokens encrypted
                var currentUser = WindowsIdentity.GetCurrent().User?.Value;
                if (!string.IsNullOrEmpty(currentUser))
                {
                    Encryption.EncryptToFile(
                        _xstsTokensPath,
                        xstsResponseContent,
                        Encoding.UTF8,
                        currentUser);
                }
            }
        }

        private async Task<bool> CheckAuthenticationAsync()
        {
            try
            {
                var authData = LoadXstsTokens();
                if (authData == null)
                {
                    return false;
                }

                // Verify session is still valid
                using (var client = new HttpClient())
                {
                    SetAuthenticationHeaders(client.DefaultRequestHeaders, authData);

                    var profileRequest = new ProfileRequest
                    {
                        settings = new List<string> { "GameDisplayName" },
                        userIds = new List<ulong> { ulong.Parse(authData.DisplayClaims.xui[0].xid) }
                    };

                    var response = await client.PostAsync(
                        ProfileUrl,
                        new StringContent(Serialization.ToJson(profileRequest), Encoding.UTF8, "application/json")).ConfigureAwait(false);

                    return response.IsSuccessStatusCode;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[XboxAch] Check authentication failed.");
                return false;
            }
        }

        private async Task<AuthorizationData> TryAcquireAuthorizationAsync(CancellationToken ct, bool forceRefresh)
        {
            // Check if we have fresh cached authorization data
            if (!forceRefresh && HasFreshCachedAuthorization())
            {
                return _cachedAuthData;
            }

            await _tokenSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!forceRefresh && HasFreshCachedAuthorization())
                {
                    return _cachedAuthData;
                }

                // Try loading existing tokens
                var authData = LoadXstsTokens();
                if (authData == null)
                {
                    ClearCachedAuthorization();
                    return null;
                }

                // Check if session is valid
                var isValid = await CheckAuthenticationAsync().ConfigureAwait(false);
                if (!isValid)
                {
                    // Try refreshing tokens
                    var liveTokens = LoadLiveTokens();
                    if (liveTokens != null && !string.IsNullOrEmpty(liveTokens.refresh_token))
                    {
                        try
                        {
                            var refreshedTokens = await RefreshOAuthTokenAsync(liveTokens.refresh_token).ConfigureAwait(false);
                            liveTokens.access_token = refreshedTokens.access_token;
                            liveTokens.refresh_token = refreshedTokens.refresh_token;
                            SaveLiveTokens(liveTokens);

                            await AuthenticateXboxLiveAsync(liveTokens.access_token).ConfigureAwait(false);
                            authData = LoadXstsTokens();
                        }
                        catch (Exception ex)
                        {
                            _logger?.Debug(ex, "[XboxAuth] Token refresh failed.");
                        }
                    }

                    isValid = await CheckAuthenticationAsync().ConfigureAwait(false);
                    if (!isValid)
                    {
                        ClearCachedAuthorization();
                        return null;
                    }
                }

                SetCachedAuthorization(authData);
                return authData;
            }
            finally
            {
                _tokenSemaphore.Release();
            }
        }

        private bool HasFreshCachedAuthorization()
        {
            return _cachedAuthData != null &&
                   (DateTime.UtcNow - _tokenAcquiredUtc) < CachedTokenLifetime;
        }

        private void SetCachedAuthorization(AuthorizationData authData)
        {
            if (authData == null)
            {
                ClearCachedAuthorization();
                return;
            }

            _cachedAuthData = authData;
            _tokenAcquiredUtc = DateTime.UtcNow;
        }

        private void ClearCachedAuthorization()
        {
            _cachedAuthData = null;
            _tokenAcquiredUtc = DateTime.MinValue;
        }

        private void SaveLiveTokens(LiveTokens tokens)
        {
            try
            {
                var directory = Path.GetDirectoryName(_liveTokensPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var currentUser = WindowsIdentity.GetCurrent().User?.Value;
                if (!string.IsNullOrEmpty(currentUser))
                {
                    Encryption.EncryptToFile(
                        _liveTokensPath,
                        Serialization.ToJson(tokens),
                        Encoding.UTF8,
                        currentUser);
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[XboxAuth] Failed to save Live tokens.");
            }
        }

        private LiveTokens LoadLiveTokens()
        {
            try
            {
                if (!File.Exists(_liveTokensPath))
                {
                    return null;
                }

                var currentUser = WindowsIdentity.GetCurrent().User?.Value;
                if (string.IsNullOrEmpty(currentUser))
                {
                    return null;
                }

                var json = Encryption.DecryptFromFile(_liveTokensPath, Encoding.UTF8, currentUser);
                return Serialization.FromJson<LiveTokens>(json);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[XboxAuth] Failed to load Live tokens.");
                return null;
            }
        }

        private AuthorizationData LoadXstsTokens()
        {
            try
            {
                if (!File.Exists(_xstsTokensPath))
                {
                    return null;
                }

                var currentUser = WindowsIdentity.GetCurrent().User?.Value;
                if (string.IsNullOrEmpty(currentUser))
                {
                    return null;
                }

                var json = Encryption.DecryptFromFile(_xstsTokensPath, Encoding.UTF8, currentUser);
                return Serialization.FromJson<AuthorizationData>(json);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[XboxAuth] Failed to load XSTS tokens.");
                return null;
            }
        }

        // ---------------------------------------------------------------------
        // Legacy Type Conversion
        // ---------------------------------------------------------------------

        private XboxAuthResult ConvertToLegacyResult(AuthProbeResult result)
        {
            var outcome = result.Outcome switch
            {
                AuthOutcome.AlreadyAuthenticated => XboxAuthOutcome.AlreadyAuthenticated,
                AuthOutcome.Authenticated => XboxAuthOutcome.Authenticated,
                AuthOutcome.NotAuthenticated => XboxAuthOutcome.NotAuthenticated,
                AuthOutcome.Cancelled => XboxAuthOutcome.Cancelled,
                AuthOutcome.TimedOut => XboxAuthOutcome.TimedOut,
                AuthOutcome.Failed => XboxAuthOutcome.Failed,
                AuthOutcome.ProbeFailed => XboxAuthOutcome.ProbeFailed,
                _ => XboxAuthOutcome.Failed
            };

            return XboxAuthResult.Create(
                outcome,
                result.MessageKey,
                result.WindowOpened);
        }

        /// <summary>
        /// Extracts a query parameter value from a URL query string.
        /// </summary>
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

        /// <summary>
        /// Sets authentication headers for an HTTP request.
        /// </summary>
        public static void SetAuthenticationHeaders(
            System.Net.Http.Headers.HttpRequestHeaders headers,
            AuthorizationData auth,
            string contractVersion = "2",
            string acceptLanguage = "en-US")
        {
            headers.Add("x-xbl-contract-version", contractVersion);
            headers.Add("Authorization", $"XBL3.0 x={auth.DisplayClaims.xui[0].uhs};{auth.Token}");
            headers.Add("Accept-Language", acceptLanguage);
        }
    }
}
