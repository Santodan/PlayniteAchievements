using PlayniteAchievements.Models;

namespace PlayniteAchievements.Services.ThemeIntegration
{
    internal static class ThemeDelegatedPropertyCatalog
    {
        public static readonly string[] CompatibilityAllGames =
        {
            nameof(PlayniteAchievementsSettings.AllGamesWithAchievements),
            nameof(PlayniteAchievementsSettings.PlatinumGames),
            nameof(PlayniteAchievementsSettings.PlatinumGamesAscending),
            nameof(PlayniteAchievementsSettings.GSTotal),
            nameof(PlayniteAchievementsSettings.GSPlat),
            nameof(PlayniteAchievementsSettings.GS90),
            nameof(PlayniteAchievementsSettings.GS30),
            nameof(PlayniteAchievementsSettings.GS15),
            nameof(PlayniteAchievementsSettings.GSScore),
            nameof(PlayniteAchievementsSettings.GSLevel),
            nameof(PlayniteAchievementsSettings.GSLevelProgress),
            nameof(PlayniteAchievementsSettings.GSRank)
        };

        // NOTE: PlatinumGames is notified via compatibility surface only to avoid duplicate notifications.
        public static readonly string[] ModernAllGamesCore =
        {
            nameof(PlayniteAchievementsSettings.HasDataAllGames),
            nameof(PlayniteAchievementsSettings.CompletedGamesAsc),
            nameof(PlayniteAchievementsSettings.CompletedGamesDesc),
            nameof(PlayniteAchievementsSettings.GameSummariesAsc),
            nameof(PlayniteAchievementsSettings.GameSummariesDesc),
            nameof(PlayniteAchievementsSettings.GamesWithAchievements),
            nameof(PlayniteAchievementsSettings.TotalTrophies),
            nameof(PlayniteAchievementsSettings.PlatinumTrophies),
            nameof(PlayniteAchievementsSettings.GoldTrophies),
            nameof(PlayniteAchievementsSettings.SilverTrophies),
            nameof(PlayniteAchievementsSettings.BronzeTrophies),
            nameof(PlayniteAchievementsSettings.TotalCommon),
            nameof(PlayniteAchievementsSettings.TotalUncommon),
            nameof(PlayniteAchievementsSettings.TotalRare),
            nameof(PlayniteAchievementsSettings.TotalUltraRare),
            nameof(PlayniteAchievementsSettings.TotalRareAndUltraRare),
            nameof(PlayniteAchievementsSettings.TotalOverall),
            nameof(PlayniteAchievementsSettings.Level),
            nameof(PlayniteAchievementsSettings.LevelProgress),
            nameof(PlayniteAchievementsSettings.Rank),
            nameof(PlayniteAchievementsSettings.MostRecentUnlocksTop3),
            nameof(PlayniteAchievementsSettings.MostRecentUnlocksTop5),
            nameof(PlayniteAchievementsSettings.MostRecentUnlocksTop10),
            nameof(PlayniteAchievementsSettings.RarestRecentUnlocksTop3),
            nameof(PlayniteAchievementsSettings.RarestRecentUnlocksTop5),
            nameof(PlayniteAchievementsSettings.RarestRecentUnlocksTop10),
            nameof(PlayniteAchievementsSettings.SteamGames),
            nameof(PlayniteAchievementsSettings.GOGGames),
            nameof(PlayniteAchievementsSettings.EpicGames),
            nameof(PlayniteAchievementsSettings.XboxGames),
            nameof(PlayniteAchievementsSettings.PSNGames),
            nameof(PlayniteAchievementsSettings.RetroAchievementsGames),
            nameof(PlayniteAchievementsSettings.RPCS3Games),
            nameof(PlayniteAchievementsSettings.XeniaGames),
            nameof(PlayniteAchievementsSettings.ShadPS4Games),
            nameof(PlayniteAchievementsSettings.ManualGames)
        };

        public static readonly string[] ModernAllGamesHeavy =
        {
            nameof(PlayniteAchievementsSettings.AllAchievementsUnlockAsc),
            nameof(PlayniteAchievementsSettings.AllAchievementsUnlockDesc),
            nameof(PlayniteAchievementsSettings.AllAchievementsRarityAsc),
            nameof(PlayniteAchievementsSettings.AllAchievementsRarityDesc),
            nameof(PlayniteAchievementsSettings.MostRecentUnlocks),
            nameof(PlayniteAchievementsSettings.RarestRecentUnlocks)
        };

        public static readonly string[] SingleGameTheme =
        {
            nameof(PlayniteAchievementsSettings.HasData),
            nameof(PlayniteAchievementsSettings.HasAchievements),
            nameof(PlayniteAchievementsSettings.AchievementCount),
            nameof(PlayniteAchievementsSettings.UnlockedCount),
            nameof(PlayniteAchievementsSettings.LockedCount),
            nameof(PlayniteAchievementsSettings.ProgressPercentage),
            nameof(PlayniteAchievementsSettings.IsCompleted)
        };

        public static readonly string[] SingleGameLegacy =
        {
            nameof(PlayniteAchievementsSettings.HasDataLegacy),
            nameof(PlayniteAchievementsSettings.Total),
            nameof(PlayniteAchievementsSettings.Unlocked),
            nameof(PlayniteAchievementsSettings.Percent),
            nameof(PlayniteAchievementsSettings.Is100Percent),
            nameof(PlayniteAchievementsSettings.Locked),
            nameof(PlayniteAchievementsSettings.Common),
            nameof(PlayniteAchievementsSettings.Uncommon),
            nameof(PlayniteAchievementsSettings.Rare),
            nameof(PlayniteAchievementsSettings.UltraRare),
            nameof(PlayniteAchievementsSettings.RareAndUltraRare),
            nameof(PlayniteAchievementsSettings.ListAchievements),
            nameof(PlayniteAchievementsSettings.ListAchUnlockDateAsc),
            nameof(PlayniteAchievementsSettings.ListAchUnlockDateDesc)
        };
    }
}
