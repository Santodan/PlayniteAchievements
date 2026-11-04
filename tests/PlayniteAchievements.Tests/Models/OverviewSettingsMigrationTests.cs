using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Showcase;

namespace PlayniteAchievements.Models.Tests
{
    [TestClass]
    public class OverviewSettingsMigrationTests
    {
        [TestMethod]
        public void MigrateFromJson_RenamesOverviewAndGameSummarySettings()
        {
            const string json =
                @"{
                    ""Persisted"": {
                        ""ShowSidebarCollectionScoreCard"": false,
                        ""ShowSidebarGameMetadata"": false,
                        ""SidebarPieSmallSliceMode"": ""Hide"",
                        ""GamesOverviewGridSortMode"": ""Alphabetical"",
                        ""GamesOverviewGridSortDescending"": false,
                        ""SidebarOverviewGridRowHeight"": 84.0,
                        ""SidebarOverviewGridMaxRows"": 3,
                        ""SidebarSelectedGameGridSortMode"": ""None"",
                        ""SidebarSelectedGameGridSortDescending"": false,
                        ""SidebarOverviewLeftColumnRatio"": 0.64,
                        ""SidebarTimelineRange"": ""SixMonths"",
                        ""ShowOverviewGridColumnHeaders"": false,
                        ""StartPageGamesOverviewGrid"": {
                            ""RowHeight"": 72.0,
                            ""MaxRows"": 11,
                            ""SortMode"": ""Progress"",
                            ""SortDescending"": false
                        }
                    }
                }";

            var migrated = JObject.Parse(OverviewSettingsMigration.MigrateFromJson(json));
            var persisted = (JObject)migrated["Persisted"];

            // The renamed Collection toggle (off) and the unsaved Prestige toggle (on) become slots,
            // and the slots become cards: Prestige shown, then the default second card hidden.
            AssertCard(persisted, 1, "Prestige", true);
            AssertCard(persisted, 2, "Prestige", false);
            Assert.IsNull(persisted["ShowOverviewCollectionScoreCard"]);
            Assert.IsNull(persisted["ShowOverviewGameMetadata"]);
            Assert.AreEqual(false, persisted["ShowOverviewGameMetadataPlatform"].Value<bool>());
            Assert.AreEqual(false, persisted["ShowOverviewGameMetadataPlaytime"].Value<bool>());
            Assert.AreEqual(false, persisted["ShowOverviewGameMetadataRegion"].Value<bool>());
            // The strip's pie and timeline settings land on the mini-showcase widgets.
            var mini = ReadMiniShowcase(persisted);
            Assert.IsTrue(MiniWidgets(mini, ShowcaseWidgetKind.Pie)
                .All(pie => ShowcaseWidgetOptions.GetPieSmallSliceMode(pie) == OverviewPieSmallSliceMode.Hide));
            Assert.IsNull(persisted["OverviewPieSmallSliceMode"]);
            Assert.AreEqual("Alphabetical", persisted["OverviewGameSummariesGridSortMode"].Value<string>());
            Assert.AreEqual(false, persisted["OverviewGameSummariesGridSortDescending"].Value<bool>());
            Assert.AreEqual(84.0, persisted["OverviewGameSummariesGridRowHeight"].Value<double>());
            Assert.AreEqual(3, persisted["OverviewGameSummariesGridMaxRows"].Value<int>());
            Assert.AreEqual("None", persisted["OverviewSelectedGameGridSortMode"].Value<string>());
            Assert.AreEqual(false, persisted["OverviewSelectedGameGridSortDescending"].Value<bool>());
            Assert.AreEqual(0.64, persisted["OverviewLeftColumnRatio"].Value<double>());
            // The rename chain lands on the TimeWindow property, which the timeline widget takes.
            Assert.AreEqual(
                TimeWindow.FromPreset(TimelineRange.SixMonths),
                ShowcaseTimelineOptions.GetWindow(MiniWidgets(mini, ShowcaseWidgetKind.Timeline).Single()));
            Assert.IsNull(persisted["OverviewTimeWindow"]);
            Assert.IsNull(persisted["OverviewTimelineRange"]);
            Assert.AreEqual(false, persisted["ShowOverviewGameSummariesGridColumnHeaders"].Value<bool>());
            Assert.IsNotNull(persisted["StartPageGameSummariesGrid"]);
            Assert.IsNull(persisted["ShowSidebarCollectionScoreCard"]);
            Assert.IsNull(persisted["GamesOverviewGridSortMode"]);
            Assert.IsNull(persisted["StartPageGamesOverviewGrid"]);
        }

