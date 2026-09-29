using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.Ffxiv;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Ffxiv.Tests
{
    [TestClass]
    public class FfxivCharacterCacheTests
    {
        private const long CharacterId = 62353154;

        private string _dataPath;
        private DateTime _now;
        private int _fetchCount;

        [TestInitialize]
        public void Initialize()
        {
            _dataPath = Path.Combine(Path.GetTempPath(), "pa-ffxiv-cache-" + Guid.NewGuid().ToString("N"));
            _now = new DateTime(2026, 9, 10, 19, 46, 47, DateTimeKind.Utc);
            _fetchCount = 0;
        }

        [TestCleanup]
        public void Cleanup()
        {
            try
            {
                Directory.Delete(_dataPath, recursive: true);
            }
            catch
            {
            }
        }

        private FfxivCharacterCache CreateCache() =>
            new FfxivCharacterCache(null, _dataPath, () => _now);

        private Task<FfxivCharacter> FetchCharacter(CancellationToken token)
        {
            _fetchCount++;
            return Task.FromResult(new FfxivCharacter { Id = CharacterId, Name = "Fetch " + _fetchCount });
        }

        private Task<FfxivCharacter> FetchNotIndexed(CancellationToken token)
        {
            _fetchCount++;
            throw new FfxivCharacterNotIndexedException(CharacterId);
        }

        [TestMethod]
        public async Task BackToBackRefreshes_FetchOnce()
        {
            var cache = CreateCache();

            for (var i = 0; i < 50; i++)
            {
                _now = _now.AddSeconds(2);
                var character = await cache.GetAsync(CharacterId, FetchCharacter, CancellationToken.None);
                Assert.AreEqual("Fetch 1", character.Name);
            }

            Assert.AreEqual(1, _fetchCount);
        }

        [TestMethod]
        public async Task FetchesAgainOnceTheIntervalElapses()
        {
            var cache = CreateCache();
            await cache.GetAsync(CharacterId, FetchCharacter, CancellationToken.None);

            _now = _now.Add(FfxivCharacterCache.MinFetchInterval).AddSeconds(-1);
            await cache.GetAsync(CharacterId, FetchCharacter, CancellationToken.None);
            Assert.AreEqual(1, _fetchCount);

            _now = _now.AddSeconds(1);
            var character = await cache.GetAsync(CharacterId, FetchCharacter, CancellationToken.None);
            Assert.AreEqual(2, _fetchCount);
            Assert.AreEqual("Fetch 2", character.Name);
        }

        [TestMethod]
        public async Task IntervalSurvivesARestart()
        {
            await CreateCache().GetAsync(CharacterId, FetchCharacter, CancellationToken.None);

            _now = _now.AddMinutes(30);
            var character = await CreateCache().GetAsync(CharacterId, FetchCharacter, CancellationToken.None);

            Assert.AreEqual(1, _fetchCount);
            Assert.AreEqual("Fetch 1", character.Name);
        }

        [TestMethod]
        public async Task DifferentCharacter_Fetches()
        {
            var cache = CreateCache();
            await cache.GetAsync(CharacterId, FetchCharacter, CancellationToken.None);
            await cache.GetAsync(CharacterId + 1, FetchCharacter, CancellationToken.None);

            Assert.AreEqual(2, _fetchCount);
        }

        [TestMethod]
        public async Task ClockMovedBackwards_Fetches()
        {
            var cache = CreateCache();
            await cache.GetAsync(CharacterId, FetchCharacter, CancellationToken.None);

            _now = _now.AddMinutes(-5);
            await cache.GetAsync(CharacterId, FetchCharacter, CancellationToken.None);

            Assert.AreEqual(2, _fetchCount);
        }

        [TestMethod]
        public async Task NotIndexed_IsRethrownWithinItsShorterInterval()
        {
            var cache = CreateCache();

            await Assert.ThrowsExceptionAsync<FfxivCharacterNotIndexedException>(
                () => cache.GetAsync(CharacterId, FetchNotIndexed, CancellationToken.None));

            _now = _now.Add(FfxivCharacterCache.NotIndexedRetryInterval).AddSeconds(-1);
            await Assert.ThrowsExceptionAsync<FfxivCharacterNotIndexedException>(
                () => cache.GetAsync(CharacterId, FetchCharacter, CancellationToken.None));
            Assert.AreEqual(1, _fetchCount);

            _now = _now.AddSeconds(1);
            var character = await cache.GetAsync(CharacterId, FetchCharacter, CancellationToken.None);
            Assert.AreEqual(2, _fetchCount);
            Assert.IsNotNull(character);
        }

        [TestMethod]
        public async Task OtherFailures_AreNotCached()
        {
            var cache = CreateCache();

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(
                () => cache.GetAsync(
                    CharacterId,
                    token =>
                    {
                        _fetchCount++;
                        throw new InvalidOperationException();
                    },
                    CancellationToken.None));

            await cache.GetAsync(CharacterId, FetchCharacter, CancellationToken.None);
            Assert.AreEqual(2, _fetchCount);
        }

        [TestMethod]
        public void UserAgent_NamesVersionContactAndCharacter()
        {
            Assert.AreEqual(
                "PlayniteAchievements/3.2.2 (+https://github.com/justin-delano/PlayniteAchievements; character 62353154)",
                FfxivApiClient.BuildUserAgent("3.2.2", CharacterId));
        }

        [TestMethod]
        public void UserAgent_OmitsMissingVersionAndCharacter()
        {
            Assert.AreEqual(
                "PlayniteAchievements (+https://github.com/justin-delano/PlayniteAchievements)",
                FfxivApiClient.BuildUserAgent(null, null));
        }
    }
}
