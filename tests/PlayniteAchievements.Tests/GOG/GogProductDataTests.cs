using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.GOG.Models;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Gog.Tests
{
    [TestClass]
    public class GogProductDataTests
    {
        [TestMethod]
        public void CandidateBuildMetaUrls_OrdersListedThenNewestPublished()
        {
            var data = new GogProductData
            {
                Builds = new List<GogProductBuild>
                {
                    new GogProductBuild { Link = "https://example.com/unlisted-new", Listed = false, DatePublished = "2025-01-01T00:00:00Z" },
                    new GogProductBuild { Link = "https://example.com/listed-old", Listed = true, DatePublished = "2024-01-01T00:00:00Z" },
                    new GogProductBuild { Link = "https://example.com/listed-new", Listed = true, DatePublished = "2025-01-01T00:00:00Z" }
                }
            };

            CollectionAssert.AreEqual(
                new[]
                {
                    "https://example.com/listed-new",
                    "https://example.com/listed-old",
                    "https://example.com/unlisted-new"
                },
                data.CandidateBuildMetaUrls.ToList());
        }

        [TestMethod]
        public void CandidateBuildMetaUrls_SkipsBlankAndDuplicateLinks()
        {
            var data = new GogProductData
            {
                Builds = new List<GogProductBuild>
                {
                    null,
                    new GogProductBuild { Link = " ", Listed = true },
                    new GogProductBuild { Link = "https://example.com/a", Listed = true, DatePublished = "2025-01-01T00:00:00Z" },
                    new GogProductBuild { Link = "https://example.com/a", Listed = true, DatePublished = "2024-01-01T00:00:00Z" },
                    new GogProductBuild { Link = "https://example.com/b", Listed = false }
                }
            };

            CollectionAssert.AreEqual(
                new[] { "https://example.com/a", "https://example.com/b" },
                data.CandidateBuildMetaUrls.ToList());
        }

        [TestMethod]
        public void CandidateBuildMetaUrls_CapsCandidateCount()
        {
            var data = new GogProductData
            {
                Builds = Enumerable.Range(0, 45)
                    .Select(i => new GogProductBuild { Link = "https://example.com/" + i, Listed = true })
                    .ToList()
            };

            Assert.AreEqual(GogProductData.MaxBuildMetaCandidates, data.CandidateBuildMetaUrls.Count);
        }

        [TestMethod]
        public void CandidateBuildMetaUrls_EmptyWhenNoBuildsPresent()
        {
            var data = new GogProductData();
            Assert.AreEqual(0, data.CandidateBuildMetaUrls.Count);
        }

        [TestMethod]
        public void ResolvedClientId_FallsBackToCamelCaseField()
        {
            var data = new GogProductData
            {
                ClientId = null,
                ClientIdCamel = "camel-client-id"
            };

            Assert.AreEqual("camel-client-id", data.ResolvedClientId);
        }
    }
}