        [DataTestMethod]
        [DataRow(true, true, "Collection", true, "Prestige", true)]
        [DataRow(true, false, "Collection", true, "Prestige", false)]
        [DataRow(false, true, "Prestige", true, "Prestige", false)]
        [DataRow(false, false, "Collection", false, "Prestige", false)]
        public void MigrateFromJson_ConvertsScoreCardTogglesToCardsOnce(
            bool collection,
            bool prestige,
            string card1,
            bool shown1,
            string card2,
            bool shown2)
        {
            var json = "{\"Persisted\":{\"ShowOverviewCollectionScoreCard\":" + collection.ToString().ToLowerInvariant() +
                ",\"ShowOverviewPrestigeScoreCard\":" + prestige.ToString().ToLowerInvariant() + "}}";

            var migrated = OverviewSettingsMigration.MigrateFromJson(json);
            var persisted = (JObject)JObject.Parse(migrated)["Persisted"];

            AssertCard(persisted, 1, card1, shown1);
            AssertCard(persisted, 2, card2, shown2);
            Assert.IsNull(persisted["OverviewScoreCardSlot1"]);
            Assert.IsNull(persisted["OverviewScoreCardSlot2"]);
            Assert.IsNull(persisted["ShowOverviewCollectionScoreCard"]);
            Assert.IsNull(persisted["ShowOverviewPrestigeScoreCard"]);
            // Nothing left to convert: a second pass changes nothing.
            Assert.AreEqual(migrated, OverviewSettingsMigration.MigrateFromJson(migrated));

            // Saved slots win over stray legacy toggles.
            var saved = "{\"Persisted\":{\"OverviewScoreCardSlot1\":\"Gamerscore\",\"ShowOverviewCollectionScoreCard\":true}}";
            var kept = (JObject)JObject.Parse(OverviewSettingsMigration.MigrateFromJson(saved))["Persisted"];
            AssertCard(kept, 1, "Points:Xbox", true);
            AssertCard(kept, 2, "Prestige", true);
        }

        [TestMethod]
        public void MigrateFromJson_ConvertsScoreCardSlotsAndColorsIntoCards()
        {
            var json = "{\"Persisted\":{\"OverviewScoreCardSlot1\":\"None\",\"OverviewScoreCardSlot2\":\"Points:PSN\",\"OverviewScoreCardsUseCustomColors\":true}}";

            var migrated = OverviewSettingsMigration.MigrateFromJson(json);
            var persisted = (JObject)JObject.Parse(migrated)["Persisted"];

            AssertCard(persisted, 1, "Collection", false);
            AssertCard(persisted, 2, "Points:PSN", true);
            Assert.IsTrue(ShowcaseWidgetOptions.GetScoreCardUseCustomColors(ReadCard(persisted, 1)));
            Assert.IsTrue(ShowcaseWidgetOptions.GetScoreCardUseCustomColors(ReadCard(persisted, 2)));
            Assert.IsNull(persisted["OverviewScoreCardsUseCustomColors"]);
            Assert.AreEqual(migrated, OverviewSettingsMigration.MigrateFromJson(migrated));

            // The migrated JSON loads into the settings model.
            var settings = persisted.ToObject<PersistedSettings>();
            Assert.IsFalse(settings.ShowOverviewScoreCard1);
            Assert.AreEqual("Points:PSN", ShowcaseWidgetOptions.GetScoreCardType(settings.OverviewScoreCard2).Key);
        }

        private static ShowcaseWidgetInstanceSettings ReadCard(JObject persisted, int index)
        {
            return persisted["OverviewScoreCard" + index].ToObject<ShowcaseWidgetInstanceSettings>();
        }

