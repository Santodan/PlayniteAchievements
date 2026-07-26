using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Steam;
using System.Collections.Generic;

namespace PlayniteAchievements.Steam.Tests
{
    [TestClass]
    public class SteamHuntersCategoryEnricherTests
    {
        [TestMethod]
        public void ApplyGroups_DlcAndUpdate_TypesByDlcAppIdPresence()
        {
            // Mirrors Starfield/Atomic Heart ordering: the DLC group (has DlcAppId) comes
            // first in the array, and the update group (no DlcAppId) comes after. Position
            // must not matter -- only DlcAppId presence should decide Base+Update vs DLC.
            var achievements = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "dlc_ach" },
                new AchievementDetail { ApiName = "update_ach" },
                new AchievementDetail { ApiName = "ungrouped_ach" }
            };
            var groups = new List<SteamHuntersAchievementGroup>
            {
                new SteamHuntersAchievementGroup
                {
                    DlcAppId = 2721670,
                    DlcAppName = "Expansion",
                    AchievementApiNames = new List<string> { "dlc_ach" }
                },
                new SteamHuntersAchievementGroup
                {
                    Name = "Update #1",
                    AchievementApiNames = new List<string> { "update_ach" }
                }
            };

            SteamHuntersCategoryEnricher.ApplyGroups(achievements, groups, "dlcandupdate", "My Game");

