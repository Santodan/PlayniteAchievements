using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.ViewModels.ManageAchievements;

namespace PlayniteAchievements.Tests.ViewModels
{
    /// <summary>
    /// The editor writes as it goes, so its undo rewinds stored state rather than a buffer, and
    /// the history is assembled from the record before and after each write. What has to be right
    /// is the grouping: one gesture is one press of Ctrl+Z, however many writes it took.
    /// </summary>
    [TestClass]
    public class EditorUndoJournalTests
    {
        private static readonly EditorEditIntent PointsEdit =
            EditorEditIntent.FieldEdit("PointsText", "LOCPlayAch_Undo_FieldEdit");

        private static readonly EditorEditIntent TrophyEdit =
            EditorEditIntent.FieldEdit("TrophyType", "LOCPlayAch_Undo_FieldEdit");

        private static readonly EditorEditIntent ResetAll =
            EditorEditIntent.Atomic("ResetAll", "LOCPlayAch_Button_ResetAll");

        [TestMethod]
        public void OneWrite_BecomesOneStepOnceClosed()
        {
            var journal = new EditorUndoJournal();

            journal.Record(Order("a"), Order("b"), true, true, PointsEdit);
            Assert.IsFalse(journal.CanUndo, "A step still being collected is not on the history yet.");

            journal.CommitOpenStep();

            Assert.IsTrue(journal.CanUndo);
            Assert.AreEqual("LOCPlayAch_Undo_FieldEdit", journal.UndoLabelKey);
        }

        [TestMethod]
        public void ManyWritesOfOneGesture_BecomeASingleStep()
        {
            var journal = new EditorUndoJournal();

            // What a fan-out looks like from the store: one write per selected row, all belonging
            // to the same gesture.
            journal.Record(Order("a"), Order("b"), true, true, PointsEdit);
            journal.Record(Order("b"), Order("c"), true, true, PointsEdit);
            journal.Record(Order("c"), Order("d"), true, true, PointsEdit);
            journal.CommitOpenStep();

            var entry = journal.Undo();

            Assert.IsNotNull(entry);
            Assert.IsFalse(journal.CanUndo, "Three writes of one gesture undo in one press.");

            // The step spans the whole gesture: the first write's starting point to the last
            // write's result.
            var target = Order("d");
            entry.Facets.Single().ApplyBefore(target);
            CollectionAssert.AreEqual(new[] { "a" }, target.AchievementOrder);
        }

        [TestMethod]
        public void ADifferentGesture_ClosesTheOpenStep()
        {
            var journal = new EditorUndoJournal();

            journal.Record(Order("a"), Order("b"), true, true, PointsEdit);
            journal.Record(Order("b"), Order("c"), true, true, TrophyEdit);
            journal.CommitOpenStep();

            Assert.IsNotNull(journal.Undo());
            Assert.IsTrue(journal.CanUndo, "Editing one field then another is two steps.");
            Assert.IsNotNull(journal.Undo());
            Assert.IsFalse(journal.CanUndo);
        }

        [TestMethod]
        public void AnAtomicGesture_NeverMergesWithItsNeighbours()
        {
            var journal = new EditorUndoJournal();

            journal.Record(Order("a"), Order("b"), true, true, ResetAll);
            journal.Record(Order("b"), Order("c"), true, true, ResetAll);
            journal.CommitOpenStep();

            Assert.IsNotNull(journal.Undo());
            Assert.IsTrue(
                journal.CanUndo,
                "Two runs of an atomic gesture are two steps even though the intent matches.");
        }

        [TestMethod]
        public void AGestureThatEndsWhereItStarted_IsNotAStep()
        {
            var journal = new EditorUndoJournal();

            journal.Record(Order("a"), Order("b"), true, true, PointsEdit);
            journal.Record(Order("b"), Order("a"), true, true, PointsEdit);
            journal.CommitOpenStep();

            Assert.IsFalse(journal.CanUndo, "Typing a value and typing it back changed nothing.");
        }

