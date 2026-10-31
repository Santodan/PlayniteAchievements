using System;
using System.IO;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK.Models;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class MemoriesWildcardTests
    {
        [TestMethod]
        public void CustomPath_ExpandsFolderAndFilenameUsingReportedUnlockTime()
        {
            var root = Path.Combine(Path.GetTempPath(), "MemoriesWildcardTests_" + Guid.NewGuid());
            try
            {
                var settings = new LocalSettings
                {
                    ScreenshotSaveFolder = root + @"\<provider>\<gameName>\<date>",
                    ScreenshotFilenameTemplate = "<dateTime>_<achievementName>_<unlockIndex>_<unlockCount>_<gameId>"
                };
                var game = new Game { Id = Guid.NewGuid(), Name = "Test: Game" };
                var utc = new DateTime(2026, 10, 20, 12, 34, 56, DateTimeKind.Utc);
                var local = utc.ToLocalTime();
                var method = typeof(UnlockScreenshotService).GetMethod("BuildCustomScreenshotPath", BindingFlags.NonPublic | BindingFlags.Static);
                var path = (string)method.Invoke(null, new object[] { settings, game, "Local", "Cat: 7", 8, 100, "notification", false, utc });
                Assert.AreEqual(Path.Combine(root, "Local", "Test Game", local.ToString("yyyy-MM-dd")), Path.GetDirectoryName(path));
                Assert.AreEqual(local.ToString("yyyy-MM-dd_HH-mm-ss") + "_Cat_ 7_8_100_" + game.Id + "_notification.png", Path.GetFileName(path));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
    }
}
