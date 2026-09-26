using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Views.Converters;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class AchievementStatusToolTipConverterTests
    {
        private readonly AchievementStatusToolTipConverter _converter = new AchievementStatusToolTipConverter();

        private object Convert(
            bool unlocked,
            bool capstone = false,
            bool goal = false,
            bool filtered = false,
            bool filteredFromSummaries = false,
            bool missable = false,
            bool unobtainable = false)
        {
            return _converter.Convert(
                new object[] { unlocked, capstone, goal, filtered, filteredFromSummaries, missable, unobtainable },
                typeof(object),
                null,
                null);
        }

        [TestMethod]
        public void LockedAndUnlockedUsePlainState()
        {
            Assert.AreEqual("Locked", Convert(unlocked: false));
            Assert.AreEqual("Unlocked", Convert(unlocked: true));
        }

        [TestMethod]
        public void LockedMissableAppendsMissable()
        {
            Assert.AreEqual("Locked (Missable)", Convert(unlocked: false, missable: true));
        }

        [TestMethod]
        public void LockedUnobtainableAppendsUnobtainable()
        {
            Assert.AreEqual("Locked (Unobtainable)", Convert(unlocked: false, unobtainable: true));
            Assert.AreEqual("Unlocked", Convert(unlocked: true, unobtainable: true));
        }

        [TestMethod]
        public void UnlockedMissableDropsMissable()
        {
            Assert.AreEqual("Unlocked", Convert(unlocked: true, missable: true));
        }

        [TestMethod]
        public void CapstoneQualifiersCombineWithMissable()
        {
            Assert.AreEqual("Unlocked (Capstone)", Convert(unlocked: true, capstone: true));
            Assert.AreEqual("Locked (Capstone, Missable)", Convert(unlocked: false, capstone: true, missable: true));
        }

        [TestMethod]
        public void GoalTakesPriorityOverCapstone()
        {
            Assert.AreEqual("Locked (Goal)", Convert(unlocked: false, capstone: true, goal: true));
        }

        [TestMethod]
        public void FilteredStatesOverrideUnlockState()
        {
            Assert.AreEqual("Excluded", Convert(unlocked: true, filtered: true));
            Assert.AreEqual("Excluded from Summaries", Convert(unlocked: false, filteredFromSummaries: true, missable: true));
        }

        [TestMethod]
        public void IsMissableMatchesCombinedAndAliasedTypes()
        {
            Assert.IsTrue(AchievementCategoryTypeHelper.IsMissable("Base|Update|Missable"));
            Assert.IsTrue(AchievementCategoryTypeHelper.IsMissable("miss-able"));
            Assert.IsFalse(AchievementCategoryTypeHelper.IsMissable("Base|DLC"));
            Assert.IsFalse(AchievementCategoryTypeHelper.IsMissable(null));
            Assert.IsTrue(AchievementCategoryTypeHelper.IsUnobtainable("Base|Unobtainable"));
            Assert.IsFalse(AchievementCategoryTypeHelper.IsUnobtainable("Base|Missable"));
        }
    }
}