        [TestMethod]
        public void TheStepsFlags_AreFoldedRatherThanNarrowed()
        {
            var journal = new EditorUndoJournal();

            journal.Record(Order("a"), Order("b"), false, false, PointsEdit);
            journal.Record(Order("b"), Order("c"), true, false, PointsEdit);
            journal.CommitOpenStep();

            var entry = journal.Undo();

            Assert.IsTrue(
                entry.AffectsSummaryData,
                "A reversal has to resync everything the gesture did, so the flags widen.");
            Assert.IsFalse(entry.AffectsOverrideMirror);
        }

        [TestMethod]
        public void UndoThenRedo_MovesTheStepBetweenTheStacks()
        {
            var journal = new EditorUndoJournal();
            journal.Record(Order("a"), Order("b"), true, true, PointsEdit);
            journal.CommitOpenStep();

            journal.Undo();
            Assert.IsFalse(journal.CanUndo);
            Assert.IsTrue(journal.CanRedo);

            journal.Redo();
            Assert.IsTrue(journal.CanUndo);
            Assert.IsFalse(journal.CanRedo);
        }

        [TestMethod]
        public void ANewStep_MakesTheForwardHistoryUnreachable()
        {
            var journal = new EditorUndoJournal();
            journal.Record(Order("a"), Order("b"), true, true, PointsEdit);
            journal.CommitOpenStep();
            journal.Undo();
            Assert.IsTrue(journal.CanRedo);

            journal.Record(Order("a"), Order("z"), true, true, TrophyEdit);
            journal.CommitOpenStep();

            Assert.IsFalse(journal.CanRedo);
        }

        [TestMethod]
        public void UndoClosesTheOpenStepFirst()
        {
            var journal = new EditorUndoJournal();

            journal.Record(Order("a"), Order("b"), true, true, PointsEdit);

            // Pressing undo right after an edit has to reverse that edit, not the one before it.
            var entry = journal.Undo();

            Assert.IsNotNull(entry);
            var target = Order("b");
            entry.Facets.Single().ApplyBefore(target);
            CollectionAssert.AreEqual(new[] { "a" }, target.AchievementOrder);
        }

        [TestMethod]
        public void AnUnclaimedWrite_IsNotRecorded()
        {
            var journal = new EditorUndoJournal();

            journal.Record(Order("a"), Order("b"), true, true, intent: null);

            Assert.IsFalse(journal.CanUndo, "A write nobody claimed is not the editor's to undo.");
        }

        [TestMethod]
        public void AWriteWithNoKnownStartingPoint_DropsTheHistory()
        {
            var journal = new EditorUndoJournal();
            journal.Record(Order("a"), Order("b"), true, true, PointsEdit);
            journal.CommitOpenStep();

            journal.Record(null, Order("c"), true, true, PointsEdit);

            Assert.IsFalse(
                journal.CanUndo,
                "Without the state before a write, the steps already held can no longer be trusted.");
        }

        [TestMethod]
        public void AForeignWriteTouchingAHeldFacet_DropsTheHistory()
        {
            var journal = new EditorUndoJournal();
            journal.Record(Order("a"), Order("b"), true, true, PointsEdit);
            journal.CommitOpenStep();

            journal.NoteForeignWrite(Order("b"), Order("elsewhere"));

            Assert.IsFalse(journal.CanUndo);
        }

        [TestMethod]
        public void AForeignWriteElsewhere_LeavesTheHistoryAlone()
        {
            var journal = new EditorUndoJournal();
            journal.Record(Order("a"), Order("b"), true, true, PointsEdit);
            journal.CommitOpenStep();

            // A refresh writing something the editor does not edit. Clearing here would empty the
            // history on nearly every refresh.
            var before = Order("b");
            var after = Order("b");
            after.ExcludedFromRefreshes = true;
            journal.NoteForeignWrite(before, after);

            Assert.IsTrue(journal.CanUndo);
        }

        [TestMethod]
        public void TheHistory_StopsAtItsDepthCap()
        {
            var journal = new EditorUndoJournal();

            for (var index = 0; index < EditorUndoJournal.MaxDepth + 10; index++)
            {
                journal.Record(
                    Order("step" + index),
                    Order("step" + (index + 1)),
                    true,
                    true,
                    EditorEditIntent.Command("c" + index, "LOCPlayAch_Undo_FieldEdit"));
                journal.CommitOpenStep();
            }

            var depth = 0;
            while (journal.Undo() != null)
            {
                depth++;
            }

            Assert.AreEqual(EditorUndoJournal.MaxDepth, depth);
        }

