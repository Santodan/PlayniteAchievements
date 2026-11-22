using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database;
using System;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// What the library editor reads its rows from: the resolved achievement reads and their change
    /// feed, and the hydrated game data its single-game mode loads, as the Manage window's does.
    /// </summary>
    internal sealed class EditorLibrarySources
    {
        public EditorLibrarySources(ICacheReadOptimizations reads, AchievementDataService dataService, IReadScheduler scheduler)
        {
            Reads = reads ?? throw new ArgumentNullException(nameof(reads));
            DataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
            Scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        }

        public ICacheReadOptimizations Reads { get; }

        public AchievementDataService DataService { get; }

        public IReadScheduler Scheduler { get; }
    }
}
