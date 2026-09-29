using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Providers.Xenia;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class XeniaAccountResolverTests
    {
        [TestMethod]
        public void MergeProgress_UnlockedAnywhere_TakesEarliestUnlockedTime()
        {
            var merged = XeniaAccountResolver.MergeProgress(new[]
            {
                new[]
                {
                    Progress(1, true, 200),
                    Progress(2, false, 0),
                    Progress(3, true, 0)
                },
                new[]
                {
                    Progress(1, true, 100),
                    Progress(2, true, 300),
                    Progress(3, true, 700),
                    Progress(4, false, 0)
                }
            });

            CollectionAssert.AreEqual(new uint[] { 1, 2, 3, 4 }, merged.Select(p => p.Id).ToArray());
            AssertProgress(merged[0], true, 100);
            AssertProgress(merged[1], true, 300);
            AssertProgress(merged[2], true, 700);
            AssertProgress(merged[3], false, 0);
        }

        [TestMethod]
        public void MergeProgress_UnlockedCopyReplacesLockedCopyTime()
        {
            var merged = XeniaAccountResolver.MergeProgress(new[]
            {
                new[] { Progress(1, false, 50) },
                new[] { Progress(1, true, 900) }
            });

            AssertProgress(merged.Single(), true, 900);
        }

        [TestMethod]
        public void MergeProgress_SingleCopy_PassesThroughUnchanged()
        {
            var merged = XeniaAccountResolver.MergeProgress(new[]
            {
                new[] { Progress(1, true, 10), Progress(2, false, 20) }
            });

            AssertProgress(merged[0], true, 10);
            AssertProgress(merged[1], false, 20);
        }

        [TestMethod]
        public void FindAccountDirectoriesUnderRoot_FindsEveryProfileWithAccountFile()
        {
            var root = CreateTempDirectory();
            try
            {
                var first = Path.Combine(root, "content", "E03000000000000A", "FFFE07D1", "00010000", "E03000000000000A");
                var second = Path.Combine(root, "content", "E03000000000000B", "FFFE07D1", "00010000", "E03000000000000B");
                var noAccount = Path.Combine(root, "content", "E03000000000000C", "FFFE07D1", "00010000", "E03000000000000C");
                foreach (var directory in new[] { first, second, noAccount })
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllBytes(Path.Combine(first, "Account"), new byte[1]);
                File.WriteAllBytes(Path.Combine(second, "Account"), new byte[1]);

                var found = XeniaAccountResolver.FindAccountDirectoriesUnderRoot(root).ToList();

                CollectionAssert.AreEqual(new[] { first, second }, found);
                Assert.AreEqual(
                    root.TrimEnd('\\'),
                    XeniaAccountResolver.GetXeniaRoot(first + "\\").TrimEnd('\\'));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void ResolveAccountDirectories_SkipsConfiguredPathsWithoutAccountFile()
        {
            var root = CreateTempDirectory();
            try
            {
                var valid = XeniaScannerTests.CreateAccountDirectory(root, "canary");
                var noAccount = Path.Combine(root, "empty");
                Directory.CreateDirectory(noAccount);

                var settings = new XeniaSettings
                {
                    AccountPaths = new List<string> { noAccount, valid, Path.Combine(root, "missing") }
                };

                CollectionAssert.AreEqual(
                    new[] { valid },
                    XeniaAccountResolver.ResolveAccountDirectories(null, settings, null));
                Assert.AreEqual("LOCPlayAch_XeniaValidation_NoAccount", XeniaAccountResolver.ValidateAccountPath(noAccount).MessageKey);
                Assert.AreEqual("LOCPlayAch_InvalidPath", XeniaAccountResolver.ValidateAccountPath(Path.Combine(root, "missing")).MessageKey);
                Assert.IsTrue(XeniaAccountResolver.ValidateAccountPath(valid).IsValid);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void Settings_LegacyAccountPath_LoadsAsOneEntryAndIsNotWrittenBack()
        {
            var settings = new XeniaSettings();
            settings.DeserializeFromJson("{\"IsEnabled\":true,\"AccountPath\":\"  C:\\\\Xenia\\\\account  \"}");

            CollectionAssert.AreEqual(new[] { "C:\\Xenia\\account" }, settings.AccountPaths);

            var json = JObject.Parse(settings.SerializeToJson());
            Assert.IsNull(json["AccountPath"]);
            CollectionAssert.AreEqual(new[] { "C:\\Xenia\\account" }, json["AccountPaths"].ToObject<string[]>());
        }

        [TestMethod]
        public void Settings_Clone_PreservesEveryAccountPath()
        {
            var settings = new XeniaSettings
            {
                AccountPaths = new List<string> { "C:\\stock", "C:\\canary" }
            };

            var clone = (XeniaSettings)settings.Clone();

            Assert.AreNotSame(settings.AccountPaths, clone.AccountPaths);
            CollectionAssert.AreEqual(settings.AccountPaths, clone.AccountPaths);
        }

        [TestMethod]
        public void ProviderPathList_Normalize_TrimsDropsBlanksAndDuplicates()
        {
            var normalized = ProviderPathList.Normalize(new[]
            {
                " C:\\a ",
                "",
                null,
                "\"C:\\b\"",
                "c:\\A\\",
                "C:\\b"
            });

            CollectionAssert.AreEqual(new[] { "C:\\a", "C:\\b" }, normalized);
        }

        private static XeniaAchievementProgress Progress(uint id, bool unlocked, ulong time)
        {
            return new XeniaAchievementProgress { Id = id, Unlocked = unlocked, UnlockTime = time };
        }

        private static void AssertProgress(XeniaAchievementProgress progress, bool unlocked, ulong time)
        {
            Assert.AreEqual(unlocked, progress.Unlocked, $"Unlocked for id {progress.Id}");
            Assert.AreEqual(time, progress.UnlockTime, $"UnlockTime for id {progress.Id}");
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievementsTests",
                nameof(XeniaAccountResolverTests),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
