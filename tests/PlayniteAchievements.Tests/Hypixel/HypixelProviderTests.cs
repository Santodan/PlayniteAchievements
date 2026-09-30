using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Hypixel;

namespace PlayniteAchievements.Hypixel.Tests
{
    [TestClass]
    public class HypixelProviderTests
    {
        /// <summary>
        /// Trimmed from a real hypixel.net/player/&lt;name&gt;/achievements page: a panel with a game
        /// icon, one-time, tiered and legacy sections, two one-time rows sharing a name, an empty
        /// panel, and a panel whose slug is not the catalog key.
        /// </summary>
        private const string ProfileHtml = @"
<html><body>
<div class=""panel game-skywars "">
    <h2>
        <img src=""/styles/hypixel-uix/hypixel/game-icons/Skywars-44.png"" loading=""lazy"" />
        SkyWars
    </h2>
    <div class=""sections"">
        <div class=""section"">
            <h3>One Time</h3>
            <ul class=""achievements"" data-xf-init=""achievements"">
                <li class=""completed"" data-name=""Iron Punch"" data-description=""Get a kill with an Iron Golem"">
                    <div class=""achievementIcon"" ></div>
                </li>
                <li class="""" data-name=""No Chest Challenge"" data-description=""Win a Mega game without opening any chest"">
                    <div class=""achievementIcon"" ></div>
                </li>
                <li class=""completed"" data-name=""No Chest Challenge"" data-description=""Win a game doing the No Chest Challenge"">
                    <div class=""achievementIcon"" ></div>
                </li>
            </ul>
        </div>
        <div class=""section"">
            <h3>Tiered</h3>
            <ul class=""achievements"" data-xf-init=""achievements"">
                <li class=""completed"" data-name=""Collectors Edition"" data-description=""Collect %%value%% wool from enemy teams"" data-progress=""312"" data-amount=""250"" data-tier=""4"" data-title="""">
                    <div class=""achievementIcon"" data-icon=""/styles/hypixel-uix/hypixel/achievements/WOOL.svg""></div>
                </li>
                <li class="""" data-name=""Collectors Edition"" data-description=""Collect %%value%% wool from enemy teams"" data-progress=""312"" data-amount=""500"" data-tier=""5"" data-title="""">
                    <div class=""achievementIcon"" data-icon=""/styles/hypixel-uix/hypixel/achievements/WOOL.svg""></div>
                </li>
            </ul>
        </div>
        <div class=""section"">
            <h3>One Time (Legacy)</h3>
            <ul class=""achievements"" data-xf-init=""achievements"">
                <li class="""" data-name=""Old Times"" data-description=""Play the old mode &amp; win"">
                    <div class=""achievementIcon"" ></div>
                </li>
            </ul>
        </div>
    </div>
</div>
<div class=""panel game-wool-games "">
    <h2>Wool Games</h2>
    <div class=""sections""></div>
</div>
<div class=""panel game-murder-mystery "">
    <h2>Murder Mystery</h2>
    <div class=""sections"">
        <div class=""section"">
            <h3>One Time</h3>
            <ul class=""achievements"">
                <li class="""" data-name=""Hero"" data-description=""Kill the murderer"">
                    <div class=""achievementIcon"" ></div>
                </li>
                <li class="""" data-name=""Unlisted"" data-description=""Not in the catalog"">
                    <div class=""achievementIcon"" ></div>
                </li>
            </ul>
        </div>
    </div>
</div>
</body></html>";

        /// <summary>Mirrors the real /v2/resources/achievements shape for the games above.</summary>
        private const string CatalogJson = @"{
            ""success"": true, ""lastUpdated"": 1790687890481,
            ""achievements"": {
                ""skywars"": {
                    ""one_time"": {
                        ""IRON_PUNCH"": { ""points"": 5, ""name"": ""Iron Punch"", ""description"": ""Get a kill with an Iron Golem"", ""gamePercentUnlocked"": 17.5, ""globalPercentUnlocked"": 12.4 },
                        ""NO_CHEST_CHALLENGE"": { ""points"": 10, ""name"": ""No Chest Challenge"", ""description"": ""Win a Mega game without opening any chest"", ""globalPercentUnlocked"": 0.14 },
                        ""CHALLENGE_NO_CHEST"": { ""points"": 5, ""name"": ""No Chest Challenge"", ""description"": ""Win a game doing the No Chest Challenge"", ""globalPercentUnlocked"": 0.51 },
                        ""OLD_TIMES"": { ""points"": 0, ""name"": ""Old Times"", ""description"": ""Play the old mode & win"", ""legacy"": true }
                    },
                    ""tiered"": {
                        ""COLLECTORS_EDITION"": { ""name"": ""Collectors Edition"", ""description"": ""Collect %%value%% wool from enemy teams"",
                            ""tiers"": [ { ""tier"": 4, ""points"": 20, ""amount"": 250 }, { ""tier"": 5, ""points"": 25, ""amount"": 500 } ] }
                    }
                },
                ""murdermystery"": {
                    ""one_time"": { ""HERO"": { ""points"": 10, ""name"": ""Hero"", ""description"": ""Kill the murderer"", ""globalPercentUnlocked"": 30.0 } },
                    ""tiered"": {}
                }
            }
        }";

        private static HypixelAchievementsResponse Catalog() =>
            JsonConvert.DeserializeObject<HypixelAchievementsResponse>(CatalogJson);

        [TestMethod]
        public void ParseProfile_ReadsPanelsSectionsAndRows()
        {
            var profile = HypixelParsing.ParseProfile(ProfileHtml);

            CollectionAssert.AreEqual(
                new[] { "skywars", "wool-games", "murder-mystery" },
                profile.Panels.Select(p => p.Slug).ToArray());

            var skywars = profile.Panels[0];
            Assert.AreEqual("SkyWars", skywars.DisplayName);
            Assert.AreEqual("https://hypixel.net/styles/hypixel-uix/hypixel/game-icons/Skywars-44.png", skywars.IconUrl);
            Assert.AreEqual(6, skywars.Entries.Count);

            var ironPunch = skywars.Entries[0];
            Assert.IsTrue(ironPunch.Completed);
            Assert.IsFalse(ironPunch.IsTiered);
            Assert.IsFalse(ironPunch.Legacy);

            var tier4 = skywars.Entries[3];
            Assert.AreEqual(4, tier4.Tier);
            Assert.AreEqual(312L, tier4.Progress);
            Assert.AreEqual(250L, tier4.Amount);

            var legacy = skywars.Entries[5];
            Assert.IsTrue(legacy.Legacy);
            Assert.AreEqual("Play the old mode & win", legacy.Description);

            Assert.AreEqual(0, profile.Panels[1].Entries.Count);
            Assert.IsNull(profile.Panels[1].IconUrl);
        }

        [TestMethod]
        public void ParseProfile_PageWithoutPanels_IsEmpty()
        {
            Assert.AreEqual(0, HypixelParsing.ParseProfile("<html><body><div class=\"panel\">x</div></body></html>").Panels.Count);
            Assert.AreEqual(0, HypixelParsing.ParseProfile(string.Empty).Panels.Count);
            Assert.AreEqual(0, HypixelParsing.ParseProfile(null).Panels.Count);
        }

        [DataTestMethod]
        [DataRow("general", "general")]
        [DataRow("bedwars", "bedwars")]
        [DataRow("murder-mystery", "murdermystery")]
        [DataRow("arcade-games", "arcade")]
        [DataRow("uhc-champions", "uhc")]
        [DataRow("arena-brawl", "arena")]
        [DataRow("build-battle", "buildbattle")]
        [DataRow("cops-and-crims", "copsandcrims")]
        [DataRow("megawalls", "walls3")]
        [DataRow("paintball-warfare", "paintball")]
        [DataRow("quakecraft", "quake")]
        [DataRow("blitz-survivalgames", "blitz")]
        [DataRow("smash-heroes", "supersmash")]
        [DataRow("tnt-games", "tntgames")]
        [DataRow("turbo-kart-racers", "gingerbread")]
        [DataRow("wool-games", "woolgames")]
        [DataRow("crazywalls", "truecombat")]
        [DataRow("halloween2017", "halloween2017")]
        public void ResolveCatalogGameKey_MapsEveryProfileSlug(string slug, string expected)
        {
            Assert.AreEqual(expected, HypixelParsing.ResolveCatalogGameKey(slug));
        }

        [DataTestMethod]
        [DataRow("Hypixel")]
        [DataRow("Minecraft: Hypixel")]
        [DataRow("hypixel network")]
        public void IsHypixelTitle_MatchesHypixelNames(string title)
        {
            Assert.IsTrue(HypixelParsing.IsHypixelTitle(title));
        }

        [DataTestMethod]
        [DataRow("Minecraft")]
        [DataRow("Minecraft: Java Edition")]
        [DataRow("")]
        [DataRow(null)]
        public void IsHypixelTitle_RejectsOtherNames(string title)
        {
            Assert.IsFalse(HypixelParsing.IsHypixelTitle(title));
        }

        [TestMethod]
        public void BuildProfileUri_EscapesTheUsername()
        {
            Assert.AreEqual(
                "https://hypixel.net/player/Some%20Name/achievements",
                HypixelParsing.BuildProfileUri(" Some Name ").AbsoluteUri);
        }

        [TestMethod]
        public void BuildAchievements_OneTimeRowsCarryCatalogIdentityPointsAndRarity()
        {
            var rows = HypixelAchievementMapper.BuildAchievements(HypixelParsing.ParseProfile(ProfileHtml), Catalog());

            var ironPunch = rows.Single(r => r.ApiName == "skywars_iron_punch");
            Assert.AreEqual("Iron Punch", ironPunch.DisplayName);
            Assert.AreEqual("SkyWars", ironPunch.Category);
            Assert.AreEqual(5, ironPunch.Points);
            Assert.IsTrue(ironPunch.Unlocked);
            Assert.IsNull(ironPunch.UnlockTimeUtc);
            Assert.AreEqual(12.4, ironPunch.GlobalPercentUnlocked.Value, 1e-9);
            Assert.AreEqual(PercentRarityHelper.GetRarityTier(12.4), ironPunch.Rarity);
            Assert.AreEqual("https://hypixel.net/styles/hypixel-uix/hypixel/game-icons/Skywars-44.png", ironPunch.UnlockedIconPath);
            Assert.AreEqual(ironPunch.UnlockedIconPath, ironPunch.LockedIconPath);
            Assert.IsNull(ironPunch.CategoryType);
        }

        [TestMethod]
        public void BuildAchievements_SharedNamesAreToldApartByDescription()
        {
            var rows = HypixelAchievementMapper.BuildAchievements(HypixelParsing.ParseProfile(ProfileHtml), Catalog());

            var mega = rows.Single(r => r.ApiName == "skywars_no_chest_challenge");
            var normal = rows.Single(r => r.ApiName == "skywars_challenge_no_chest");
            Assert.IsFalse(mega.Unlocked);
            Assert.AreEqual(10, mega.Points);
            Assert.IsTrue(normal.Unlocked);
            Assert.AreEqual(5, normal.Points);
        }

        [TestMethod]
        public void BuildAchievements_TieredRowsCarryThresholdProgressAndTierPoints()
        {
            var rows = HypixelAchievementMapper.BuildAchievements(HypixelParsing.ParseProfile(ProfileHtml), Catalog());

            var tier4 = rows.Single(r => r.ApiName == "skywars_collectors_edition:t4");
            Assert.AreEqual(HypixelAchievementMapper.BuildTierDisplayName("Collectors Edition", 250), tier4.DisplayName);
            Assert.AreEqual(HypixelParsing.FormatTieredDescription("Collect %%value%% wool from enemy teams", 250), tier4.Description);
            Assert.IsTrue(tier4.Unlocked);
            Assert.AreEqual(20, tier4.Points);
            Assert.AreEqual(250, tier4.ProgressNum);
            Assert.AreEqual(250, tier4.ProgressDenom);
            Assert.IsNull(tier4.GlobalPercentUnlocked);

            var tier5 = rows.Single(r => r.ApiName == "skywars_collectors_edition:t5");
            Assert.IsFalse(tier5.Unlocked);
            Assert.AreEqual(25, tier5.Points);
            Assert.AreEqual(312, tier5.ProgressNum);
            Assert.AreEqual(500, tier5.ProgressDenom);
        }

        [TestMethod]
        public void BuildAchievements_LegacyRowsAreUnobtainable()
        {
            var rows = HypixelAchievementMapper.BuildAchievements(HypixelParsing.ParseProfile(ProfileHtml), Catalog());

            Assert.AreEqual("Unobtainable", rows.Single(r => r.ApiName == "skywars_old_times").CategoryType);
        }

        [TestMethod]
        public void BuildAchievements_UsesCatalogKeyForAliasedSlugAndKeepsUnlistedRows()
        {
            var rows = HypixelAchievementMapper.BuildAchievements(HypixelParsing.ParseProfile(ProfileHtml), Catalog());

            var hero = rows.Single(r => r.ApiName == "murdermystery_hero");
            Assert.AreEqual("Murder Mystery", hero.Category);
            Assert.AreEqual(10, hero.Points);

            var unlisted = rows.Single(r => r.ApiName == "murdermystery:Unlisted");
            Assert.IsNull(unlisted.Points);
            Assert.IsNull(unlisted.GlobalPercentUnlocked);
            Assert.AreEqual(RarityTier.Common, unlisted.Rarity);
        }

        [TestMethod]
        public void BuildAchievements_EmitsOneRowPerPageEntryWithUniqueIds()
        {
            var rows = HypixelAchievementMapper.BuildAchievements(HypixelParsing.ParseProfile(ProfileHtml), Catalog());

            Assert.AreEqual(8, rows.Count);
            Assert.AreEqual(rows.Count, rows.Select(r => r.ApiName).Distinct().Count());
        }

        [TestMethod]
        public void BuildAchievements_DuplicateIdentityGetsSuffix()
        {
            var html = @"<div class=""panel game-murder-mystery""><h2>Murder Mystery</h2><div class=""section""><h3>One Time</h3><ul>
                <li class="""" data-name=""Twin"" data-description=""a""></li>
                <li class="""" data-name=""Twin"" data-description=""b""></li></ul></div></div>";

            var rows = HypixelAchievementMapper.BuildAchievements(HypixelParsing.ParseProfile(html), Catalog());

            CollectionAssert.AreEqual(
                new[] { "murdermystery:Twin", "murdermystery:Twin#2" },
                rows.Select(r => r.ApiName).ToArray());
        }

        [TestMethod]
        public void FormatTieredDescription_LeavesTextWithoutAmountAlone()
        {
            Assert.AreEqual("Collect %%value%% wool", HypixelParsing.FormatTieredDescription("Collect %%value%% wool", null));
            Assert.IsNull(HypixelParsing.FormatTieredDescription(null, 5));
        }
    }
}
