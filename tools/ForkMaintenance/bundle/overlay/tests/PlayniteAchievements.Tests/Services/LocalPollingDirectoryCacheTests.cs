using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.InGameMonitoring;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class LocalPollingDirectoryCacheTests
    {
        [TestMethod]
        public void UnchangedTopologyReusesDiscoveryAndSettingsOrDirectoryChangesInvalidateIt()
        {
            var root = Path.Combine(Path.GetTempPath(), "PollingDirectoryCache-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            try
            {
                var cache = new LocalPollingDirectoryCache();
                var discoveries = 0;
                IEnumerable<string> Discover(Action<string> observe)
                {
                    discoveries++;
                    observe(root);
                    return Directory.GetDirectories(root);
                }
                Assert.AreEqual(0, cache.GetOrDiscover("settings-a", Discover).Length);
                cache.GetOrDiscover("settings-a", Discover);
                Assert.AreEqual(1, discoveries, "An empty but successful scan can be reused.");

                Directory.CreateDirectory(Path.Combine(root, "new-source"));
                // Avoid depending on filesystem timestamp resolution or the wall clock.
                Directory.SetLastWriteTimeUtc(root, Directory.GetLastWriteTimeUtc(root).AddSeconds(10));
                Assert.AreEqual(1, cache.GetOrDiscover("settings-a", Discover).Length);
                Assert.AreEqual(2, discoveries);
                cache.GetOrDiscover("settings-b", Discover);
                Assert.AreEqual(3, discoveries, "Changed expanded paths/exclusions require discovery.");
                new LocalPollingDirectoryCache().GetOrDiscover("settings-b", Discover);
                Assert.AreEqual(4, discoveries, "A new game session cannot inherit discovery.");
            }
            finally { Directory.Delete(root, true); }
        }

        [TestMethod]
        public void FailedOrDeletedDirectoryNeverLeavesAReusableSuccessfulSnapshot()
        {
            var root = Path.Combine(Path.GetTempPath(), "PollingDirectoryCache-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            try
            {
                var cache = new LocalPollingDirectoryCache();
                var discoveries = 0;
                IEnumerable<string> Discover(Action<string> observe)
                {
                    discoveries++;
                    observe(root);
                    observe(null); // Enumeration/access failure reported by the resolver.
                    return new[] { root };
                }
                cache.GetOrDiscover("settings", Discover);
                cache.GetOrDiscover("settings", Discover);
                Assert.AreEqual(2, discoveries);
                Directory.Delete(root);
                cache.GetOrDiscover("settings", Discover);
                Assert.AreEqual(3, discoveries);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