        private static void AssertCard(JObject persisted, int index, string cardKey, bool shown)
        {
            var card = ReadCard(persisted, index);
            Assert.AreEqual(ShowcaseWidgetKind.Scores, card.Kind);
            Assert.AreEqual(cardKey, ShowcaseWidgetOptions.GetScoreCardType(card).Key);
            Assert.AreEqual(shown, persisted["ShowOverviewScoreCard" + index].Value<bool>());
        }

        [TestMethod]
        public void MigrateFromJson_RenamesColumnDictionariesAndKeys()
        {
            const string json =
                @"{
                    ""Persisted"": {
                        ""GamesOverviewColumnWidths"": {
                            ""OverviewGameName"": 500.0,
                            ""OverviewProvider"": 40.0,
                            ""TotalAchievements"": 180.0
                        },
                        ""StartPageGamesOverviewColumnOrder"": {
                            ""OverviewProgression"": 2,
                            ""GameSummaryName"": 1
                        },
                        ""SidebarAchievementColumnWidths"": {
                            ""Achievement"": 520.0
                        },
                        ""SidebarGameColumnAlignments"": {
                            ""Rarity"": ""Right""
                        }
                    }
                }";

            var migrated = JObject.Parse(OverviewSettingsMigration.MigrateFromJson(json));
            var persisted = (JObject)migrated["Persisted"];
            var overviewWidths = (JObject)persisted["OverviewGameSummariesColumnWidths"];
            var startPageOrder = (JObject)persisted["StartPageGameSummariesColumnOrder"];

