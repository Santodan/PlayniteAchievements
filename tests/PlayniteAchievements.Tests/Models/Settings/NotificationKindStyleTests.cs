using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Tests.Models.Settings
{
    /// <summary>
    /// The per-kind notification styles: which kind an unlock resolves to, and the seeding,
    /// cloning and clearing of a kind's own copy of a style.
    /// </summary>
    [TestClass]
    public class NotificationKindStyleTests
    {
        [TestMethod]
        public void Resolve_MapsEachFlagToItsKind()
        {
            Assert.AreEqual(NotificationKind.Base, NotificationKindResolver.Resolve(null));
            Assert.AreEqual(
                NotificationKind.Base,
                NotificationKindResolver.Resolve(new AchievementUnlockedEventArgs()));
            Assert.AreEqual(
                NotificationKind.Capstone,
                NotificationKindResolver.Resolve(new AchievementUnlockedEventArgs { IsCapstone = true }));
            Assert.AreEqual(
                NotificationKind.Completion,
                NotificationKindResolver.Resolve(new AchievementUnlockedEventArgs { IsGameCompleted = true }));
            Assert.AreEqual(
                NotificationKind.Friend,
                NotificationKindResolver.Resolve(new AchievementUnlockedEventArgs { IsFriendUnlock = true }));
            Assert.AreEqual(
                NotificationKind.Progress,
                NotificationKindResolver.Resolve(new AchievementUnlockedEventArgs { IsProgressUpdate = true }));
        }

        [TestMethod]
        public void Resolve_FallsThroughToTheRarityTierForAPlainUnlock()
        {
            Assert.AreEqual(
                NotificationKind.Common,
                NotificationKindResolver.Resolve(
                    new AchievementUnlockedEventArgs { RarityTier = "Common" }));
            Assert.AreEqual(
                NotificationKind.Uncommon,
                NotificationKindResolver.Resolve(
                    new AchievementUnlockedEventArgs { RarityTier = "uncommon" }));
            Assert.AreEqual(
                NotificationKind.Rare,
                NotificationKindResolver.Resolve(
                    new AchievementUnlockedEventArgs { RarityTier = "Rare" }));
            Assert.AreEqual(
                NotificationKind.UltraRare,
                NotificationKindResolver.Resolve(
                    new AchievementUnlockedEventArgs { RarityTier = "UltraRare" }));

            // No usable tier means the shared style, not a guessed one.
            Assert.AreEqual(
                NotificationKind.Base,
                NotificationKindResolver.Resolve(new AchievementUnlockedEventArgs { RarityTier = "  " }));
            Assert.AreEqual(
                NotificationKind.Base,
                NotificationKindResolver.Resolve(new AchievementUnlockedEventArgs { RarityTier = "legendary" }));
        }

        [TestMethod]
        public void Resolve_LetsTheSpecialKindsBeatTheRarityTier()
        {
            Assert.AreEqual(
                NotificationKind.Capstone,
                NotificationKindResolver.Resolve(
                    new AchievementUnlockedEventArgs { IsCapstone = true, RarityTier = "Rare" }));
            Assert.AreEqual(
                NotificationKind.Friend,
                NotificationKindResolver.Resolve(
                    new AchievementUnlockedEventArgs { IsFriendUnlock = true, RarityTier = "Rare" }));
            Assert.AreEqual(
                NotificationKind.Completion,
                NotificationKindResolver.Resolve(
                    new AchievementUnlockedEventArgs { IsGameCompleted = true, RarityTier = "Common" }));
        }

        [TestMethod]
        public void Resolve_PrefersCompletionThenCapstoneWhenFlagsOverlap()
        {
            // A friend's 100% is a completion notification, not a friend one.
            Assert.AreEqual(
                NotificationKind.Completion,
                NotificationKindResolver.Resolve(
                    new AchievementUnlockedEventArgs { IsGameCompleted = true, IsFriendUnlock = true }));

            // The capstone unlock that finishes a game stays a capstone; the standalone 100%
            // notification that follows it is the one that reports Completion.
            Assert.AreEqual(
                NotificationKind.Capstone,
                NotificationKindResolver.Resolve(
                    new AchievementUnlockedEventArgs { IsCapstone = true, IsCompletionAchievement = true }));

            Assert.AreEqual(
                NotificationKind.Completion,
                NotificationKindResolver.Resolve(
                    new AchievementUnlockedEventArgs { IsGameCompleted = true, IsCapstone = true }));

            Assert.AreEqual(
                NotificationKind.Progress,
                NotificationKindResolver.Resolve(
                    new AchievementUnlockedEventArgs { IsProgressUpdate = true, IsFriendUnlock = true }));
        }

        [TestMethod]
        public void KindStyle_FollowsTheSharedStyleUntilEnabled()
        {
            var style = NotificationStyleSettings.CreateDefault();

            Assert.IsFalse(style.HasKindStyle(NotificationKind.Capstone));
            Assert.AreSame(style, style.ResolveKind(NotificationKind.Capstone));
            Assert.AreSame(style, style.ResolveKind(NotificationKind.Base));
            Assert.AreSame(style, NotificationStyleResolver.ApplyKind(style, NotificationKind.Capstone));
        }

        [TestMethod]
        public void EnableKindStyle_SeedsFromTheSharedStyleAndThenStandsApart()
        {
            var style = NotificationStyleSettings.CreateDefault();
            style.Toast.ShowIcon = false;
            style.Toast.CardWidth = 420;

            var capstone = style.EnableKindStyle(NotificationKind.Capstone);

            Assert.IsNotNull(capstone);
            Assert.AreNotSame(style, capstone);
            Assert.IsFalse(capstone.Toast.ShowIcon, "the seed is a copy of the shared style");
            Assert.AreEqual(420d, capstone.Toast.CardWidth);
            Assert.AreEqual(0, capstone.KindStyles.Count, "a kind style is a leaf");

            // Later edits to the shared style do not reach an enabled kind, and the reverse.
            style.Toast.CardWidth = 500;
            capstone.Toast.ShowName = false;
            Assert.AreEqual(420d, capstone.Toast.CardWidth);
            Assert.IsTrue(style.Toast.ShowName);

            Assert.IsTrue(style.HasKindStyle(NotificationKind.Capstone));
            Assert.AreSame(capstone, style.ResolveKind(NotificationKind.Capstone));
            Assert.AreSame(capstone, NotificationStyleResolver.ApplyKind(style, NotificationKind.Capstone));
            Assert.AreSame(style, style.ResolveKind(NotificationKind.Completion));

            // Enabling again returns the existing copy rather than re-seeding it.
            Assert.AreSame(capstone, style.EnableKindStyle(NotificationKind.Capstone));
        }

        [TestMethod]
        public void EnableKindStyle_IsANoOpForBase()
        {
            var style = NotificationStyleSettings.CreateDefault();

            Assert.AreSame(style, style.EnableKindStyle(NotificationKind.Base));
            Assert.AreEqual(0, style.KindStyles.Count);
            Assert.IsFalse(style.ClearKindStyle(NotificationKind.Base));
        }

        [TestMethod]
        public void ClearKindStyle_PutsTheKindBackOnTheSharedStyle()
        {
            var style = NotificationStyleSettings.CreateDefault();
            style.EnableKindStyle(NotificationKind.Progress);

            Assert.IsTrue(style.ClearKindStyle(NotificationKind.Progress));
            Assert.IsFalse(style.HasKindStyle(NotificationKind.Progress));
            Assert.AreSame(style, style.ResolveKind(NotificationKind.Progress));
            Assert.IsFalse(style.ClearKindStyle(NotificationKind.Progress));
        }

        [TestMethod]
        public void Clone_CopiesKindStylesOneLevelDeep()
        {
            var style = NotificationStyleSettings.CreateDefault();
            var friend = style.EnableKindStyle(NotificationKind.Friend);
            friend.Toast.ShowIcon = false;
            friend.Frame.CardWidth = 640;

            var clone = style.Clone();

            Assert.IsTrue(clone.HasKindStyle(NotificationKind.Friend));
            var clonedFriend = clone.ResolveKind(NotificationKind.Friend);
            Assert.AreNotSame(friend, clonedFriend);
            Assert.IsFalse(clonedFriend.Toast.ShowIcon);
            Assert.AreEqual(640d, clonedFriend.Frame.CardWidth);
            Assert.AreEqual(0, clonedFriend.KindStyles.Count);

            // The clone's kind copy is independent of the original's.
            clonedFriend.Toast.ShowIcon = true;
            Assert.IsFalse(friend.Toast.ShowIcon);
        }

        [TestMethod]
        public void Clone_KeepsShowIcon()
        {
            var style = NotificationStyleSettings.CreateDefault();
            Assert.IsTrue(style.Toast.ShowIcon);
            Assert.IsTrue(style.Frame.ShowIcon);

            style.Toast.ShowIcon = false;
            var clone = style.Clone();

            Assert.IsFalse(clone.Toast.ShowIcon);
            Assert.IsTrue(clone.Frame.ShowIcon);
        }
    }
}
