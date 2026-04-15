namespace PlayniteAchievements.Models.ThemeIntegration
{
    internal static class DynamicThemeViewKeys
    {
        public const string All = "All";
        public const string Unlocked = "Unlocked";
        public const string Locked = "Locked";
        public const string Default = "Default";
        public const string UnlockTime = "UnlockTime";
        public const string Rarity = "Rarity";
        public const string LastUnlock = "LastUnlock";
        public const string LastPlayed = "LastPlayed";
        public const string UnlockedCount = "UnlockedCount";
        public const string Ascending = "Ascending";
        public const string Descending = "Descending";
    }

    internal sealed class SelectedGameAchievementViewState
    {
        public string FilterKey { get; set; } = DynamicThemeViewKeys.All;

        public string SortKey { get; set; } = DynamicThemeViewKeys.Default;

        public string SortDirectionKey { get; set; } = DynamicThemeViewKeys.Descending;
    }

    internal sealed class LibraryAchievementViewState
    {
        public string ProviderKey { get; set; } = DynamicThemeViewKeys.All;

        public string SortKey { get; set; } = DynamicThemeViewKeys.UnlockTime;

        public string SortDirectionKey { get; set; } = DynamicThemeViewKeys.Descending;
    }

    internal sealed class GameSummaryViewState
    {
        public string ProviderKey { get; set; } = DynamicThemeViewKeys.All;

        public string SortKey { get; set; } = DynamicThemeViewKeys.LastUnlock;

        public string SortDirectionKey { get; set; } = DynamicThemeViewKeys.Descending;
    }
}