            Assert.AreEqual(500.0, overviewWidths["GameSummaryName"].Value<double>());
            Assert.AreEqual(40.0, overviewWidths["GameSummaryProvider"].Value<double>());
            Assert.AreEqual(180.0, overviewWidths["TotalAchievements"].Value<double>());
            Assert.IsNull(overviewWidths["OverviewGameName"]);
            Assert.AreEqual(2, startPageOrder["GameSummaryProgression"].Value<int>());
            Assert.AreEqual(1, startPageOrder["GameSummaryName"].Value<int>());
            Assert.IsNotNull(persisted["OverviewRecentAchievementColumnWidths"]);
            Assert.IsNotNull(persisted["OverviewSelectedGameAchievementColumnAlignments"]);
            Assert.IsNull(persisted["GamesOverviewColumnWidths"]);
            Assert.IsNull(persisted["SidebarAchievementColumnWidths"]);
            Assert.IsNull(persisted["SidebarGameColumnAlignments"]);
        }

        [TestMethod]
        public void MigrateFromJson_PreservesExistingNewValuesWhenBothNamesExist()
        {
            const string json =
                @"{
                    ""Persisted"": {
                        ""ShowSidebarGameMetadata"": false,
                        ""ShowOverviewGameMetadata"": true,
                        ""GamesOverviewColumnOrder"": { ""OverviewGameName"": 2 },
                        ""OverviewGameSummariesColumnOrder"": { ""GameSummaryName"": 1 }
                    }
                }";

            var migrated = JObject.Parse(OverviewSettingsMigration.MigrateFromJson(json));
            var persisted = (JObject)migrated["Persisted"];
            var order = (JObject)persisted["OverviewGameSummariesColumnOrder"];

            Assert.IsNull(persisted["ShowOverviewGameMetadata"]);
            Assert.AreEqual(true, persisted["ShowOverviewGameMetadataPlatform"].Value<bool>());
            Assert.AreEqual(true, persisted["ShowOverviewGameMetadataPlaytime"].Value<bool>());
            Assert.AreEqual(true, persisted["ShowOverviewGameMetadataRegion"].Value<bool>());
            Assert.AreEqual(1, order["GameSummaryName"].Value<int>());
            Assert.IsNull(persisted["ShowSidebarGameMetadata"]);
            Assert.IsNull(persisted["GamesOverviewColumnOrder"]);
        }

        [TestMethod]
        public void MigrateFromJson_RenamesIntermediateGameSummariesSettings()
        {
            const string json =
                @"{
                    ""Persisted"": {
                        ""ShowGameSummariesGridColumnHeaders"": false,
                        ""GameSummariesGridSortMode"": ""Alphabetical"",
                        ""GameSummariesGridSortDescending"": false,
                        ""GameSummariesColumnHeaderAlignments"": {
                            ""OverviewGameName"": ""Right""
                        },
                        ""GameSummariesColumnVerticalAlignments"": {
                            ""OverviewProvider"": ""Bottom""
                        }
                    }
                }";

            var migrated = JObject.Parse(OverviewSettingsMigration.MigrateFromJson(json));
            var persisted = (JObject)migrated["Persisted"];
            var headerAlignments = (JObject)persisted["OverviewGameSummariesColumnHeaderAlignments"];
            var verticalAlignments = (JObject)persisted["OverviewGameSummariesColumnVerticalAlignments"];

            Assert.AreEqual(false, persisted["ShowOverviewGameSummariesGridColumnHeaders"].Value<bool>());
            Assert.AreEqual("Alphabetical", persisted["OverviewGameSummariesGridSortMode"].Value<string>());
            Assert.AreEqual(false, persisted["OverviewGameSummariesGridSortDescending"].Value<bool>());
            Assert.AreEqual("Right", headerAlignments["GameSummaryName"].Value<string>());
            Assert.AreEqual("Bottom", verticalAlignments["GameSummaryProvider"].Value<string>());
            Assert.IsNull(persisted["ShowGameSummariesGridColumnHeaders"]);
            Assert.IsNull(persisted["GameSummariesGridSortMode"]);
            Assert.IsNull(persisted["GameSummariesColumnHeaderAlignments"]);
        }

        [TestMethod]
        public void MigrateFromJson_CopiesLegacyAchievementColumnVisibilityToScopeMaps()
        {
            const string json =
                @"{
                    ""Persisted"": {
                        ""DataGridColumnVisibility"": {
                            ""Title"": false,
                            ""Rarity"": true
                        }
                    }
                }";

            var migrated = JObject.Parse(OverviewSettingsMigration.MigrateFromJson(json));
            var persisted = (JObject)migrated["Persisted"];
            var recentVisibility = (JObject)persisted["OverviewRecentAchievementColumnVisibility"];
            var selectedVisibility = (JObject)persisted["OverviewSelectedGameAchievementColumnVisibility"];
            var singleGameVisibility = (JObject)persisted["SingleGameColumnVisibility"];

            Assert.IsFalse(recentVisibility["Title"].Value<bool>());
            Assert.IsTrue(recentVisibility["Rarity"].Value<bool>());
            Assert.IsFalse(selectedVisibility["Title"].Value<bool>());
            Assert.IsTrue(selectedVisibility["Rarity"].Value<bool>());
            Assert.IsFalse(singleGameVisibility["Title"].Value<bool>());
            Assert.IsTrue(singleGameVisibility["Rarity"].Value<bool>());
            Assert.IsNotNull(persisted["DataGridColumnVisibility"]);
        }

        [TestMethod]
        public void MigrateFromJson_DoesNotOverwriteExistingAchievementColumnVisibilityMaps()
        {
            const string json =
                @"{
                    ""Persisted"": {
                        ""DataGridColumnVisibility"": {
                            ""Title"": false
                        },
                        ""OverviewRecentAchievementColumnVisibility"": {
                            ""Icon"": false
                        },
                        ""OverviewSelectedGameAchievementColumnVisibility"": {
                            ""Rarity"": false
                        },
                        ""SingleGameColumnVisibility"": {
                            ""Points"": false
                        }
                    }
                }";

            var migrated = JObject.Parse(OverviewSettingsMigration.MigrateFromJson(json));
            var persisted = (JObject)migrated["Persisted"];
            var recentVisibility = (JObject)persisted["OverviewRecentAchievementColumnVisibility"];
            var selectedVisibility = (JObject)persisted["OverviewSelectedGameAchievementColumnVisibility"];
            var singleGameVisibility = (JObject)persisted["SingleGameColumnVisibility"];

            Assert.IsFalse(recentVisibility["Icon"].Value<bool>());
            Assert.IsNull(recentVisibility["Title"]);
            Assert.IsFalse(selectedVisibility["Rarity"].Value<bool>());
            Assert.IsNull(selectedVisibility["Title"]);
            Assert.IsFalse(singleGameVisibility["Points"].Value<bool>());
            Assert.IsNull(singleGameVisibility["Title"]);
        }

        [TestMethod]
        public void MigrateFromJson_ForcesProgressColumnRightAcrossSurfacesWhenFlagAbsent()
        {
            const string json =
                @"{
                    ""Persisted"": {
                        ""OverviewGameSummariesColumnAlignments"": { ""GameSummaryName"": 1 }
                    }
                }";

            var migrated = JObject.Parse(OverviewSettingsMigration.MigrateFromJson(json));
            var persisted = (JObject)migrated["Persisted"];

            Assert.AreEqual(
                (int)GridAlignment.Right,
                persisted["OverviewGameSummariesColumnAlignments"]["GameSummaryProgression"].Value<int>());
            Assert.AreEqual(
                (int)GridAlignment.Right,
                persisted["StartPageGameSummariesColumnAlignments"]["GameSummaryProgression"].Value<int>());
            Assert.AreEqual(
                (int)GridAlignment.Right,
                persisted["ViewAchievementsGameSummariesColumnAlignments"]["GameSummaryProgression"].Value<int>());
            // Pre-existing entries on a touched dictionary are preserved.
            Assert.AreEqual(1, persisted["OverviewGameSummariesColumnAlignments"]["GameSummaryName"].Value<int>());
            Assert.IsTrue(persisted["ProgressColumnAlignmentDefaulted"].Value<bool>());
        }

        [TestMethod]
        public void MigrateFromJson_RenamesLegacyProgressionKeyThenForcesRight()
        {
            // Legacy key with an inert non-Right value: rename to canonical key, then force Right.
            const string json =
                @"{
                    ""Persisted"": {
                        ""GamesOverviewColumnAlignments"": { ""OverviewProgression"": 1 }
                    }
                }";

            var migrated = JObject.Parse(OverviewSettingsMigration.MigrateFromJson(json));
            var persisted = (JObject)migrated["Persisted"];
            var alignments = (JObject)persisted["OverviewGameSummariesColumnAlignments"];

            Assert.IsNull(alignments["OverviewProgression"]);
            Assert.AreEqual((int)GridAlignment.Right, alignments["GameSummaryProgression"].Value<int>());
            Assert.IsTrue(persisted["ProgressColumnAlignmentDefaulted"].Value<bool>());
        }

        [TestMethod]
        public void MigrateFromJson_RespectsProgressAlignmentOnceFlagIsSet()
        {
            const string json =
                @"{
                    ""Persisted"": {
                        ""ProgressColumnAlignmentDefaulted"": true,
                        ""OverviewGameSummariesColumnAlignments"": { ""GameSummaryProgression"": 1 }
                    }
                }";

            var migrated = JObject.Parse(OverviewSettingsMigration.MigrateFromJson(json));
            var persisted = (JObject)migrated["Persisted"];

            // Already defaulted: the user's own choice (Center) is left untouched.
            Assert.AreEqual(
                (int)GridAlignment.Center,
                persisted["OverviewGameSummariesColumnAlignments"]["GameSummaryProgression"].Value<int>());
        }

        [TestMethod]
        public void MigrateFromJson_FansOutGameMetadataTogglesAcrossSurfaces()
        {
            const string json =
                @"{
                    ""Persisted"": {
                        ""ShowOverviewGameMetadata"": false,
                        ""ViewAchievementsGameSummariesShowGameMetadata"": true,
                        ""StartPageGameSummariesGrid"": {
                            ""ShowGameMetadata"": false
                        }
                    }
                }";

            var migrated = JObject.Parse(OverviewSettingsMigration.MigrateFromJson(json));
            var persisted = (JObject)migrated["Persisted"];
            var startPage = (JObject)persisted["StartPageGameSummariesGrid"];

            Assert.IsNull(persisted["ShowOverviewGameMetadata"]);
            Assert.AreEqual(false, persisted["ShowOverviewGameMetadataPlatform"].Value<bool>());
            Assert.AreEqual(false, persisted["ShowOverviewGameMetadataPlaytime"].Value<bool>());
            Assert.AreEqual(false, persisted["ShowOverviewGameMetadataRegion"].Value<bool>());

            Assert.IsNull(persisted["ViewAchievementsGameSummariesShowGameMetadata"]);
            Assert.AreEqual(true, persisted["ViewAchievementsGameSummariesShowMetadataPlatform"].Value<bool>());
            Assert.AreEqual(true, persisted["ViewAchievementsGameSummariesShowMetadataPlaytime"].Value<bool>());
            Assert.AreEqual(true, persisted["ViewAchievementsGameSummariesShowMetadataRegion"].Value<bool>());

            Assert.IsNull(startPage["ShowGameMetadata"]);
            Assert.AreEqual(false, startPage["ShowMetadataPlatform"].Value<bool>());
            Assert.AreEqual(false, startPage["ShowMetadataPlaytime"].Value<bool>());
            Assert.AreEqual(false, startPage["ShowMetadataRegion"].Value<bool>());
        }

        [DataTestMethod]
        [DataRow("ShowOverviewPiePercentages", true, "Percentage")]
        [DataRow("ShowOverviewPiePercentages", false, "Empty")]
        [DataRow("ShowSidebarPiePercentages", false, "Empty")]
        public void MigrateFromJson_ConvertsPiePercentageToggleToCenterMode(
            string oldName,
            bool showPercentages,
            string expected)
        {
            var json = new JObject
            {
                ["Persisted"] = new JObject { [oldName] = showPercentages }
            }.ToString();

            var persisted = (JObject)JObject.Parse(OverviewSettingsMigration.MigrateFromJson(json))["Persisted"];

            var expectedMode = (PieCenterMode)Enum.Parse(typeof(PieCenterMode), expected);
            Assert.IsTrue(MiniWidgets(ReadMiniShowcase(persisted), ShowcaseWidgetKind.Pie)
                .All(pie => ShowcaseWidgetOptions.GetPieCenterMode(pie) == expectedMode));
            Assert.IsNull(persisted["OverviewPieCenterMode"]);
            Assert.IsNull(persisted["ShowOverviewPiePercentages"]);
            Assert.IsNull(persisted["ShowSidebarPiePercentages"]);
        }

        [TestMethod]
        public void MigrateFromJson_KeepsExistingCenterModeOverLegacyToggle()
        {
            const string json =
                @"{ ""Persisted"": { ""ShowOverviewPiePercentages"": false, ""OverviewPieCenterMode"": ""Filled"" } }";

            var persisted = (JObject)JObject.Parse(OverviewSettingsMigration.MigrateFromJson(json))["Persisted"];

            Assert.IsTrue(MiniWidgets(ReadMiniShowcase(persisted), ShowcaseWidgetKind.Pie)
                .All(pie => ShowcaseWidgetOptions.GetPieCenterMode(pie) == PieCenterMode.Filled));
            Assert.IsNull(persisted["ShowOverviewPiePercentages"]);
        }

        [TestMethod]
        public void MigrateFromJson_SeedsMiniShowcaseFromShownChartsInStripOrder()
        {
            const string json =
                @"{ ""Persisted"": {
                    ""ShowOverviewGamesPieChart"": true,
                    ""ShowOverviewProviderPieChart"": false,
                    ""ShowOverviewRarityPieChart"": true,
                    ""ShowOverviewTrophyPieChart"": false,
                    ""ShowOverviewBarCharts"": true,
                    ""OverviewPieCenterMode"": 2,
                    ""ShowOverviewPieIcons"": false,
                    ""ShowOverviewPieLegend"": true,
                    ""OverviewPieLegendPosition"": 1,
                    ""OverviewPieIncludeLocked"": false,
                    ""OverviewTimelineGranularity"": ""Week"",
                    ""OverviewTimelineSplitByPlatform"": true
                } }";

            var persisted = (JObject)JObject.Parse(OverviewSettingsMigration.MigrateFromJson(json))["Persisted"];
            var mini = ReadMiniShowcase(persisted);
            var page = mini.Pages.Single();
            var placed = page.Blocks
                .OrderBy(block => block.Column)
                .Select(block => mini.WidgetInstances.Single(widget => widget.InstanceId == block.WidgetInstanceId))
                .ToList();

            Assert.AreEqual(1, page.RowCount);
            Assert.AreEqual(3, placed.Count);
            Assert.AreEqual(ShowcasePieMode.CompletedGames, ShowcaseWidgetOptions.GetPieMode(placed[0]));
            Assert.AreEqual(ShowcasePieMode.Rarity, ShowcaseWidgetOptions.GetPieMode(placed[1]));
            Assert.AreEqual(ShowcaseWidgetKind.Timeline, placed[2].Kind);
            CollectionAssert.AreEqual(new[] { 1d, 1d, 2d }, page.ColumnWeights);

            Assert.AreEqual(PieCenterMode.Filled, ShowcaseWidgetOptions.GetPieCenterMode(placed[0]));
            Assert.IsFalse(ShowcaseWidgetOptions.GetPieShowIcons(placed[0]));
            Assert.IsTrue(ShowcaseWidgetOptions.GetShowLegend(placed[1]));
            Assert.AreEqual(PieLegendPosition.Left, ShowcaseWidgetOptions.GetPieLegendPosition(placed[1]));
            Assert.IsFalse(ShowcaseWidgetOptions.GetPieIncludeLocked(placed[1]));
            Assert.AreEqual(TimelineGranularity.Week, ShowcaseTimelineOptions.GetGranularity(placed[2]));
            Assert.AreEqual(ShowcaseTimelineSplit.Platform, ShowcaseTimelineOptions.GetSplit(placed[2]));
            // The strip's own default window, not the timeline widget's.
            Assert.AreEqual(TimeWindow.FromPreset(TimelineRange.OneYear), ShowcaseTimelineOptions.GetWindow(placed[2]));

            Assert.IsTrue(persisted["ShowOverviewMiniShowcase"].Value<bool>());
            Assert.IsNull(persisted["ShowOverviewGamesPieChart"]);
            Assert.IsNull(persisted["ShowOverviewBarCharts"]);
            Assert.IsNull(persisted["OverviewTimelineSplitByPlatform"]);
        }

        [TestMethod]
        public void MigrateFromJson_HidesMiniShowcaseWhenNoChartWasShown()
        {
            const string json =
                @"{ ""Persisted"": { ""ShowOverviewPieCharts"": false, ""ShowOverviewBarCharts"": false } }";

            var persisted = (JObject)JObject.Parse(OverviewSettingsMigration.MigrateFromJson(json))["Persisted"];

            Assert.IsFalse(persisted["ShowOverviewMiniShowcase"].Value<bool>());
            Assert.AreEqual(0, ReadMiniShowcase(persisted).WidgetInstances.Count);
        }

        [TestMethod]
        public void MigrateFromJson_KeepsExistingMiniShowcaseAndDropsLegacyKeys()
        {
            var existing = OverviewMiniShowcaseLayout.Create(
                new[] { ShowcasePieMode.Trophy },
                includeTimeline: false,
                configurePie: null,
                configureTimeline: null);
            var json = new JObject
            {
                ["Persisted"] = new JObject
                {
                    ["OverviewMiniShowcase"] = JObject.FromObject(existing),
                    ["ShowOverviewBarCharts"] = true
                }
            }.ToString();

            var persisted = (JObject)JObject.Parse(OverviewSettingsMigration.MigrateFromJson(json))["Persisted"];

            Assert.AreEqual(ShowcasePieMode.Trophy, ShowcaseWidgetOptions.GetPieMode(
                ReadMiniShowcase(persisted).WidgetInstances.Single()));
            Assert.IsNull(persisted["ShowOverviewBarCharts"]);
        }

        private static ShowcaseSettings ReadMiniShowcase(JObject persisted)
        {
            Assert.IsNotNull(persisted["OverviewMiniShowcase"], "the mini-showcase was not seeded");
            return persisted["OverviewMiniShowcase"].ToObject<ShowcaseSettings>();
        }

        private static IEnumerable<ShowcaseWidgetInstanceSettings> MiniWidgets(
            ShowcaseSettings mini,
            ShowcaseWidgetKind kind)
        {
            return mini.WidgetInstances.Where(widget => widget.Kind == kind);
        }
    }
}