            Assert.AreEqual("DLC", achievements[0].CategoryType);
            Assert.AreEqual("Expansion", achievements[0].Category);
            Assert.AreEqual("Base|Update", achievements[1].CategoryType);
            Assert.AreEqual("Update #1", achievements[1].Category);
            Assert.AreEqual("Base", achievements[2].CategoryType);
            Assert.AreEqual("My Game", achievements[2].Category);
        }

        [TestMethod]
        public void ApplyGroups_DlcUpdate_TypesAsDlcAndUpdate()
        {
            // Mirrors The Binding of Isaac: a DLC group with no Name is the DLC launch set
            // (DLC), while a group carrying BOTH a DlcAppId and a Name (e.g. "Booster Pack #5")
            // is a post-launch update to that DLC and types as "DLC|Update". The two signals
            // are independent: DlcAppId -> DLC vs Base, Name -> Update.
            var achievements = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "dlc_launch" },
                new AchievementDetail { ApiName = "dlc_update" }
            };
            var groups = new List<SteamHuntersAchievementGroup>
            {
                new SteamHuntersAchievementGroup
                {
                    DlcAppId = 570660,
                    DlcAppName = "Expansion",
                    AchievementApiNames = new List<string> { "dlc_launch" }
                },
                new SteamHuntersAchievementGroup
                {
                    Name = "Booster Pack #5",
                    DlcAppId = 570660,
                    DlcAppName = "Expansion",
                    AchievementApiNames = new List<string> { "dlc_update" }
                }
            };

            SteamHuntersCategoryEnricher.ApplyGroups(achievements, groups, "dlcandupdate", "My Game");

            Assert.AreEqual("DLC", achievements[0].CategoryType);
            Assert.AreEqual("Expansion", achievements[0].Category);
            Assert.AreEqual("DLC|Update", achievements[1].CategoryType);
            Assert.AreEqual("Booster Pack #5", achievements[1].Category);
        }

        [TestMethod]
        public void ApplyGroups_SingleDlcGroup_TypesAsDlc()
        {
            // Mirrors Cyberpunk 2077: a single DLC group at index 0. Previously this was
            // always forced to Base, so nothing was ever typed DLC.
            var achievements = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "dlc_ach" },
                new AchievementDetail { ApiName = "ungrouped_ach" }
            };
            var groups = new List<SteamHuntersAchievementGroup>
            {
                new SteamHuntersAchievementGroup
                {
                    DlcAppId = 2138330,
                    DlcAppName = "Phantom Liberty",
                    AchievementApiNames = new List<string> { "dlc_ach" }
                }
            };

            SteamHuntersCategoryEnricher.ApplyGroups(achievements, groups, "dlcandupdate", "Cyberpunk 2077");

            Assert.AreEqual("DLC", achievements[0].CategoryType);
            Assert.AreEqual("Phantom Liberty", achievements[0].Category);
            Assert.AreEqual("Base", achievements[1].CategoryType);
        }

        [TestMethod]
        public void ApplyGroups_MultipleDlcGroups_AllTypeAsDlc()
        {
            // Mirrors Atomic Heart: four DLC groups in sequence, all carrying DlcAppId.
            var achievements = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "annihilation_ach" },
                new AchievementDetail { ApiName = "limbo_ach" },
                new AchievementDetail { ApiName = "sea_ach" },
                new AchievementDetail { ApiName = "crystal_ach" }
            };
            var groups = new List<SteamHuntersAchievementGroup>
            {
                new SteamHuntersAchievementGroup
                {
                    DlcAppId = 2214821,
                    DlcAppName = "Annihilation Instinct",
                    AchievementApiNames = new List<string> { "annihilation_ach" }
                },
                new SteamHuntersAchievementGroup
                {
                    DlcAppId = 2214822,
                    DlcAppName = "Trapped in Limbo",
                    AchievementApiNames = new List<string> { "limbo_ach" }
                },
                new SteamHuntersAchievementGroup
                {
                    DlcAppId = 2214823,
                    DlcAppName = "Enchantment Under the Sea",
                    AchievementApiNames = new List<string> { "sea_ach" }
                },
                new SteamHuntersAchievementGroup
                {
                    DlcAppId = 2214824,
                    DlcAppName = "Blood On Crystal",
                    AchievementApiNames = new List<string> { "crystal_ach" }
                }
            };

            SteamHuntersCategoryEnricher.ApplyGroups(achievements, groups, "dlcandupdate", "Atomic Heart");

            Assert.AreEqual("DLC", achievements[0].CategoryType);
            Assert.AreEqual("DLC", achievements[1].CategoryType);
            Assert.AreEqual("DLC", achievements[2].CategoryType);
            Assert.AreEqual("DLC", achievements[3].CategoryType);
        }

        [TestMethod]
        public void ApplyGroups_GameGroupBy_MarksAllGroupsBase()
        {
            var achievements = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "halo_ce" },
                new AchievementDetail { ApiName = "halo_2" }
            };
            var groups = new List<SteamHuntersAchievementGroup>
            {
                new SteamHuntersAchievementGroup
                {
                    Name = "Halo: Combat Evolved",
                    AchievementApiNames = new List<string> { "halo_ce" }
                },
                new SteamHuntersAchievementGroup
                {
                    // DlcAppId set to prove the "game" groupBy override forces Base even
                    // when a group happens to carry a DlcAppId.
                    DlcAppId = 12345,
                    Name = "Halo 2: Anniversary",
                    AchievementApiNames = new List<string> { "halo_2" }
                }
            };

            SteamHuntersCategoryEnricher.ApplyGroups(achievements, groups, "game");

            Assert.AreEqual("Base", achievements[0].CategoryType);
            Assert.AreEqual("Halo: Combat Evolved", achievements[0].Category);
            Assert.AreEqual("Base", achievements[1].CategoryType);
            Assert.AreEqual("Halo 2: Anniversary", achievements[1].Category);
        }

        [TestMethod]
        public void ApplyGroups_EmptyGroups_MarksAllAchievementsBase()
        {
            var achievements = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "one" },
                new AchievementDetail { ApiName = "two" }
            };

            SteamHuntersCategoryEnricher.ApplyGroups(
                achievements,
                new List<SteamHuntersAchievementGroup>(),
                groupBy: null,
                gameName: "Fallback Game");

            Assert.AreEqual("Base", achievements[0].CategoryType);
            Assert.AreEqual("Fallback Game", achievements[0].Category);
            Assert.AreEqual("Base", achievements[1].CategoryType);
            Assert.AreEqual("Fallback Game", achievements[1].Category);
        }

        [TestMethod]
        public void BuildCategoryImagePlan_WithoutBaseAppId_OnlyDlcGroupsGetEntries()
        {
            var groups = new List<SteamHuntersAchievementGroup>
            {
                new SteamHuntersAchievementGroup
                {
                    DlcAppId = 2138330,
                    DlcAppName = "Phantom Liberty",
                    AchievementApiNames = new List<string> { "dlc_ach" }
                },
                new SteamHuntersAchievementGroup
                {
                    Name = "Update #1",
                    AchievementApiNames = new List<string> { "update_ach" }
                }
            };

            var plan = SteamHuntersCategoryEnricher.BuildCategoryImagePlan(groups, "dlcandupdate");

            Assert.AreEqual(1, plan.Count);
            Assert.AreEqual("Phantom Liberty", plan[0].Key);
            Assert.AreEqual(2138330, plan[0].Value);
        }

        [TestMethod]
        public void BuildCategoryImagePlan_UpdateGroups_MapToBaseAppId()
        {
            var groups = new List<SteamHuntersAchievementGroup>
            {
                new SteamHuntersAchievementGroup
                {
                    DlcAppId = 2138330,
                    DlcAppName = "Phantom Liberty"
                },
                new SteamHuntersAchievementGroup
                {
                    Name = "Update #1"
                }
            };

            var plan = SteamHuntersCategoryEnricher.BuildCategoryImagePlan(
                groups, "dlcandupdate", "Cyberpunk 2077", appId: 1091500);

            Assert.AreEqual(3, plan.Count);
            Assert.AreEqual("Cyberpunk 2077", plan[0].Key);
            Assert.AreEqual(1091500, plan[0].Value);
            Assert.AreEqual("Phantom Liberty", plan[1].Key);
            Assert.AreEqual(2138330, plan[1].Value);
            Assert.AreEqual("Update #1", plan[2].Key);
            Assert.AreEqual(1091500, plan[2].Value);
        }

        [TestMethod]
        public void BuildCategoryImagePlan_DlcUpdateGroups_MapToDlcAppId()
        {
            // A DLC-update group (DlcAppId + Name) must still pull the DLC's banner, not the
            // base game's -- art keys on the DlcAppId signal, independent of the update Name.
            var groups = new List<SteamHuntersAchievementGroup>
            {
                new SteamHuntersAchievementGroup
                {
                    DlcAppId = 570660,
                    DlcAppName = "Expansion"
                },
                new SteamHuntersAchievementGroup
                {
                    Name = "Booster Pack #5",
                    DlcAppId = 570660,
                    DlcAppName = "Expansion"
                }
            };

            var plan = SteamHuntersCategoryEnricher.BuildCategoryImagePlan(
                groups, "dlcandupdate", "My Game", appId: 250900);

            Assert.AreEqual(3, plan.Count);
            Assert.AreEqual("My Game", plan[0].Key);
            Assert.AreEqual(250900, plan[0].Value);
            Assert.AreEqual("Expansion", plan[1].Key);
            Assert.AreEqual(570660, plan[1].Value);
            Assert.AreEqual("Booster Pack #5", plan[2].Key);
            Assert.AreEqual(570660, plan[2].Value);
        }

        [TestMethod]
        public void BuildCategoryImagePlan_GameGroupBy_MapsSubGamesToBaseAppId()
        {
            var groups = new List<SteamHuntersAchievementGroup>
            {
                new SteamHuntersAchievementGroup
                {
                    // groupBy "game" forces Base even with a DlcAppId present.
                    DlcAppId = 12345,
                    Name = "Halo 2: Anniversary"
                }
            };

            var plan = SteamHuntersCategoryEnricher.BuildCategoryImagePlan(
                groups, "game", "Halo: The Master Chief Collection", appId: 976730);

            Assert.AreEqual(2, plan.Count);
            Assert.AreEqual("Halo: The Master Chief Collection", plan[0].Key);
            Assert.AreEqual(976730, plan[0].Value);
            Assert.AreEqual("Halo 2: Anniversary", plan[1].Key);
            Assert.AreEqual(976730, plan[1].Value);
        }

        [TestMethod]
        public void BuildCategoryImagePlan_GameGroupBy_YieldsNothing()
        {
            var groups = new List<SteamHuntersAchievementGroup>
            {
                new SteamHuntersAchievementGroup
                {
                    DlcAppId = 12345,
                    Name = "Halo 2: Anniversary",
                    AchievementApiNames = new List<string> { "halo_2" }
                }
            };

            var plan = SteamHuntersCategoryEnricher.BuildCategoryImagePlan(groups, "game");

            Assert.AreEqual(0, plan.Count);
        }

        [TestMethod]
        public void BuildCategoryImagePlan_DuplicateLabels_KeepsFirstDlcAppId()
        {
            var groups = new List<SteamHuntersAchievementGroup>
            {
                new SteamHuntersAchievementGroup
                {
                    DlcAppId = 111,
                    Name = "Expansion"
                },
                new SteamHuntersAchievementGroup
                {
                    DlcAppId = 222,
                    Name = "expansion"
                }
            };

            var plan = SteamHuntersCategoryEnricher.BuildCategoryImagePlan(groups, "dlcandupdate");

            Assert.AreEqual(1, plan.Count);
            Assert.AreEqual(111, plan[0].Value);
        }

        [TestMethod]
        public void BuildCategoryImagePlan_MissingLabelOrInvalidAppId_Excluded()
        {
            var groups = new List<SteamHuntersAchievementGroup>
            {
                new SteamHuntersAchievementGroup { DlcAppId = 333, Name = "   " },
                new SteamHuntersAchievementGroup { DlcAppId = 0, Name = "Zero App" },
                new SteamHuntersAchievementGroup { DlcAppId = 444, DlcAppName = "Fallback Name" }
            };

            var plan = SteamHuntersCategoryEnricher.BuildCategoryImagePlan(groups, "dlcandupdate");

            Assert.AreEqual(1, plan.Count);
            Assert.AreEqual("Fallback Name", plan[0].Key);
            Assert.AreEqual(444, plan[0].Value);
        }

        [TestMethod]
        public void ApplyGroups_LabelWithGameNamePrefix_StripsPrefix()
        {
            var achievements = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "dlc_ach" }
            };
            var groups = new List<SteamHuntersAchievementGroup>
            {
                new SteamHuntersAchievementGroup
                {
                    DlcAppId = 2138330,
                    DlcAppName = "Cyberpunk 2077: Phantom Liberty",
                    AchievementApiNames = new List<string> { "dlc_ach" }
                }
            };

            SteamHuntersCategoryEnricher.ApplyGroups(achievements, groups, "dlcandupdate", "Cyberpunk 2077");

            Assert.AreEqual("Phantom Liberty", achievements[0].Category);
        }

        [TestMethod]
        public void BuildCategoryImagePlan_LabelWithGameNamePrefix_KeysByStrippedLabel()
        {
            var groups = new List<SteamHuntersAchievementGroup>
            {
                new SteamHuntersAchievementGroup
                {
                    DlcAppId = 2138330,
                    DlcAppName = "Cyberpunk 2077: Phantom Liberty"
                }
            };

            var plan = SteamHuntersCategoryEnricher.BuildCategoryImagePlan(groups, "dlcandupdate", "Cyberpunk 2077");

            Assert.AreEqual(1, plan.Count);
            Assert.AreEqual("Phantom Liberty", plan[0].Key);
        }

        [TestMethod]
        public void StripGameNamePrefix_HandlesSeparatorsAndGuards()
        {
            Assert.AreEqual(
                "Phantom Liberty",
                SteamHuntersCategoryEnricher.StripGameNamePrefix("Cyberpunk 2077: Phantom Liberty", "Cyberpunk 2077"));
            Assert.AreEqual(
                "Trapped in Limbo",
                SteamHuntersCategoryEnricher.StripGameNamePrefix("Atomic Heart - Trapped in Limbo", "Atomic Heart"));
            Assert.AreEqual(
                "Endgame",
                SteamHuntersCategoryEnricher.StripGameNamePrefix("Deep Rock Galactic – Endgame", "deep rock galactic"));

            // No separator after the game name: leave the label alone.
            Assert.AreEqual(
                "Cyberpunk 2077 Ultimate",
                SteamHuntersCategoryEnricher.StripGameNamePrefix("Cyberpunk 2077 Ultimate", "Cyberpunk 2077"));
            // Label identical to the game name: leave the label alone.
            Assert.AreEqual(
                "Cyberpunk 2077",
                SteamHuntersCategoryEnricher.StripGameNamePrefix("Cyberpunk 2077", "Cyberpunk 2077"));
            // Nothing left after stripping: leave the label alone.
            Assert.AreEqual(
                "Cyberpunk 2077:",
                SteamHuntersCategoryEnricher.StripGameNamePrefix("Cyberpunk 2077:", "Cyberpunk 2077"));
            // Missing game name: leave the label alone.
            Assert.AreEqual(
                "Game: DLC",
                SteamHuntersCategoryEnricher.StripGameNamePrefix("Game: DLC", null));
        }

        [TestMethod]
        public void BuildCategoryImagePlan_WithGameNameAndAppId_IncludesBaseEntryFirst()
        {
            var groups = new List<SteamHuntersAchievementGroup>
            {
                new SteamHuntersAchievementGroup
                {
                    DlcAppId = 2138330,
                    DlcAppName = "Cyberpunk 2077: Phantom Liberty"
                }
            };

            var plan = SteamHuntersCategoryEnricher.BuildCategoryImagePlan(
                groups, "dlcandupdate", "Cyberpunk 2077", appId: 1091500);

            Assert.AreEqual(2, plan.Count);
            Assert.AreEqual("Cyberpunk 2077", plan[0].Key);
            Assert.AreEqual(1091500, plan[0].Value);
            Assert.AreEqual("Phantom Liberty", plan[1].Key);
            Assert.AreEqual(2138330, plan[1].Value);
        }

        [TestMethod]
        public void BuildCategoryImagePlan_DlcLabelCollidingWithGameName_KeepsBaseEntry()
        {
            var groups = new List<SteamHuntersAchievementGroup>
            {
                new SteamHuntersAchievementGroup
                {
                    DlcAppId = 555,
                    Name = "My Game"
                }
            };

            var plan = SteamHuntersCategoryEnricher.BuildCategoryImagePlan(
                groups, "dlcandupdate", "My Game", appId: 111);

            Assert.AreEqual(1, plan.Count);
            Assert.AreEqual(111, plan[0].Value);
        }

        [TestMethod]
        public void BuildCategoryImagePlan_NoAppId_OmitsBaseEntry()
        {
            var plan = SteamHuntersCategoryEnricher.BuildCategoryImagePlan(
                new List<SteamHuntersAchievementGroup>(), "dlcandupdate", "My Game");

            Assert.AreEqual(0, plan.Count);
        }

        [TestMethod]
        public void BuildCategoryImagePlan_NullOrEmptyGroups_YieldsNothing()
        {
            Assert.AreEqual(0, SteamHuntersCategoryEnricher.BuildCategoryImagePlan(null, "dlcandupdate").Count);
            Assert.AreEqual(
                0,
                SteamHuntersCategoryEnricher.BuildCategoryImagePlan(
                    new List<SteamHuntersAchievementGroup>(), "dlcandupdate").Count);
        }

        [TestMethod]
        public void ApplyGroups_DuplicateApiName_KeepsFirstGroupAssignment()
        {
            var achievements = new List<AchievementDetail>
            {
                new AchievementDetail { ApiName = "shared" }
            };
            var groups = new List<SteamHuntersAchievementGroup>
            {
                new SteamHuntersAchievementGroup
                {
                    Name = "Base Label",
                    AchievementApiNames = new List<string> { "shared" }
                },
                new SteamHuntersAchievementGroup
                {
                    DlcAppId = 999,
                    Name = "DLC Label",
                    AchievementApiNames = new List<string> { "shared" }
                }
            };

            SteamHuntersCategoryEnricher.ApplyGroups(achievements, groups);

            Assert.AreEqual("Base", achievements[0].CategoryType);
            Assert.AreEqual("Base Label", achievements[0].Category);
        }
    }
}
