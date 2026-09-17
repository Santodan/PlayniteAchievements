using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Models.Tests
{
    /// <summary>
    /// The editor's column layout has to survive the two paths every persisted setting takes: the
    /// clone the settings window edits against, and the copy back when that window is accepted. A
    /// surface missing from either one reverts silently, with nothing to show the user why.
    /// </summary>
    [TestClass]
    public class ManageAchievementsGridOptionsTests
    {
        [TestMethod]
        public void Catalog_SeedsTheEditorSurface()
        {
            var catalog = new GridOptionsCatalog();

            var options = catalog.GetManageAchievements(GridOptionKeys.ManageAchievements.Editor);

            Assert.IsNotNull(options);
            Assert.IsNotNull(options.Columns);
            CollectionAssert.Contains(
                new List<string>(catalog.ManageAchievements.Keys),
                GridOptionKeys.ManageAchievements.Editor);
        }

        [TestMethod]
        public void Catalog_StartsWithNoSeededColumnState()
        {
            var columns = new GridOptionsCatalog()
                .GetManageAchievements(GridOptionKeys.ManageAchievements.Editor)
                .Columns;

            // Defaults live in the view and are merged on read. A pre-seeded record would put back
            // every column the user had hidden on the next load.
            Assert.AreEqual(0, columns.Visibility.Count);
            Assert.AreEqual(0, columns.Widths.Count);
            Assert.AreEqual(0, columns.Order.Count);
        }

        [TestMethod]
        public void CatalogClone_RoundTripsTheEditorColumnLayout()
        {
            var catalog = new GridOptionsCatalog();
            SeedEditorLayout(catalog);

            var clone = catalog.Clone();

            AssertEditorLayout(clone, "GridOptionsCatalog.Clone() dropped the editor column layout.");
        }

        [TestMethod]
        public void PersistedSettingsClone_RoundTripsTheEditorColumnLayout()
        {
            var settings = new PersistedSettings();
            SeedEditorLayout(settings.GridOptions);

            var clone = settings.Clone();

            AssertEditorLayout(clone.GridOptions, "PersistedSettings.Clone() dropped the editor column layout.");
        }

        [TestMethod]
        public void CopyFrom_PreservesTheEditorColumnLayout()
        {
            var source = new PersistedSettings();
            SeedEditorLayout(source.GridOptions);
            var target = new PersistedSettings();

            target.CopyFrom(source);

            AssertEditorLayout(
                target.GridOptions,
                "CopyFrom dropped the editor column layout, so accepting the settings window would revert it.");
        }

        [TestMethod]
        public void CatalogClone_DoesNotShareTheEditorColumnMaps()
        {
            var catalog = new GridOptionsCatalog();
            SeedEditorLayout(catalog);

            var clone = catalog.Clone();
            clone.GetManageAchievements(GridOptionKeys.ManageAchievements.Editor)
                .Columns.Visibility["EditorNote"] = true;

            var original = catalog.GetManageAchievements(GridOptionKeys.ManageAchievements.Editor);
            Assert.IsFalse(
                original.Columns.Visibility.ContainsKey("EditorNote"),
                "A clone must not write back into the instance it was cloned from.");
        }

        private static void SeedEditorLayout(GridOptionsCatalog catalog)
        {
            var columns = catalog
                .GetManageAchievements(GridOptionKeys.ManageAchievements.Editor)
                .Columns;

            columns.Visibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["EditorDescription"] = false,
                ["EditorName"] = true
            };
            columns.Widths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["EditorName"] = 275
            };
            columns.Order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["EditorName"] = 4
            };
        }

        private static void AssertEditorLayout(GridOptionsCatalog catalog, string message)
        {
            var columns = catalog
                .GetManageAchievements(GridOptionKeys.ManageAchievements.Editor)
                .Columns;

            Assert.IsFalse(columns.Visibility["EditorDescription"], message);
            Assert.IsTrue(columns.Visibility["EditorName"], message);
            Assert.AreEqual(275, columns.Widths["EditorName"], message);
            Assert.AreEqual(4, columns.Order["EditorName"], message);
        }
    }
}
