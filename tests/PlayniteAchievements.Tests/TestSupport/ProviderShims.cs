using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Settings;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK.Models;

namespace PlayniteAchievements.Providers
{
    public interface IDataProvider
    {
        string ProviderName { get; }
        string ProviderKey { get; }
        string ProviderIconKey { get; }
        string ProviderColorHex { get; }
        bool IsCapable(Game game);
        bool IsAuthenticated { get; }
        ISessionManager AuthSession { get; }
        Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel);
        IProviderSettings GetSettings();
        void ApplySettings(IProviderSettings settings);
        ProviderSettingsViewBase CreateSettingsView();
    }
}

namespace PlayniteAchievements.Providers.Epic
{
    public class EpicSessionManager
    {
        public string AccessToken { get; set; }
        public string AccountId { get; set; }
        public bool TryRefreshTokenResult { get; set; }

        public Task<string> GetAccessTokenAsync(CancellationToken ct)
        {
            return Task.FromResult(AccessToken);
        }

        public string GetAccountId()
        {
            return AccountId;
        }

        public Task<bool> TryRefreshTokenAsync(CancellationToken ct)
        {
            return Task.FromResult(TryRefreshTokenResult);
        }
    }
}

namespace PlayniteAchievements.Providers.GOG
{
    public class GogSessionManager
    {
        public string AccessToken { get; set; }

        public string GetAccessToken()
        {
            return AccessToken;
        }
    }
}

namespace PlayniteAchievements.Providers.Exophase
{
    public sealed class ExophaseCookieSnapshotStore
    {
        public bool TryLoad(out List<HttpCookie> cookies)
        {
            cookies = new List<HttpCookie>();
            return false;
        }

        public static List<string> GetMissingCriticalCookies(IReadOnlyCollection<HttpCookie> cookies)
        {
            return new List<string>();
        }
    }

    public class ExophaseSessionManager : ISessionManager
    {
        public string ProviderKey => "Exophase";

        public bool IsAuthenticated { get; set; }

        public AuthProbeResult ProbeResult { get; set; } = AuthProbeResult.NotAuthenticated();

        public int ProbeCallCount { get; private set; }

        public int LoadCookiesCallCount { get; private set; }

        public Action<CookieContainer> CookieLoader { get; set; }

        public ExophaseCookieSnapshotStore CookieSnapshotStore { get; set; } = new ExophaseCookieSnapshotStore();

        public Task<AuthProbeResult> ProbeAuthStateAsync(CancellationToken ct)
        {
            ProbeCallCount++;
            return Task.FromResult(ProbeResult);
        }

        public Task<AuthProbeResult> AuthenticateInteractiveAsync(
            bool forceInteractive,
            CancellationToken ct,
            IProgress<AuthProgressStep> progress = null)
        {
            return Task.FromResult(ProbeResult);
        }

        public void ClearSession()
        {
        }

        public void LoadCefCookiesIntoJar(CookieContainer cookieJar)
        {
            LoadCookiesCallCount++;
            CookieLoader?.Invoke(cookieJar);
        }
    }
}
