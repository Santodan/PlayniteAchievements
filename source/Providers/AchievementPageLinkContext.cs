using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Providers
{
    public sealed class AchievementPageLinkContext
    {
        public AchievementPageLinkContext(
            Game game,
            GameAchievementData gameData,
            GameAchievementData rawGameData,
            ManualAchievementLink manualLink)
        {
            Game = game;
            GameData = gameData;
            RawGameData = rawGameData;
            ManualLink = manualLink;
        }

        public Game Game { get; }

        public GameAchievementData GameData { get; }

        public GameAchievementData RawGameData { get; }

        public ManualAchievementLink ManualLink { get; }

        public GameAchievementData BestGameData => GameData ?? RawGameData;

        public string CachedProviderKey => BestGameData?.ProviderKey;
    }
}
