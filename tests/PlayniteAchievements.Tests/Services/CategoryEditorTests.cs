using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class CategoryEditorTests
    {
        private static readonly Guid GameId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        private string _tempDir;
        private GameCustomDataStore _store;
        private RecordingWriter _writer;
        private CategoryEditor _editor;

        [TestInitialize]
        public void Initialize()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "pa-category-editor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _store = new GameCustomDataStore(Path.Combine(_tempDir, "store"));
            _writer = new RecordingWriter();
            _editor = new CategoryEditor(_writer, () => null, store: _store);
        }

        [TestCleanup]
        public void Cleanup()
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
            }
        }

        private static CategoryEditSnapshot Snapshot(
            IReadOnlyList<string> renderedLabels,
            params CategoryEditMember[] members)
        {
            return new CategoryEditSnapshot(GameId, members, renderedLabels);
        }

        private static CategoryEditMember Member(string apiName, string category, string providerCategory = null)
        {
            return new CategoryEditMember(apiName, category, providerCategory ?? category);
        }

        [TestMethod]
        public void ResolveRenderedLabels_InsertsAncestorsAndDefaultAtTheEnd()
        {
            var labels = CategoryEditor.ResolveRenderedLabels(
                new[] { "A::B", "C" },
                categoryOrder: null,
                imageLabels: new[] { "D" },
                summaryLabel: "E");

            CollectionAssert.AreEqual(new[] { "A", "A::B", "C", "D", "E", "Default" }, labels.ToList());
        }

        [TestMethod]
        public void ResolveRenderedLabels_PlacesDefaultAtItsStoredOrderSlot()
        {
            var labels = CategoryEditor.ResolveRenderedLabels(
                new[] { "A", "B" },
                categoryOrder: new[] { "B", "Default", "A" },
                imageLabels: null,
                summaryLabel: null);

            CollectionAssert.AreEqual(new[] { "B", "Default", "A" }, labels.ToList());
        }

        [TestMethod]
        public void TryPlanCategoryRename_RefusesDefaultSelfAndDescendantTargets()
        {
            Assert.IsFalse(CategoryEditor.TryPlanCategoryRename("Default", "X", out _));
            Assert.IsFalse(CategoryEditor.TryPlanCategoryRename("A", "A", out _));
            Assert.IsFalse(CategoryEditor.TryPlanCategoryRename("A", "A::B", out _));
            Assert.IsFalse(CategoryEditor.TryPlanCategoryRename("A", "Default::A", out _));

            Assert.IsTrue(CategoryEditor.TryPlanCategoryRename("A", "B::A", out var move));
            Assert.AreEqual("A", move.Key);
            Assert.AreEqual("B::A", move.Value);
        }

        [TestMethod]
        public void CountAchievementsInCategories_CountsTheSubtree()
        {
            var snapshot = Snapshot(
                new[] { "A", "A::B", "C", "Default" },
                Member("1", "A"),
                Member("2", "A::B"),
                Member("3", "C"),
                Member("4", null));

            Assert.AreEqual(2, CategoryEditor.CountAchievementsInCategories(snapshot, new[] { "A" }));
            Assert.AreEqual(1, CategoryEditor.CountAchievementsInCategories(snapshot, new[] { "A::B" }));
            Assert.AreEqual(0, CategoryEditor.CountAchievementsInCategories(snapshot, new[] { "Missing" }));
        }

        [TestMethod]
        public void CanNestCategoryRowsUnder_IgnoresDefaultAsTargetOrSource()
        {
            var snapshot = Snapshot(new[] { "A", "B", "Default" });

            Assert.IsTrue(CategoryEditor.CanNestCategoryRowsUnder(snapshot, new[] { "A" }, "B"));
            Assert.IsFalse(CategoryEditor.CanNestCategoryRowsUnder(snapshot, new[] { "A" }, "Default"));
            Assert.IsFalse(CategoryEditor.CanNestCategoryRowsUnder(snapshot, new[] { "Default" }, "B"));
        }

        [TestMethod]
        public void RenameCategoryLabel_RewritesMembershipAndDropsRedundantOverrides()
        {
            var snapshot = Snapshot(
                new[] { "A", "A::B", "Default" },
                Member("1", "A", providerCategory: "A"),
                Member("2", "A::B", providerCategory: "A::B"),
                Member("3", "A", providerCategory: "C"));

            var outcome = _editor.RenameCategoryLabel(snapshot, "A", "C");

            Assert.IsTrue(outcome.Applied);
            Assert.IsFalse(outcome.AffectsSummaryData);
            Assert.AreEqual(1, _writer.AssignmentWrites.Count);
            var overrides = _writer.AssignmentWrites[0].CategoryOverrides;
            Assert.AreEqual("C", overrides["1"]);
            Assert.AreEqual("C::B", overrides["2"]);
            // "3" was provided as C and is now filed under C again: no override to keep.
            Assert.IsFalse(overrides.ContainsKey("3"));
            CollectionAssert.AreEqual(new[] { "C" }, outcome.MovedSelectionLabels.ToList());
        }

        [TestMethod]
        public void NestCategoryRowsUnder_MovesTheSubtreeAndSelectsTheNewLabel()
        {
            var snapshot = Snapshot(
                new[] { "A", "A::B", "C", "Default" },
                Member("1", "A::B"));

            var outcome = _editor.NestCategoryRowsUnder(snapshot, new[] { "A" }, "C");

            Assert.IsTrue(outcome.Applied);
            Assert.AreEqual("C::A::B", _writer.AssignmentWrites[0].CategoryOverrides["1"]);
            CollectionAssert.AreEqual(new[] { "C::A" }, outcome.MovedSelectionLabels.ToList());
        }

        [TestMethod]
        public void MergeCategoryInto_FlattensTheSubtreeOntoTheTarget()
        {
            var snapshot = Snapshot(
                new[] { "A", "A::B", "C", "Default" },
                Member("1", "A"),
                Member("2", "A::B"));

            var outcome = _editor.MergeCategoryInto(snapshot, "A", "C");

            Assert.IsTrue(outcome.Applied);
            var overrides = _writer.AssignmentWrites[0].CategoryOverrides;
            Assert.AreEqual("C", overrides["1"]);
            Assert.AreEqual("C", overrides["2"]);
            Assert.IsNull(outcome.MovedSelectionLabels);
        }

        [TestMethod]
        public void MergeCategoryInto_RefusesDefaultSource()
        {
            var snapshot = Snapshot(new[] { "A", "Default" }, Member("1", null));

            Assert.IsFalse(_editor.MergeCategoryInto(snapshot, "Default", "A").Applied);
            Assert.AreEqual(0, _writer.AssignmentWrites.Count);
        }

        [TestMethod]
        public void DuplicateCategories_InsertsTheCopyAfterTheSourceSubtree()
        {
            var snapshot = Snapshot(new[] { "A", "A::B", "C", "Default" });

            var outcome = _editor.DuplicateCategories(snapshot, new[] { "A" });

            Assert.IsTrue(outcome.Applied);
            Assert.AreEqual("A (2)", outcome.CreatedLabel);
            CollectionAssert.AreEqual(
                new[] { "A", "A::B", "A (2)", "C", "Default" },
                _writer.MetadataWrites[0].CategoryOrder.ToList());
        }

        [TestMethod]
        public void DeleteCategories_SendsTheSubtreeBackToDefaultAndDropsItFromTheOrder()
        {
            var snapshot = Snapshot(
                new[] { "A", "A::B", "C", "Default" },
                Member("1", "A", providerCategory: "A"),
                Member("2", "A::B", providerCategory: "Default"));

            var outcome = _editor.DeleteCategories(snapshot, new[] { "A", "A::B" });

            Assert.IsTrue(outcome.Applied);
            var write = _writer.AssignmentWrites[0];
            Assert.AreEqual("Default", write.CategoryOverrides["1"]);
            // Provided as Default and now back in Default: the override is dropped, not written.
            Assert.IsFalse(write.CategoryOverrides.ContainsKey("2"));
            CollectionAssert.AreEqual(new[] { "C", "Default" }, write.CategoryOrder.ToList());
        }

        [TestMethod]
        public void DeleteCategories_RefusesDefault()
        {
            var snapshot = Snapshot(new[] { "A", "Default" });

            Assert.IsFalse(_editor.DeleteCategories(snapshot, new[] { "Default" }).Applied);
        }

        [TestMethod]
        public void AddNewCategory_AppendsAUniqueTopLevelLabel()
        {
            var snapshot = Snapshot(new[] { "New Category", "Default" });

            var outcome = _editor.AddNewCategory(snapshot, "New Category");

            Assert.IsTrue(outcome.Applied);
            Assert.AreEqual("New Category (2)", outcome.CreatedLabel);
            CollectionAssert.AreEqual(
                new[] { "New Category", "Default", "New Category (2)" },
                _writer.MetadataWrites[0].CategoryOrder.ToList());
        }

        [TestMethod]
        public void CreateCategory_AdoptsAnExistingLabelWithoutWriting()
        {
            var snapshot = Snapshot(new[] { "A", "Default" });

            var outcome = _editor.CreateCategory(snapshot, "A");

            Assert.IsFalse(outcome.Applied);
            Assert.AreEqual("A", outcome.CreatedLabel);
            Assert.AreEqual(0, _writer.MetadataWrites.Count);
        }

        [TestMethod]
        public void AddNewCategory_DoesNothingWhenNothingRenders()
        {
            var outcome = _editor.AddNewCategory(Snapshot(Array.Empty<string>()), "New Category");

            Assert.IsFalse(outcome.Applied);
            Assert.IsNull(outcome.CreatedLabel);
        }

        private sealed class AssignmentWrite
        {
            public IReadOnlyDictionary<string, string> CategoryOverrides;
            public IReadOnlyDictionary<string, string> CategoryKinds;
            public IReadOnlyList<string> CategoryOrder;
            public bool AffectsSummaryData;
        }

        private sealed class MetadataWrite
        {
            public IReadOnlyList<string> CategoryOrder;
            public bool AffectsSummaryData;
        }

        private sealed class RecordingWriter : ICategoryMetadataWriter
        {
            public List<AssignmentWrite> AssignmentWrites { get; } = new List<AssignmentWrite>();

            public List<MetadataWrite> MetadataWrites { get; } = new List<MetadataWrite>();

            public bool SetAchievementCategoryAssignmentAndMetadata(
                Guid gameId,
                IReadOnlyDictionary<string, string> categoryOverrides,
                IReadOnlyDictionary<string, string> categoryKinds,
                IReadOnlyList<string> categoryOrder,
                IReadOnlyDictionary<string, CategoryImageOverrideData> categoryImageOverrides,
                GameSummaryCategoryData gameSummaryCategory,
                bool affectsSummaryData = true,
                bool rescopeAutoCapstones = false)
            {
                Assert.AreEqual(GameId, gameId);
                AssignmentWrites.Add(new AssignmentWrite
                {
                    CategoryOverrides = new Dictionary<string, string>(categoryOverrides.ToDictionary(p => p.Key, p => p.Value)),
                    CategoryKinds = categoryKinds,
                    CategoryOrder = categoryOrder?.ToList(),
                    AffectsSummaryData = affectsSummaryData
                });
                return false;
            }

            public void SetAchievementCategoryMetadata(
                Guid gameId,
                IReadOnlyList<string> categoryOrder,
                IReadOnlyDictionary<string, CategoryImageOverrideData> categoryImageOverrides,
                GameSummaryCategoryData gameSummaryCategory,
                bool affectsSummaryData = true)
            {
                Assert.AreEqual(GameId, gameId);
                MetadataWrites.Add(new MetadataWrite
                {
                    CategoryOrder = categoryOrder?.ToList(),
                    AffectsSummaryData = affectsSummaryData
                });
            }
        }
    }
}
