using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>
    /// Covers how auto capstone text templates resolve and render, and which stored capstone text
    /// the rewriter treats as a template's (rewritten) or as an edit (left alone).
    /// </summary>
    [TestClass]
    public class AutoCapstoneTextTests
    {
        private const string GermanCategoryDescription = "Schalte jeden Erfolg in {0} frei.";

        [TestMethod]
        public void Defaults_MatchTheTextCapstonesWereAuthoredWithBefore()
        {
            var (title, description) = AutoCapstoneText.Describe(AutoCapstoneText.Defaults(), "Halo", null);
            Assert.AreEqual("Halo", title);
            Assert.AreEqual("Obtain every base game achievement.", description);

            var (categoryTitle, categoryDescription) = AutoCapstoneText.Describe(AutoCapstoneText.Defaults(), "Halo", "ODST");
            Assert.AreEqual("Halo: ODST", categoryTitle);
            Assert.AreEqual("Obtain every achievement in ODST.", categoryDescription);
        }

        [TestMethod]
        public void ToCategoryTemplate_MovesTheCategoryToTheSecondPlaceholder()
        {
            Assert.AreEqual("Schalte jeden Erfolg in {1} frei.", AutoCapstoneText.ToCategoryTemplate(GermanCategoryDescription));
            Assert.IsNull(AutoCapstoneText.ToCategoryTemplate("Broken {0"));
            Assert.IsNull(AutoCapstoneText.ToCategoryTemplate(null));
        }

        [TestMethod]
        public void Resolve_UsesAValidStoredTemplate_AndFallsBackOnAnInvalidOne()
        {
            var settings = new PersistedSettings
            {
                AutoCapstoneGameNameTemplate = "{0} Mastery",
                AutoCapstoneCategoryNameTemplate = "{1 broken"
            };

            var resolved = AutoCapstoneText.Resolve(settings);

            Assert.AreEqual("{0} Mastery", resolved.GameName);
            Assert.AreEqual(AutoCapstoneText.DefaultCategoryNameTemplate, resolved.CategoryName);
        }

        [TestMethod]
        public void Render_ReturnsNullForTemplatesThatDoNotFormatOrRenderBlank()
        {
            Assert.IsNull(AutoCapstoneText.Render("{2}", "Halo", "ODST"));
            Assert.IsNull(AutoCapstoneText.Render("{1}", "Halo", null));
            Assert.AreEqual("{Halo}", AutoCapstoneText.Render("{{{0}}}", "Halo", null));
        }

        [TestMethod]
        public void Describe_FallsBackToTheDefault_WhenTheTemplateRendersBlank()
        {
            var templates = new AutoCapstoneTemplates("{1}", "", "{0}: {1}", "x");

            var (title, description) = AutoCapstoneText.Describe(templates, "Halo", null);

            Assert.AreEqual("Halo", title);
            Assert.AreEqual("Obtain every base game achievement.", description);
        }

        [TestMethod]
        public void NormalizeForStore_StoresNullForBlankOrDefault()
        {
            Assert.IsNull(AutoCapstoneText.NormalizeForStore("  ", "{0}"));
            Assert.IsNull(AutoCapstoneText.NormalizeForStore(" {0} ", "{0}"));
            Assert.AreEqual("{0} Mastery", AutoCapstoneText.NormalizeForStore(" {0} Mastery ", "{0}"));
        }

        [TestMethod]
        public void RecordInHistory_KeepsTheMostRecentLast_WithoutDuplicates()
        {
            var settings = new PersistedSettings();

            Assert.IsTrue(AutoCapstoneText.RecordInHistory(settings, "A"));
            Assert.IsTrue(AutoCapstoneText.RecordInHistory(settings, "B"));
            Assert.IsFalse(AutoCapstoneText.RecordInHistory(settings, "B"));
            Assert.IsTrue(AutoCapstoneText.RecordInHistory(settings, "A"));
            Assert.IsFalse(AutoCapstoneText.RecordInHistory(settings, null));

            CollectionAssert.AreEqual(new[] { "B", "A" }, settings.AutoCapstoneTemplateHistory);
        }

        [TestMethod]
        public void RecordInHistory_IsBounded()
        {
            var settings = new PersistedSettings();
            for (var i = 0; i < 100; i++)
            {
                AutoCapstoneText.RecordInHistory(settings, "T" + i);
            }

            Assert.AreEqual(32, settings.AutoCapstoneTemplateHistory.Count);
            Assert.AreEqual("T99", settings.AutoCapstoneTemplateHistory.Last());
        }

        [TestMethod]
        public void Rewrite_UpdatesDefaultTextFromAnotherLanguage()
        {
            var definition = AutoCapstone("Halo: ODST", "Schalte jeden Erfolg in ODST frei.", wholeGame: false);
            var current = new AutoCapstoneTemplates("{0}", "Beat {0}.", "{1} ({0})", "Beat everything in {1}.");

            var changed = AutoCapstoneTextRewriter.Rewrite(definition, "ODST", Names("Halo"), current, Known(current));

            Assert.IsTrue(changed);
            Assert.AreEqual("ODST (Halo)", definition.DisplayName);
            Assert.AreEqual("Beat everything in ODST.", definition.Description);
        }

        [TestMethod]
        public void Rewrite_LeavesAnEditedTitle_AndStillUpdatesADefaultDescription()
        {
            var definition = AutoCapstone("Finish the fight", "Obtain every base game achievement.", wholeGame: true);
            var current = new AutoCapstoneTemplates("{0} Mastery", "Beat {0}.", "{0}: {1}", "x {1}");

            var changed = AutoCapstoneTextRewriter.Rewrite(definition, null, Names("Halo"), current, Known(current));

            Assert.IsTrue(changed);
            Assert.AreEqual("Finish the fight", definition.DisplayName);
            Assert.AreEqual("Beat Halo.", definition.Description);
        }

        [TestMethod]
        public void Rewrite_TreatsTextNamingAnOldGameNameAsEdited()
        {
            var definition = AutoCapstone("Halo CE", "Obtain every base game achievement.", wholeGame: true);
            var current = new AutoCapstoneTemplates("{0} Mastery", "Obtain every base game achievement.", "{0}: {1}", "x {1}");

            var changed = AutoCapstoneTextRewriter.Rewrite(
                definition, null, Names("Halo: Combat Evolved"), current, Known(current));

            Assert.IsFalse(changed);
            Assert.AreEqual("Halo CE", definition.DisplayName);
        }

        [TestMethod]
        public void Rewrite_ReadsALegacyWholeGameCapstoneFiledInACategory()
        {
            // Authored before the scope was stored: filed with the main game's category, and not
            // marked as standing for the whole game.
            var definition = AutoCapstone("Halo", "Obtain every base game achievement.", wholeGame: false);
            var current = new AutoCapstoneTemplates("{0} Mastery", "Beat {0}.", "{0}: {1}", "x {1}");

            AutoCapstoneTextRewriter.Rewrite(definition, "Base Game", Names("Halo"), current, Known(current));

            Assert.AreEqual("Halo Mastery", definition.DisplayName);
            Assert.AreEqual("Beat Halo.", definition.Description);
        }

        [TestMethod]
        public void Rewrite_RecognizesTextFromATemplateInTheHistory()
        {
            var definition = AutoCapstone("Halo Mastery", "Obtain every base game achievement.", wholeGame: true);
            var current = new AutoCapstoneTemplates("All of {0}", "Obtain every base game achievement.", "{0}: {1}", "x {1}");
            var known = new AutoCapstoneKnownTemplates(_ => Enumerable.Empty<string>(), new[] { "{0} Mastery" }, current);

            Assert.IsTrue(AutoCapstoneTextRewriter.Rewrite(definition, null, Names("Halo"), current, known));
            Assert.AreEqual("All of Halo", definition.DisplayName);

            var forgotten = AutoCapstone("Halo Mastery", "Obtain every base game achievement.", wholeGame: true);
            Assert.IsFalse(AutoCapstoneTextRewriter.Rewrite(forgotten, null, Names("Halo"), current, Known(current)));
        }

        [TestMethod]
        public void Rewrite_IgnoresAnAuthoredAchievementThatIsNotAnAutoCapstone()
        {
            var definition = AutoCapstone("Halo", "Obtain every base game achievement.", wholeGame: true);
            definition.IsAutoCapstone = false;
            var current = new AutoCapstoneTemplates("{0} Mastery", "Beat {0}.", "{0}: {1}", "x {1}");

            Assert.IsFalse(AutoCapstoneTextRewriter.Rewrite(definition, null, Names("Halo"), current, Known(current)));
            Assert.AreEqual("Halo", definition.DisplayName);
        }

        private static CustomAchievementDefinition AutoCapstone(string title, string description, bool wholeGame)
        {
            return new CustomAchievementDefinition
            {
                Id = "auto-capstone",
                DisplayName = title,
                Description = description,
                IsAutoCapstone = true,
                IsWholeGameAutoCapstone = wholeGame
            };
        }

        private static IReadOnlyList<string> Names(string gameName) => new[] { gameName, "Auto Capstone" };

        /// <summary>The shipped defaults as the catalog would list them for English and German.</summary>
        private static AutoCapstoneKnownTemplates Known(AutoCapstoneTemplates current)
        {
            return new AutoCapstoneKnownTemplates(
                field => field == AutoCapstoneTextField.CategoryDescription
                    ? new[] { AutoCapstoneText.ToCategoryTemplate(GermanCategoryDescription) }
                    : Enumerable.Empty<string>(),
                null,
                current);
        }
    }
}
