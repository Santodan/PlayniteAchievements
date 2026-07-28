using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.RPCS3;
using System;
using System.IO;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class Rpcs3InstallationContextTests
    {
        [TestMethod]
        public void Create_UsesPersistedActiveUser_NotLowestNumberedProfile()
        {
            var root = CreateRoot();
            try
            {
                CreateUser(root, "00000001");
                CreateUser(root, "00000002");
                WriteActiveUser(root, "00000002");

                var context = Rpcs3InstallationContext.Create(root, null, null, null);

                Assert.IsNotNull(context);
                Assert.AreEqual("00000002", context.UserId);
                StringAssert.Contains(context.TrophyFolder, Path.Combine("00000002", "trophy"));
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public void Create_ExplicitLaunchUser_OverridesPersistedActiveUser()
        {
            var root = CreateRoot();
            try
            {
                CreateUser(root, "00000001");
                CreateUser(root, "00000002");
                WriteActiveUser(root, "00000002");

                var context = Rpcs3InstallationContext.Create(root, "00000001", "--user-id game action", null);

                Assert.IsNotNull(context);
                Assert.AreEqual("00000001", context.UserId);
                Assert.AreEqual("--user-id game action", context.UserIdSource);
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public void Create_MissingPersistedProfile_DoesNotFallBackToAnotherUser()
        {
            var root = CreateRoot();
            try
            {
                CreateUser(root, "00000001");
                WriteActiveUser(root, "00000002");

                Assert.IsNull(Rpcs3InstallationContext.Create(root, null, null, null));
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public void Create_PortableConfigAndVfsMapping_UsesPortableActiveProfile()
        {
            var root = CreateRoot();
            var relocatedHdd = Path.Combine(root, "relocated-hdd");
            var portableRoot = Path.Combine(root, "portable");
            try
            {
                Directory.CreateDirectory(Path.Combine(portableRoot, "config"));
                Directory.CreateDirectory(Path.Combine(portableRoot, "GuiConfigs"));
                Directory.CreateDirectory(Path.Combine(relocatedHdd, "home", "00000002", "trophy"));
                File.WriteAllText(Path.Combine(portableRoot, "config", "vfs.yml"), "/dev_hdd0/: '" + relocatedHdd + "'");
                File.WriteAllText(Path.Combine(portableRoot, "GuiConfigs", "persistent_settings.dat"), "[Users]\r\nactive_user=00000002\r\n");

                var context = Rpcs3InstallationContext.Create(root, null, null, null);

                Assert.IsNotNull(context);
                Assert.AreEqual("00000002", context.UserId);
                Assert.AreEqual(Path.GetFullPath(portableRoot), context.ConfigurationRoot);
                Assert.AreEqual(Path.GetFullPath(relocatedHdd), context.DevHdd0Root);
            }
            finally
            {
                DeleteDirectory(root);
            }
        }

        [TestMethod]
        public void TryGetRequestedUserId_ParsesBothSupportedLaunchForms()
        {
            Assert.IsTrue(Rpcs3InstallationResolver.TryGetRequestedUserId("--user-id 00000002 --no-gui", out var separated, out var separatedSpecified));
            Assert.IsTrue(separatedSpecified);
            Assert.AreEqual("00000002", separated);

            Assert.IsTrue(Rpcs3InstallationResolver.TryGetRequestedUserId("--user-id=\"00000003\"", out var equals, out var equalsSpecified));
            Assert.IsTrue(equalsSpecified);
            Assert.AreEqual("00000003", equals);
        }

        private static string CreateRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "PlayniteAchievementsTests", nameof(Rpcs3InstallationContextTests), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static void CreateUser(string root, string userId)
        {
            Directory.CreateDirectory(Path.Combine(root, "dev_hdd0", "home", userId, "trophy"));
        }

        private static void WriteActiveUser(string root, string userId)
        {
            var settingsDirectory = Path.Combine(root, "GuiConfigs");
            Directory.CreateDirectory(settingsDirectory);
            File.WriteAllText(Path.Combine(settingsDirectory, "persistent_settings.dat"), "[Users]\r\nactive_user=" + userId + "\r\n");
        }

        private static void DeleteDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }
}
