using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Tests.Services.UI
{
    [TestClass]
    [TestCategory("Services")]
    public class IcoFontCatalogTests
    {
        [TestMethod]
        public void CategoryOf_PlacesACodeInTheRunItStartsOrFallsIn()
        {
            Assert.AreEqual("Abstract", IcoFontCatalog.CategoryOf("E800"));
            Assert.AreEqual("Abstract", IcoFontCatalog.CategoryOf("E828"));
            Assert.AreEqual("Animal", IcoFontCatalog.CategoryOf("E829"));
            Assert.AreEqual("Sport", IcoFontCatalog.CategoryOf("EDDE"));
            Assert.AreEqual("Web Application", IcoFontCatalog.CategoryOf("F02E"));
        }

        [TestMethod]
        public void CategoryOf_IsNullBeforeTheFirstCategoryOrForANonCode()
        {
            Assert.IsNull(IcoFontCatalog.CategoryOf("E000"));
            Assert.IsNull(IcoFontCatalog.CategoryOf("not-hex"));
        }

        [TestMethod]
        public void Categories_ListEveryCategoryOnceInFontOrder()
        {
            Assert.AreEqual(30, IcoFontCatalog.Categories.Count);
            Assert.AreEqual("Abstract", IcoFontCatalog.Categories[0].Name);
            Assert.AreEqual("Web Application", IcoFontCatalog.Categories[29].Name);
        }

        [TestMethod]
        public void EveryCategoryIcon_IsOneOfItsOwnGlyphs()
        {
            foreach (var category in IcoFontCatalog.Categories)
            {
                Assert.AreEqual(category.Name, IcoFontCatalog.CategoryOf(category.Icon), category.Icon);
            }
        }
    }
}
