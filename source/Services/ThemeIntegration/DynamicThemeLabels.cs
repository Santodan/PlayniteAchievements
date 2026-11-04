using Playnite.SDK;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.ThemeIntegration
{
    internal static class DynamicThemeLabels
    {
        private static readonly IReadOnlyDictionary<string, (string ResourceKey, string Fallback)> LabelMap =
            CreateLabelMap(
                (DynamicThemeViewKeys.All, "LOCPlayAch_Common_All", DynamicThemeViewKeys.All),
                (DynamicThemeViewKeys.Unlocked, "LOCPlayAch_Common_Unlocked", DynamicThemeViewKeys.Unlocked),
                (DynamicThemeViewKeys.Locked, "LOCPlayAch_Common_Locked", DynamicThemeViewKeys.Locked),
                (DynamicThemeViewKeys.Visible, "LOCPlayAch_Dynamic_Visible", "Visible"),
                (DynamicThemeViewKeys.Hidden, "LOCPlayAch_Filter_Hidden", "Hidden"),
                (DynamicThemeViewKeys.InProgress, "LOCPlayAch_Filter_InProgress", "In Progress"),
                (DynamicThemeViewKeys.NoProgress, "LOCPlayAch_Filter_NoProgress", "No Progress"),
                (DynamicThemeViewKeys.HasNotes, "LOCPlayAch_ManageAchievements_Notes_WithNotes", "Has Notes"),
                (DynamicThemeViewKeys.NoNotes, "LOCPlayAch_ManageAchievements_Notes_WithoutNotes", "No Notes"),
                (DynamicThemeViewKeys.Capstone, "LOCPlayAch_Dynamic_Capstone", "Capstone"),
                (DynamicThemeViewKeys.Common, "LOCPlayAch_Rarity_Common", DynamicThemeViewKeys.Common),
                (DynamicThemeViewKeys.Uncommon, "LOCPlayAch_Rarity_Uncommon", DynamicThemeViewKeys.Uncommon),
                (DynamicThemeViewKeys.Rare, "LOCPlayAch_Rarity_Rare", DynamicThemeViewKeys.Rare),
                (DynamicThemeViewKeys.UltraRare, "LOCPlayAch_Rarity_UltraRare", "Ultra Rare"),
                (DynamicThemeViewKeys.Platinum, "LOCPlayAch_Trophy_Platinum", DynamicThemeViewKeys.Platinum),
                (DynamicThemeViewKeys.Gold, "LOCPlayAch_Trophy_Gold", DynamicThemeViewKeys.Gold),
                (DynamicThemeViewKeys.Silver, "LOCPlayAch_Trophy_Silver", DynamicThemeViewKeys.Silver),
                (DynamicThemeViewKeys.Bronze, "LOCPlayAch_Trophy_Bronze", DynamicThemeViewKeys.Bronze),
                ("Base", "LOCPlayAch_CategoryKind_Base", "Base"),
                ("DLC", "LOCPlayAch_CategoryKind_DLC", "DLC"),
                ("Update", "LOCPlayAch_CategoryKind_Update", "Update"),
                ("Subset", "LOCPlayAch_CategoryKind_Subset", "Subset"),
                ("Singleplayer", "LOCPlayAch_AchievementTag_Singleplayer", "Singleplayer"),
                ("Multiplayer", "LOCPlayAch_AchievementTag_Multiplayer", "Multiplayer"),
                ("Progression", "LOCPlayAch_AchievementTag_Progression", "Progression"),
                ("SideQuest", "LOCPlayAch_AchievementTag_SideQuest", "Side Quest"),
                ("WinCondition", "LOCPlayAch_AchievementTag_WinCondition", "Win Condition"),
                ("PostGame", "LOCPlayAch_AchievementTag_PostGame", "Post Game"),
                ("Completion", "LOCPlayAch_AchievementTag_Completion", "Completion"),
                ("Collectable", "LOCPlayAch_AchievementTag_Collectable", "Collectable"),
                ("Cumulative", "LOCPlayAch_AchievementTag_Cumulative", "Cumulative"),
                ("Challenge", "LOCPlayAch_AchievementTag_Challenge", "Challenge"),
                ("Difficulty", "LOCPlayAch_AchievementTag_Difficulty", "Difficulty"),
                ("Stackable", "LOCPlayAch_AchievementTag_Stackable", "Stackable"),
                ("Missable", "LOCPlayAch_AchievementTag_Missable", "Missable"),
                ("Unobtainable", "LOCPlayAch_AchievementTag_Unobtainable", "Unobtainable"),
                ("Miscellaneous", "LOCPlayAch_AchievementTag_Miscellaneous", "Miscellaneous"),
                ("Softcore", "LOCPlayAch_AchievementTag_Softcore", "Softcore"),
                ("Hardcore", "LOCPlayAch_AchievementTag_Hardcore", "Hardcore"),
                (DynamicThemeViewKeys.Completed, "LOCPlayAch_Completed", DynamicThemeViewKeys.Completed),
                (DynamicThemeViewKeys.Incomplete, "LOCPlayAch_Overview_Incomplete", DynamicThemeViewKeys.Incomplete),
                (DynamicThemeViewKeys.Started, "LOCPlayAch_Dynamic_Started", DynamicThemeViewKeys.Started),
                (DynamicThemeViewKeys.NotStarted, "LOCPlayAch_Dynamic_NotStarted", "Not Started"),
                (DynamicThemeViewKeys.Played, "LOCPlayAch_Filter_Played", "Played"),
                (DynamicThemeViewKeys.Unplayed, "LOCPlayAch_Filter_Unplayed", "Unplayed"),
                (DynamicThemeViewKeys.HasLastUnlock, "LOCPlayAch_Dynamic_HasLastUnlock", "Has Last Unlock"),
                (DynamicThemeViewKeys.NoLastUnlock, "LOCPlayAch_Dynamic_NoLastUnlock", "No Last Unlock"),
                (DynamicThemeViewKeys.Default, "LOCPlayAch_Common_Default", DynamicThemeViewKeys.Default),
                (DynamicThemeViewKeys.Name, "LOCPlayAch_Common_Name", DynamicThemeViewKeys.Name),
                (DynamicThemeViewKeys.Game, "LOCPlayAch_Column_Game", DynamicThemeViewKeys.Game),
                (DynamicThemeViewKeys.Provider, "LOCPlayAch_Common_Platform", DynamicThemeViewKeys.Provider),
                (DynamicThemeViewKeys.Progress, "LOCPlayAch_Progress", DynamicThemeViewKeys.Progress),
                (DynamicThemeViewKeys.AchievementCount, "LOCPlayAch_Column_Total", "Achievement Count"),
                (DynamicThemeViewKeys.SharedGamesCount, "LOCPlayAch_Overview_GameSummaries", "Games"),
                (DynamicThemeViewKeys.Status, "LOCPlayAch_Column_Status", DynamicThemeViewKeys.Status),
                (DynamicThemeViewKeys.RarityPercent, "LOCPlayAch_Column_RarityPercent", "Rarity Percent"),
                (DynamicThemeViewKeys.Points, "LOCPlayAch_Column_Points", DynamicThemeViewKeys.Points),
                (DynamicThemeViewKeys.CollectionScore, "LOCPlayAch_Score_Collection", "Collection Score"),
                (DynamicThemeViewKeys.PrestigeScore, "LOCPlayAch_Score_Prestige", "Prestige Score"),
                (DynamicThemeViewKeys.TrophyType, "LOCPlayAch_Column_Trophy", "Trophy Type"),
                (DynamicThemeViewKeys.CategoryType, "LOCPlayAch_Common_Tags", "Tags"),
                (DynamicThemeViewKeys.CategoryLabel, "LOCPlayAch_Common_Category", "Category"),
                (DynamicThemeViewKeys.Notes, "LOCPlayAch_NotesDialog_ViewTitle", "Notes"),
                (DynamicThemeViewKeys.UnlockTime, "LOCPlayAch_Common_UnlockTime", DynamicThemeViewKeys.UnlockTime),
                (DynamicThemeViewKeys.Rarity, "LOCPlayAch_Column_Rarity", DynamicThemeViewKeys.Rarity),
                (DynamicThemeViewKeys.LastUnlock, "LOCPlayAch_Common_LastUnlock", "Last Unlock"),
                (DynamicThemeViewKeys.LastPlayed, "LOCPlayAch_Common_LastPlayed", "Last Played"),
                (DynamicThemeViewKeys.UnlockedCount, "LOCPlayAch_Common_UnlockedCount", "Unlocked Count"),
                (DynamicThemeViewKeys.Ascending, "LOCPlayAch_Common_Ascending", DynamicThemeViewKeys.Ascending),
                (DynamicThemeViewKeys.Descending, "LOCPlayAch_Common_Descending", DynamicThemeViewKeys.Descending));

        public static string GetLabel(string key, string defaultKey)
        {
            var filterKeys = DynamicThemeFilterExpression.Enumerate(key).ToList();
            if (filterKeys.Count > 1)
            {
                return string.Join(" + ", filterKeys.Select(filterKey => GetLabel(filterKey, filterKey)));
            }

            // Kinds and tags name themselves, so a renamed or user-made tag shows its current name.
            if (Services.Achievements.AchievementClassificationFilter.IsFilterKey(key) &&
                !string.Equals(key, DynamicThemeViewKeys.All, StringComparison.OrdinalIgnoreCase))
            {
                return Services.Achievements.AchievementClassificationFilter.GetDisplayName(key);
            }

            if (!LabelMap.TryGetValue(key ?? string.Empty, out var localized) &&
                !LabelMap.TryGetValue(defaultKey, out localized))
            {
                return key ?? defaultKey ?? string.Empty;
            }

            return L(localized.ResourceKey, localized.Fallback);
        }

        public static string GetProviderLabel(string key)
        {
            if (string.IsNullOrWhiteSpace(key) ||
                string.Equals(key, DynamicThemeViewKeys.All, StringComparison.OrdinalIgnoreCase))
            {
                return L("LOCPlayAch_Common_All", DynamicThemeViewKeys.All);
            }

            return ProviderRegistry.GetLocalizedName(key);
        }

        private static IReadOnlyDictionary<string, (string ResourceKey, string Fallback)> CreateLabelMap(
            params (string Key, string ResourceKey, string Fallback)[] labels)
        {
            var map = new Dictionary<string, (string ResourceKey, string Fallback)>(StringComparer.Ordinal);
            foreach (var label in labels ?? Array.Empty<(string Key, string ResourceKey, string Fallback)>())
            {
                if (!string.IsNullOrWhiteSpace(label.Key))
                {
                    map[label.Key] = (label.ResourceKey, label.Fallback);
                }
            }

            return map;
        }

        private static string L(string key, string fallback)
        {
            var value = ResourceProvider.GetString(key);
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            if (value.Length > 4 &&
                value.StartsWith("<!", StringComparison.Ordinal) &&
                value.EndsWith("!>", StringComparison.Ordinal))
            {
                return fallback;
            }

            return value;
        }
    }
}
