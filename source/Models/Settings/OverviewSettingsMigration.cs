using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Migrates old Sidebar/GamesOverview and intermediate GameSummaries settings
    /// to the canonical OverviewGameSummaries/StartPageGameSummaries shape.
    /// Runs before settings deserialization so existing user config values are preserved.
    /// </summary>
    public static class OverviewSettingsMigration
    {
        private static readonly (string OldName, string NewName)[] PropertyRenames =
        {
            ("ShowSidebarCollectionScoreCard", "ShowOverviewCollectionScoreCard"),
            ("ShowSidebarPrestigeScoreCard", "ShowOverviewPrestigeScoreCard"),
            ("ShowSidebarPieCharts", "ShowOverviewPieCharts"),
            ("ShowSidebarGamesPieChart", "ShowOverviewGamesPieChart"),
            ("ShowSidebarProviderPieChart", "ShowOverviewProviderPieChart"),
            ("ShowSidebarRarityPieChart", "ShowOverviewRarityPieChart"),
            ("ShowSidebarTrophyPieChart", "ShowOverviewTrophyPieChart"),
            ("ShowSidebarPiePercentages", "ShowOverviewPiePercentages"),
            ("SidebarPieSmallSliceMode", "OverviewPieSmallSliceMode"),
            ("ShowSidebarBarCharts", "ShowOverviewBarCharts"),
            ("ShowSidebarGameMetadata", "ShowOverviewGameMetadata"),
            ("ShowGameSummariesGridColumnHeaders", "ShowOverviewGameSummariesGridColumnHeaders"),
            ("ShowOverviewGridColumnHeaders", "ShowOverviewGameSummariesGridColumnHeaders"),
            ("GameSummariesGridSortMode", "OverviewGameSummariesGridSortMode"),
            ("GamesOverviewGridSortMode", "OverviewGameSummariesGridSortMode"),
            ("GameSummariesGridSortDescending", "OverviewGameSummariesGridSortDescending"),
            ("GamesOverviewGridSortDescending", "OverviewGameSummariesGridSortDescending"),
            ("SidebarSelectedGameGridSortMode", "OverviewSelectedGameGridSortMode"),
            ("SidebarSelectedGameGridSortDescending", "OverviewSelectedGameGridSortDescending"),
            ("SidebarOverviewGridRowHeight", "OverviewGameSummariesGridRowHeight"),
            ("SidebarRecentAchievementsGridRowHeight", "OverviewRecentAchievementsGridRowHeight"),
            ("SidebarSelectedGameGridRowHeight", "OverviewSelectedGameGridRowHeight"),
            ("StartPageGamesOverviewGridRowHeight", "StartPageGameSummariesGridRowHeight"),
            ("SidebarOverviewGridMaxRows", "OverviewGameSummariesGridMaxRows"),
            ("SidebarRecentAchievementsGridMaxRows", "OverviewRecentAchievementsGridMaxRows"),
            ("SidebarSelectedGameGridMaxRows", "OverviewSelectedGameGridMaxRows"),
            ("StartPageGamesOverviewGridMaxRows", "StartPageGameSummariesGridMaxRows"),
            ("StartPageGamesOverviewGrid", "StartPageGameSummariesGrid"),
            ("SidebarAchievementColumnWidths", "OverviewRecentAchievementColumnWidths"),
            ("SidebarAchievementColumnOrder", "OverviewRecentAchievementColumnOrder"),
            ("SidebarAchievementColumnAlignments", "OverviewRecentAchievementColumnAlignments"),
            ("SidebarGameColumnWidths", "OverviewSelectedGameAchievementColumnWidths"),
            ("SidebarGameColumnOrder", "OverviewSelectedGameAchievementColumnOrder"),
            ("SidebarGameColumnAlignments", "OverviewSelectedGameAchievementColumnAlignments"),
            ("GameSummariesColumnVisibility", "OverviewGameSummariesColumnVisibility"),
            ("GamesOverviewColumnVisibility", "OverviewGameSummariesColumnVisibility"),
            ("GameSummariesColumnWidths", "OverviewGameSummariesColumnWidths"),
            ("GamesOverviewColumnWidths", "OverviewGameSummariesColumnWidths"),
            ("GameSummariesColumnOrder", "OverviewGameSummariesColumnOrder"),
            ("GamesOverviewColumnOrder", "OverviewGameSummariesColumnOrder"),
            ("GameSummariesColumnAlignments", "OverviewGameSummariesColumnAlignments"),
            ("GamesOverviewColumnAlignments", "OverviewGameSummariesColumnAlignments"),
            ("GameSummariesColumnVerticalAlignments", "OverviewGameSummariesColumnVerticalAlignments"),
            ("GamesOverviewColumnVerticalAlignments", "OverviewGameSummariesColumnVerticalAlignments"),
            ("GameSummariesColumnHeaderAlignments", "OverviewGameSummariesColumnHeaderAlignments"),
            ("GamesOverviewColumnHeaderAlignments", "OverviewGameSummariesColumnHeaderAlignments"),
            ("StartPageGamesOverviewColumnVisibility", "StartPageGameSummariesColumnVisibility"),
            ("StartPageGamesOverviewColumnWidths", "StartPageGameSummariesColumnWidths"),
            ("StartPageGamesOverviewColumnOrder", "StartPageGameSummariesColumnOrder"),
            ("StartPageGamesOverviewColumnAlignments", "StartPageGameSummariesColumnAlignments"),
            ("StartPageGamesOverviewColumnVerticalAlignments", "StartPageGameSummariesColumnVerticalAlignments"),
            ("StartPageGamesOverviewColumnHeaderAlignments", "StartPageGameSummariesColumnHeaderAlignments"),
            ("SidebarOverviewLeftColumnRatio", "OverviewLeftColumnRatio"),
            ("SidebarTimelineRange", "OverviewTimelineRange")
        };

        private static readonly (string OldName, string NewName)[] GameSummaryColumnRenames =
        {
            ("OverviewGameName", "GameSummaryName"),
            ("OverviewPlatform", "GameSummaryPlatform"),
            ("OverviewLastPlayed", "GameSummaryLastPlayed"),
            ("OverviewPlaytime", "GameSummaryPlaytime"),
            ("OverviewProgression", "GameSummaryProgression"),
            ("OverviewCollectionScore", "GameSummaryCollectionScore"),
            ("OverviewPrestigeScore", "GameSummaryPrestigeScore"),
            ("OverviewProvider", "GameSummaryProvider")
        };

        private static readonly string[] GameSummaryColumnDictionaries =
        {
            "OverviewGameSummariesColumnVisibility",
            "OverviewGameSummariesColumnWidths",
            "OverviewGameSummariesColumnOrder",
            "OverviewGameSummariesColumnAlignments",
            "OverviewGameSummariesColumnVerticalAlignments",
            "OverviewGameSummariesColumnHeaderAlignments",
            "StartPageGameSummariesColumnVisibility",
            "StartPageGameSummariesColumnWidths",
            "StartPageGameSummariesColumnOrder",
            "StartPageGameSummariesColumnAlignments",
            "StartPageGameSummariesColumnVerticalAlignments",
            "StartPageGameSummariesColumnHeaderAlignments"
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
                foreach (var rename in PropertyRenames)
                {
                    changed |= MoveProperty(persisted, rename.OldName, rename.NewName);
                }

                changed |= FanOutGameMetadataToggle(
                    persisted,
                    "ShowOverviewGameMetadata",
                    "ShowOverviewGameMetadataPlatform",
                    "ShowOverviewGameMetadataPlaytime",
                    "ShowOverviewGameMetadataRegion");
                changed |= FanOutGameMetadataToggle(
                    persisted,
                    "ViewAchievementsGameSummariesShowGameMetadata",
                    "ViewAchievementsGameSummariesShowMetadataPlatform",
                    "ViewAchievementsGameSummariesShowMetadataPlaytime",
                    "ViewAchievementsGameSummariesShowMetadataRegion");
                changed |= FanOutGameMetadataToggle(
                    persisted["StartPageGameSummariesGrid"] as JObject,
                    "ShowGameMetadata",
                    "ShowMetadataPlatform",
                    "ShowMetadataPlaytime",
                    "ShowMetadataRegion");

                changed |= CopyLegacyAchievementGridHeaderVisibility(persisted);

                foreach (var dictionaryName in GameSummaryColumnDictionaries)
                {
                    changed |= RenameColumnKeys(persisted[dictionaryName] as JObject);
                }

                changed |= CopyLegacyAchievementColumnVisibility(persisted);

                changed |= ForceProgressColumnRightDefault(persisted);

                return changed
                    ? root.ToString(Formatting.None)
                    : json;
            }
            catch (Exception)
            {
                return json;
            }
        }

        /// <summary>
        /// Splits a single combined game-metadata visibility toggle into the three per-field
        /// toggles (platform/playtime/region), copying the old value into each new key when absent
        /// so an existing on/off choice is preserved, then removing the old key.
        /// </summary>
        private static bool FanOutGameMetadataToggle(
            JObject obj,
            string oldName,
            string platformName,
            string playtimeName,
            string regionName)
        {
            if (obj == null || obj[oldName] == null)
            {
                return false;
            }

            var value = obj[oldName];
            foreach (var targetName in new[] { platformName, playtimeName, regionName })
            {
                if (obj[targetName] == null)
                {
                    obj[targetName] = value.DeepClone();
                }
            }

            obj.Remove(oldName);
            return true;
        }

        private static bool MoveProperty(JObject obj, string oldName, string newName)
        {
            if (obj == null || obj[oldName] == null)
            {
                return false;
            }

            if (obj[newName] == null)
            {
                obj[newName] = obj[oldName];
            }

            obj.Remove(oldName);
            return true;
        }

        private static bool RenameColumnKeys(JObject dictionary)
        {
            if (dictionary == null)
            {
                return false;
            }

            var changed = false;
            foreach (var rename in GameSummaryColumnRenames)
            {
                if (dictionary[rename.OldName] == null)
                {
                    continue;
                }

                if (dictionary[rename.NewName] == null)
                {
                    dictionary[rename.NewName] = dictionary[rename.OldName];
                }

                dictionary.Remove(rename.OldName);
                changed = true;
            }

            return changed;
        }

        /// <summary>
        /// Seeds the Progress column to Right alignment across all three game-summaries surfaces so an
        /// updating user keeps the legacy footer layout, now that the footer responds to the column's
        /// horizontal alignment. Runs once: gated by the <c>ProgressColumnAlignmentDefaulted</c> flag,
        /// which is set afterward so a user's own later alignment choice is never re-forced. Forces the
        /// value (overwriting any prior inert setting) since alignment had no effect on this column
        /// before. Values are written as integers to match how the GridAlignment enum is serialized.
        /// </summary>
        private static bool ForceProgressColumnRightDefault(JObject persisted)
        {
            const string flagName = nameof(PersistedSettings.ProgressColumnAlignmentDefaulted);

            var flag = persisted[flagName];
            if (flag != null && flag.Type == JTokenType.Boolean && flag.Value<bool>())
            {
                return false;
            }

            foreach (var dictionaryName in new[]
            {
                nameof(PersistedSettings.OverviewGameSummariesColumnAlignments),
                nameof(PersistedSettings.StartPageGameSummariesColumnAlignments),
                nameof(PersistedSettings.ViewAchievementsGameSummariesColumnAlignments)
            })
            {
                if (!(persisted[dictionaryName] is JObject dictionary))
                {
                    dictionary = new JObject();
                    persisted[dictionaryName] = dictionary;
                }

                dictionary[PersistedSettings.ProgressColumnKey] = (int)GridAlignment.Right;
            }

            persisted[flagName] = true;
            return true;
        }

        private static bool CopyLegacyAchievementGridHeaderVisibility(JObject persisted)
        {
            const string oldName = "ShowAchievementGridColumnHeaders";

            if (persisted == null || persisted[oldName] == null)
            {
                return false;
            }

            foreach (var propertyName in new[]
            {
                "ShowOverviewRecentAchievementsGridColumnHeaders",
                "ShowOverviewSelectedGameGridColumnHeaders"
            })
            {
                if (persisted[propertyName] != null)
                {
                    continue;
                }

                persisted[propertyName] = persisted[oldName].DeepClone();
            }

            persisted.Remove(oldName);
            return true;
        }

        private static bool CopyLegacyAchievementColumnVisibility(JObject persisted)
        {
            if (persisted == null || persisted["DataGridColumnVisibility"] == null)
            {
                return false;
            }

            var changed = false;
            foreach (var propertyName in new[]
            {
                "OverviewRecentAchievementColumnVisibility",
                "OverviewSelectedGameAchievementColumnVisibility",
                "SingleGameColumnVisibility"
            })
            {
                if (persisted[propertyName] != null)
                {
                    continue;
                }

                persisted[propertyName] = persisted["DataGridColumnVisibility"].DeepClone();
                changed = true;
            }

            return changed;
        }
    }
}