        [TestMethod]
        public void Clear_EmptiesBothStacksAndTheOpenStep()
        {
            var journal = new EditorUndoJournal();
            journal.Record(Order("a"), Order("b"), true, true, PointsEdit);
            journal.CommitOpenStep();
            journal.Undo();
            journal.Record(Order("a"), Order("b"), true, true, PointsEdit);

            journal.Clear();

            Assert.IsFalse(journal.CanUndo);
            Assert.IsFalse(journal.CanRedo);
            Assert.IsFalse(journal.HasOpenStep);
        }

        [TestMethod]
        public void AStep_DoesNotDriftWhenTheStoresRecordIsMutated()
        {
            var journal = new EditorUndoJournal();
            var before = Order("a");
            var after = Order("b");

            journal.Record(before, after, true, true, PointsEdit);

            // The store caches the record it wrote and the next write mutates that instance in
            // place, so a step holding it by reference would change underneath.
            after.AchievementOrder[0] = "mutated";
            before.AchievementOrder[0] = "mutated";
            journal.CommitOpenStep();

            var entry = journal.Undo();
            var target = Order("anything");
            entry.Facets.Single().ApplyBefore(target);
            CollectionAssert.AreEqual(new[] { "a" }, target.AchievementOrder);
        }

        [TestMethod]
        public void AStep_HoldsNoRowsAndNoDelegates()
        {
            var journal = new EditorUndoJournal();
            journal.Record(Order("a"), Order("b"), true, true, PointsEdit, new[] { "one", "two" });
            journal.CommitOpenStep();

            var entry = journal.Undo();

            // The guarantee that lets a history outlive a grid: nothing in a step can reach a row
            // or a view model. A command-object history would fail this by construction, since its
            // undo closures capture what they were built from.
            var offenders = new List<string>();
            WalkForOffenders(entry, new HashSet<object>(ReferenceEqualityComparer.Instance), offenders, depth: 0);

            Assert.AreEqual(
                0,
                offenders.Count,
                "A step must hold only plain data: " + string.Join(", ", offenders));
        }

        [TestMethod]
        public void AStep_NamesTheAchievementsItTouched()
        {
            var journal = new EditorUndoJournal();

            journal.Record(Order("a"), Order("b"), true, true, PointsEdit, new[] { "one" });
            journal.Record(Order("b"), Order("c"), true, true, PointsEdit, new[] { "one", "two" });
            journal.CommitOpenStep();

            var entry = journal.Undo();

            CollectionAssert.AreEquivalent(
                new[] { "one", "two" },
                entry.AffectedApiNames.ToArray(),
                "The names are what reselects the rows after an undo, without holding them.");
        }

        [TestMethod]
        public void AFieldEdit_IsHeldAsTheFieldRatherThanTheRecord()
        {
            var journal = new EditorUndoJournal();

            journal.Record(Order("a"), Order("b"), true, true, PointsEdit);
            journal.RecordRowValue("one", "PointsText", "10", "20", PointsEdit);
            journal.CommitOpenStep();

            var entry = journal.Undo();

            Assert.IsTrue(
                entry.IsRowValueStep,
                "Reversing the field is an edit; restoring the record it lives in is not.");
            var change = entry.RowValues.Single();
            Assert.AreEqual("one", change.ApiName);
            Assert.AreEqual("PointsText", change.PropertyName);
            Assert.AreEqual("10", change.OldValue);
            Assert.AreEqual("20", change.NewValue);
        }

        [TestMethod]
        public void AFieldWrittenRepeatedly_ReversesToWhereTheGestureStarted()
        {
            var journal = new EditorUndoJournal();
            journal.Record(Order("a"), Order("b"), true, true, PointsEdit);

            // One gesture can write the same field more than once - a commit that raises several
            // changes, or a save that re-runs.
            journal.RecordRowValue("one", "PointsText", "10", "20", PointsEdit);
            journal.RecordRowValue("one", "PointsText", "20", "30", PointsEdit);
            journal.CommitOpenStep();

            var change = journal.Undo().RowValues.Single();

            Assert.AreEqual("10", change.OldValue, "The value it started at is what undo restores.");
            Assert.AreEqual("30", change.NewValue, "The value it ended at is what redo restores.");
        }

