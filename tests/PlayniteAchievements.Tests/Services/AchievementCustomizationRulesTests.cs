using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.ViewModels.ManageAchievements;
using System;

namespace PlayniteAchievements.Services.Tests
{
    /// <summary>
    /// Covers which facets of an achievement count as user customization. The editor marks a row
    /// and filters the grid on this answer, so a facet silently reading as customized would mark
    /// the whole library, and one reading as untouched would hide an edit the user made.
    /// </summary>
    [TestClass]
    public class AchievementCustomizationRulesTests
    {
        /// <summary>A provider achievement showing exactly what the provider supplied.</summary>
        private static AchievementCustomizationInputs Untouched()
        {
            return new AchievementCustomizationInputs
            {
                IsAuthored = false,
                DisplayName = "First Blood",
                ProviderDisplayName = "First Blood",
                Description = "Win a match.",
                ProviderDescription = "Win a match.",
                Points = 10,
                ProviderPoints = 10,
                TrophyType = "bronze",
                ProviderTrophyType = "bronze",
                UnlockTimeUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                ProviderUnlockTimeUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                Category = "Base",
                ProviderCategory = "Base",
                CategoryType = "Base",
                ProviderCategoryType = "Base",
                Note = null,
                UnlockedIconPath = "icon_cache\\first.png",
                ProviderUnlockedIconPath = "icon_cache\\first.png",
                LockedIconPath = null,
                ProviderLockedIconPath = null,
                Hidden = false,
                ProviderHidden = false,
                IsFiltered = false,
                IsSummaryFiltered = false,
                IsGoal = false,
                IsCapstone = false,
                ProviderIsCapstone = false
            };
        }

        [TestMethod]
        public void UntouchedProviderAchievement_CarriesNoFacets()
        {
            Assert.AreEqual(
                AchievementCustomizationFacet.None,
                AchievementCustomizationRules.Resolve(Untouched()));
        }

        [TestMethod]
        public void NullInputs_CarryNoFacets()
        {
            Assert.AreEqual(
                AchievementCustomizationFacet.None,
                AchievementCustomizationRules.Resolve(null));
        }

        [TestMethod]
        public void AuthoredAchievement_IsOneFacetRatherThanEveryField()
        {
            // It has no provider values behind it, so comparing field by field would read all of
            // them as overrides. Reverting it deletes it, which is why it is marked apart.
            var inputs = Untouched();
            inputs.IsAuthored = true;
            inputs.ProviderDisplayName = null;
            inputs.ProviderDescription = null;
            inputs.ProviderPoints = null;
            inputs.ProviderTrophyType = null;
            inputs.ProviderUnlockTimeUtc = null;
            inputs.Category = null;
            inputs.ProviderCategory = null;
            inputs.CategoryType = null;
            inputs.ProviderCategoryType = null;
            inputs.ProviderUnlockedIconPath = null;

            Assert.AreEqual(
                AchievementCustomizationFacet.Authored,
                AchievementCustomizationRules.Resolve(inputs));
        }

        [TestMethod]
        public void RenamedAchievement_ReportsTheNameAlone()
        {
            var inputs = Untouched();
            inputs.DisplayName = "First Kill";

            Assert.AreEqual(
                AchievementCustomizationFacet.DisplayName,
                AchievementCustomizationRules.Resolve(inputs));
        }

        [TestMethod]
        public void RecasedName_CountsAsRenamed()
        {
            // Case is the whole edit when a user fixes capitalization, so the name comparison is
            // ordinal rather than case-insensitive.
            var inputs = Untouched();
            inputs.DisplayName = "FIRST BLOOD";

            Assert.AreEqual(
                AchievementCustomizationFacet.DisplayName,
                AchievementCustomizationRules.Resolve(inputs));
        }

        [TestMethod]
        public void RecasedTrophyType_IsNotAnEdit()
        {
            // Unlike the name, the trophy type is a token rather than text the user reads.
            var inputs = Untouched();
            inputs.TrophyType = "Bronze";

            Assert.AreEqual(
                AchievementCustomizationFacet.None,
                AchievementCustomizationRules.Resolve(inputs));
        }

        [TestMethod]
        public void ClearedUnlockTime_ReportsTheTimestamp()
        {
            // Clearing it is itself a stored state, not a fallback to the provider's.
            var inputs = Untouched();
            inputs.UnlockTimeUtc = null;

            Assert.AreEqual(
                AchievementCustomizationFacet.UnlockTime,
                AchievementCustomizationRules.Resolve(inputs));
        }

        [TestMethod]
        public void ClearedPoints_ReportTheScore()
        {
            var inputs = Untouched();
            inputs.Points = null;

            Assert.AreEqual(
                AchievementCustomizationFacet.Points,
                AchievementCustomizationRules.Resolve(inputs));
        }

        [TestMethod]
        public void AnyNote_IsCustomization()
        {
            // No provider supplies a note, so carrying one is the customization.
            var inputs = Untouched();
            inputs.Note = "Grind the tutorial.";

            Assert.AreEqual(
                AchievementCustomizationFacet.Note,
                AchievementCustomizationRules.Resolve(inputs));
        }

        [TestMethod]
        public void BlankNote_IsNotCustomization()
        {
            var inputs = Untouched();
            inputs.Note = "   ";

            Assert.AreEqual(
                AchievementCustomizationFacet.None,
                AchievementCustomizationRules.Resolve(inputs));
        }

        [TestMethod]
        public void IconMatchingTheProviders_IsNotAnOverride()
        {
            // Matched to how the icon overrides are written: only a differing non-blank path is
            // stored, so a row showing the provider's art carries nothing.
            var inputs = Untouched();
            inputs.UnlockedIconPath = "ICON_CACHE\\FIRST.PNG";

            Assert.AreEqual(
                AchievementCustomizationFacet.None,
                AchievementCustomizationRules.Resolve(inputs));
        }

