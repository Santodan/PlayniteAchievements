using System;
using System.Collections.Generic;
using Playnite.SDK;
#if !TEST
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Library;
#endif
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Widgets;

namespace PlayniteAchievements.Services.StartPage
{
    /// <summary>
    /// Compatibility facade for existing StartPage views. New widget hosts use the
    /// shared <see cref="WidgetDataCoordinator"/> contract.
    /// </summary>
    public sealed class StartPageDataCoordinator : WidgetDataCoordinator
    {
#if !TEST
        internal StartPageDataCoordinator(
            AchievementDataService achievementDataService,
            LibraryProjectionService libraryProjectionService,
            IReadOnlyList<IDataProvider> providers,
            IPlayniteAPI playniteApi,
            ILogger logger,
            PlayniteAchievementsSettings settings,
            Func<List<PlayniteAchievements.Models.Friends.FriendIdentity>> currentUserIdentityLoader = null)
            : base(
                achievementDataService,
                libraryProjectionService,
                providers,
                playniteApi,
                logger,
                settings,
                currentUserIdentityLoader)
        {
        }
#endif

        public StartPageDataCoordinator(
            Func<OverviewDataSnapshot> snapshotFactory,
            ILogger logger = null)
            : base(snapshotFactory, logger)
        {
        }
    }
}
