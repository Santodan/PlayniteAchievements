using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Tests.Common
{
    [TestClass]
    public class LruCacheTests
    {
        [TestMethod]
        public void Set_BeyondCapacity_DropsLeastRecentlyUsed()
        {
            var cache = new LruCache<string, int>(2);
            cache.Set("a", 1);
            cache.Set("b", 2);
            Assert.IsTrue(cache.TryGetValue("a", out _));

            cache.Set("c", 3);

            Assert.AreEqual(2, cache.Count);
            Assert.IsTrue(cache.TryGetValue("a", out var a));
            Assert.AreEqual(1, a);
            Assert.IsFalse(cache.TryGetValue("b", out _));
            Assert.IsTrue(cache.TryGetValue("c", out _));
        }

        [TestMethod]
        public void Set_ExistingKey_ReplacesWithoutEvicting()
        {
            var cache = new LruCache<string, int>(2, StringComparer.OrdinalIgnoreCase);
            cache.Set("a", 1);
            cache.Set("b", 2);
            cache.Set("A", 10);

            Assert.AreEqual(2, cache.Count);
            Assert.IsTrue(cache.TryGetValue("a", out var a));
            Assert.AreEqual(10, a);
            Assert.IsTrue(cache.TryGetValue("b", out _));
        }

        [TestMethod]
        public void Clear_EmptiesTheCache()
        {
            var cache = new LruCache<int, string>(4);
            cache.Set(1, "one");
            cache.Clear();

            Assert.AreEqual(0, cache.Count);
            Assert.IsFalse(cache.TryGetValue(1, out _));
        }
    }
}
