using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Images;
using System.IO;

namespace PlayniteAchievements.Services.Friends.Tests
{
    [TestClass]
    public class FriendImageCachePathBuilderTests
    {
        [TestMethod]
        public void BuildAvatarRelativePath_KeysByProviderAndUser()
        {
            var path = FriendImageCachePathBuilder.BuildAvatarRelativePath("exophase", "12345");

            Assert.AreEqual(
                Path.Combine("icon_cache", "friendavatars", "exophase_12345.png"),
                path);
        }

        [TestMethod]
        public void BuildGameImageRelativePath_SanitizesProviderAndGameKeySegments()
        {
            var coverPath = FriendImageCachePathBuilder.BuildGameImageRelativePath(
                "exophase",
                "publisher/game:1",
                FriendImageCachePathBuilder.GameCoverFileName);

            Assert.AreEqual(
                Path.Combine("icon_cache", "friendgames", "exophase", "publisher_game_1", "cover.png"),
                coverPath);
        }

        [TestMethod]
        public void GetAchievementFileName_AppendsVariantSuffix()
        {
            Assert.AreEqual("boss_win.png",
                FriendImageCachePathBuilder.GetAchievementFileName("boss_win", AchievementIconVariant.Unlocked));
            Assert.AreEqual("boss_win.locked.png",
                FriendImageCachePathBuilder.GetAchievementFileName("boss_win", AchievementIconVariant.Locked));
        }

        [TestMethod]
        public void GetAchievementFileName_FallsBackWhenStemMissing()
        {
            Assert.AreEqual("achievement.png",
                FriendImageCachePathBuilder.GetAchievementFileName(null, AchievementIconVariant.Unlocked));
            Assert.AreEqual("achievement.locked.png",
                FriendImageCachePathBuilder.GetAchievementFileName("   ", AchievementIconVariant.Locked));
        }

        [TestMethod]
        public void AchievementIconPath_IsStableAndSharedAcrossFriends()
        {
            // The same game/achievement always maps to the same path regardless of caller (friend),
            // so a single cached copy is reused rather than re-downloaded per friend.
            var stems = AchievementIconCachePathBuilder.BuildFileStems(new[] { " boss:win " });
            var stem = stems["boss:win"];

            var unlocked = FriendImageCachePathBuilder.BuildGameImageRelativePath(
                "exophase",
                "game-123",
                FriendImageCachePathBuilder.GetAchievementFileName(stem, AchievementIconVariant.Unlocked));
            var locked = FriendImageCachePathBuilder.BuildGameImageRelativePath(
                "exophase",
                "game-123",
                FriendImageCachePathBuilder.GetAchievementFileName(stem, AchievementIconVariant.Locked));

            Assert.AreEqual(
                Path.Combine("icon_cache", "friendgames", "exophase", "game-123", "boss_win.png"),
                unlocked);
            Assert.AreEqual(
                Path.Combine("icon_cache", "friendgames", "exophase", "game-123", "boss_win.locked.png"),
                locked);
        }
    }
}
