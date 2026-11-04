using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Services.Showcase;

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
            ("SidebarTimelineRange", "OverviewTimelineRange"),
            // The enum-typed range became a TimeWindow; the converter still reads the old integer.
            ("OverviewTimelineRange", "OverviewTimeWindow"),
            ("ViewAchievementsTimelineRange", "ViewAchievementsTimeWindow")
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

                changed |= ConvertPiePercentagesToCenterMode(persisted);

                changed |= ConvertScoreCardVisibilityToSlots(persisted);

                changed |= ConvertScoreCardSlotsToCards(persisted);

                changed |= SeedOverviewMiniShowcase(persisted);

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

        /// <summary>
        /// Replaces the old show/hide pie percentage toggle with the pie center mode: on becomes
        /// <see cref="PieCenterMode.Percentage"/>, off becomes <see cref="PieCenterMode.Empty"/>.
        /// Runs after the property renames so the Sidebar-era name is covered too.
        /// </summary>
        private static bool ConvertPiePercentagesToCenterMode(JObject persisted)
        {
            const string oldName = "ShowOverviewPiePercentages";
            // A legacy key itself now: SeedOverviewMiniShowcase reads it into the pies, then drops it.
            const string newName = "OverviewPieCenterMode";

            var oldValue = persisted[oldName];
            if (oldValue == null)
            {
                return false;
            }

            if (persisted[newName] == null)
            {
                var showPercentages = oldValue.Type != JTokenType.Boolean || oldValue.Value<bool>();
                persisted[newName] = (showPercentages ? PieCenterMode.Percentage : PieCenterMode.Empty).ToString();
            }

            persisted.Remove(oldName);
            return true;
        }

        /// <summary>
        /// Replaces the overview's two score card visibility toggles with its two card slots, in
        /// order: both shown become Collection and Prestige, one shown becomes that card and None,
        /// neither becomes None and None. Runs after the property renames so the Sidebar-era names
        /// are covered too, and only when neither slot is saved yet.
        /// </summary>
        private static bool ConvertScoreCardVisibilityToSlots(JObject persisted)
        {
            const string collectionName = "ShowOverviewCollectionScoreCard";
            const string prestigeName = "ShowOverviewPrestigeScoreCard";
            // Legacy names now: ConvertScoreCardSlotsToCards carries the slots into the cards.
            const string slot1Name = "OverviewScoreCardSlot1";
            const string slot2Name = "OverviewScoreCardSlot2";

            if (persisted[collectionName] == null && persisted[prestigeName] == null)
            {
                return false;
            }

            if (persisted[slot1Name] == null && persisted[slot2Name] == null)
            {
                var slots = new List<string>();
                if (ReadBool(persisted[collectionName], true))
                {
                    slots.Add(ScoreCardSlots.Collection);
                }

                if (ReadBool(persisted[prestigeName], true))
                {
                    slots.Add(ScoreCardSlots.Prestige);
                }

                persisted[slot1Name] = slots.Count > 0 ? slots[0] : ScoreCardSlots.NoneKey;
                persisted[slot2Name] = slots.Count > 1 ? slots[1] : ScoreCardSlots.NoneKey;
            }

            persisted.Remove(collectionName);
            persisted.Remove(prestigeName);
            return true;
        }

        /// <summary>
        /// Replaces the overview's two card slots and shared custom-colors flag with a card and a
        /// show toggle per slot: a card key becomes that card, shown; None becomes the slot's
        /// default card, hidden; the colors flag is copied into both cards. Runs after the
        /// visibility-to-slot conversion, so the oldest profiles pass through both. A card that is
        /// already saved is kept.
        /// </summary>
        private static bool ConvertScoreCardSlotsToCards(JObject persisted)
        {
            const string slot1Name = "OverviewScoreCardSlot1";
            const string slot2Name = "OverviewScoreCardSlot2";
            const string customColorsName = "OverviewScoreCardsUseCustomColors";

            if (persisted[slot1Name] == null && persisted[slot2Name] == null && persisted[customColorsName] == null)
            {
                return false;
            }

            var useCustomColors = ReadBool(persisted[customColorsName], false);
            ConvertScoreCardSlot(
                persisted,
                persisted[slot1Name],
                ScoreCardType.Collection,
                nameof(PersistedSettings.OverviewScoreCard1),
                nameof(PersistedSettings.ShowOverviewScoreCard1),
                useCustomColors);
            ConvertScoreCardSlot(
                persisted,
                persisted[slot2Name],
                ScoreCardType.Prestige,
                nameof(PersistedSettings.OverviewScoreCard2),
                nameof(PersistedSettings.ShowOverviewScoreCard2),
                useCustomColors);

            persisted.Remove(slot1Name);
            persisted.Remove(slot2Name);
            persisted.Remove(customColorsName);
            return true;
        }

        private static void ConvertScoreCardSlot(
            JObject persisted,
            JToken slot,
            ScoreCardType defaultType,
            string cardName,
            string showName,
            bool useCustomColors)
        {
            if (persisted[cardName] != null)
            {
                return;
            }

            var key = slot != null && slot.Type == JTokenType.String
                ? ScoreCardSlots.Normalize(slot.Value<string>())
                : defaultType.Key;
            var shown = ScoreCardSlots.TryGetCardType(key, out var type);
            var card = PersistedSettings.CreateOverviewScoreCard(shown ? type : defaultType);
            ShowcaseWidgetOptions.SetScoreCardUseCustomColors(card, useCustomColors);
            persisted[cardName] = JObject.FromObject(card);
            if (persisted[showName] == null)
            {
                persisted[showName] = shown;
            }
        }
        /// <summary>
        /// The settings that drove the overview's fixed chart strip before it became the
        /// mini-showcase. Removed once their values have been carried into the strip's widgets.
        /// </summary>
        private static readonly string[] LegacyOverviewChartSettings =
        {
            "ShowOverviewPieCharts",
            "ShowOverviewGamesPieChart",
            "ShowOverviewProviderPieChart",
            "ShowOverviewRarityPieChart",
            "ShowOverviewTrophyPieChart",
            "ShowOverviewBarCharts",
            "OverviewPieCenterMode",
            "ShowOverviewPieIcons",
            "ShowOverviewPieLegend",
            "OverviewPieLegendPosition",
            "OverviewPieSmallSliceMode",
            "OverviewPieIncludeLocked",
            "OverviewTimeWindow",
            "OverviewTimelineGranularity",
            "OverviewTimelineSplitByPlatform"
        };

        /// <summary>
        /// Rebuilds the overview's chart strip as the mini-showcase: one Pie widget per pie that
        /// was shown, in the strip's order, carrying the shared pie settings, and a Timeline
        /// widget carrying the strip's window, granularity and platform split when the timeline
        /// was shown. Runs only when the profile has legacy chart settings and no mini-showcase
        /// yet; a fresh profile gets the default strip from the settings getter instead. The
        /// legacy keys are dropped either way.
        /// </summary>
        private static bool SeedOverviewMiniShowcase(JObject persisted)
        {
            const string layoutName = nameof(PersistedSettings.OverviewMiniShowcase);
            const string visibleName = nameof(PersistedSettings.ShowOverviewMiniShowcase);

            if (persisted == null || !LegacyOverviewChartSettings.Any(name => persisted[name] != null))
            {
                return false;
            }

            if (persisted[layoutName] == null)
            {
                var allPies = ReadBool(persisted["ShowOverviewPieCharts"], true);
                var pieModes = new List<ShowcasePieMode>();
                foreach (var (name, mode) in new[]
                {
                    ("ShowOverviewGamesPieChart", ShowcasePieMode.CompletedGames),
                    ("ShowOverviewProviderPieChart", ShowcasePieMode.Provider),
                    ("ShowOverviewRarityPieChart", ShowcasePieMode.Rarity),
                    ("ShowOverviewTrophyPieChart", ShowcasePieMode.Trophy)
                })
                {
                    if (ReadBool(persisted[name], allPies))
                    {
                        pieModes.Add(mode);
                    }
                }

                var showTimeline = ReadBool(persisted["ShowOverviewBarCharts"], true);
                var centerMode = ReadEnum(persisted["OverviewPieCenterMode"], PieCenterMode.Percentage);
                var showIcons = ReadBool(persisted["ShowOverviewPieIcons"], true);
                var showLegend = ReadBool(persisted["ShowOverviewPieLegend"], false);
                var legendPosition = ReadEnum(persisted["OverviewPieLegendPosition"], PieLegendPosition.Right);
                var smallSliceMode = ReadEnum(persisted["OverviewPieSmallSliceMode"], OverviewPieSmallSliceMode.Round);
                var includeLocked = ReadBool(persisted["OverviewPieIncludeLocked"], true);
                var window = ReadTimeWindow(persisted["OverviewTimeWindow"], TimeWindow.FromPreset(TimelineRange.OneYear));
                var granularity = ReadEnum(persisted["OverviewTimelineGranularity"], TimelineGranularity.Auto);
                var splitByPlatform = ReadBool(persisted["OverviewTimelineSplitByPlatform"], false);

                var layout = OverviewMiniShowcaseLayout.Create(
                    pieModes,
                    showTimeline,
                    pie =>
                    {
                        ShowcaseWidgetOptions.SetPieCenterMode(pie, centerMode);
                        ShowcaseWidgetOptions.SetPieShowIcons(pie, showIcons);
                        ShowcaseWidgetOptions.SetShowLegend(pie, showLegend);
                        ShowcaseWidgetOptions.SetPieLegendPosition(pie, legendPosition);
                        ShowcaseWidgetOptions.SetPieSmallSliceMode(pie, smallSliceMode);
                        ShowcaseWidgetOptions.SetPieIncludeLocked(pie, includeLocked);
                    },
                    timeline =>
                    {
                        ShowcaseTimelineOptions.SetWindow(timeline, window);
                        ShowcaseTimelineOptions.SetGranularity(timeline, granularity);
                        ShowcaseTimelineOptions.SetSplit(
                            timeline,
                            splitByPlatform ? ShowcaseTimelineSplit.Platform : ShowcaseTimelineSplit.None);
                        // The strip's timeline always showed its window picker and split toggle.
                        ShowcaseWidgetOptions.ShowOnlyControlBarItems(
                            timeline,
                            ShowcaseControlBarItems.TimelineItems,
                            ShowcaseControlBarItems.TimeWindow,
                            ShowcaseControlBarItems.SplitByPlatform);
                    });
                persisted[layoutName] = JObject.FromObject(layout);
                if (persisted[visibleName] == null)
                {
                    persisted[visibleName] = pieModes.Count > 0 || showTimeline;
                }
            }

            foreach (var name in LegacyOverviewChartSettings)
            {
                persisted.Remove(name);
            }

            return true;
        }

        private static bool ReadBool(JToken token, bool fallback)
        {
            return token != null && token.Type == JTokenType.Boolean ? token.Value<bool>() : fallback;
        }

        /// <summary>Reads an enum stored either by name or by ordinal.</summary>
        private static T ReadEnum<T>(JToken token, T fallback)
            where T : struct
        {
            if (token == null)
            {
                return fallback;
            }

            if (token.Type == JTokenType.Integer)
            {
                var value = (T)Enum.ToObject(typeof(T), token.Value<long>());
                return Enum.IsDefined(typeof(T), value) ? value : fallback;
            }

            return token.Type == JTokenType.String && Enum.TryParse(token.Value<string>(), true, out T parsed)
                ? parsed
                : fallback;
        }

        private static TimeWindow ReadTimeWindow(JToken token, TimeWindow fallback)
        {
            if (token == null)
            {
                return fallback;
            }

            var serializer = new JsonSerializer();
            serializer.Converters.Add(new TimeWindowJsonConverter());
            try
            {
                return token.ToObject<TimeWindow>(serializer) ?? fallback;
            }
            catch (Exception)
            {
                return fallback;
            }
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
