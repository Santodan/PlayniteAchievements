using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class AchievementSpoilerVisibilityDefinitionTests
    {
        [TestMethod]
        public void DisplayItem_GatesVisibilityOnUnlockedForVisibility()
        {
            var code = File.ReadAllText(FindRepoFile("source", "ViewModels", "Items", "AchievementDisplayItem.cs"));

            AssertContainsAll(
                code,
                "public virtual bool UnlockedForVisibility => Unlocked;",
                "public bool CanReveal => !UnlockedForVisibility && (!ShowLockedIcon",
                "public bool IsLockedIconHidden => !UnlockedForVisibility && !ShowLockedIcon && !IsRevealed;",
                // DisplayIcon reuses IsIconHidden and IsLockedIconHidden to pick the masked
                // placeholder, so the spoiler gate above is the single definition to guard.
                "if (IsLockedIconHidden)",
                "ShowFriendSpoilers = persisted?.ShowFriendSpoilers ?? false");
        }

        [TestMethod]
        public void DisplayItem_MasksEveryFieldOnItsOwnHiddenOrLockedToggle()
        {
            var code = File.ReadAllText(FindRepoFile("source", "ViewModels", "Items", "AchievementDisplayItem.cs"));

            AssertContainsAll(
                code,
                // A hidden achievement is also locked, so each field picks its toggle off Hidden.
                "private bool TitleMaskApplies => Hidden ? !ShowHiddenTitle : !ShowLockedTitle;",
                "private bool DescriptionMaskApplies => Hidden ? !ShowHiddenDescription : !ShowLockedDescription;",
                // Trophy and points additionally require the row to carry the field, so a row with
                // nothing to hide is never made revealable by these toggles.
                "private bool TrophyMaskApplies => HasTrophyType && (Hidden ? !ShowHiddenTrophy : !ShowLockedTrophy);",
                "private bool PointsMaskApplies => HasPoints && (Hidden ? !ShowHiddenPoints : !ShowLockedPoints);",
                "public bool IsTitleHidden => IsHidden && TitleMaskApplies;",
                "public bool IsDescriptionHidden => IsHidden && DescriptionMaskApplies;",
                "public bool IsTrophyHidden => IsHidden && TrophyMaskApplies;",
                "public bool IsPointsHidden => IsHidden && PointsMaskApplies;",
                "public string PointsTextResolved => IsPointsHidden ? MaskedValuePlaceholder : PointsText;",
                // Every new toggle defaults to revealing, so an existing profile masks nothing new.
                "ShowLockedTitle = persisted?.ShowLockedTitle ?? true",
                "ShowLockedDescription = persisted?.ShowLockedDescription ?? true",
                "ShowHiddenTrophy = persisted?.ShowHiddenTrophy ?? true",
                "ShowHiddenPoints = persisted?.ShowHiddenPoints ?? true",
                "ShowLockedTrophy = persisted?.ShowLockedTrophy ?? true",
                "ShowLockedPoints = persisted?.ShowLockedPoints ?? true");

            // The gate has to name every toggle, or a row masked only on trophy or points would
            // render a placeholder that no click can clear.
            AssertContainsAll(
                code,
                "|| TitleMaskApplies",
                "|| DescriptionMaskApplies",
                "|| TrophyMaskApplies",
                "|| PointsMaskApplies");
        }

        [TestMethod]
        public void TrophyColumn_DoesNotNameTheGradeWhileItIsMasked()
        {
            var xaml = File.ReadAllText(
                FindRepoFile("source", "Views", "Controls", "AchievementDataGridControl.xaml"));

            // Each grade tooltip must be conditioned on the mask being off. A bare DataTrigger on
            // TrophyType would print "Platinum" over the placeholder.
            foreach (var grade in new[] { "platinum", "gold", "silver", "bronze" })
            {
                var trigger =
                    "<Condition Binding=\"{Binding TrophyType}\" Value=\"" + grade + "\"/>" +
                    Environment.NewLine +
                    "                                    <Condition Binding=\"{Binding IsTrophyHidden}\" Value=\"False\"/>";
                Assert.IsTrue(
                    xaml.Contains(trigger),
                    $"Trophy tooltip for '{grade}' is not gated on IsTrophyHidden.");
            }

            Assert.IsTrue(
                xaml.Contains("<DataTrigger Binding=\"{Binding IsTrophyHidden}\" Value=\"True\">"),
                "Masked trophy cell has no click-to-reveal tooltip.");
        }

        [TestMethod]
        public void FriendDisplayItem_UsesOwnUnlockStateWhenHidingSpoilers()
        {
            var code = File.ReadAllText(FindRepoFile("source", "ViewModels", "Items", "FriendAchievementDisplayItem.cs"));

            AssertContainsAll(
                code,
                "public override bool UnlockedForVisibility =>",
                "ShowFriendSpoilers ? base.UnlockedForVisibility : UnlockedBySelf;");
        }

        [TestMethod]
        public void SpoilersSettings_HoldTheWholeRevealMatrixAndTheSpoilerToggle()
        {
            var xaml = File.ReadAllText(FindRepoFile("source", "Views", "Settings", "Display", "SpoilersSection.xaml"));
            var general = File.ReadAllText(FindRepoFile("source", "Views", "Settings", "Display", "DisplayGeneralSection.xaml"));

            // Both halves of every field's pair, so a setting cannot be added to the model and left
            // unreachable in the UI.
            AssertContainsAll(
                xaml,
                "IsChecked=\"{Binding Persisted.ShowHiddenIcon}\"",
                "IsChecked=\"{Binding Persisted.ShowLockedIcon}\"",
                "IsChecked=\"{Binding Persisted.ShowHiddenTitle}\"",
                "IsChecked=\"{Binding Persisted.ShowLockedTitle}\"",
                "IsChecked=\"{Binding Persisted.ShowHiddenDescription}\"",
                "IsChecked=\"{Binding Persisted.ShowLockedDescription}\"",
                "IsChecked=\"{Binding Persisted.ShowHiddenTrophy}\"",
                "IsChecked=\"{Binding Persisted.ShowLockedTrophy}\"",
                "IsChecked=\"{Binding Persisted.ShowHiddenPoints}\"",
                "IsChecked=\"{Binding Persisted.ShowLockedPoints}\"",
                "IsChecked=\"{Binding Persisted.ShowHiddenSuffix}\"",
                "IsChecked=\"{Binding Persisted.ShowFriendSpoilers}\"",
                "Visibility=\"{Binding Persisted.EnableFriendsFeatures, Converter={StaticResource BoolToVis}}\"");

            // The cover images only take effect while a row is masked, so they belong here.
            AssertContainsAll(
                xaml,
                "Persisted.LockedFallbackIconPath",
                "Persisted.HiddenFallbackIconPath");

            // And none of it may be left behind on the General page.
            foreach (var moved in new[]
            {
                "Persisted.ShowHiddenIcon",
                "Persisted.ShowLockedIcon",
                "Persisted.ShowFriendSpoilers",
                "Persisted.LockedFallbackIconPath"
            })
            {
                Assert.IsFalse(general.Contains(moved), $"'{moved}' should have moved to the Spoilers page.");
            }

            // The Advanced expander and its reset action stay on the General page.
            Assert.IsTrue(
                general.IndexOf("LOCPlayAch_Settings_Advanced", StringComparison.Ordinal) >= 0,
                "Advanced expander missing from the General page.");
        }

        [TestMethod]
        public void SpoilersSettings_AreReachableFromTheDisplayNavigation()
        {
            var tab = File.ReadAllText(
                FindRepoFile("source", "Views", "Settings", "Display", "DisplaySettingsTab.xaml.cs"));

            AssertContainsAll(
                tab,
                "\"Spoilers\"",
                "LOCPlayAch_Settings_Spoilers",
                "new SpoilersSection(settings, plugin, logger)",
                // The page subscribes to persisted settings, so it has to be disposed with the tab.
                "_spoilersSection?.Dispose()",
                // Reset-to-defaults lives on the General page, so it has to refresh this one.
                "_spoilersSection?.RefreshVisibilityPreview()");
        }

        [TestMethod]
        public void DisplaySettings_RoundRarityPercentagesLivesInGridDefaults()
        {
            var general = File.ReadAllText(FindRepoFile("source", "Views", "Settings", "Display", "DisplayGeneralSection.xaml"));
            var appearance = File.ReadAllText(FindRepoFile("source", "Views", "Settings", "Display", "AppearanceSection.xaml"));
            var previewProperties = File.ReadAllText(FindRepoFile("source", "Views", "Settings", "Display", "DisplayPreviewProperties.cs"));

            var gridDefaultsIndex = general.IndexOf("LOCPlayAch_Settings_Display_GridDefaults", StringComparison.Ordinal);
            var roundRarityIndex = general.IndexOf("LOCPlayAch_Settings_RoundRarityPercentages", StringComparison.Ordinal);

            Assert.IsTrue(gridDefaultsIndex >= 0, "Grid Defaults section missing.");
            Assert.IsTrue(roundRarityIndex > gridDefaultsIndex, "Round rarity setting must live under Grid Defaults.");
            Assert.IsFalse(appearance.Contains("LOCPlayAch_Settings_RoundRarityPercentages"),
                "Round rarity setting should not live in Appearance.");
            AssertContainsAll(
                general,
                "IsChecked=\"{Binding Persisted.RoundRarityPercentages}\"",
                "LOCPlayAch_Settings_RoundRarityPercentages_Help");
            AssertContainsAll(
                previewProperties,
                "nameof(PersistedSettings.RoundRarityPercentages)");
        }

        private static void AssertContainsAll(string content, params string[] expected)
        {
            var missing = expected
                .Where(value => !content.Contains(value))
                .ToList();

            CollectionAssert.AreEqual(new List<string>(), missing);
        }

        private static string FindRepoFile(params string[] parts)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                if (File.Exists(path))
                {
                    return path;
                }

                directory = directory.Parent;
            }

            Assert.Fail("Could not find " + Path.Combine(parts));
            return null;
        }
    }
}
