using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Apple
{
    public sealed class AppleDataProvider : IDataProvider
    {
        private AppleSettings _providerSettings;

        public AppleDataProvider(ILogger logger, PlayniteAchievementsSettings settings, IPlayniteAPI playniteApi)
        {
            _ = logger ?? throw new ArgumentNullException(nameof(logger));
            _ = settings ?? throw new ArgumentNullException(nameof(settings));
            _ = playniteApi ?? throw new ArgumentNullException(nameof(playniteApi));

            _providerSettings = ProviderRegistry.Settings<AppleSettings>();
        }

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_Apple");
        public string ProviderKey => "Apple";
        public string ProviderIconKey => "ProviderIconApple";
        public string ProviderColorHex => "#E5E5E5";
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
            if (settings is AppleSettings appleSettings)
            {
                _providerSettings.CopyFrom(appleSettings);
            }
        }

        public ProviderSettingsViewBase CreateSettingsView() => null;
    }
}
