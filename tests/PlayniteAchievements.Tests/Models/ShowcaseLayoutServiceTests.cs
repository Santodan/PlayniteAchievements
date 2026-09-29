using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Showcase;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class ShowcaseLayoutServiceTests
    {
        [TestMethod]
        public void SeededTemplates_AreValidAndNeverContainTimeline()
        {
            foreach (ShowcasePageTemplate template in Enum.GetValues(typeof(ShowcasePageTemplate)))
            {
                var settings = ShowcaseLayoutService.CreateDefault();
                settings.Pages.Clear();
                settings.WidgetInstances.Clear();
                var page = ShowcaseLayoutService.AddPage(settings, template);

                Assert.IsTrue(
                    ShowcaseLayoutService.IsValidPartition(page.Blocks, page.GridSize),
                    template.ToString());
                Assert.IsFalse(page.Blocks
                    .Where(block => !string.IsNullOrWhiteSpace(block.WidgetInstanceId))
                    .Select(block => settings.WidgetInstances.Single(widget =>
                        widget.InstanceId == block.WidgetInstanceId))
                    .Any(widget => widget.Kind == ShowcaseWidgetKind.Timeline), template.ToString());
            }
        }

        [TestMethod]
        public void CreateWidget_SeedsTheMatchingDefaultPinCollection()
        {
            var settings = ShowcaseLayoutService.CreateDefault();
            var achievements = ShowcaseLayoutService.CreateWidget(
                settings,
                ShowcaseWidgetKind.RecentAchievements);
            var games = ShowcaseLayoutService.CreateWidget(
                settings,
                ShowcaseWidgetKind.GameSummaries);

            Assert.AreEqual(
                settings.DefaultAchievementPinCollectionId,
                PlayniteAchievements.Models.ShowcaseWidgetOptions.GetPinCollectionId(achievements));
            Assert.AreEqual(
                settings.DefaultGamePinCollectionId,
                PlayniteAchievements.Models.ShowcaseWidgetOptions.GetPinCollectionId(games));
        }

        [TestMethod]
        public void DefaultLayout_UsesRequestedScoreModeAndExpectedGeometry()
        {
            var collection = ShowcaseLayoutService.CreateDefault(true, false);
            var score = collection.WidgetInstances.Single(widget => widget.Kind == ShowcaseWidgetKind.Scores);

            Assert.AreEqual(ShowcaseScoreMode.Collection, score.GetOption("Mode", ShowcaseScoreMode.Dual));
            Assert.AreEqual(5, collection.Pages.Single().Blocks.Count);
            Assert.IsTrue(ShowcaseLayoutService.IsValidPartition(
                collection.Pages.Single().Blocks,
                collection.Pages.Single().GridSize));

            var none = ShowcaseLayoutService.CreateDefault(false, false);
            var scoreBlock = none.Pages.Single().Blocks.Single(block =>
                block.Row == 0 && block.Column == 3 && block.ColumnSpan == 2);
            Assert.IsNull(scoreBlock.WidgetInstanceId);
        }

        [TestMethod]
        public void SplitAndMerge_PreservePartitionAndRejectOccupiedMerge()
        {
            var settings = ShowcaseLayoutService.CreateDefault();
            var page = settings.Pages.Single();
            var profileBlock = page.Blocks.Single(block => block.Row == 0 && block.Column == 0);

            // Splitting at line 2 keeps the profile widget in the larger left half,
            // so placing another widget on the right yields two occupied blocks.
            Assert.IsTrue(ShowcaseLayoutService.TrySplit(
                settings, page.PageId, profileBlock.BlockId, vertical: true, gridLine: 2));
            Assert.IsTrue(ShowcaseLayoutService.IsValidPartition(page.Blocks, page.GridSize));

            var left = page.Blocks.Single(block => block.Row == 0 && block.Column == 0);
            var right = page.Blocks.Single(block => block.Row == 0 && block.Column == 2);
            var extra = ShowcaseLayoutService.CreateWidget(settings, ShowcaseWidgetKind.Pie);
            Assert.IsTrue(ShowcaseLayoutService.PlaceWidget(settings, page.PageId, right.BlockId, extra.InstanceId));
            Assert.IsFalse(ShowcaseLayoutService.TryMerge(
                settings, page.PageId, left.BlockId, right.BlockId));

            ShowcaseLayoutService.DeleteWidget(settings, extra.InstanceId);
            Assert.IsTrue(ShowcaseLayoutService.TryMerge(
                settings, page.PageId, left.BlockId, right.BlockId));
            Assert.IsTrue(ShowcaseLayoutService.IsValidPartition(page.Blocks, page.GridSize));
        }

        [TestMethod]
        public void Split_KeepsAnOccupiedWidgetInTheLargerResultingBlock()
        {
            var settings = ShowcaseLayoutService.CreateDefault();
            var page = settings.Pages.Single();
            var scoreBlock = page.Blocks.Single(block =>
                block.Row == 0 && block.Column == 3 && block.ColumnSpan == 2);
            var scoreId = scoreBlock.WidgetInstanceId;

            Assert.IsTrue(ShowcaseLayoutService.TryMerge(
                settings,
                page.PageId,
                page.Blocks.Single(block => block.Row == 0 && block.Column == 0).BlockId,
                scoreBlock.BlockId,
                scoreId));
            var fullWidth = page.Blocks.Single(block => block.Row == 0);
            Assert.AreEqual(5, fullWidth.ColumnSpan);

            Assert.IsTrue(ShowcaseLayoutService.TrySplit(
                settings,
                page.PageId,
                fullWidth.BlockId,
                vertical: true,
                gridLine: 1));

            Assert.IsNull(page.Blocks.Single(block =>
                block.Row == 0 && block.Column == 0).WidgetInstanceId);
            Assert.AreEqual(scoreId, page.Blocks.Single(block =>
                block.Row == 0 && block.Column == 1).WidgetInstanceId);
            Assert.IsTrue(ShowcaseLayoutService.IsValidPartition(page.Blocks, page.GridSize));
        }

        [TestMethod]
        public void Normalize_RepairsOverlapAndMissingCells()
        {
            var settings = new ShowcaseSettings
            {
                Pages =
                {
                    new ShowcasePageSettings
                    {
                        Name = "Broken",
                        GridSize = 3,
                        Blocks =
                        {
                            new ShowcaseBlockSettings { Row = 0, Column = 0, RowSpan = 2, ColumnSpan = 2 },
                            new ShowcaseBlockSettings { Row = 1, Column = 1, RowSpan = 2, ColumnSpan = 2 },
                            new ShowcaseBlockSettings { Row = 9, Column = 9, RowSpan = 1, ColumnSpan = 1 }
                        }
                    }
                }
            };

            ShowcaseLayoutService.Normalize(settings);

            Assert.IsTrue(ShowcaseLayoutService.IsValidPartition(
                settings.Pages.Single().Blocks,
                settings.Pages.Single().GridSize));
            Assert.AreEqual(6, settings.Pages.Single().Blocks.Count);
        }

        [TestMethod]
        public void NormalizeTrackWeights_ClampsRepairsAndDefaultsToEqualThirds()
        {
            CollectionAssert.AreEqual(
                new[] { 1d, 1d, 1d },
                ShowcaseLayoutService.NormalizeTrackWeights(null));
            CollectionAssert.AreEqual(
                new[] { 1d, 1d, 1d },
                ShowcaseLayoutService.NormalizeTrackWeights(new double[0]));
            CollectionAssert.AreEqual(
                new[]
                {
                    ShowcaseLayoutService.MinTrackWeight,
                    ShowcaseLayoutService.MaxTrackWeight,
                    1d
                },
                ShowcaseLayoutService.NormalizeTrackWeights(new[] { 0.01, 99d, double.NaN }));
            CollectionAssert.AreEqual(
                new[] { 0.5, 1.5, 1d },
                ShowcaseLayoutService.NormalizeTrackWeights(new[] { 0.5, 1.5, 1d }));
        }

        [TestMethod]
        public void Normalize_RepairsPageTrackWeightsAndKeepsAbsentOnesNull()
        {
            var settings = ShowcaseLayoutService.CreateDefault();
            var page = settings.Pages.Single();
            page.RowWeights = new System.Collections.Generic.List<double> { 0.01, 2d };
            Assert.IsNull(page.ColumnWeights);

            ShowcaseLayoutService.Normalize(settings);

            CollectionAssert.AreEqual(
                new[] { ShowcaseLayoutService.MinTrackWeight, 2d, 1d, 1d, 1d },
                page.RowWeights);
            Assert.IsNull(page.ColumnWeights);

            var clone = page.Clone();
            CollectionAssert.AreEqual(page.RowWeights, clone.RowWeights);
            Assert.IsNull(clone.ColumnWeights);
            clone.RowWeights[0] = 3d;
            Assert.AreEqual(ShowcaseLayoutService.MinTrackWeight, page.RowWeights[0]);
        }

        [TestMethod]
        public void DuplicateAndDeletePage_DeletesRemovedPageWidgets()
        {
            var settings = ShowcaseLayoutService.CreateDefault();
            var original = settings.Pages.Single();
            var duplicate = ShowcaseLayoutService.DuplicatePage(settings, original.PageId);
            var duplicatedWidgetIds = duplicate.Blocks
                .Where(block => !string.IsNullOrWhiteSpace(block.WidgetInstanceId))
                .Select(block => block.WidgetInstanceId)
                .ToList();

            Assert.AreEqual(2, settings.Pages.Count);
            Assert.IsTrue(duplicatedWidgetIds.All(id =>
                settings.WidgetInstances.Any(widget => widget.InstanceId == id)));
            Assert.IsTrue(ShowcaseLayoutService.DeletePage(settings, duplicate.PageId));
            Assert.IsTrue(duplicatedWidgetIds.All(id =>
                settings.WidgetInstances.All(widget => widget.InstanceId != id)));
            Assert.IsFalse(ShowcaseLayoutService.DeletePage(settings, original.PageId));
        }

        [TestMethod]
        public void OccupiedMerge_KeepsSelectedWidgetAndDeletesOtherInstance()
        {
            var settings = new ShowcaseSettings();
            var page = ShowcaseLayoutService.AddPage(settings, ShowcasePageTemplate.Blank);
            var left = page.Blocks.Single(block => block.Row == 0 && block.Column == 0);
            var right = page.Blocks.Single(block => block.Row == 0 && block.Column == 1);
            var keep = ShowcaseLayoutService.CreateWidget(settings, ShowcaseWidgetKind.Scores);
            var remove = ShowcaseLayoutService.CreateWidget(settings, ShowcaseWidgetKind.Pie);
            Assert.IsTrue(ShowcaseLayoutService.PlaceWidget(
                settings, page.PageId, left.BlockId, keep.InstanceId));
            Assert.IsTrue(ShowcaseLayoutService.PlaceWidget(
                settings, page.PageId, right.BlockId, remove.InstanceId));

            Assert.IsTrue(ShowcaseLayoutService.TryMerge(
                settings,
                page.PageId,
                left.BlockId,
                right.BlockId,
                keep.InstanceId));

            Assert.AreEqual(24, page.Blocks.Count);
            Assert.AreEqual(keep.InstanceId, page.Blocks.Single(block =>
                block.Row == 0 && block.Column == 0).WidgetInstanceId);
            Assert.IsFalse(settings.WidgetInstances.Any(widget =>
                widget.InstanceId == remove.InstanceId));
            Assert.IsTrue(ShowcaseLayoutService.IsValidPartition(page.Blocks, page.GridSize));
        }

        [TestMethod]
        public void StaggeredEdgeMerge_UsesMinimalRectangularClosureAndChosenSurvivor()
        {
            var page = new ShowcasePageSettings
            {
                Name = "Staggered",
                GridSize = 3,
                Blocks =
                {
                    new ShowcaseBlockSettings { Row = 0, Column = 0, RowSpan = 2, ColumnSpan = 2 },
                    new ShowcaseBlockSettings { Row = 0, Column = 2, RowSpan = 1, ColumnSpan = 1 },
                    new ShowcaseBlockSettings { Row = 1, Column = 2, RowSpan = 1, ColumnSpan = 1 },
                    new ShowcaseBlockSettings { Row = 2, Column = 0, RowSpan = 1, ColumnSpan = 1 },
                    new ShowcaseBlockSettings { Row = 2, Column = 1, RowSpan = 1, ColumnSpan = 1 },
                    new ShowcaseBlockSettings { Row = 2, Column = 2, RowSpan = 1, ColumnSpan = 1 }
                }
            };
            var settings = new ShowcaseSettings { Pages = { page } };
            var first = ShowcaseLayoutService.CreateWidget(settings, ShowcaseWidgetKind.Scores);
            var second = ShowcaseLayoutService.CreateWidget(settings, ShowcaseWidgetKind.Pie);
            var survivor = ShowcaseLayoutService.CreateWidget(settings, ShowcaseWidgetKind.Statistics);
            page.Blocks[0].WidgetInstanceId = first.InstanceId;
            page.Blocks[1].WidgetInstanceId = second.InstanceId;
            page.Blocks[2].WidgetInstanceId = survivor.InstanceId;

            var closure = ShowcaseLayoutService.GetMergeClosure(
                settings,
                page.PageId,
                page.Blocks[0].BlockId,
                page.Blocks[1].BlockId);
            Assert.AreEqual(3, closure.Count);
            Assert.IsTrue(ShowcaseLayoutService.TryMergeWithFallback(
                settings,
                page.PageId,
                page.Blocks[0].BlockId,
                page.Blocks[1].BlockId,
                survivor.InstanceId));

            Assert.AreEqual(4, page.Blocks.Count);
            var merged = page.Blocks.Single(block => block.Row == 0 && block.Column == 0);
            Assert.AreEqual(2, merged.RowSpan);
            Assert.AreEqual(3, merged.ColumnSpan);
            Assert.AreEqual(survivor.InstanceId, merged.WidgetInstanceId);
            Assert.IsFalse(settings.WidgetInstances.Any(widget => widget.InstanceId == first.InstanceId));
            Assert.IsFalse(settings.WidgetInstances.Any(widget => widget.InstanceId == second.InstanceId));
            Assert.IsTrue(ShowcaseLayoutService.IsValidPartition(page.Blocks, page.GridSize));
        }

        [TestMethod]
        public void MergePreview_ReturnsRectangularClosureWithoutMutating()
        {
            var page = new ShowcasePageSettings
            {
                Name = "Staggered",
                Blocks =
                {
                    new ShowcaseBlockSettings { Row = 0, Column = 0, RowSpan = 2, ColumnSpan = 2 },
                    new ShowcaseBlockSettings { Row = 0, Column = 2, RowSpan = 1, ColumnSpan = 1 },
                    new ShowcaseBlockSettings { Row = 1, Column = 2, RowSpan = 1, ColumnSpan = 1 },
                    new ShowcaseBlockSettings { Row = 2, Column = 0, RowSpan = 1, ColumnSpan = 1 },
                    new ShowcaseBlockSettings { Row = 2, Column = 1, RowSpan = 1, ColumnSpan = 1 },
                    new ShowcaseBlockSettings { Row = 2, Column = 2, RowSpan = 1, ColumnSpan = 1 }
                }
            };
            var settings = new ShowcaseSettings { Pages = { page } };
            var widget = ShowcaseLayoutService.CreateWidget(settings, ShowcaseWidgetKind.Scores);
            page.Blocks[0].WidgetInstanceId = widget.InstanceId;
            var blocksBefore = page.Blocks
                .Select(block => (block.BlockId, block.Row, block.Column, block.RowSpan, block.ColumnSpan, block.WidgetInstanceId))
                .ToList();
            var widgetCountBefore = settings.WidgetInstances.Count;

            // The staggered pair expands to a three-block rectangular closure.
            Assert.IsTrue(ShowcaseLayoutService.TryGetMergePreview(
                settings,
                page.PageId,
                page.Blocks[0].BlockId,
                page.Blocks[1].BlockId,
                out var closure));
            Assert.AreEqual(3, closure.Count);

            // The preview mutated nothing: same blocks, same geometry, same widgets.
            CollectionAssert.AreEqual(
                blocksBefore,
                page.Blocks
                    .Select(block => (block.BlockId, block.Row, block.Column, block.RowSpan, block.ColumnSpan, block.WidgetInstanceId))
                    .ToList());
            Assert.AreEqual(widgetCountBefore, settings.WidgetInstances.Count);
        }

        [TestMethod]
        public void MergePreview_DoesNotNormalizeBrokenLayouts()
        {
            // A page missing cells: Normalize would repair it by adding blocks.
            var page = new ShowcasePageSettings
            {
                Name = "Broken",
                Blocks =
                {
                    new ShowcaseBlockSettings { Row = 0, Column = 0, RowSpan = 1, ColumnSpan = 1 },
                    new ShowcaseBlockSettings { Row = 0, Column = 1, RowSpan = 1, ColumnSpan = 1 }
                }
            };
            var settings = new ShowcaseSettings { Pages = { page } };

            Assert.IsTrue(ShowcaseLayoutService.TryGetMergePreview(
                settings,
                page.PageId,
                page.Blocks[0].BlockId,
                page.Blocks[1].BlockId,
                out var closure));
            Assert.AreEqual(2, closure.Count);

            // Pure predicate: the broken layout is left exactly as it was.
            Assert.AreEqual(2, page.Blocks.Count);
        }

        [TestMethod]
        public void MergePreview_RejectsNonAdjacentBlocks()
        {
            var settings = ShowcaseLayoutService.CreateDefault();
            var page = settings.Pages.Single();
            var corner = page.Blocks.First(block => block.Row == 0 && block.Column == 0);
            var far = page.Blocks.FirstOrDefault(block =>
                !ShowcaseGeometry.RangesOverlap(block.Row, block.RowSpan, corner.Row, corner.RowSpan) &&
                !ShowcaseGeometry.RangesOverlap(block.Column, block.ColumnSpan, corner.Column, corner.ColumnSpan));

            if (far == null)
            {
                // Fall back to a hand-built page when the seed layout has no diagonal pair.
                page = new ShowcasePageSettings
                {
                    Blocks =
                    {
                        new ShowcaseBlockSettings { Row = 0, Column = 0 },
                        new ShowcaseBlockSettings { Row = 2, Column = 2 }
                    }
                };
                settings = new ShowcaseSettings { Pages = { page } };
                corner = page.Blocks[0];
                far = page.Blocks[1];
            }

            Assert.IsFalse(ShowcaseLayoutService.TryGetMergePreview(
                settings,
                page.PageId,
                corner.BlockId,
                far.BlockId,
                out var closure));
            Assert.AreEqual(0, closure.Count);
        }

        [TestMethod]
        public void PruneOrphanedWidgets_RemovesReplacedInstanceButKeepsPlacedWidgets()
        {
            var settings = new ShowcaseSettings();
            var page = ShowcaseLayoutService.AddPage(settings, ShowcasePageTemplate.Blank);
            var block = page.Blocks[0];
            var original = ShowcaseLayoutService.CreateWidget(settings, ShowcaseWidgetKind.Pie);
            var replacement = ShowcaseLayoutService.CreateWidget(settings, ShowcaseWidgetKind.Statistics);
            Assert.IsTrue(ShowcaseLayoutService.PlaceWidget(
                settings, page.PageId, block.BlockId, original.InstanceId));
            Assert.IsTrue(ShowcaseLayoutService.PlaceWidget(
                settings, page.PageId, block.BlockId, replacement.InstanceId));

            Assert.AreEqual(1, ShowcaseLayoutService.PruneOrphanedWidgets(settings));
            Assert.IsFalse(settings.WidgetInstances.Any(widget =>
                widget.InstanceId == original.InstanceId));
            Assert.IsTrue(settings.WidgetInstances.Any(widget =>
                widget.InstanceId == replacement.InstanceId));
        }

        [TestMethod]
        public void Clone_IsDeepForPagesWidgetsPinsAndProfile()
        {
            var settings = ShowcaseLayoutService.CreateDefault();
            settings.GamePinCollections[0].GameIds.Add(Guid.NewGuid());
            settings.AchievementPinCollections[0].Pins.Add(new PinnedAchievementReference
            {
                GameId = Guid.NewGuid(),
                ApiName = "first",
                LastKnownAchievementName = "First"
            });
            var profileWidget = settings.WidgetInstances.Single(widget => widget.Kind == ShowcaseWidgetKind.Profile);
            profileWidget.Profile.DisplayName = "Player";
            profileWidget.Profile.Links = new List<ShowcaseProfileLink>
            {
                new ShowcaseProfileLink { ProviderKey = "Steam", Value = "player" }
            };

            var clone = settings.Clone();
            clone.Pages[0].Name = "Changed";
            clone.WidgetInstances[0].SetOption("Mode", "Changed");
            clone.GamePinCollections[0].GameIds.Clear();
            clone.AchievementPinCollections[0].Pins[0].ApiName = "changed";
            var clonedProfile = clone.WidgetInstances.Single(widget => widget.Kind == ShowcaseWidgetKind.Profile).Profile;
            clonedProfile.DisplayName = "Changed";
            clonedProfile.Links[0].Value = "changed";

            Assert.AreEqual("Showcase", settings.Pages[0].Name);
            Assert.AreEqual(1, settings.GamePinCollections[0].GameIds.Count);
            Assert.AreEqual("first", settings.AchievementPinCollections[0].Pins[0].ApiName);
            Assert.AreEqual("Player", profileWidget.Profile.DisplayName);
            Assert.AreEqual("player", profileWidget.Profile.Links[0].Value);
        }

        [TestMethod]
        public void Normalize_MovesLegacyProfileOntoProfileWidgetsAndClearsIt()
        {
            var settings = ShowcaseLayoutService.CreateDefault();
            var pageProfile = settings.WidgetInstances.Single(widget => widget.Kind == ShowcaseWidgetKind.Profile);
            pageProfile.Profile = null;
            var edited = ShowcaseLayoutService.CreateWidget(settings, ShowcaseWidgetKind.Profile);
            edited.Profile = new ShowcaseProfileSettings { DisplayName = "Mine" };
            settings.StartPageInstances["start:1"] = new ShowcaseWidgetInstanceSettings
            {
                Kind = ShowcaseWidgetKind.Profile
            };
            settings.StartPageInstances["start:2"] = new ShowcaseWidgetInstanceSettings
            {
                Kind = ShowcaseWidgetKind.Pie
            };
            settings.Profile = new ShowcaseProfileSettings
            {
                DisplayName = "Legacy",
                BackgroundPath = @"C:\art\banner.png"
            };

            ShowcaseLayoutService.Normalize(settings);

            Assert.IsNull(settings.Profile, "legacy object is consumed");
            Assert.AreEqual("Legacy", pageProfile.Profile.DisplayName);
            Assert.AreEqual(@"C:\art\banner.png", pageProfile.Profile.BackgroundPath);
            Assert.AreEqual("Legacy", settings.StartPageInstances["start:1"].Profile.DisplayName);
            Assert.AreEqual("Mine", edited.Profile.DisplayName, "a card with its own data keeps it");
            Assert.IsNull(settings.StartPageInstances["start:2"].Profile, "only profile widgets carry one");
            Assert.AreNotSame(pageProfile.Profile, settings.StartPageInstances["start:1"].Profile);

            settings.Profile = new ShowcaseProfileSettings { DisplayName = "Again" };
            pageProfile.Profile.DisplayName = "Edited";
            ShowcaseLayoutService.Normalize(settings);
            Assert.AreEqual("Edited", pageProfile.Profile.DisplayName, "never reseeds an existing card");
        }

        [TestMethod]
        public void Normalize_GivesEveryProfileWidgetItsOwnProfileObject()
        {
            var settings = ShowcaseLayoutService.CreateDefault();
            var profile = settings.WidgetInstances.Single(widget => widget.Kind == ShowcaseWidgetKind.Profile);
            profile.Profile = null;
            var pie = settings.WidgetInstances.First(widget => widget.Kind != ShowcaseWidgetKind.Profile);
            pie.Profile = new ShowcaseProfileSettings { DisplayName = "Stray" };

            ShowcaseLayoutService.Normalize(settings);

            Assert.IsNotNull(profile.Profile);
            Assert.IsNull(pie.Profile);
        }

        [TestMethod]
        public void DuplicatePage_ProfileEditOnCopyLeavesOriginalUnchanged()
        {
            var settings = ShowcaseLayoutService.CreateDefault();
            var page = settings.Pages.Single();
            var original = settings.WidgetInstances.Single(widget => widget.Kind == ShowcaseWidgetKind.Profile);
            original.Profile.DisplayName = "Original";
            original.Profile.BackgroundPath = @"C:\art\one.png";
            original.Profile.Links = new List<ShowcaseProfileLink>
            {
                new ShowcaseProfileLink { ProviderKey = "Steam", Value = "one" }
            };

            var copy = ShowcaseLayoutService.DuplicatePage(settings, page.PageId);
            var copied = copy.Blocks
                .Where(block => block.WidgetInstanceId != null)
                .Select(block => settings.WidgetInstances.Single(widget => widget.InstanceId == block.WidgetInstanceId))
                .Single(widget => widget.Kind == ShowcaseWidgetKind.Profile);

            Assert.AreNotSame(original, copied);
            Assert.AreEqual("Original", copied.Profile.DisplayName, "the copy starts from the original");
            copied.Profile.DisplayName = "Copy";
            copied.Profile.BackgroundPath = @"C:\art\two.png";
            copied.Profile.Links[0].Value = "two";

            Assert.AreEqual("Original", original.Profile.DisplayName);
            Assert.AreEqual(@"C:\art\one.png", original.Profile.BackgroundPath);
            Assert.AreEqual("one", original.Profile.Links[0].Value);
        }

        [TestMethod]
        public void PageOperations_RenameReorderResetAndProtectLastPage()
        {
            var settings = ShowcaseLayoutService.CreateDefault();
            var first = settings.Pages.Single();
            var second = ShowcaseLayoutService.AddPage(
                settings,
                ShowcasePageTemplate.Analytics,
                "Insights");

            Assert.IsTrue(ShowcaseLayoutService.RenamePage(settings, second.PageId, "Stats"));
            Assert.AreEqual("Stats", second.Name);
            Assert.IsTrue(ShowcaseLayoutService.MovePage(settings, second.PageId, -1));
            Assert.AreSame(second, settings.Pages[0]);
            Assert.IsTrue(ShowcaseLayoutService.ResetPage(
                settings,
                second.PageId,
                ShowcasePageTemplate.Collection));
            Assert.IsTrue(ShowcaseLayoutService.IsValidPartition(
                settings.Pages[0].Blocks,
                settings.Pages[0].GridSize));
            Assert.IsFalse(settings.Pages[0].Blocks
                .Where(block => !string.IsNullOrWhiteSpace(block.WidgetInstanceId))
                .Select(block => settings.WidgetInstances.Single(widget =>
                    widget.InstanceId == block.WidgetInstanceId))
                .Any(widget => widget.Kind == ShowcaseWidgetKind.Timeline));
            Assert.IsTrue(ShowcaseLayoutService.DeletePage(settings, first.PageId));
            Assert.IsFalse(ShowcaseLayoutService.DeletePage(settings, second.PageId));
        }

        [TestMethod]
        public void BoundaryOperations_HandleBothDirectionsAndRejectNonRectangularUnion()
        {
            var settings = new ShowcaseSettings();
            var page = ShowcaseLayoutService.AddPage(settings, ShowcasePageTemplate.Blank);
            var topLeft = page.Blocks.Single(block => block.Row == 0 && block.Column == 0);
            var topMiddle = page.Blocks.Single(block => block.Row == 0 && block.Column == 1);
            var middleLeft = page.Blocks.Single(block => block.Row == 1 && block.Column == 0);
            var middleMiddle = page.Blocks.Single(block => block.Row == 1 && block.Column == 1);

            Assert.IsFalse(ShowcaseLayoutService.TryMerge(
                settings,
                page.PageId,
                topLeft.BlockId,
                middleMiddle.BlockId));
            Assert.IsTrue(ShowcaseLayoutService.TryMerge(
                settings,
                page.PageId,
                topLeft.BlockId,
                topMiddle.BlockId));
            Assert.IsTrue(ShowcaseLayoutService.TryMerge(
                settings,
                page.PageId,
                middleLeft.BlockId,
                middleMiddle.BlockId));
            Assert.IsTrue(ShowcaseLayoutService.TryMerge(
                settings,
                page.PageId,
                topLeft.BlockId,
                middleLeft.BlockId));
            Assert.IsTrue(ShowcaseLayoutService.IsValidPartition(page.Blocks, page.GridSize));
            Assert.IsTrue(ShowcaseLayoutService.TrySplit(
                settings,
                page.PageId,
                topLeft.BlockId,
                vertical: false,
                gridLine: 1));
            Assert.IsTrue(ShowcaseLayoutService.IsValidPartition(page.Blocks, page.GridSize));
        }

        [TestMethod]
        public void PlaceWidget_SwapsAssignmentsAndEnforcesSingletonKindPerPage()
        {
            var settings = new ShowcaseSettings();
            var page = ShowcaseLayoutService.AddPage(settings, ShowcasePageTemplate.Blank);
            var first = page.Blocks[0];
            var second = page.Blocks[1];
            var firstPie = ShowcaseLayoutService.CreateWidget(settings, ShowcaseWidgetKind.Pie);
            var secondPie = ShowcaseLayoutService.CreateWidget(settings, ShowcaseWidgetKind.Pie);

            Assert.IsTrue(ShowcaseLayoutService.PlaceWidget(
                settings, page.PageId, first.BlockId, firstPie.InstanceId));
            Assert.IsTrue(ShowcaseLayoutService.PlaceWidget(
                settings, page.PageId, second.BlockId, secondPie.InstanceId));
            Assert.IsTrue(ShowcaseLayoutService.PlaceWidget(
                settings, page.PageId, second.BlockId, firstPie.InstanceId));
            Assert.AreEqual(firstPie.InstanceId, second.WidgetInstanceId);
            Assert.AreEqual(secondPie.InstanceId, first.WidgetInstanceId);

            var profileOne = ShowcaseLayoutService.CreateWidget(
                settings,
                ShowcaseWidgetKind.Profile);
            var profileTwo = ShowcaseLayoutService.CreateWidget(
                settings,
                ShowcaseWidgetKind.Profile);
            Assert.IsTrue(ShowcaseLayoutService.PlaceWidget(
                settings, page.PageId, page.Blocks[2].BlockId, profileOne.InstanceId));
            Assert.IsTrue(ShowcaseLayoutService.CanPlaceWidget(
                settings, page.PageId, page.Blocks[3].BlockId, profileOne.InstanceId));
            Assert.IsTrue(ShowcaseLayoutService.PlaceWidget(
                settings, page.PageId, page.Blocks[3].BlockId, profileOne.InstanceId));
            Assert.AreEqual(profileOne.InstanceId, page.Blocks[3].WidgetInstanceId);
            Assert.IsNull(page.Blocks[2].WidgetInstanceId);
            Assert.IsFalse(ShowcaseLayoutService.PlaceWidget(
                settings, page.PageId, page.Blocks[4].BlockId, profileTwo.InstanceId));
        }

        [TestMethod]
        public void Normalize_PreservesValidLastSelectedPage()
        {
            var settings = ShowcaseLayoutService.CreateDefault();
            var second = ShowcaseLayoutService.AddPage(
                settings,
                ShowcasePageTemplate.Blank,
                "Second");
            settings.LastSelectedPageId = second.PageId;

            ShowcaseLayoutService.Normalize(settings);

            Assert.AreEqual(second.PageId, settings.LastSelectedPageId);
        }

        [DataTestMethod]
        [DataRow(true, true, (int)ShowcaseScoreMode.Dual)]
        [DataRow(true, false, (int)ShowcaseScoreMode.Collection)]
        [DataRow(false, true, (int)ShowcaseScoreMode.Prestige)]
        [DataRow(false, false, -1)]
        public void PersistedSettings_MigratesEveryLegacyScoreVisibilityCombinationOnce(
            bool collectionVisible,
            bool prestigeVisible,
            int expectedMode)
        {
            var persisted = new PersistedSettings
            {
                ShowOverviewCollectionScoreCard = collectionVisible,
                ShowOverviewPrestigeScoreCard = prestigeVisible
            };

            var first = persisted.Showcase;
            var pageId = first.Pages.Single().PageId;
            var score = first.WidgetInstances.SingleOrDefault(widget =>
                widget.Kind == ShowcaseWidgetKind.Scores);
            if (expectedMode < 0)
            {
                Assert.IsNull(score);
            }
            else
            {
                Assert.IsNotNull(score);
                Assert.AreEqual(
                    (ShowcaseScoreMode)expectedMode,
                    score.GetOption("Mode", ShowcaseScoreMode.Dual));
            }

            Assert.AreSame(first, persisted.Showcase);
            Assert.AreEqual(pageId, persisted.Showcase.Pages.Single().PageId);
        }

        [TestMethod]
        public void Normalize_KeepsOrderedLastKnownPinsAndRemovesDuplicateIdentity()
        {
            var gameId = Guid.NewGuid();
            var otherGameId = Guid.NewGuid();
            var settings = ShowcaseLayoutService.CreateDefault();
            settings.GamePinCollections[0].GameIds.Add(gameId);
            settings.GamePinCollections[0].GameIds.Add(otherGameId);
            settings.GamePinCollections[0].GameIds.Add(gameId);
            settings.AchievementPinCollections[0].Pins.Add(new PinnedAchievementReference
            {
                GameId = gameId,
                ApiName = "missing-api",
                LastKnownGameName = "Removed Game",
                LastKnownAchievementName = "Still Manageable"
            });
            settings.AchievementPinCollections[0].Pins.Add(new PinnedAchievementReference
            {
                GameId = gameId,
                ApiName = "MISSING-API",
                LastKnownAchievementName = "Duplicate"
            });

            ShowcaseLayoutService.Normalize(settings);

            CollectionAssert.AreEqual(
                new[] { gameId, otherGameId },
                settings.GamePinCollections[0].GameIds);
            Assert.AreEqual(1, settings.AchievementPinCollections[0].Pins.Count);
            Assert.AreEqual(
                "Still Manageable",
                settings.AchievementPinCollections[0].Pins[0].LastKnownAchievementName);
            Assert.AreEqual(
                "Removed Game",
                settings.AchievementPinCollections[0].Pins[0].LastKnownGameName);
        }

        [TestMethod]
        public void Normalize_RepairsCollectionIdsNamesAndProtectedDefaults()
        {
            var settings = ShowcaseLayoutService.CreateDefault();
            settings.AchievementPinCollections = new List<PinnedAchievementCollection>
            {
                new PinnedAchievementCollection
                {
                    CollectionId = "duplicate-id",
                    Name = " Highlights "
                },
                new PinnedAchievementCollection
                {
                    CollectionId = "duplicate-id",
                    Name = "highlights"
                }
            };
            settings.GamePinCollections = null;

            ShowcaseLayoutService.Normalize(settings);

            Assert.AreEqual(
                settings.DefaultAchievementPinCollectionId,
                settings.AchievementPinCollections[0].CollectionId);
            Assert.AreEqual(
                settings.DefaultGamePinCollectionId,
                settings.GamePinCollections.Single().CollectionId);
            Assert.AreEqual(
                settings.AchievementPinCollections.Count,
                settings.AchievementPinCollections
                    .Select(collection => collection.CollectionId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count());
            Assert.AreEqual(
                settings.AchievementPinCollections.Count,
                settings.AchievementPinCollections
                    .Select(collection => collection.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count());
            Assert.IsTrue(settings.AchievementPinCollections.Any(collection =>
                collection.Name == "Highlights"));
            Assert.IsTrue(settings.AchievementPinCollections.Any(collection =>
                collection.Name == "highlights (2)"));
        }
    }
}
