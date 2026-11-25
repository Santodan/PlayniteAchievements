namespace PlayniteAchievements.Models.Settings
{
    public enum StartPageWidgetKind
    {
        GameSummariesGrid = 0,
        RecentUnlocksGrid = 1,
        CompletedGamesPie = 2,
        ProviderPie = 3,
        RarityPie = 4,
        TrophyPie = 5,
        CollectionScoreCard = 6,
        PrestigeScoreCard = 7,
        ShowcaseProfile = 9,
        ShowcaseDualScores = 10,
        ShowcaseTimeline = 11,
        ShowcaseStatistics = 12,
        ShowcaseNativePoints = 13,
        // 8 (FriendsRecentUnlocksGrid) is retired; the friend unlocks widget took its place.
        // 14 (ShowcasePinnedAchievements), 15 (ShowcaseFavoriteGames), and 19
        // (ShowcaseGameMosaic) are retired; their widgets collapsed into the grid and mosaic
        // kinds. Do not reuse the numbers.
        ShowcaseIconMosaic = 16,
        ShowcaseScreenshotSlideshow = 17,
        ShowcaseActivityCalendar = 18,
        ShowcaseImage = 20,
        ShowcaseText = 21,
        ShowcaseFriendUnlocks = 22,
        ShowcaseFriendLeaderboard = 23,
        ShowcaseFriendGames = 24
    }
}
