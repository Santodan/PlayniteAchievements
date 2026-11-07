using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.Services
{
    public partial class BackgroundUpdater
    {
        private Task WaitForSantodanGameplayAsync(CancellationToken token)
        {
            if (!_settings.Persisted.EnablePeriodicUpdates && !IsFriendsUpdateConfigured())
                return Task.CompletedTask;
            return GameplayRefreshDeferral.WaitAsync(
                () => ProviderRegistry.Settings<LocalSettings>().DeferScheduledRefreshesDuringGameplay &&
                    PlayniteAchievementsPlugin.Instance?.PlayniteApi?.Database?.Games?.Any(game => game.IsRunning) == true,
                ct => Task.Delay(TimeSpan.FromSeconds(15), ct), token);
        }
    }

}
