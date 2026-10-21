using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class CategoryKindHelperTests
    {
        [TestMethod]
        public void Normalize_CanonicalizesAliasesInVocabularyOrder()
        {
            Assert.AreEqual("Base|Update", CategoryKindHelper.Normalize("update|base"));
            Assert.AreEqual("DLC|Update", CategoryKindHelper.Normalize("Update, dlc"));
            Assert.AreEqual("Subset", CategoryKindHelper.Normalize("SUBSET"));
        }

        [TestMethod]
        public void Normalize_DropsBlankDefaultAndNone()
        {
            Assert.IsNull(CategoryKindHelper.Normalize(null));
            Assert.IsNull(CategoryKindHelper.Normalize("Default"));
            Assert.IsNull(CategoryKindHelper.Normalize(CategoryKindHelper.None));
        }

        [TestMethod]
        public void Normalize_KeepsAnUnknownKindAfterTheKnownOnes()
        {
            Assert.AreEqual("DLC|Season", CategoryKindHelper.Normalize("Season|DLC"), "a kind a later build adds survives");
        }

        [TestMethod]
        public void NormalizeStored_KeepsNone()
        {
            Assert.AreEqual(CategoryKindHelper.None, CategoryKindHelper.NormalizeStored("none"));
            Assert.AreEqual("Base", CategoryKindHelper.NormalizeStored(" base "));
            Assert.IsNull(CategoryKindHelper.NormalizeStored(" "));
        }

        [TestMethod]
        public void WithKind_AddsAndRemovesOneKind()
        {
            Assert.AreEqual("DLC|Update", CategoryKindHelper.WithKind("DLC", "Update", include: true));
            Assert.AreEqual("DLC", CategoryKindHelper.WithKind("DLC|Update", "update", include: false));
            Assert.IsNull(CategoryKindHelper.WithKind("Base", "Base", include: false));
        }

        [TestMethod]
        public void Predicates_ReadCombinedKinds()
        {
            Assert.IsTrue(CategoryKindHelper.IsDlc("DLC|Update"));
            Assert.IsTrue(CategoryKindHelper.IsUpdate("DLC|Update"));
            Assert.IsFalse(CategoryKindHelper.IsBase("DLC|Update"));
        }

        [TestMethod]
        public void ResolveUnanimous_NeedsEveryValueToAgree()
        {
            Assert.AreEqual("DLC", CategoryKindHelper.ResolveUnanimous(new[] { "DLC", "dlc" }));
            Assert.IsNull(CategoryKindHelper.ResolveUnanimous(new[] { "DLC", "DLC", "Base" }), "no majority vote");
            Assert.IsNull(CategoryKindHelper.ResolveUnanimous(new[] { "DLC", null }), "a member without a kind breaks agreement");
            Assert.IsNull(CategoryKindHelper.ResolveUnanimous(new string[0]));
        }

        [TestMethod]
        public void ToCategoryLabelCellText_DefaultRendersBlank()
        {
            Assert.AreEqual(string.Empty, AchievementCategoryHelper.ToCategoryLabelCellText(null));
            Assert.AreEqual(string.Empty, AchievementCategoryHelper.ToCategoryLabelCellText("  "));
            Assert.AreEqual(string.Empty, AchievementCategoryHelper.ToCategoryLabelCellText("Default"));
            Assert.AreEqual("Story DLC", AchievementCategoryHelper.ToCategoryLabelCellText(" Story DLC "));
        }

        [TestMethod]
        public void ResolveEffective_PrefersCategoryThenProvider()
        {
            Assert.AreEqual("DLC", CategoryKindHelper.ResolveEffective("DLC", "Base"));
            Assert.IsNull(CategoryKindHelper.ResolveEffective(CategoryKindHelper.None, "Base"), "None on the category says it has none");
            Assert.AreEqual("Base", CategoryKindHelper.ResolveEffective(null, "base"));
            Assert.IsNull(CategoryKindHelper.ResolveEffective(null, null));
        }
    }
}
