using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Seeds the typed GridOptions catalog from the older flat per-grid settings. Legacy
    /// settings remain readable through JsonIgnore compatibility properties after load, but
    /// all copied values are persisted through the catalog.
    /// </summary>
    public static class GridOptionsSettingsMigration
    {
        private static readonly (string GroupName, string Id, string Prefix)[] ColumnSets =
        {
            (nameof(GridOptionsCatalog.Achievement), GridOptionKeys.Achievement.Default, "DataGrid"),
            (nameof(GridOptionsCatalog.Achievement), GridOptionKeys.Achievement.SingleGame, "SingleGame"),
            (nameof(GridOptionsCatalog.Achievement), GridOptionKeys.Achievement.OverviewRecent, "OverviewRecentAchievement"),
            (nameof(GridOptionsCatalog.Achievement), GridOptionKeys.Achievement.OverviewSelectedGame, "OverviewSelectedGameAchievement"),
            (nameof(GridOptionsCatalog.Achievement), GridOptionKeys.Achievement.FriendsOverviewRecent, "FriendsOverviewAchievement"),
            (nameof(GridOptionsCatalog.Achievement), GridOptionKeys.Achievement.ViewFriendsAchievements, "ViewFriendsAchievements"),
            (nameof(GridOptionsCatalog.Achievement), GridOptionKeys.Achievement.StartPageRecent, "StartPageAchievement"),
            (nameof(GridOptionsCatalog.Achievement), GridOptionKeys.Achievement.StartPageFriendAchievements, "StartPageFriendAchievement"),
            (nameof(GridOptionsCatalog.Achievement), GridOptionKeys.Achievement.DesktopTheme, "DesktopTheme"),

            (nameof(GridOptionsCatalog.GameSummaries), GridOptionKeys.GameSummaries.Overview, "OverviewGameSummaries"),
            (nameof(GridOptionsCatalog.GameSummaries), GridOptionKeys.GameSummaries.StartPage, "StartPageGameSummaries"),
            (nameof(GridOptionsCatalog.GameSummaries), GridOptionKeys.GameSummaries.ViewAchievements, "ViewAchievementsGameSummaries"),
            (nameof(GridOptionsCatalog.GameSummaries), GridOptionKeys.GameSummaries.FriendsOverview, "FriendsOverviewGameSummaries"),
            (nameof(GridOptionsCatalog.GameSummaries), GridOptionKeys.GameSummaries.FriendsOverviewSelectedFriend, "FriendsOverviewSelectedFriendGameSummaries"),

            (nameof(GridOptionsCatalog.FriendSummaries), GridOptionKeys.FriendSummaries.FriendsOverview, "FriendsOverviewFriendSummaries"),
            (nameof(GridOptionsCatalog.FriendSummaries), GridOptionKeys.FriendSummaries.ViewFriendsAchievements, "ViewFriendsAchievementsFriends"),

            (nameof(GridOptionsCatalog.CategorySummaries), GridOptionKeys.CategorySummaries.ViewAchievements, "ViewAchievementsCategorySummaries"),
            (nameof(GridOptionsCatalog.CategorySummaries), GridOptionKeys.CategorySummaries.OverviewSelectedGame, "OverviewSelectedGameCategorySummaries"),
            (nameof(GridOptionsCatalog.CategorySummaries), GridOptionKeys.CategorySummaries.FriendsOverview, "FriendsOverviewCategorySummaries"),
            (nameof(GridOptionsCatalog.CategorySummaries), GridOptionKeys.CategorySummaries.ViewFriendsAchievements, "ViewFriendsAchievementsCategorySummaries"),
            (nameof(GridOptionsCatalog.CategorySummaries), GridOptionKeys.CategorySummaries.DesktopTheme, "DesktopThemeCategorySummaries")
        };

        private static readonly (string SourceName, string TargetName)[] ColumnDictionaryNames =
        {
            ("ColumnVisibility", nameof(GridColumnLayoutOptions.Visibility)),
            ("ColumnWidths", nameof(GridColumnLayoutOptions.Widths)),
            ("ColumnOrder", nameof(GridColumnLayoutOptions.Order)),
            ("ColumnAlignments", nameof(GridColumnLayoutOptions.CellAlignments)),
            ("ColumnVerticalAlignments", nameof(GridColumnLayoutOptions.CellVerticalAlignments)),
            ("ColumnHeaderAlignments", nameof(GridColumnLayoutOptions.HeaderAlignments))
        };

        public static string MigrateFromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return json;
            }

            try
            {
                var root = JObject.Parse(json);
                var persisted = root["Persisted"] as JObject;
                if (persisted == null)
                {
                    return json;
                }

                var changed = false;
                var gridOptions = GetOrCreateObject(persisted, nameof(PersistedSettings.GridOptions), ref changed);

                foreach (var set in ColumnSets)
                {
                    changed |= CopyColumnSet(persisted, gridOptions, set.GroupName, set.Id, set.Prefix);
                }

                changed |= CopyAchievementOptions(persisted, gridOptions);
                changed |= CopyGameSummaryOptions(persisted, gridOptions);
                changed |= CopyFriendSummaryOptions(persisted, gridOptions);
                changed |= SeedStartPageControlBarDefaults(gridOptions);

                return changed
                    ? root.ToString(Formatting.None)
                    : json;
            }
            catch (Exception)
            {
                return json;
            }
        }

        private static bool CopyAchievementOptions(JObject persisted, JObject gridOptions)
        {
            var changed = false;

            changed |= CopyScalars(
                persisted,
                gridOptions,
                nameof(GridOptionsCatalog.Achievement),
                GridOptionKeys.Achievement.Default,
                ("AchievementDataGridSortMode", nameof(AchievementGridOptions.SortMode)),
                ("AchievementDataGridSortDescending", nameof(AchievementGridOptions.SortDescending)),
                ("AchievementDataGridMaxHeight", nameof(AchievementGridOptions.MaxHeight)));

            changed |= CopyScalars(
                persisted,
                gridOptions,
                nameof(GridOptionsCatalog.Achievement),
                GridOptionKeys.Achievement.SingleGame,
                ("SingleGameGridRowHeight", nameof(AchievementGridOptions.RowHeight)),
                ("SingleGameGridMaxRows", nameof(AchievementGridOptions.MaxRows)),
                ("SingleGameGridSortMode", nameof(AchievementGridOptions.SortMode)),
                ("SingleGameGridSortDescending", nameof(AchievementGridOptions.SortDescending)),
                ("ViewAchievementsAchievementsUnlockDateMode", nameof(AchievementGridOptions.UnlockDateMode)),
                ("ShowViewAchievementsAchievementGridControlBar", nameof(AchievementGridOptions.ShowControlBar)));

            changed |= CopyScalars(
                persisted,
                gridOptions,
                nameof(GridOptionsCatalog.Achievement),
                GridOptionKeys.Achievement.OverviewRecent,
                ("ShowOverviewRecentAchievementsGridColumnHeaders", nameof(AchievementGridOptions.ShowColumnHeaders)),
                ("ShowOverviewRecentAchievementsGridControlBar", nameof(AchievementGridOptions.ShowControlBar)),
                ("OverviewRecentAchievementsGridRowHeight", nameof(AchievementGridOptions.RowHeight)),
                ("OverviewRecentAchievementsGridMaxRows", nameof(AchievementGridOptions.MaxRows)),
                ("OverviewRecentAchievementsUseCoverImages", nameof(AchievementGridOptions.UseCoverImages)),
                ("OverviewRecentAchievementsShowRarityGlow", nameof(AchievementGridOptions.ShowRarityGlow)),
                ("OverviewRecentAchievementsColorNamesByRarity", nameof(AchievementGridOptions.ColorNamesByRarity)),
                ("OverviewRecentAchievementsUnlockDateMode", nameof(AchievementGridOptions.UnlockDateMode)));

            changed |= CopyScalars(
                persisted,
                gridOptions,
                nameof(GridOptionsCatalog.Achievement),
                GridOptionKeys.Achievement.OverviewSelectedGame,
                ("ShowOverviewSelectedGameGridColumnHeaders", nameof(AchievementGridOptions.ShowColumnHeaders)),
                ("ShowOverviewSelectedGameGridControlBar", nameof(AchievementGridOptions.ShowControlBar)),
                ("OverviewSelectedGameGridRowHeight", nameof(AchievementGridOptions.RowHeight)),
                ("OverviewSelectedGameGridMaxRows", nameof(AchievementGridOptions.MaxRows)),
                ("OverviewSelectedGameShowRarityGlow", nameof(AchievementGridOptions.ShowRarityGlow)),
                ("OverviewSelectedGameColorNamesByRarity", nameof(AchievementGridOptions.ColorNamesByRarity)),
                ("OverviewSelectedGameAchievementsUnlockDateMode", nameof(AchievementGridOptions.UnlockDateMode)),
                ("OverviewSelectedGameGridSortMode", nameof(AchievementGridOptions.SortMode)),
                ("OverviewSelectedGameGridSortDescending", nameof(AchievementGridOptions.SortDescending)));

            changed |= CopyScalars(
                persisted,
                gridOptions,
                nameof(GridOptionsCatalog.Achievement),
                GridOptionKeys.Achievement.FriendsOverviewRecent,
                ("ShowFriendsOverviewAchievementsGridColumnHeaders", nameof(AchievementGridOptions.ShowColumnHeaders)),
                ("ShowFriendsOverviewAchievementsGridControlBar", nameof(AchievementGridOptions.ShowControlBar)),
                ("FriendsOverviewAchievementsGridRowHeight", nameof(AchievementGridOptions.RowHeight)),
                ("FriendsOverviewAchievementsGridMaxRows", nameof(AchievementGridOptions.MaxRows)),
                ("FriendsOverviewAchievementsUseCoverImages", nameof(AchievementGridOptions.UseCoverImages)),
                ("FriendsOverviewAchievementsShowRarityGlow", nameof(AchievementGridOptions.ShowRarityGlow)),
                ("FriendsOverviewAchievementsColorNamesByRarity", nameof(AchievementGridOptions.ColorNamesByRarity)),
                ("FriendsOverviewAchievementsUnlockDateMode", nameof(AchievementGridOptions.UnlockDateMode)));

            changed |= CopyScalars(
                persisted["StartPageRecentUnlocksGrid"] as JObject,
                gridOptions,
                nameof(GridOptionsCatalog.Achievement),
                GridOptionKeys.Achievement.StartPageRecent,
                (nameof(StartPageRecentUnlocksGridSettings.ShowColumnHeaders), nameof(AchievementGridOptions.ShowColumnHeaders)),
                (nameof(StartPageRecentUnlocksGridSettings.ShowControlBar), nameof(AchievementGridOptions.ShowControlBar)),
                (nameof(StartPageRecentUnlocksGridSettings.RowHeight), nameof(AchievementGridOptions.RowHeight)),
                (nameof(StartPageRecentUnlocksGridSettings.MaxRows), nameof(AchievementGridOptions.MaxRows)),
                (nameof(StartPageRecentUnlocksGridSettings.UseCoverImages), nameof(AchievementGridOptions.UseCoverImages)),
                (nameof(StartPageRecentUnlocksGridSettings.ShowRarityGlow), nameof(AchievementGridOptions.ShowRarityGlow)),
                (nameof(StartPageRecentUnlocksGridSettings.ColorNamesByRarity), nameof(AchievementGridOptions.ColorNamesByRarity)),
                (nameof(StartPageRecentUnlocksGridSettings.SortMode), nameof(AchievementGridOptions.SortMode)),
                (nameof(StartPageRecentUnlocksGridSettings.SortDescending), nameof(AchievementGridOptions.SortDescending)));

            changed |= CopyScalars(
                persisted,
                gridOptions,
                nameof(GridOptionsCatalog.Achievement),
                GridOptionKeys.Achievement.StartPageRecent,
                ("StartPageRecentAchievementsGridRowHeight", nameof(AchievementGridOptions.RowHeight)),
                ("StartPageRecentAchievementsGridMaxRows", nameof(AchievementGridOptions.MaxRows)),
                ("StartPageAchievementsUnlockDateMode", nameof(AchievementGridOptions.UnlockDateMode)));

            changed |= CopyScalars(
                persisted["StartPageFriendsRecentUnlocksGrid"] as JObject,
                gridOptions,
                nameof(GridOptionsCatalog.Achievement),
                GridOptionKeys.Achievement.StartPageFriendAchievements,
                (nameof(StartPageRecentUnlocksGridSettings.ShowColumnHeaders), nameof(AchievementGridOptions.ShowColumnHeaders)),
                (nameof(StartPageRecentUnlocksGridSettings.ShowControlBar), nameof(AchievementGridOptions.ShowControlBar)),
                (nameof(StartPageRecentUnlocksGridSettings.RowHeight), nameof(AchievementGridOptions.RowHeight)),
                (nameof(StartPageRecentUnlocksGridSettings.MaxRows), nameof(AchievementGridOptions.MaxRows)),
                (nameof(StartPageRecentUnlocksGridSettings.UseCoverImages), nameof(AchievementGridOptions.UseCoverImages)),
                (nameof(StartPageRecentUnlocksGridSettings.ShowRarityGlow), nameof(AchievementGridOptions.ShowRarityGlow)),
                (nameof(StartPageRecentUnlocksGridSettings.ColorNamesByRarity), nameof(AchievementGridOptions.ColorNamesByRarity)),
                (nameof(StartPageRecentUnlocksGridSettings.SortMode), nameof(AchievementGridOptions.SortMode)),
                (nameof(StartPageRecentUnlocksGridSettings.SortDescending), nameof(AchievementGridOptions.SortDescending)));

            changed |= CopyScalars(
                persisted,
                gridOptions,
                nameof(GridOptionsCatalog.Achievement),
                GridOptionKeys.Achievement.StartPageFriendAchievements,
                ("ShowStartPageFriendsRecentAchievementsGridColumnHeaders", nameof(AchievementGridOptions.ShowColumnHeaders)),
                ("ShowStartPageFriendsRecentAchievementsGridControlBar", nameof(AchievementGridOptions.ShowControlBar)),
                ("StartPageFriendsRecentAchievementsGridRowHeight", nameof(AchievementGridOptions.RowHeight)),
                ("StartPageFriendsRecentAchievementsGridMaxRows", nameof(AchievementGridOptions.MaxRows)),
                ("StartPageFriendsRecentAchievementsUseCoverImages", nameof(AchievementGridOptions.UseCoverImages)),
                ("StartPageFriendsRecentAchievementsShowRarityGlow", nameof(AchievementGridOptions.ShowRarityGlow)),
                ("StartPageFriendsRecentAchievementsColorNamesByRarity", nameof(AchievementGridOptions.ColorNamesByRarity)),
                ("StartPageFriendsRecentAchievementsUnlockDateMode", nameof(AchievementGridOptions.UnlockDateMode)));

            changed |= CopyScalars(
                persisted,
                gridOptions,
                nameof(GridOptionsCatalog.Achievement),
                GridOptionKeys.Achievement.DesktopTheme,
                ("ShowDesktopThemeAchievementGridColumnHeaders", nameof(AchievementGridOptions.ShowColumnHeaders)),
                ("ShowDesktopThemeAchievementGridControlBar", nameof(AchievementGridOptions.ShowControlBar)),
                ("DesktopThemeAchievementGridRowHeight", nameof(AchievementGridOptions.RowHeight)),
                ("DesktopThemeAchievementGridMaxRows", nameof(AchievementGridOptions.MaxRows)),
                ("DesktopThemeAchievementsUnlockDateMode", nameof(AchievementGridOptions.UnlockDateMode)),
                ("AchievementDataGridMaxHeight", nameof(AchievementGridOptions.MaxHeight)));

            return changed;
        }

        private static bool CopyGameSummaryOptions(JObject persisted, JObject gridOptions)
        {
            var changed = false;

            changed |= CopyScalars(
                persisted,
                gridOptions,
                nameof(GridOptionsCatalog.GameSummaries),
                GridOptionKeys.GameSummaries.Overview,
                ("ShowOverviewGameSummariesGridColumnHeaders", nameof(GameSummaryGridOptions.ShowColumnHeaders)),
                ("ShowOverviewGameSummariesGridControlBar", nameof(GameSummaryGridOptions.ShowControlBar)),
                ("OverviewGameSummariesGridRowHeight", nameof(GameSummaryGridOptions.RowHeight)),
                ("OverviewGameSummariesGridMaxRows", nameof(GameSummaryGridOptions.MaxRows)),
                ("OverviewGameSummariesUseCoverImages", nameof(GameSummaryGridOptions.UseCoverImages)),
                ("ShowOverviewGameMetadataPlatform", nameof(GameSummaryGridOptions.ShowMetadataPlatform)),
                ("ShowOverviewGameMetadataPlaytime", nameof(GameSummaryGridOptions.ShowMetadataPlaytime)),
                ("ShowOverviewGameMetadataRegion", nameof(GameSummaryGridOptions.ShowMetadataRegion)),
                ("OverviewGameSummariesLastPlayedDateMode", nameof(GameSummaryGridOptions.LastPlayedDateMode)),
                ("OverviewGameSummariesGridSortMode", nameof(GameSummaryGridOptions.SortMode)),
                ("OverviewGameSummariesGridSortDescending", nameof(GameSummaryGridOptions.SortDescending)));

            changed |= CopyScalars(
                persisted["StartPageGameSummariesGrid"] as JObject,
                gridOptions,
                nameof(GridOptionsCatalog.GameSummaries),
                GridOptionKeys.GameSummaries.StartPage,
                (nameof(StartPageGameSummariesGridSettings.ShowColumnHeaders), nameof(GameSummaryGridOptions.ShowColumnHeaders)),
                (nameof(StartPageGameSummariesGridSettings.ShowControlBar), nameof(GameSummaryGridOptions.ShowControlBar)),
                (nameof(StartPageGameSummariesGridSettings.RowHeight), nameof(GameSummaryGridOptions.RowHeight)),
                (nameof(StartPageGameSummariesGridSettings.MaxRows), nameof(GameSummaryGridOptions.MaxRows)),
                (nameof(StartPageGameSummariesGridSettings.UseCoverImages), nameof(GameSummaryGridOptions.UseCoverImages)),
                (nameof(StartPageGameSummariesGridSettings.ShowMetadataPlatform), nameof(GameSummaryGridOptions.ShowMetadataPlatform)),
                (nameof(StartPageGameSummariesGridSettings.ShowMetadataPlaytime), nameof(GameSummaryGridOptions.ShowMetadataPlaytime)),
                (nameof(StartPageGameSummariesGridSettings.ShowMetadataRegion), nameof(GameSummaryGridOptions.ShowMetadataRegion)),
                (nameof(StartPageGameSummariesGridSettings.SortMode), nameof(GameSummaryGridOptions.SortMode)),
                (nameof(StartPageGameSummariesGridSettings.SortDescending), nameof(GameSummaryGridOptions.SortDescending)));

            changed |= CopyScalars(
                persisted,
                gridOptions,
                nameof(GridOptionsCatalog.GameSummaries),
                GridOptionKeys.GameSummaries.StartPage,
                ("StartPageGameSummariesGridRowHeight", nameof(GameSummaryGridOptions.RowHeight)),
                ("StartPageGameSummariesGridMaxRows", nameof(GameSummaryGridOptions.MaxRows)),
                ("StartPageGameSummariesLastPlayedDateMode", nameof(GameSummaryGridOptions.LastPlayedDateMode)));

            changed |= CopyScalars(
                persisted,
                gridOptions,
                nameof(GridOptionsCatalog.GameSummaries),
                GridOptionKeys.GameSummaries.ViewAchievements,
                ("ShowViewAchievementsGameSummariesGridColumnHeaders", nameof(GameSummaryGridOptions.ShowColumnHeaders)),
                ("ViewAchievementsGameSummariesGridRowHeight", nameof(GameSummaryGridOptions.RowHeight)),
                ("ViewAchievementsGameSummariesUseCoverImages", nameof(GameSummaryGridOptions.UseCoverImages)),
                ("ViewAchievementsGameSummariesShowMetadataPlatform", nameof(GameSummaryGridOptions.ShowMetadataPlatform)),
                ("ViewAchievementsGameSummariesShowMetadataPlaytime", nameof(GameSummaryGridOptions.ShowMetadataPlaytime)),
                ("ViewAchievementsGameSummariesShowMetadataRegion", nameof(GameSummaryGridOptions.ShowMetadataRegion)),
                ("ViewAchievementsGameSummariesLastPlayedDateMode", nameof(GameSummaryGridOptions.LastPlayedDateMode)));

            changed |= CopyScalars(
                persisted,
                gridOptions,
                nameof(GridOptionsCatalog.GameSummaries),
                GridOptionKeys.GameSummaries.FriendsOverview,
                ("ShowFriendsOverviewGameSummariesGridColumnHeaders", nameof(GameSummaryGridOptions.ShowColumnHeaders)),
                ("ShowFriendsOverviewGameSummariesGridControlBar", nameof(GameSummaryGridOptions.ShowControlBar)),
                ("FriendsOverviewGameSummariesGridRowHeight", nameof(GameSummaryGridOptions.RowHeight)),
                ("FriendsOverviewGameSummariesGridMaxRows", nameof(GameSummaryGridOptions.MaxRows)),
                ("FriendsOverviewGameSummariesUseCoverImages", nameof(GameSummaryGridOptions.UseCoverImages)),
                ("FriendsOverviewGameSummariesShowMetadataPlatform", nameof(GameSummaryGridOptions.ShowMetadataPlatform)),
                ("FriendsOverviewGameSummariesShowMetadataPlaytime", nameof(GameSummaryGridOptions.ShowMetadataPlaytime)),
                ("FriendsOverviewGameSummariesShowMetadataRegion", nameof(GameSummaryGridOptions.ShowMetadataRegion)),
                ("FriendsOverviewGameSummariesLastPlayedDateMode", nameof(GameSummaryGridOptions.LastPlayedDateMode)));

            changed |= CopyScalars(
                persisted,
                gridOptions,
                nameof(GridOptionsCatalog.GameSummaries),
                GridOptionKeys.GameSummaries.FriendsOverviewSelectedFriend,
                ("FriendsOverviewGameSummariesLastPlayedDateMode", nameof(GameSummaryGridOptions.LastPlayedDateMode)));

            return changed;
        }

        private static bool CopyFriendSummaryOptions(JObject persisted, JObject gridOptions)
        {
            return CopyScalars(
                persisted,
                gridOptions,
                nameof(GridOptionsCatalog.FriendSummaries),
                GridOptionKeys.FriendSummaries.FriendsOverview,
                ("ShowFriendsOverviewFriendSummariesGridColumnHeaders", nameof(FriendSummaryGridOptions.ShowColumnHeaders)),
                ("ShowFriendsOverviewFriendSummariesGridControlBar", nameof(FriendSummaryGridOptions.ShowControlBar)),
                ("FriendsOverviewFriendSummariesGridRowHeight", nameof(FriendSummaryGridOptions.RowHeight)),
                ("FriendsOverviewFriendSummariesGridMaxRows", nameof(FriendSummaryGridOptions.MaxRows)),
                ("FriendsOverviewFriendSummariesLastUnlockDateMode", nameof(FriendSummaryGridOptions.LastUnlockDateMode)));
        }

        private static bool SeedStartPageControlBarDefaults(JObject gridOptions)
        {
            var changed = false;
            changed |= SeedControlBarDefault(
                gridOptions,
                nameof(GridOptionsCatalog.Achievement),
                GridOptionKeys.Achievement.StartPageRecent);
            changed |= SeedControlBarDefault(
                gridOptions,
                nameof(GridOptionsCatalog.Achievement),
                GridOptionKeys.Achievement.StartPageFriendAchievements);
            changed |= SeedControlBarDefault(
                gridOptions,
                nameof(GridOptionsCatalog.GameSummaries),
                GridOptionKeys.GameSummaries.StartPage);
            return changed;
        }

        private static bool SeedControlBarDefault(JObject gridOptions, string groupName, string id)
        {
            var entry = (gridOptions?[groupName] as JObject)?[id] as JObject;
            if (entry == null || entry[nameof(GridCommonOptions.ShowControlBar)] != null)
            {
                return false;
            }

            entry[nameof(GridCommonOptions.ShowControlBar)] = false;
            return true;
        }

        private static bool CopyColumnSet(
            JObject persisted,
            JObject gridOptions,
            string groupName,
            string id,
            string prefix)
        {
            if (persisted == null || gridOptions == null || !HasAnyColumnSource(persisted, prefix))
            {
                return false;
            }

            var changed = false;
            var entry = GetGridEntry(gridOptions, groupName, id, ref changed);
            var columns = GetOrCreateObject(entry, nameof(GridCommonOptions.Columns), ref changed);

            foreach (var dictionaryName in ColumnDictionaryNames)
            {
                changed |= CopyDictionary(
                    persisted,
                    prefix + dictionaryName.SourceName,
                    columns,
                    dictionaryName.TargetName);
            }

            return changed;
        }

        private static bool CopyScalars(
            JObject source,
            JObject gridOptions,
            string groupName,
            string id,
            params (string SourceName, string TargetName)[] properties)
        {
            if (source == null || gridOptions == null || properties == null)
            {
                return false;
            }

            var hasAnySource = false;
            foreach (var property in properties)
            {
                if (source[property.SourceName] != null)
                {
                    hasAnySource = true;
                    break;
                }
            }

            if (!hasAnySource)
            {
                return false;
            }

            var changed = false;
            var entry = GetGridEntry(gridOptions, groupName, id, ref changed);
            foreach (var property in properties)
            {
                changed |= CopyScalar(source, property.SourceName, entry, property.TargetName);
            }

            return changed;
        }

        private static bool HasAnyColumnSource(JObject persisted, string prefix)
        {
            foreach (var dictionaryName in ColumnDictionaryNames)
            {
                if (persisted[prefix + dictionaryName.SourceName] is JObject source &&
                    source.HasValues)
                {
                    return true;
                }
            }

            return false;
        }

        private static JObject GetGridEntry(
            JObject gridOptions,
            string groupName,
            string id,
            ref bool changed)
        {
            var group = GetOrCreateObject(gridOptions, groupName, ref changed);
            return GetOrCreateObject(group, id, ref changed);
        }

        private static JObject GetOrCreateObject(JObject parent, string propertyName, ref bool changed)
        {
            if (parent[propertyName] is JObject existing)
            {
                return existing;
            }

            var created = new JObject();
            parent[propertyName] = created;
            changed = true;
            return created;
        }

        private static bool CopyScalar(
            JObject source,
            string sourceName,
            JObject target,
            string targetName)
        {
            if (source == null || target == null || source[sourceName] == null || target[targetName] != null)
            {
                return false;
            }

            target[targetName] = source[sourceName].DeepClone();
            return true;
        }

        private static bool CopyDictionary(
            JObject sourceRoot,
            string sourceName,
            JObject targetRoot,
            string targetName)
        {
            if (!(sourceRoot?[sourceName] is JObject source) || !source.HasValues)
            {
                return false;
            }

            if (!(targetRoot[targetName] is JObject target))
            {
                targetRoot[targetName] = source.DeepClone();
                return true;
            }

            var changed = false;
            foreach (var property in source.Properties())
            {
                if (target[property.Name] != null)
                {
                    continue;
                }

                target[property.Name] = property.Value.DeepClone();
                changed = true;
            }

            return changed;
        }
    }
}
