using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK.Models;
using PlayniteAchievements.Providers.Xbox;
using System;
using System.IO;

namespace PlayniteAchievements.Providers.Tests
{
    [TestClass]
    public class XboxTitleIdResolverTests
    {
        [TestMethod]
        public void TryResolveFromGameInstall_RootConfigHexTitleId_ReturnsDecimal()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                WriteConfig(tempDirectory, "<Game><TitleId>FFFFFFFF</TitleId></Game>");
                var game = new Game { InstallDirectory = tempDirectory };

                var result = XboxTitleIdResolver.TryResolveFromGameInstall(game, null, out var titleId);

                Assert.IsTrue(result);
                Assert.AreEqual("4294967295", titleId);
            }
            finally
            {
                DeleteTempDirectory(tempDirectory);
            }
        }

        [TestMethod]
        public void TryResolveFromGameInstall_ContentConfigHexTitleId_ReturnsDecimal()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var contentDirectory = Path.Combine(tempDirectory, "Content");
                Directory.CreateDirectory(contentDirectory);
                WriteConfig(contentDirectory, "<Game><TitleId>0000000A</TitleId></Game>");
                var game = new Game { InstallDirectory = tempDirectory };

                var result = XboxTitleIdResolver.TryResolveFromGameInstall(game, null, out var titleId);

                Assert.IsTrue(result);
                Assert.AreEqual("10", titleId);
            }
            finally
            {
                DeleteTempDirectory(tempDirectory);
            }
        }

        [TestMethod]
        public void TryResolveFromGameInstall_DecimalTitleId_PassesThrough()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                WriteConfig(tempDirectory, "<Game><TitleId>123456789</TitleId></Game>");
                var game = new Game { InstallDirectory = tempDirectory };

                var result = XboxTitleIdResolver.TryResolveFromGameInstall(game, null, out var titleId);

                Assert.IsTrue(result);
                Assert.AreEqual("123456789", titleId);
            }
            finally
            {
                DeleteTempDirectory(tempDirectory);
            }
        }

        [TestMethod]
        public void TryResolveFromGameInstall_MissingConfig_ReturnsFalse()
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                var game = new Game { InstallDirectory = tempDirectory };

                var result = XboxTitleIdResolver.TryResolveFromGameInstall(game, null, out var titleId);

                Assert.IsFalse(result);
                Assert.IsNull(titleId);
            }
            finally
            {
                DeleteTempDirectory(tempDirectory);
            }
        }

        [DataTestMethod]
        [DataRow("<Game></Game>")]
        [DataRow("<Game><TitleId>FFFFFFFF")]
        [DataRow("<Game><TitleId>not-a-title</TitleId></Game>")]
        [DataRow("<Game><TitleId></TitleId></Game>")]
        public void TryResolveFromGameInstall_InvalidConfig_ReturnsFalse(string configContent)
        {
            var tempDirectory = CreateTempDirectory();

            try
            {
                WriteConfig(tempDirectory, configContent);
                var game = new Game { InstallDirectory = tempDirectory };

                var result = XboxTitleIdResolver.TryResolveFromGameInstall(game, null, out var titleId);

                Assert.IsFalse(result);
                Assert.IsNull(titleId);
            }
            finally
            {
                DeleteTempDirectory(tempDirectory);
            }
        }

        private static string CreateTempDirectory()
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "PlayniteAchievements.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            return tempDirectory;
        }

        private static void WriteConfig(string directory, string content)
        {
            File.WriteAllText(Path.Combine(directory, "MicrosoftGame.config"), content);
        }

        private static void DeleteTempDirectory(string tempDirectory)
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, true);
            }
        }
    }
}
