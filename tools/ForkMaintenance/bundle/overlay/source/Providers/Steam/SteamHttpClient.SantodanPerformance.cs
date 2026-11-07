using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.Providers.Steam
{
    public sealed partial class SteamHttpClient
    {
        private bool _santodanCookiesDeferred;

        private void InitializeSantodanSteamCookies()
        {
            _santodanCookiesDeferred = ProviderRegistry.Settings<LocalSettings>().DeferSteamCefCookieLoading;
            if (!_santodanCookiesDeferred) LoadCookiesFromCefIntoJar();
        }

        private void EnsureSantodanDeferredCookiesLoaded()
        {
            // The direct token resolver deliberately skips the session probe, but still
            // needs the cookie import that used to happen in the constructor. Normal
            // EnsureSessionAsync already imports cookies and clears the deferred flag.
            lock (_cookieLock)
            {
                if (_santodanCookiesDeferred) LoadCookiesFromCefIntoJar();
            }
        }
    }
}
