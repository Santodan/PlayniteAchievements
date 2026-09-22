using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

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
                // A hidden achievement is also locked, so either column masks it. This matches
                // IsLockedIconHidden, which has always applied the locked icon setting to hidden
                // rows; the other four fields must not disagree with the icon beside them.
                "private bool TitleMaskApplies => !ShowLockedTitle || (Hidden && !ShowHiddenTitle);",
                "private bool DescriptionMaskApplies => !ShowLockedDescription || (Hidden && !ShowHiddenDescription);",
                // Trophy and points additionally require the row to carry the field, so a row with
                // nothing to hide is never made revealable by these toggles.
                "HasTrophyType && (!ShowLockedTrophy || (Hidden && !ShowHiddenTrophy));",
                "HasPoints && (!ShowLockedPoints || (Hidden && !ShowHiddenPoints));",
                "public bool IsTitleHidden => IsHidden && TitleMaskApplies;",
                "public bool IsDescriptionHidden => IsHidden && DescriptionMaskApplies;",
                "public bool IsTrophyHidden => IsHidden && TrophyMaskApplies;",
                "public bool IsPointsHidden => IsHidden && PointsMaskApplies;",
                "public string PointsTextResolved => IsPointsHidden ? MaskedValuePlaceholder : PointsText;",
                // The trophy resolves to a sentinel like the other fields resolve to placeholders,
                // so the cell template switches on one property instead of racing triggers.
                "public string TrophyTypeResolved => IsTrophyHidden ? MaskedTrophyType : TrophyType;",
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
        public void AppearanceSnapshot_IsBothAppliedAndCapturedFieldForField()
        {
            var code = File.ReadAllText(FindRepoFile("source", "ViewModels", "Items", "AchievementDisplayItem.cs"));

            // The snapshot is the only channel these settings travel on, so a field declared on it
            // and left out of either direction silently reverts to the item's default.
            var snapshot = Between(code, "public sealed class AppearanceSettingsSnapshot", "        private AchievementDetail _source;");
            var apply = Between(code, "public void ApplyAppearanceSettings(AppearanceSettingsSnapshot snapshot)", "public AppearanceSettingsSnapshot CaptureAppearanceSettings()");
            var capture = Between(code, "public AppearanceSettingsSnapshot CaptureAppearanceSettings()", "public void RefreshIconDisplay()");

            var fields = Regex.Matches(snapshot, @"public bool (\w+) \{ get; set; \}")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .ToList();

            Assert.IsTrue(fields.Count >= 14, $"Only found {fields.Count} snapshot fields; the parse is probably wrong.");

            var missing = new List<string>();
            foreach (var field in fields)
            {
                if (!apply.Contains("resolved." + field))
                {
                    missing.Add(field + " (not applied)");
                }

                if (!capture.Contains(field + " = " + field))
                {
                    missing.Add(field + " (not captured)");
                }
            }

            CollectionAssert.AreEqual(new List<string>(), missing);
        }

        [TestMethod]
        public void EverySiteThatCopiesAppearanceGoesThroughTheSnapshot()
        {
            // Hand-kept field lists are how the trophy and points masks were lost: the grids clone
            // every row before rendering, and Clone() copied five of the appearance fields by hand,
            // so the ones added later silently reverted to the item's defaults on screen. Each of
            // these sites must route through the snapshot so there is one list, not four.
            var copySites = new[]
            {
                new[] { "source", "ViewModels", "Items", "AchievementDisplayItem.cs" },
                new[] { "source", "ViewModels", "CapstoneViewModel.cs" },
                new[] { "source", "ViewModels", "ManageAchievements", "ManageAchievementsAchievementIconsViewModel.cs" }
            };

            var offenders = new List<string>();
            foreach (var parts in copySites)
            {
                var path = FindRepoFile(parts);
                var code = File.ReadAllText(path);
                var name = Path.GetFileName(path);

                Assert.IsTrue(
                    code.Contains("CaptureAppearanceSettings()"),
                    $"{name} copies appearance state but never uses the snapshot.");

                // A per-field assignment of any masking setting means the list came back.
                foreach (Match m in Regex.Matches(
                    code, @"\b(?:Show(?:Hidden|Locked)\w+|UseSeparateLockedIconsWhenAvailable|ShowRarityBar|ShowFriendSpoilers)\s*=\s*(?:projected|source|sourceItem|clone)\."))
                {
                    offenders.Add($"{name}: {m.Value.Trim()}");
                }
            }

            CollectionAssert.AreEqual(new List<string>(), offenders);
        }

        [TestMethod]
        public void EditorRevealHeaders_BindThroughTheProxyNotRelativeSource()
        {
            var xaml = File.ReadAllText(FindRepoFile(
                "source", "Views", "ManageAchievements", "ManageAchievementsEditorTab.xaml"));

            Assert.IsTrue(
                xaml.Contains("<helpers:BindingProxy x:Key=\"EditorTabContext\" Data=\"{Binding}\" />"),
                "The editor tab no longer declares the binding proxy its column headers use.");

            // A DataGridColumn is outside the visual tree, so a FindAncestor binding in its header
            // resolves only if that header is realized while the tree is complete. A column that
            // starts hidden is realized later and its Command binding lands on null, leaving a
            // reveal-all button that renders but does nothing.
            var stragglers = Regex.Matches(
                xaml,
                @"Binding DataContext\.(CanRevealAny\w+|AreAll\w+Revealed|ToggleAll\w+Command|CycleAllIconStagesCommand|IconColumnStage\w*), RelativeSource")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();

            CollectionAssert.AreEqual(new List<string>(), stragglers);

            // Every column that offers a reveal-all toggle must go through the proxy.
            foreach (var path in new[]
            {
                "CycleAllIconStagesCommand", "ToggleAllTitlesRevealCommand", "ToggleAllDescriptionsRevealCommand",
                "ToggleAllTrophiesRevealCommand", "ToggleAllPointsRevealCommand"
            })
            {
                Assert.IsTrue(
                    xaml.Contains("{Binding Data." + path + ", Source={StaticResource EditorTabContext}}"),
                    $"{path} is not bound through the editor tab's binding proxy.");
            }
        }

        [TestMethod]
        public void EditorRevealsATrophyOrPointValueTheUserJustEntered()
        {
            var vm = File.ReadAllText(FindRepoFile(
                "source", "ViewModels", "ManageAchievements", "ManageAchievementsEditorViewModel.cs"));

            // Masking a value the instant it is typed hides the user's own edit. The hook sits in
            // Row_PropertyChanged, which only sees attached rows, so a load does not trip it.
            var handler = Between(vm, "private void Row_PropertyChanged", "private void RefreshComputedState");
            AssertContainsAll(
                handler,
                "e.PropertyName == nameof(AchievementEditorRow.TrophyType) && valueEditedRow.HasTrophyType",
                "valueEditedRow.IsTrophyRevealed = true;",
                "e.PropertyName == nameof(AchievementEditorRow.PointsText) && valueEditedRow.HasPoints",
                "valueEditedRow.IsPointsRevealed = true;");
        }

        private static string Between(string content, string start, string end)
        {
            var from = content.IndexOf(start, StringComparison.Ordinal);
            Assert.IsTrue(from >= 0, $"Could not find '{start}'.");
            var to = content.IndexOf(end, from, StringComparison.Ordinal);
            Assert.IsTrue(to > from, $"Could not find '{end}' after '{start}'.");
            return content.Substring(from, to - from);
        }

        [TestMethod]
        public void EditorRows_MaskTrophyAndPointsOnTheSameGatesAsTheGrid()
        {
            var vm = File.ReadAllText(FindRepoFile(
                "source", "ViewModels", "ManageAchievements", "ManageAchievementsEditorViewModel.cs"));

            // The editor row derives its gates the same way the grid item does, so the Spoilers
            // page means the same thing in both places.
            AssertContainsAll(
                vm,
                "public bool CanRevealTitle => !Unlocked && (!ShowLockedTitle || (Hidden && !ShowHiddenTitle));",
                "public bool CanRevealTrophy =>",
                "!Unlocked && HasTrophyType && (!ShowLockedTrophy || (Hidden && !ShowHiddenTrophy));",
                "public bool CanRevealPoints =>",
                "!Unlocked && HasPoints && (!ShowLockedPoints || (Hidden && !ShowHiddenPoints));",
                "public bool IsTrophyHidden => CanRevealTrophy && !IsTrophyRevealed;",
                "public bool IsPointsHidden => CanRevealPoints && !IsPointsRevealed;");

            // Seeded from the persisted settings when the row is attached, or the row would keep
            // its own defaults and ignore the Spoilers page entirely.
            AssertContainsAll(
                vm,
                "row.ShowLockedTitle = _settings?.Persisted?.ShowLockedTitle ?? true;",
                "row.ShowLockedDescription = _settings?.Persisted?.ShowLockedDescription ?? true;",
                "row.ShowHiddenTrophy = _settings?.Persisted?.ShowHiddenTrophy ?? true;",
                "row.ShowLockedTrophy = _settings?.Persisted?.ShowLockedTrophy ?? true;",
                "row.ShowHiddenPoints = _settings?.Persisted?.ShowHiddenPoints ?? true;",
                "row.ShowLockedPoints = _settings?.Persisted?.ShowLockedPoints ?? true;");

            // Reveal state must never mark the editor dirty, so every new reveal property has to be
            // listed as reveal-only.
            var guard = Between(vm, "private static bool IsRevealStateProperty", "public RelayCommand AddCustomProviderCommand");
            AssertContainsAll(
                guard,
                "nameof(AchievementEditorRow.IsTrophyRevealed)",
                "nameof(AchievementEditorRow.IsPointsRevealed)",
                "nameof(AchievementEditorRow.IsTrophyHidden)",
                "nameof(AchievementEditorRow.IsPointsHidden)",
                "nameof(AchievementEditorRow.CanRevealTrophy)",
                "nameof(AchievementEditorRow.CanRevealPoints)");

            // The header summaries are computed in one pass; a column left out of it would have a
            // toggle that never updates.
            var pass = Between(vm, "private void RefreshRevealHeaderState", "private static bool IsRevealStateProperty");
            AssertContainsAll(
                pass,
                "_canRevealAnyTrophy = false;",
                "_canRevealAnyPoints = false;",
                "if (row.CanRevealTrophy)",
                "if (row.CanRevealPoints)",
                "OnPropertyChanged(nameof(CanRevealAnyTrophy));",
                "OnPropertyChanged(nameof(CanRevealAnyPoints));",
                "OnPropertyChanged(nameof(AreAllTrophiesRevealed));",
                "OnPropertyChanged(nameof(AreAllPointsRevealed));");
        }

        [TestMethod]
        public void EditorTrophyAndPointsColumns_OfferTheSameRevealAffordanceAsNameAndDescription()
        {
            var xaml = File.ReadAllText(FindRepoFile(
                "source", "Views", "ManageAchievements", "ManageAchievementsEditorTab.xaml"));
            var code = File.ReadAllText(FindRepoFile(
                "source", "Views", "ManageAchievements", "ManageAchievementsEditorTab.xaml.cs"));

            AssertContainsAll(
                xaml,
                // Reveal-all header toggles, reached through the proxy rather than FindAncestor
                // so they still work for a column that was hidden when the window opened.
                "Data.ToggleAllTrophiesRevealCommand",
                "Data.ToggleAllPointsRevealCommand",
                "Data.CanRevealAnyTrophy",
                "Data.CanRevealAnyPoints",
                // Per-row toggles and the masked overlays they clear.
                "Click=\"ToggleTrophyRevealButton_Click\"",
                "Click=\"TogglePointsRevealButton_Click\"",
                "PreviewMouseLeftButtonDown=\"MaskedTrophy_PreviewMouseLeftButtonDown\"",
                "PreviewMouseLeftButtonDown=\"MaskedPoints_PreviewMouseLeftButtonDown\"",
                // The real value must be hidden while masked, not merely overlaid.
                "Visibility=\"{Binding IsTrophyHidden, Converter={StaticResource InverseBoolToVis}}\"",
                "Visibility=\"{Binding IsPointsHidden, Converter={StaticResource InverseBoolToVis}}\"",
                // Converting Header to a template loses the column-picker caption unless it is set.
                "helpers:ColumnVisibilityHelper.ColumnDisplayName=\"{DynamicResource LOCPlayAch_Column_Trophy}\"",
                "helpers:ColumnVisibilityHelper.ColumnDisplayName=\"{DynamicResource LOCPlayAch_Column_Points}\"");

            AssertContainsAll(
                code,
                "row.ToggleTrophyReveal();",
                "row.TogglePointsReveal();",
                "row.RevealTrophy();",
                "row.RevealPoints();");
        }

        [TestMethod]
        public void TrophyColumn_DoesNotNameTheGradeWhileItIsMasked()
        {
            var xaml = File.ReadAllText(
                FindRepoFile("source", "Views", "Controls", "AchievementDataGridControl.xaml"));

            // Every grade tooltip must read the resolved grade, never the raw one: binding
            // TrophyType here would print "Platinum" over the masked placeholder.
            foreach (var grade in new[] { "platinum", "gold", "silver", "bronze" })
            {
                Assert.IsTrue(
                    xaml.Contains("<DataTrigger Binding=\"{Binding TrophyTypeResolved}\" Value=\"" + grade + "\">"),
                    $"Trophy tooltip for '{grade}' does not switch on TrophyTypeResolved.");
            }

            Assert.IsFalse(
                xaml.Contains("<Condition Binding=\"{Binding TrophyType}\""),
                "The trophy column still reads the raw grade somewhere.");

            Assert.IsTrue(
                xaml.Contains("<DataTrigger Binding=\"{Binding TrophyTypeResolved}\" Value=\"unknown\">"),
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
        public void DisplaySettings_RoundRarityPercentagesLivesUnderRarity()
        {
            var general = File.ReadAllText(FindRepoFile("source", "Views", "Settings", "Display", "DisplayGeneralSection.xaml"));
            var colors = File.ReadAllText(FindRepoFile("source", "Views", "Settings", "Display", "ColorsSection.xaml"));
            var previewProperties = File.ReadAllText(FindRepoFile("source", "Views", "Settings", "Display", "DisplayPreviewProperties.cs"));

            // The Rarity heading reuses the Rarity column string; the Formatting heading above it
            // is the nearest preceding section, so ordering pins which one the setting sits under.
            var formattingIndex = general.IndexOf("LOCPlayAch_Settings_Display_Formatting", StringComparison.Ordinal);
            var rarityHeadingIndex = general.IndexOf("LOCPlayAch_Column_Rarity", StringComparison.Ordinal);
            var roundRarityIndex = general.IndexOf("LOCPlayAch_Settings_RoundRarityPercentages", StringComparison.Ordinal);

            Assert.IsTrue(formattingIndex >= 0, "Formatting section missing.");
            Assert.IsTrue(rarityHeadingIndex > formattingIndex, "Rarity section must follow Formatting.");
            Assert.IsTrue(roundRarityIndex > rarityHeadingIndex, "Round rarity setting must live under Rarity.");
            Assert.IsFalse(colors.Contains("LOCPlayAch_Settings_RoundRarityPercentages"),
                "Round rarity setting should not live in Colors.");
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
