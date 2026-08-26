using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Hydration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class AchievementDetailHydratorTests
    {
        [TestMethod]
        public void HydrateAll_EmptyCustomOrder_StampsProviderPositions()
        {
            var details = Details("first", "second", "third");

            Hydrate(details, new ResolvedGameCustomData());

            CollectionAssert.AreEqual(
                new[] { 0, 1, 2 },
                details.Select(d => d.DefaultOrderIndex).ToArray());
        }

        [TestMethod]
        public void HydrateAll_CustomOrder_RanksMatchedAndAppendsUnmatchedInSourceOrder()
        {
            var details = Details("alpha", "beta", "gamma", "delta");
            var customData = new ResolvedGameCustomData
            {
                // Case differs from the details to cover case-insensitive matching.
                AchievementOrder = new List<string> { "GAMMA", "Alpha" }
            };

            Hydrate(details, customData);

            Assert.AreEqual(1, details.Single(d => d.ApiName == "alpha").DefaultOrderIndex);
            Assert.AreEqual(2, details.Single(d => d.ApiName == "beta").DefaultOrderIndex);
            Assert.AreEqual(0, details.Single(d => d.ApiName == "gamma").DefaultOrderIndex);
            Assert.AreEqual(3, details.Single(d => d.ApiName == "delta").DefaultOrderIndex);
        }

        [TestMethod]
        public void HydrateAll_Rehydration_IsIdempotent()
        {
            var details = Details("alpha", "beta");
            var customData = new ResolvedGameCustomData
            {
                AchievementOrder = new List<string> { "beta" }
            };

            Hydrate(details, customData);
            Hydrate(details, customData);

            Assert.AreEqual(1, details.Single(d => d.ApiName == "alpha").DefaultOrderIndex);
            Assert.AreEqual(0, details.Single(d => d.ApiName == "beta").DefaultOrderIndex);
        }

        private static void Hydrate(List<AchievementDetail> details, ResolvedGameCustomData customData)
        {
            new AchievementDetailHydrator(new PersistedSettings()).HydrateAllWithCapstoneOverride(
                details,
                Guid.Empty,
                "steam",
                customData);
        }

        private static List<AchievementDetail> Details(params string[] apiNames)
        {
            return apiNames
                .Select(apiName => new AchievementDetail
                {
                    ApiName = apiName,
                    DisplayName = apiName
                })
                .ToList();
        }
    }
}
