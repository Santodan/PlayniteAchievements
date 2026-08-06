using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Tests.Services.UI
{
    /// <summary>
    /// Covers the font catalog's invariants rather than any particular machine's font set: which
    /// names survive the filters, and that the list is deduplicated, sorted, and cached.
    /// </summary>
    [TestClass]
    public class SystemFontCatalogTests
    {
        private const int GdiTruncatedNameLength = 31;

        [TestMethod]
        public void Families_IsNotEmpty()
        {
            Assert.IsTrue(SystemFontCatalog.Families.Count > 0);
        }

        [TestMethod]
        public void Families_OnlyContainsNamesWpfCanRender()
        {
            // The whole point of the validation gate: an unresolvable name would silently render as
            // the fallback font instead of what the picker showed.
            var unrenderable = SystemFontCatalog.Families
                .Where(option => !SystemFontCatalog.CanRender(option.FamilyName))
                .Select(option => option.FamilyName)
                .ToList();

            Assert.AreEqual(
                0,
                unrenderable.Count,
                "Unrenderable: " + string.Join(", ", unrenderable));
        }

        [TestMethod]
        public void Families_ExcludesGdiTruncatedNames()
        {
            Assert.IsFalse(
                SystemFontCatalog.Families.Any(
                    option => option.FamilyName.Length == GdiTruncatedNameLength));
        }

        [TestMethod]
        public void Families_ExcludesNamesThatWouldChangeMeaningInAFontFamilyString()
        {
            // A comma separates fallback entries and '#' introduces a packed font name.
            Assert.IsFalse(SystemFontCatalog.Families.Any(
                option => option.FamilyName.IndexOf(',') >= 0
                          || option.FamilyName.IndexOf('#') >= 0));
        }

        [TestMethod]
        public void Families_HasNoDuplicateNames()
        {
            var duplicates = SystemFontCatalog.Families
                .GroupBy(option => option.FamilyName, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();

            Assert.AreEqual(0, duplicates.Count, "Duplicated: " + string.Join(", ", duplicates));
        }

        [TestMethod]
        public void Families_IsSortedForDisplay()
        {
            var names = SystemFontCatalog.Families.Select(option => option.FamilyName).ToList();
            var sorted = names.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToList();

            CollectionAssert.AreEqual(sorted, names);
        }

        [TestMethod]
        public void Families_EveryOptionIsBindableAsAPickerRow()
        {
            foreach (var option in SystemFontCatalog.Families)
            {
                // DisplayName doubles as the stored value, and the row renders itself in
                // PreviewFamily, so neither may be null.
                Assert.AreEqual(option.FamilyName, option.DisplayName);
                Assert.IsNotNull(option.PreviewFamily);
            }
        }

        [TestMethod]
        public void Families_IsCached()
        {
            Assert.AreSame(SystemFontCatalog.Families, SystemFontCatalog.Families);
        }

        [TestMethod]
        public void Prewarm_BuildsTheSameCachedList()
        {
            SystemFontCatalog.Prewarm();

            Assert.AreSame(SystemFontCatalog.Families, SystemFontCatalog.Families);
        }

        [TestMethod]
        public void CanRender_RejectsUnknownAndEmptyNames()
        {
            Assert.IsFalse(SystemFontCatalog.CanRender("ThisFontIsNotInstalledAnywhere"));
            Assert.IsFalse(SystemFontCatalog.CanRender(null));
            Assert.IsFalse(SystemFontCatalog.CanRender(string.Empty));
            Assert.IsFalse(SystemFontCatalog.CanRender("   "));
        }

        [TestMethod]
        public void Families_IncludesEveryRenderableWpfBaseFamily()
        {
            var catalog = new HashSet<string>(
                SystemFontCatalog.Families.Select(option => option.FamilyName),
                StringComparer.OrdinalIgnoreCase);

            var missing = Fonts.SystemFontFamilies
                .Select(family => family.Source)
                .Where(name => !string.IsNullOrWhiteSpace(name)
                               && name.Length != GdiTruncatedNameLength
                               && name.IndexOf(',') < 0
                               && name.IndexOf('#') < 0
                               && SystemFontCatalog.CanRender(name))
                .Where(name => !catalog.Contains(name))
                .ToList();

            Assert.AreEqual(0, missing.Count, "Missing: " + string.Join(", ", missing));
        }

        [TestMethod]
        public void Families_AddsVariantsThatWpfDoesNotEnumerateAsFamilies()
        {
            // The reason the catalog exists: Fonts.SystemFontFamilies omits faces such as
            // "Segoe UI Semilight", which GDI+ reports as families of their own.
            var wpfFamilies = new HashSet<string>(
                Fonts.SystemFontFamilies.Select(family => family.Source),
                StringComparer.OrdinalIgnoreCase);

            var expectedVariants = GetGdiFamilyNames()
                .Where(name => !wpfFamilies.Contains(name)
                               && name.Length != GdiTruncatedNameLength
                               && name.IndexOf(',') < 0
                               && name.IndexOf('#') < 0
                               && SystemFontCatalog.CanRender(name))
                .ToList();

            if (expectedVariants.Count == 0)
            {
                Assert.Inconclusive("No renderable variant-only font families are installed.");
                return;
            }

            var catalog = new HashSet<string>(
                SystemFontCatalog.Families.Select(option => option.FamilyName),
                StringComparer.OrdinalIgnoreCase);

            var missing = expectedVariants.Where(name => !catalog.Contains(name)).ToList();

            Assert.AreEqual(0, missing.Count, "Missing variants: " + string.Join(", ", missing));
        }

        [TestMethod]
        public void CreateUnlistedOption_KeepsAnUninstalledFontSelectable()
        {
            // Feeds the picker a row for a stored font the catalog has no entry for, so a Selector
            // does not report the value back as null and erase it.
            var option = SystemFontCatalog.CreateUnlistedOption("ThisFontIsNotInstalledAnywhere");

            Assert.AreEqual("ThisFontIsNotInstalledAnywhere", option.FamilyName);
            Assert.AreEqual("ThisFontIsNotInstalledAnywhere", option.DisplayName);
            Assert.IsNotNull(option.PreviewFamily);
        }

        private static IEnumerable<string> GetGdiFamilyNames()
        {
            using (var installed = new System.Drawing.Text.InstalledFontCollection())
            {
                return installed.Families
                    .Select(family => family.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList();
            }
        }
    }
}
