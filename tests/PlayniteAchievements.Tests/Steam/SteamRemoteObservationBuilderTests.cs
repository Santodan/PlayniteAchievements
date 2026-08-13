using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Providers.Steam.Models;

namespace PlayniteAchievements.Steam.Tests
{
    [TestClass]
    public class SteamRemoteObservationBuilderTests
    {
        private static SchemaAndPercentages BuildSchema()
        {
            return new SchemaAndPercentages
            {
                Achievements = new List<SchemaAchievement>
                {
                    new SchemaAchievement
                    {
                        Name = "ThrowStones",
                        DisplayName = "One Bird, Three Stones",
                        Description = "Hit three birds",
                        Icon = "https://cdn.example/apps/1/stones.jpg",
                        IconGray = "https://cdn.example/apps/1/stones_gray.jpg"
                    },
                    new SchemaAchievement
                    {
                        Name = "WhipEars",
                        DisplayName = "Rose's Best Friend",
                        Description = "Pet the creature",
                        Icon = "https://cdn.example/apps/1/ears.jpg",
                        IconGray = "https://cdn.example/apps/1/ears_gray.jpg"
                    }
                }
            };
        }

        [TestMethod]
        public void Build_EmitsSchemaApiNames_NotScrapedCompositeKeys()
        {
            var rows = new List<ScrapedAchievement>
            {
                new ScrapedAchievement
                {
                    // What the page parser actually produces: title|description, not an api name.
                    Key = "One Bird, Three Stones|Hit three birds",
                    DisplayName = "One Bird, Three Stones",
                    Description = "Hit three birds",
                    IconUrl = "https://community.cdn/apps/1/stones.jpg",
                    IsUnlocked = true
                }
            };

            var built = SteamRemoteObservationBuilder.Build(BuildSchema(), rows);

            Assert.AreEqual(1, built.Observations.Count);
            Assert.AreEqual("ThrowStones", built.Observations[0].ApiName);
            Assert.AreEqual(0, built.UnresolvedUnlockedRows);
            Assert.IsFalse(
                built.Observations.Any(o => o.ApiName.Contains("|")),
                "A composite display key reached the writer, where it can never match a stored api name.");
        }

        [TestMethod]
        public void Build_ResolvesThroughLocalizedDisplayText()
        {
            var rows = new List<ScrapedAchievement>
            {
                new ScrapedAchievement
                {
                    Key = "Un oiseau, trois pierres|Touchez trois oiseaux",
                    DisplayName = "Un oiseau, trois pierres",
                    Description = "Touchez trois oiseaux",
                    IconUrl = "https://community.cdn/apps/1/stones.jpg",
                    IsUnlocked = true
                }
            };

            var built = SteamRemoteObservationBuilder.Build(BuildSchema(), rows);

            Assert.AreEqual(1, built.Observations.Count);
            Assert.AreEqual("ThrowStones", built.Observations[0].ApiName);
        }

        [TestMethod]
        public void Build_CarriesUnlockStateAndTime()
        {
            var unlockedAt = new DateTime(2026, 8, 13, 2, 2, 40, DateTimeKind.Utc);
            var rows = new List<ScrapedAchievement>
            {
                new ScrapedAchievement
                {
                    DisplayName = "One Bird, Three Stones",
                    Description = "Hit three birds",
                    IconUrl = "https://community.cdn/apps/1/stones.jpg",
                    IsUnlocked = true,
                    UnlockTimeUtc = unlockedAt
                }
            };

            var built = SteamRemoteObservationBuilder.Build(BuildSchema(), rows);

            Assert.IsTrue(built.Observations[0].Unlocked);
            Assert.AreEqual(unlockedAt, built.Observations[0].UnlockTimeUtc);
        }

        [TestMethod]
        public void Build_IgnoresLockedRows()
        {
            var rows = new List<ScrapedAchievement>
            {
                new ScrapedAchievement
                {
                    DisplayName = "Rose's Best Friend",
                    Description = "Pet the creature",
                    IconUrl = "https://community.cdn/apps/1/ears_gray.jpg",
                    IsUnlocked = false
                }
            };

            var built = SteamRemoteObservationBuilder.Build(BuildSchema(), rows);

            Assert.AreEqual(0, built.Observations.Count);
            Assert.AreEqual(0, built.UnresolvedUnlockedRows, "A locked row is not an unresolved one.");
        }

        [TestMethod]
        public void Build_DropsUnlockedRowsWithNoIconMatch_AndReportsTheCount()
        {
            var rows = new List<ScrapedAchievement>
            {
                new ScrapedAchievement
                {
                    Key = "Mystery|Unknown art",
                    DisplayName = "Mystery",
                    Description = "Unknown art",
                    IconUrl = "https://community.cdn/apps/1/not_in_schema.jpg",
                    IsUnlocked = true
                }
            };

            var built = SteamRemoteObservationBuilder.Build(BuildSchema(), rows);

            Assert.AreEqual(0, built.Observations.Count);
            Assert.AreEqual(1, built.UnresolvedUnlockedRows);
        }

        [TestMethod]
        public void Build_ReturnsNothing_WhenSchemaIsUnusable()
        {
            var rows = new List<ScrapedAchievement>
            {
                new ScrapedAchievement
                {
                    Key = "One Bird, Three Stones|Hit three birds",
                    DisplayName = "One Bird, Three Stones",
                    Description = "Hit three birds",
                    IconUrl = "https://community.cdn/apps/1/stones.jpg",
                    IsUnlocked = true
                }
            };

            foreach (var schema in new[] { null, new SchemaAndPercentages(), new SchemaAndPercentages { Achievements = new List<SchemaAchievement>() } })
            {
                var built = SteamRemoteObservationBuilder.Build(schema, rows);
                Assert.AreEqual(0, built.Observations.Count);
            }
        }

        [TestMethod]
        public void Build_HandlesNullAndEmptyRows()
        {
            Assert.AreEqual(0, SteamRemoteObservationBuilder.Build(BuildSchema(), null).Observations.Count);
            Assert.AreEqual(0, SteamRemoteObservationBuilder.Build(BuildSchema(), new List<ScrapedAchievement>()).Observations.Count);
            Assert.AreEqual(0, SteamRemoteObservationBuilder.Build(BuildSchema(), new List<ScrapedAchievement> { null }).Observations.Count);
        }
    }
}
