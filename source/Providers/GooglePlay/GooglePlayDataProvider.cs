using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.GooglePlay
{
    public sealed class GooglePlayDataProvider : IDataProvider
    {
        private GooglePlaySettings _providerSettings;

        public GooglePlayDataProvider(ILogger logger, PlayniteAchievementsSettings settings, IPlayniteAPI playniteApi)
        {
            _ = logger ?? throw new ArgumentNullException(nameof(logger));
            _ = settings ?? throw new ArgumentNullException(nameof(settings));
            _ = playniteApi ?? throw new ArgumentNullException(nameof(playniteApi));

            _providerSettings = ProviderRegistry.Settings<GooglePlaySettings>();
        }

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_GooglePlay");
        public string ProviderKey => "GooglePlay";
        public string ProviderIconKey => "ProviderIconGooglePlay";
        public string ProviderColorHex => "#0F9D58";
        public bool IsAuthenticated => false;
        public ISessionManager AuthSession => null;

        public bool IsCapable(Game game) => false;

        public Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            return Task.FromResult(new RebuildPayload
            {
                Summary = new RebuildSummary()
            });
        }

        public IProviderSettings GetSettings() => _providerSettings;

        public void ApplySettings(IProviderSettings settings)
        {
            if (settings is GooglePlaySettings googlePlaySettings)
            {
                _providerSettings.CopyFrom(googlePlaySettings);
            }
        }

        public ProviderSettingsViewBase CreateSettingsView() => null;
    }
}
