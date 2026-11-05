using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Providers.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Steam.Tests
{
    [TestClass]
    public class SteamHuntersPublicSchemaTests
    {
        [TestMethod]
        public async Task Schema_UsesOnlyPublicSteamHuntersEndpoint()
        {
            string requestedUrl = null;
            var client = new SteamHuntersApiClient((url, ct) =>
            {
                requestedUrl = url;
                return Task.FromResult("[{\"apiName\":\"ACH_1\",\"name\":\"First\"}]");
            }, null, requestGapMs: 0);

            var rows = await client.GetSchemaAsync(71340, CancellationToken.None);

            Assert.AreEqual("https://steamhunters.com/api/apps/71340/achievements", requestedUrl);
            Assert.AreEqual("ACH_1", rows[0].ApiName);
            Assert.IsFalse(rows[0].Unlocked);
            Assert.IsNull(rows[0].UnlockTimeUtc);
        }

        [TestMethod]
        public void Schema_PreservesPublicDefinitionsAndFiltersInvalidOrDuplicateRows()
        {
            var rows = SteamHuntersApiClient.ParseSchema(71340, JArray.Parse(@"[
                { 'apiName': ' ACH_1 ', 'name': 'First &amp; Last', 'description': 'Find &lt;all&gt;',
                  'hidden': true, 'icon': 'abc.jpg', 'iconGray': 'https://example.com/gray.jpg',
                  'estimatedSteamPercentage': 12.5, 'points': 7 },
                { 'apiName': 'ach_1', 'name': 'Duplicate' }, { 'name': 'Missing ID' }, null
            ]"));

            Assert.AreEqual(1, rows.Count);
            Assert.AreEqual("ACH_1", rows[0].ApiName);
            Assert.AreEqual("First & Last", rows[0].DisplayName);
            Assert.AreEqual("Find <all>", rows[0].Description);
            Assert.IsTrue(rows[0].Hidden);
            StringAssert.Contains(rows[0].UnlockedIconPath, "/71340/abc.jpg");
            Assert.AreEqual("https://example.com/gray.jpg", rows[0].LockedIconPath);
            Assert.AreEqual(12.5, rows[0].GlobalPercentUnlocked);
            Assert.AreEqual(7, rows[0].Points);
        }
    }
}