        [TestMethod]
        public void MissingIconWhereTheProviderHasOne_IsNotAnOverride()
        {
            // A blank path is the absence of art, not a deliberate replacement of it.
            var inputs = Untouched();
            inputs.UnlockedIconPath = null;

            Assert.AreEqual(
                AchievementCustomizationFacet.None,
                AchievementCustomizationRules.Resolve(inputs));
        }

        [TestMethod]
        public void ReplacedIcons_ReportBothSlots()
        {
            var inputs = Untouched();
            inputs.UnlockedIconPath = "icon_cache\\mine.png";
            inputs.LockedIconPath = "icon_cache\\mine_locked.png";

            Assert.AreEqual(
                AchievementCustomizationFacet.UnlockedIcon | AchievementCustomizationFacet.LockedIcon,
                AchievementCustomizationRules.Resolve(inputs));
        }

        [TestMethod]
        public void HidingAnAchievementTheProviderShows_ReportsTheFlag()
        {
            var inputs = Untouched();
            inputs.Hidden = true;

            Assert.AreEqual(
                AchievementCustomizationFacet.Hidden,
                AchievementCustomizationRules.Resolve(inputs));
        }

        [TestMethod]
        public void RevealingAnAchievementTheProviderHides_ReportsTheFlag()
        {
            // Either direction is an override: hiding is a presentation choice, not a fact.
            var inputs = Untouched();
            inputs.Hidden = false;
            inputs.ProviderHidden = true;

            Assert.AreEqual(
                AchievementCustomizationFacet.Hidden,
                AchievementCustomizationRules.Resolve(inputs));
        }

        [TestMethod]
        public void EitherFilterList_ReportsOneFacet()
        {
            // The two lists are one choice to the user: this achievement is excluded.
            var filtered = Untouched();
            filtered.IsFiltered = true;

            var summaryFiltered = Untouched();
            summaryFiltered.IsSummaryFiltered = true;

            Assert.AreEqual(
                AchievementCustomizationFacet.FilterScope,
                AchievementCustomizationRules.Resolve(filtered));
            Assert.AreEqual(
                AchievementCustomizationFacet.FilterScope,
                AchievementCustomizationRules.Resolve(summaryFiltered));
        }

        [TestMethod]
        public void Goal_IsCustomizationOnItsOwn()
        {
            var inputs = Untouched();
            inputs.IsGoal = true;

            Assert.AreEqual(
                AchievementCustomizationFacet.Goal,
                AchievementCustomizationRules.Resolve(inputs));
        }

        [TestMethod]
        public void ProviderCapstone_IsNotCustomization()
        {
            // A game can arrive with its own capstone; only disagreeing with it is an edit.
            var inputs = Untouched();
            inputs.IsCapstone = true;
            inputs.ProviderIsCapstone = true;

            Assert.AreEqual(
                AchievementCustomizationFacet.None,
                AchievementCustomizationRules.Resolve(inputs));
        }

        [TestMethod]
        public void CapstoneTheUserSet_ReportsTheFlag()
        {
            var inputs = Untouched();
            inputs.IsCapstone = true;

            Assert.AreEqual(
                AchievementCustomizationFacet.Capstone,
                AchievementCustomizationRules.Resolve(inputs));
        }

        [TestMethod]
        public void Recategorized_ReportsBothCategoryAndType()
        {
            var inputs = Untouched();
            inputs.Category = "Endgame";
            inputs.CategoryType = "DLC";

            Assert.AreEqual(
                AchievementCustomizationFacet.Category | AchievementCustomizationFacet.CategoryType,
                AchievementCustomizationRules.Resolve(inputs));
        }

        [TestMethod]
        public void CategoryAssignedWhereTheProviderNamedNone_IsAnOverride()
        {
            // The provider named nothing, so the effective category falls back to the default
            // bucket; the user filing it elsewhere is exactly the case a marker exists for.
            var inputs = Untouched();
            inputs.ProviderCategory = "Default";
            inputs.Category = "Endgame";
            inputs.ProviderCategoryType = "Default";
            inputs.CategoryType = "DLC";

            Assert.AreEqual(
                AchievementCustomizationFacet.Category | AchievementCustomizationFacet.CategoryType,
                AchievementCustomizationRules.Resolve(inputs));
        }

        [TestMethod]
        public void UncategorizedOnBothSides_IsNotAnOverride()
        {
            // Neither side names one, so both resolve to the default bucket and agree.
            var inputs = Untouched();
            inputs.Category = "Default";
            inputs.ProviderCategory = "Default";
            inputs.CategoryType = "Default";
            inputs.ProviderCategoryType = "Default";

            Assert.AreEqual(
                AchievementCustomizationFacet.None,
                AchievementCustomizationRules.Resolve(inputs));
        }

        [TestMethod]
        public void DisagreeingWithACategoryTheProviderNamed_IsStillAnOverride()
        {
            var inputs = Untouched();
            inputs.Category = "Endgame";

            Assert.AreEqual(
                AchievementCustomizationFacet.Category,
                AchievementCustomizationRules.Resolve(inputs));
        }

        [TestMethod]
        public void SeveralEdits_AccumulateIntoOneSet()
        {
            var inputs = Untouched();
            inputs.DisplayName = "First Kill";
            inputs.Description = "Win your first match.";
            inputs.Note = "Tutorial counts.";
            inputs.IsGoal = true;

            Assert.AreEqual(
                AchievementCustomizationFacet.DisplayName |
                AchievementCustomizationFacet.Description |
                AchievementCustomizationFacet.Note |
                AchievementCustomizationFacet.Goal,
                AchievementCustomizationRules.Resolve(inputs));
        }
    }
}
