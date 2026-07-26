using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Providers.Steam.Models;

namespace PlayniteAchievements.Steam.Tests
{
    [TestClass]
    public class SteamAchievementApiNameResolverTests
    {
        [TestMethod]
        public void Resolve_MatchesByIconHash_WhenDisplayTextIsLocalized()
        {
            var schema = new SchemaAndPercentages
            {
                Achievements = new List<SchemaAchievement>
                {
                    new SchemaAchievement
                    {
                        Name = "ACH_WIN",
                        DisplayName = "Winner",
                        Description = "Win a match",
                        Icon = "https://cdn.example/apps/1/win.jpg",
                        IconGray = "https://cdn.example/apps/1/win_gray.jpg"
                    },
                    new SchemaAchievement
                    {
                        Name = "ACH_LOSE",
                        DisplayName = "Loser",
                        Description = "Lose a match",
                        Icon = "https://cdn.example/apps/1/lose.jpg",
                        IconGray = "https://cdn.example/apps/1/lose_gray.jpg"
                    }
                }
            };

            var rows = new List<ScrapedAchievement>
            {
                // French display text; only the icon filename ties back to the schema.
                new ScrapedAchievement
                {
                    DisplayName = "Gagnant",
                    Description = "Gagnez un match",
                    IconUrl = "https://community.cdn/apps/1/win.jpg",
                    IsUnlocked = true
                },
                new ScrapedAchievement
                {
                    DisplayName = "Perdant",
                    Description = "Perdez un match",
                    IconUrl = "https://community.cdn/apps/1/lose_gray.jpg",
                    IsUnlocked = false
                }
            };

            var resolved = SteamAchievementApiNameResolver.Resolve(schema, rows);

            Assert.AreEqual("ACH_WIN", resolved[rows[0]]);
            Assert.AreEqual("ACH_LOSE", resolved[rows[1]]);
        }

        [TestMethod]
        public void Resolve_UsesDescriptionTieBreaker_WhenIconShared()
        {
            var schema = new SchemaAndPercentages
            {
                Achievements = new List<SchemaAchievement>
                {
                    new SchemaAchievement { Name = "ACH_A", DisplayName = "A", Description = "First", Icon = "https://cdn/shared.jpg" },
                    new SchemaAchievement { Name = "ACH_B", DisplayName = "B", Description = "Second", Icon = "https://cdn/shared.jpg" }
                }
            };

            var rows = new List<ScrapedAchievement>
            {
                new ScrapedAchievement { DisplayName = "B", Description = "Second", IconUrl = "https://cdn/shared.jpg" }
            };

            var resolved = SteamAchievementApiNameResolver.Resolve(schema, rows);

            Assert.AreEqual("ACH_B", resolved[rows[0]]);
        }

        [TestMethod]
        public void Resolve_SkipsRow_WhenIconIsUnknown()
        {
            var schema = new SchemaAndPercentages
            {
                Achievements = new List<SchemaAchievement>
                {
                    new SchemaAchievement { Name = "ACH_A", DisplayName = "A", Description = "First", Icon = "https://cdn/a.jpg" }
                }
            };

            var rows = new List<ScrapedAchievement>
            {
                new ScrapedAchievement { DisplayName = "A", Description = "First", IconUrl = "https://cdn/unknown.jpg" }
            };

            var resolved = SteamAchievementApiNameResolver.Resolve(schema, rows);

            Assert.AreEqual(0, resolved.Count);
        }
    }
}