        [TestMethod]
        public void AFanOutFieldEdit_HoldsOneChangePerRow()
        {
            var journal = new EditorUndoJournal();
            journal.Record(Order("a"), Order("b"), true, true, PointsEdit);

            journal.RecordRowValue("one", "PointsText", "1", "50", PointsEdit);
            journal.RecordRowValue("two", "PointsText", "2", "50", PointsEdit);
            journal.RecordRowValue("three", "PointsText", "3", "50", PointsEdit);
            journal.CommitOpenStep();

            var entry = journal.Undo();

            Assert.AreEqual(3, entry.RowValues.Count, "One press reverses every row the edit touched.");
            Assert.IsFalse(journal.CanUndo);
            CollectionAssert.AreEquivalent(
                new[] { "1", "2", "3" },
                entry.RowValues.Select(change => change.OldValue).ToArray());
        }

        [TestMethod]
        public void AFieldPutBackWithinOneGesture_IsNotAStep()
        {
            var journal = new EditorUndoJournal();
            journal.Record(Order("a"), Order("a"), true, true, PointsEdit);

            journal.RecordRowValue("one", "PointsText", "10", "20", PointsEdit);
            journal.RecordRowValue("one", "PointsText", "20", "10", PointsEdit);
            journal.CommitOpenStep();

            Assert.IsFalse(journal.CanUndo, "It ended where it started, so nothing moved.");
        }

        [TestMethod]
        public void AFieldChange_IsOnlyRecordedForAFieldEdit()
        {
            var journal = new EditorUndoJournal();

            // A reset rewrites several whole records at once, so a single field is not the unit
            // it can be reversed at.
            journal.Record(Order("a"), Order("b"), true, true, ResetAll);
            journal.RecordRowValue("one", "PointsText", "10", "20", ResetAll);
            journal.CommitOpenStep();

            var entry = journal.Undo();

            Assert.IsFalse(entry.IsRowValueStep);
            Assert.AreEqual(1, entry.Facets.Count);
        }

        [TestMethod]
        public void AFieldEditStep_StillHoldsNoRowsAndNoDelegates()
        {
            var journal = new EditorUndoJournal();
            journal.Record(Order("a"), Order("b"), true, true, PointsEdit, new[] { "one" });
            journal.RecordRowValue("one", "DisplayName", "before", "after", PointsEdit);
            journal.CommitOpenStep();

            var entry = journal.Undo();

            var offenders = new List<string>();
            WalkForOffenders(entry, new HashSet<object>(ReferenceEqualityComparer.Instance), offenders, depth: 0);

            Assert.AreEqual(0, offenders.Count, "A step must hold only plain data: " + string.Join(", ", offenders));
        }

        private static GameCustomDataFile Order(params string[] apiNames)
        {
            return new GameCustomDataFile { AchievementOrder = new List<string>(apiNames) };
        }

        /// <summary>
        /// Walks an object graph looking for anything a history must not hold.
        /// </summary>
        private static void WalkForOffenders(
            object value,
            HashSet<object> seen,
            List<string> offenders,
            int depth)
        {
            if (value == null || depth > 12 || !seen.Add(value))
            {
                return;
            }

            var type = value.GetType();
            if (value is Delegate)
            {
                offenders.Add("delegate " + type.Name);
                return;
            }

            if (type.Name.Contains("AchievementEditorRow") || type.Name.Contains("ViewModel"))
            {
                offenders.Add(type.Name);
                return;
            }

            if (type.IsPrimitive || value is string || value is DateTime || type.IsEnum)
            {
                return;
            }

            if (value is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    WalkForOffenders(item, seen, offenders, depth + 1);
                }

                return;
            }

            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                WalkForOffenders(field.GetValue(value), seen, offenders, depth + 1);
            }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            public new bool Equals(object left, object right) => ReferenceEquals(left, right);

            public int GetHashCode(object value) =>
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
        }
    }
}
