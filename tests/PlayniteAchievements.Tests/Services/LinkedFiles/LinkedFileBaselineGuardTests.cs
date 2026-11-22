using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.LinkedFiles;
using System;

namespace PlayniteAchievements.Services.Tests.LinkedFiles
{
    [TestClass]
    public class LinkedFileBaselineGuardTests
    {
        private static LinkedFileLink ReadBefore(LinkedFileReadStatus status = LinkedFileReadStatus.Ok)
        {
            return new LinkedFileLink
            {
                FilePath = @"C:\games\achievements.ini",
                LastReadUtc = new DateTime(2026, 11, 17, 17, 31, 51, DateTimeKind.Utc),
                LastReadStatus = status.ToString()
            };
        }

        [TestMethod]
        public void AGameThatHasLostEverythingToALinkThatReadItBefore_IsHeldBack()
        {
            Assert.IsTrue(LinkedFileBaselineGuard.HasLostItsAchievements(ReadBefore(), 0, 0));
        }

        [TestMethod]
        public void ANewLink_AuthorsTheRowsItsFileNames()
        {
            var link = new LinkedFileLink { FilePath = @"C:\games\achievements.ini" };

            Assert.IsFalse(LinkedFileBaselineGuard.HasLostItsAchievements(link, 0, 0));
        }

        [TestMethod]
        public void ALinkWhoseOnlyReadFailed_AuthorsTheRowsItsFileNames()
        {
            Assert.IsFalse(LinkedFileBaselineGuard.HasLostItsAchievements(ReadBefore(LinkedFileReadStatus.FileMissing), 0, 0));
        }

        [TestMethod]
        public void AGameStillShowingAchievements_IsNotHeldBack()
        {
            Assert.IsFalse(LinkedFileBaselineGuard.HasLostItsAchievements(ReadBefore(), 131, 0));
        }

        [TestMethod]
        public void AGameWhoseCustomDefinitionsSurvive_IsNotHeldBack()
        {
            Assert.IsFalse(LinkedFileBaselineGuard.HasLostItsAchievements(ReadBefore(), 0, 131));
        }

        [TestMethod]
        public void NoLink_IsNotHeldBack()
        {
            Assert.IsFalse(LinkedFileBaselineGuard.HasLostItsAchievements(null, 0, 0));
        }
    }
}
