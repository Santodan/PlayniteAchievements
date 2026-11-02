using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Tests.Services.UI
{
    [TestClass]
    [TestCategory("Services")]
    public class IcoFontCatalogTests
    {
        [TestMethod]
        public void CategoryOf_PlacesACodeInTheGroupOfTheIcoFontCategoryItFallsIn()
        {
            Assert.AreEqual("Fantasy", IcoFontCatalog.CategoryOf("E800"));
            Assert.AreEqual("Fantasy", IcoFontCatalog.CategoryOf("E828"));
            Assert.AreEqual("Office & School", IcoFontCatalog.CategoryOf("EBDF"));
            Assert.AreEqual("Animals & Nature", IcoFontCatalog.CategoryOf("E829"));
            Assert.AreEqual("Sport & Health", IcoFontCatalog.CategoryOf("EDDE"));
            Assert.AreEqual("Interface", IcoFontCatalog.CategoryOf("F02E"));
        }

        [TestMethod]
        public void CategoryOf_IsNullBeforeTheFirstCategoryOrForANonCode()
        {
            Assert.IsNull(IcoFontCatalog.CategoryOf("E000"));
            Assert.IsNull(IcoFontCatalog.CategoryOf("not-hex"));
        }

        [TestMethod]
        public void Categories_ListTwelveGroupsOnce()
        {
            var names = IcoFontCatalog.Categories.Select(group => group.Name).ToList();

            Assert.AreEqual(12, names.Count);
            Assert.AreEqual(names.Count, names.Distinct().Count());
            Assert.AreEqual("Interface", names[0]);
        }

        [TestMethod]
        public void EveryGroupIcon_IsOneOfItsOwnGlyphs()
        {
            foreach (var group in IcoFontCatalog.Categories)
            {
                Assert.AreEqual(group.Name, IcoFontCatalog.CategoryOf(group.Icon), group.Icon);
            }
        }
    }
}
