using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    public class ShowcaseWpfContractTests
    {
        [TestMethod]
        public void Overview_RegistersAlwaysAvailableThirdTabAndGatesHeaderScoresPerCard()
        {
            var xaml = ReadRepoFile("source", "Views", "OverviewControl.xaml");
            var code = ReadRepoFile("source", "Views", "OverviewControl.xaml.cs");

            StringAssert.Contains(xaml, "x:Name=\"ShowcaseSubViewButton\"");
            StringAssert.Contains(xaml, "rootModels:OverviewSubView.Showcase");
            StringAssert.Contains(xaml, "x:Name=\"ShowcaseContentHost\"");
            StringAssert.Contains(
                xaml,
                "Visibility=\"{Binding EnableFriendsFeatures, Converter={StaticResource BoolToVis}}\"");
            StringAssert.Contains(
                xaml,
                "Visibility=\"{Binding ShowOverviewCollectionScoreCard, Converter={StaticResource BoolToVis}}\"");
            StringAssert.Contains(
                xaml,
                "Visibility=\"{Binding ShowOverviewPrestigeScoreCard, Converter={StaticResource BoolToVis}}\"");
            StringAssert.Contains(
                code,
                "_lastSelectedSubView == OverviewSubView.Friends");
            StringAssert.Contains(
                code,
                "ActiveSubView == OverviewSubView.Showcase");
        }

        [TestMethod]
        public void Overview_ShowcaseDatabaseEventsMarshalToTheUiDispatcher()
        {
            var code = ReadRepoFile("source", "Views", "OverviewControl.xaml.cs");
            var methodStart = code.IndexOf(
                "private void QueueShowcaseDatabaseRefresh()",
                StringComparison.Ordinal);
            var methodEnd = code.IndexOf(
                "private void ShowcaseDatabaseRefreshTimer_Tick",
                methodStart,
                StringComparison.Ordinal);
            Assert.IsTrue(methodStart >= 0 && methodEnd > methodStart);

            var method = code.Substring(methodStart, methodEnd - methodStart);
            var marshal = method.IndexOf("if (!Dispatcher.CheckAccess())", StringComparison.Ordinal);
            var activeSubViewRead = method.IndexOf(
                "ActiveSubView != OverviewSubView.Showcase",
                StringComparison.Ordinal);

            Assert.IsTrue(marshal >= 0, "Database event handling must check the UI dispatcher.");
            StringAssert.Contains(method, "new Action(QueueShowcaseDatabaseRefresh)");
            Assert.IsTrue(
                activeSubViewRead > marshal,
                "Dependency properties must not be read before dispatching to the UI thread.");
        }

        [TestMethod]
        public void Showcase_ProvidesPageControlsSelectedBlockEditingAndDynamicThemeResources()
        {
            var xaml = ReadRepoFile(
                "source",
                "Views",
                "Showcase",
                "ShowcaseControl.xaml");
            var code = ReadRepoFile(
                "source",
                "Views",
                "Showcase",
                "ShowcaseControl.xaml.cs");
            var widgetXaml = ReadRepoFile(
                "source",
                "Views",
                "Showcase",
                "ShowcaseWidgetControl.xaml");

            StringAssert.Contains(xaml, "x:Name=\"PreviousPageButton\"");
            StringAssert.Contains(xaml, "x:Name=\"PageSelector\"");
            StringAssert.Contains(xaml, "x:Name=\"NextPageButton\"");
            // The edit toolbar is gone: widget actions live on the blocks (add button and
            // right-click menu) and split/merge is tactile, on the selected block itself.
            Assert.IsFalse(xaml.Contains("EditToolsPanel"));
            Assert.IsFalse(xaml.Contains("WidgetActionButton"));
            Assert.IsFalse(xaml.Contains("LayoutActionButton"));
            Assert.IsFalse(xaml.Contains("x:Name=\"SplitColumnsButton\""));
            Assert.IsFalse(xaml.Contains("x:Name=\"MergeLeftButton\""));
            Assert.IsFalse(xaml.Contains("BoundaryCanvas"));
            Assert.IsFalse(code.Contains("OpenSplitPicker"));
            Assert.IsFalse(code.Contains("TrySplitThreeWays"));
            Assert.IsFalse(code.Contains("SplitPreviewItem"));
            StringAssert.Contains(code, "Focusable = EditLayoutButton.IsChecked == true");
            StringAssert.Contains(code, "TrySplit(");
            StringAssert.Contains(code, "CreateCutLine(");
            StringAssert.Contains(code, "AddMergeChevron(");
            StringAssert.Contains(code, "StrokeDashArrayProperty");
            StringAssert.Contains(code, "TryGetMergePreview(");
            StringAssert.Contains(code, "LOCPlayAch_Showcase_SplitAtFormat");
            StringAssert.Contains(code, "LOCPlayAch_Showcase_MergeLeftLabel");
            StringAssert.Contains(code, "Panel.SetZIndex(thumb, 39)");
            StringAssert.Contains(code, "args.Canceled");
            StringAssert.Contains(code, "TryMergeWithFallback(");
            StringAssert.Contains(code, "FindAdjacentBlocks");
            StringAssert.Contains(code, "MergeSelectedWith");
            StringAssert.Contains(code, "CanPlaceWidget");
            StringAssert.Contains(code, "ShowcaseWidgetSettingsDialog.Show");
            StringAssert.Contains(code, "Block_DragLeave");
            StringAssert.Contains(code, "border.PreviewDrop += Block_Drop");
            StringAssert.Contains(code, "border.PreviewDragOver += Block_DragOver");
            Assert.IsFalse(code.Contains("border.Drop += Block_Drop"));
            StringAssert.Contains(code, "LOCPlayAch_Showcase_DropMoveHere");
            StringAssert.Contains(code, "LOCPlayAch_Showcase_DropSwap");
            StringAssert.Contains(code, "LOCPlayAch_Showcase_DropSame");
            StringAssert.Contains(code, "DragVisualKind.ValidTarget");
            StringAssert.Contains(code, "new DoubleAnimation");
            StringAssert.Contains(code, "RepeatBehavior = RepeatBehavior.Forever");
            StringAssert.Contains(code, "PlayAch.Brush.Accent");
            StringAssert.Contains(code, "state.StatusPanel.Visibility");
            Assert.IsFalse(code.Contains("Widget drawer"));
            Assert.IsFalse(code.Contains("UnplaceWidget"));
            StringAssert.Contains(widgetXaml, "{DynamicResource PlayAch.Brush.Surface}");
            StringAssert.Contains(widgetXaml, "{DynamicResource PlayAch.Brush.Border}");
            StringAssert.Contains(widgetXaml, "{DynamicResource PlayAch.Brush.Text}");
            StringAssert.Contains(widgetXaml, "{DynamicResource PlayAch.Brush.Accent}");
            StringAssert.Contains(widgetXaml, "x:Name=\"GlyphText\"");
            StringAssert.Contains(widgetXaml, "x:Name=\"HeaderBorder\"");
            StringAssert.Contains(widgetXaml, "Visibility=\"Collapsed\"");
            Assert.IsFalse(code.Contains("definition.DescriptionKey"));
        }

        [TestMethod]
        public void Showcase_DataAndSettingsFlowsUseSharedRefreshAndSingleEditor()
        {
            var plugin = ReadRepoFile("source", "PlayniteAchievementsPlugin.cs");
            var editor = ReadRepoFile(
                "source",
                "Views",
                "Showcase",
                "ShowcaseWidgetSettingsDialog.cs");
            var localization = ReadRepoFile("source", "Localization", "en_US.xaml");
            var uiText = ReadRepoFile(
                "source",
                "Views",
                "Showcase",
                "ShowcaseUiText.cs");

            StringAssert.Contains(plugin, "Games_ItemCollectionChanged");
            StringAssert.Contains(plugin, "Games_ItemUpdated");
            StringAssert.Contains(plugin, "InvalidateStartPageData();");
            StringAssert.Contains(editor, "PlayniteUiProvider.CreateExtensionWindow");
            StringAssert.Contains(editor, "LOCPlayAch_Showcase_WidgetSettingsTitle");
            StringAssert.Contains(editor, "LOCPlayAch_Button_Clear");
            StringAssert.Contains(localization, "LOCPlayAch_Showcase_MergeDeleteConfirm");
            StringAssert.Contains(localization, "LOCPlayAch_Showcase_Stat_CurrentStreak");
            StringAssert.Contains(uiText, "Localize(string key)");
            Assert.IsFalse(uiText.Contains("Localize(string key, string fallback)"));
            Assert.IsFalse(editor.Contains("Localize(\"LOCPlayAch_Showcase_CustomTitle\", "));
        }

        [TestMethod]
        public void Showcase_LocalizationCoversCatalogAndEveryDynamicSettingValue()
        {
            var localization = ReadRepoFile("source", "Localization", "en_US.xaml");
            foreach (var definition in ShowcaseWidgetCatalog.Definitions)
            {
                AssertLocalizationKey(localization, definition.NameKey);
            }

            AssertEnumKeys<ShowcasePageTemplate>(
                localization,
                "LOCPlayAch_Showcase_Template_",
                value => value != ShowcasePageTemplate.Showcase);
            AssertEnumKeys<ShowcaseScoreMode>(localization, "LOCPlayAch_Showcase_ScoreMode_");
            AssertEnumKeys<ShowcasePieMode>(localization, "LOCPlayAch_Showcase_PieMode_");
            AssertEnumKeys<ShowcasePointsGrouping>(localization, "LOCPlayAch_Showcase_PointsGrouping_");
            AssertEnumKeys<ShowcaseMosaicSource>(localization, "LOCPlayAch_Showcase_MosaicSource_");
            AssertEnumKeys<ShowcaseScreenshotVariant>(localization, "LOCPlayAch_Showcase_ScreenshotVariant_");
            AssertEnumKeys<ShowcaseSlideshowSource>(localization, "LOCPlayAch_Showcase_SlideshowSource_");
            AssertEnumKeys<ShowcaseMosaicContent>(localization, "LOCPlayAch_Showcase_MosaicContent_");
            // Unlock Next and Finish Next reuse the mosaic's labels (ShowcaseUiText), which the
            // MosaicSource and GameMosaicSource assertions cover.
            AssertEnumKeys<ShowcaseAchievementGridSource>(
                localization,
                "LOCPlayAch_Showcase_AchievementGridSource_",
                value => value != ShowcaseAchievementGridSource.UnlockNext);
            AssertEnumKeys<ShowcaseGameGridSource>(
                localization,
                "LOCPlayAch_Showcase_GameGridSource_",
                value => value != ShowcaseGameGridSource.FinishNext);
            AssertEnumKeys<ShowcaseImageFitMode>(localization, "LOCPlayAch_Showcase_ImageFit_");
            AssertEnumKeys<ShowcaseGameMosaicSource>(localization, "LOCPlayAch_Showcase_GameMosaicSource_");
            AssertEnumKeys<UnlockNextCriterion>(localization, "LOCPlayAch_Showcase_UnlockNextCriterion_");
        }

        [TestMethod]
        public void Showcase_AllReferencedLocalizationKeysExistInEnUs()
        {
            var englishPath = FindRepoFile("source", "Localization", "en_US.xaml");
            var english = File.ReadAllText(englishPath);
            var showcaseDirectory = Path.GetDirectoryName(FindRepoFile(
                "source",
                "Views",
                "Showcase",
                "ShowcaseControl.xaml"));
            var sourceDirectory = Directory.GetParent(
                Directory.GetParent(showcaseDirectory).FullName).FullName;
            var localizationDirectory = Path.Combine(sourceDirectory, "Localization") +
                Path.DirectorySeparatorChar;
            var sourceFiles = Directory.GetFiles(
                    sourceDirectory,
                    "*.*",
                    SearchOption.AllDirectories)
                .Where(path =>
                    !path.StartsWith(localizationDirectory, StringComparison.OrdinalIgnoreCase) &&
                    path.IndexOf(
                        Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) < 0 &&
                    path.IndexOf(
                        Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) < 0 &&
                    (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)));
            var usedKeys = sourceFiles
                .SelectMany(path => Regex.Matches(
                        File.ReadAllText(path),
                        @"LOCPlayAch_Showcase_[A-Za-z0-9_]+")
                    .Cast<Match>()
                    .Select(match => match.Value))
                .Distinct(StringComparer.Ordinal)
                .Where(key => !key.EndsWith("_", StringComparison.Ordinal))
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToArray();

            foreach (var key in usedKeys)
            {
                AssertLocalizationKey(english, key);
            }
        }

        [TestMethod]
        public void RarestMosaic_LoadsTheFullUnlockedSnapshotWithoutDuplicatingAQuery()
        {
            var reader = ReadRepoFile(
                "source",
                "Services",
                "Database",
                "SummaryCacheReader.cs");
            var builder = ReadRepoFile(
                "source",
                "Services",
                "Overview",
                "OverviewDataBuilder.cs");

            StringAssert.Contains(reader, "includeAllUnlockedAchievements: requestedRecentLimit == 0");
            StringAssert.Contains(reader, "result.Achievements = mappedAchievements");
            StringAssert.Contains(builder, "snapshot.Achievements = MaterializeAchievements(");
            StringAssert.Contains(builder, "item?.Unlocked == true && item.UnlockTimeUtc.HasValue");
        }

        [TestMethod]
        public void RarityMosaic_UsesTheReusableCompactItemContractWithoutAThemeListAncestor()
        {
            var itemXaml = ReadRepoFile(
                "source",
                "Views",
                "Controls",
                "AchievementCompactItemControl.xaml");
            var itemCode = ReadRepoFile(
                "source",
                "Views",
                "Controls",
                "AchievementCompactItemControl.xaml.cs");
            var mosaicViewModel = ReadRepoFile(
                "source",
                "ViewModels",
                "Showcase",
                "Widgets",
                "IconMosaicWidgetViewModel.cs");
            var widgetTemplates = ReadRepoFile(
                "source",
                "Views",
                "Showcase",
                "ShowcaseWidgetTemplates.xaml");

            Assert.IsFalse(itemXaml.Contains("AncestorType=modern:AchievementCompactListControlBase"));
            StringAssert.Contains(itemCode, "ShowRarityGlowProperty");
            StringAssert.Contains(itemCode, "AnimateRarityGlowsProperty");
            StringAssert.Contains(itemCode, "UseLargeRarityGlowProperty");
            StringAssert.Contains(itemXaml, "Converter={StaticResource PercentToRarityGlow}");
            StringAssert.Contains(mosaicViewModel, "GetMosaicShowRarityGlow");
            StringAssert.Contains(mosaicViewModel, "AnimateRarityGlows");
            StringAssert.Contains(widgetTemplates, "AchievementCompactItemControl");
            StringAssert.Contains(widgetTemplates, "UseLargeRarityGlow=\"True\"");
            StringAssert.Contains(widgetTemplates, "Margin=\"6\"");
        }

        [TestMethod]
        public void Showcase_MediaAndScoresReuseSharedResponsivePresentation()
        {
            var commonResources = ReadRepoFile(
                "source",
                "Resources",
                "CommonResources.xaml");
            var gallery = ReadRepoFile(
                "source",
                "Views",
                "Dialogs",
                "CaptureGalleryViewer.xaml");
            var slideshow = ReadRepoFile(
                "source",
                "Views",
                "Showcase",
                "ScreenshotSlideshowControl.cs");
            var scoreXaml = ReadRepoFile(
                "source",
                "Views",
                "Controls",
                "ScoreCardControl.xaml");
            var scoreCode = ReadRepoFile(
                "source",
                "Views",
                "Controls",
                "ScoreCardControl.xaml.cs");
            var scoresViewModel = ReadRepoFile(
                "source",
                "ViewModels",
                "Showcase",
                "Widgets",
                "ScoresWidgetViewModel.cs");

            StringAssert.Contains(commonResources, "PlayAch.Capture.NavButtonStyle");
            StringAssert.Contains(commonResources, "PlayAch.Capture.GlyphButtonStyle");
            StringAssert.Contains(gallery, "{StaticResource PlayAch.Capture.NavButtonStyle}");
            Assert.IsFalse(gallery.Contains("x:Key=\"CaptureNavArrowStyle\""));
            StringAssert.Contains(slideshow, "IReadOnlyList<CaptureItem>");
            StringAssert.Contains(slideshow, "FullscreenMediaViewerPresenter.Show");
            StringAssert.Contains(slideshow, "PlayAch.Capture.NavButtonStyle");
            StringAssert.Contains(slideshow, "CreatePlaybackOrder(items)");
            StringAssert.Contains(
                slideshow,
                "_index = (_index + Math.Sign(direction) + _items.Count) % _items.Count;");
            Assert.IsFalse(slideshow.Contains("while (next == _index)"));
            StringAssert.Contains(scoreCode, "IsFeaturedProperty");
            StringAssert.Contains(scoreXaml, "Binding IsFeatured, ElementName=Root");
            // Widgets show the same content at every size; density only scales presentation.
            StringAssert.Contains(scoresViewModel, "IsFeatured = true;");
            Assert.IsFalse(scoresViewModel.Contains("IsFeatured = Density"));
        }

        [TestMethod]
        public void Showcase_PinMenusExcludeFriendOwnedRows()
        {
            var achievements = ReadRepoFile(
                "source",
                "Views",
                "Helpers",
                "AchievementRowOptionsMenuBuilder.cs");
            var games = ReadRepoFile(
                "source",
                "Views",
                "Helpers",
                "GameRowContextMenuBuilder.cs");
            var startPage = ReadRepoFile(
                "source",
                "PlayniteAchievementsPlugin.StartPage.cs");

            StringAssert.Contains(achievements, "friendOwned");
            StringAssert.Contains(games, "!(data is FriendGameSummaryItem)");
            StringAssert.Contains(startPage, "includeShowcasePin: !(data is FriendGameSummaryItem)");
        }

        [TestMethod]
        public void Showcase_PinCollectionsUseCustomSubmenusAndInstanceOwnedWidgetState()
        {
            var menuBuilder = ReadRepoFile(
                "source",
                "Views",
                "Helpers",
                "ShowcasePinMenuBuilder.cs");
            var achievementMenus = ReadRepoFile(
                "source",
                "Views",
                "Helpers",
                "AchievementRowOptionsMenuBuilder.cs");
            var gameMenus = ReadRepoFile(
                "source",
                "Views",
                "Helpers",
                "GameRowContextMenuBuilder.cs");
            var nativeMenus = ReadRepoFile(
                "source",
                "PlayniteAchievementsPlugin.Menus.cs");
            var options = ReadRepoFile(
                "source",
                "Views",
                "Showcase",
                "ShowcaseWidgetOptionsControl.cs");
            var templates = ReadRepoFile(
                "source",
                "Views",
                "Showcase",
                "ShowcaseWidgetTemplates.xaml");
            var achievementGrid = ReadRepoFile(
                "source",
                "Views",
                "Showcase",
                "ShowcaseAchievementGridControl.xaml.cs");
            var gameGrid = ReadRepoFile(
                "source",
                "Views",
                "Showcase",
                "ShowcaseGameGridControl.xaml.cs");

            StringAssert.Contains(menuBuilder, "LOCPlayAch_Showcase_PinToShowcase");
            StringAssert.Contains(menuBuilder, "IsCheckable = true");
            StringAssert.Contains(menuBuilder, "AutomationProperties.SetName(button, label)");
            StringAssert.Contains(menuBuilder, "e.Handled = true");
            StringAssert.Contains(menuBuilder, "LOCPlayAch_Showcase_NewCollection");
            StringAssert.Contains(menuBuilder, "ShowcaseConfigurationCommit.Commit()");
            StringAssert.Contains(achievementMenus, "ShowcasePinMenuBuilder.AppendAchievementMenu");
            StringAssert.Contains(gameMenus, "ShowcasePinMenuBuilder.AppendGameMenu");
            Assert.IsFalse(nativeMenus.Contains("ShowcasePinMenuBuilder"));
            Assert.IsFalse(nativeMenus.Contains("LOCPlayAch_Showcase_PinToShowcase"));

            StringAssert.Contains(options, "AddPinCollectionChoice");
            StringAssert.Contains(options, "ShowcaseGameGridSource.Pinned");
            StringAssert.Contains(options, "ShowcaseAchievementGridSource.Pinned");
            StringAssert.Contains(options, "ShowcaseMosaicSource.Pinned");
            StringAssert.Contains(options, "ShowcaseGameMosaicSource.Pinned");

            // Unlock Next ranks during selection, so its criterion replaces the generic sort rows
            // instead of stacking with them.
            StringAssert.Contains(options, "ShowcaseMosaicSource.UnlockNext");
            StringAssert.Contains(options, "ShowcaseGameMosaicSource.FinishNext");
            StringAssert.Contains(options, "ApplyUnlockNextVisibility");
            StringAssert.Contains(options, "AddLastPlayedWindowChoice");
            StringAssert.Contains(templates, "PinCollectionId=\"{Binding PinCollectionId}\"");
            StringAssert.Contains(achievementGrid, "PinCollectionId");
            StringAssert.Contains(gameGrid, "PinCollectionId");
            StringAssert.Contains(achievementGrid, "ShowcasePinService.MoveAchievement");
            StringAssert.Contains(gameGrid, "ShowcasePinService.MoveGame");
        }

        private static void AssertEnumKeys<T>(
            string localization,
            string prefix,
            Func<T, bool> include = null)
            where T : struct
        {
            foreach (T value in Enum.GetValues(typeof(T)))
            {
                if (include == null || include(value))
                {
                    AssertLocalizationKey(localization, prefix + value);
                }
            }
        }

        private static void AssertLocalizationKey(string localization, string key)
        {
            StringAssert.Contains(localization, $"x:Key=\"{key}\"");
        }

        private static string ReadRepoFile(params string[] parts)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }

                directory = directory.Parent;
            }

            Assert.Fail("Could not find " + Path.Combine(parts));
            return null;
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
