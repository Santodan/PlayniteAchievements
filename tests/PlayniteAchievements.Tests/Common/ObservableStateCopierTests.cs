using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Tests.Common
{
    /// <summary>
    /// The Manage Achievements editor refreshes rows that are already bound instead of replacing
    /// the collection they live in, because replacing it raises a Reset that measured ~136ms for
    /// the DataGrid to react plus ~162ms to re-realize a viewport whatever changed.
    ///
    /// Copying backing fields is what makes that safe: the rows' public setters validate, raise
    /// events, depend on assignment order and feed a persistence hook, so driving ~50 of them
    /// across 641 rows would risk both a different result than a rebuild and a storm of store
    /// writes. These pin the properties that safety rests on.
    /// </summary>
    [TestClass]
    public class ObservableStateCopierTests
    {
        private class BaseState
        {
            private string _basePrivate;

            // Set once in the constructor; not state a refresh can change.
            private readonly List<string> _readonlyList = new List<string>();

            public BaseState(string basePrivate)
            {
                _basePrivate = basePrivate;
            }

            public string BasePrivate
            {
                get => _basePrivate;
                set => _basePrivate = value;
            }

            public List<string> ReadonlyList => _readonlyList;
        }

        private sealed class DerivedState : BaseState
        {
            private int _number;
            private string _text;

            public DerivedState(string basePrivate = null)
                : base(basePrivate)
            {
            }

            public event EventHandler Changed;

            // An auto-property: its compiler-generated backing field is still state.
            public bool Flag { get; set; }

            public int Number
            {
                get => _number;
                set
                {
                    _number = value;
                    SetterCallCount++;
                }
            }

            public string Text
            {
                get => _text;
                set
                {
                    _text = value;
                    SetterCallCount++;
                }
            }

            /// <summary>Counts setter invocations; the copy must never drive a setter.</summary>
            public int SetterCallCount;

            public int ChangedSubscriberCount => Changed?.GetInvocationList().Length ?? 0;

            public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
        }

        private sealed class OtherState
        {
        }

        private static DerivedState Populated()
        {
            var source = new DerivedState("base-value");
            source.Number = 42;
            source.Text = "source";
            source.Flag = true;
            source.ReadonlyList.Add("shared");
            source.SetterCallCount = 0;
            return source;
        }

        [TestMethod]
        public void CopyState_CopiesEveryFieldIncludingAutoPropertiesAndBaseClassPrivates()
        {
            var source = Populated();
            var target = new DerivedState();

            ObservableStateCopier.CopyState(target, source);

            Assert.AreEqual(42, target.Number);
            Assert.AreEqual("source", target.Text);
            Assert.IsTrue(target.Flag, "An auto-property's backing field is state and must copy.");
            Assert.AreEqual(
                "base-value",
                target.BasePrivate,
                "GetFields does not return a base type's private fields, so the walk up the " +
                "hierarchy is what keeps inherited state from being silently dropped.");
        }

        [TestMethod]
        public void CopyState_NeverDrivesASetter()
        {
            var source = Populated();
            var target = new DerivedState();

            ObservableStateCopier.CopyState(target, source);

            // This is the property the whole design rests on: the editor's real setters persist
            // to the store, and running ~50 of them across 641 rows is a write storm.
            Assert.AreEqual(
                0,
                target.SetterCallCount,
                "Fields are assigned directly; no setter logic may run.");
        }

        [TestMethod]
        public void CopyState_DoesNotStealTheTargetsEventSubscribers()
        {
            var source = Populated();
            var target = new DerivedState();

            var targetNotified = 0;
            target.Changed += (_, __) => targetNotified++;

            var sourceNotified = 0;
            source.Changed += (_, __) => sourceNotified++;

            ObservableStateCopier.CopyState(target, source);

            Assert.AreEqual(
                1,
                target.ChangedSubscriberCount,
                "Copying a delegate field would repoint this object's event at the other " +
                "object's listeners, silently redirecting notifications.");

            target.RaiseChanged();
            Assert.AreEqual(1, targetNotified, "The target's own subscriber must still fire.");
            Assert.AreEqual(0, sourceNotified, "The source's subscriber must not be reached.");
        }

        [TestMethod]
        public void CopyState_LeavesReadonlyFieldsAlone()
        {
            var source = Populated();
            var target = new DerivedState();
            var targetList = target.ReadonlyList;

            ObservableStateCopier.CopyState(target, source);

            Assert.AreSame(
                targetList,
                target.ReadonlyList,
                "A readonly field is constructor state, not something a refresh replaces.");
            Assert.AreEqual(0, target.ReadonlyList.Count);
        }

        [TestMethod]
        public void CopyState_IsAFullReplacementNotAMerge()
        {
            var source = new DerivedState();
            source.SetterCallCount = 0;

            var target = Populated();

            ObservableStateCopier.CopyState(target, source);

            // A stale value surviving the copy is exactly the "reset left something on screen"
            // failure this has to rule out.
            Assert.AreEqual(0, target.Number);
            Assert.IsNull(target.Text);
            Assert.IsFalse(target.Flag);
            Assert.IsNull(target.BasePrivate);
        }

        [TestMethod]
        public void CopyState_IsIdempotent()
        {
            var source = Populated();
            var target = new DerivedState();

            ObservableStateCopier.CopyState(target, source);
            ObservableStateCopier.CopyState(target, source);

            Assert.AreEqual(42, target.Number);
            Assert.AreEqual("source", target.Text);
            Assert.AreEqual(0, target.SetterCallCount);
        }

        [TestMethod]
        public void CopyState_IgnoresNullsAndSelfCopies()
        {
            var target = Populated();

            ObservableStateCopier.CopyState(target, null);
            ObservableStateCopier.CopyState<DerivedState>(null, target);
            ObservableStateCopier.CopyState(target, target);

            Assert.AreEqual(42, target.Number, "A self-copy must not disturb the instance.");
        }

        [TestMethod]
        public void CopyState_RefusesAMismatchedRuntimeType()
        {
            // Half-applying a copy would leave the object in a state neither instance ever had.
            Assert.ThrowsException<ArgumentException>(
                () => ObservableStateCopier.CopyState<object>(new DerivedState(), new OtherState()));
        }

        [TestMethod]
        public void GetStateFields_ExcludesDelegatesAndReadonlyFieldsAndIsCached()
        {
            var fields = ObservableStateCopier.GetStateFields(typeof(DerivedState));

            Assert.IsFalse(
                fields.Any(f => typeof(Delegate).IsAssignableFrom(f.FieldType)),
                "Event backing fields must never be copied.");
            Assert.IsFalse(fields.Any(f => f.IsInitOnly), "Readonly fields must never be copied.");

            Assert.IsTrue(fields.Any(f => f.Name.Contains("_number")));
            Assert.IsTrue(
                fields.Any(f => f.Name.Contains("Flag")),
                "Auto-property backing fields must be included.");
            Assert.IsTrue(
                fields.Any(f => f.Name.Contains("_basePrivate")),
                "Base-class private fields must be included.");

            Assert.AreSame(
                fields,
                ObservableStateCopier.GetStateFields(typeof(DerivedState)),
                "Resolved once per type: this runs per row on a reload.");
        }

        [TestMethod]
        public void StateEquals_IsTrueOnlyWhenEveryFieldMatches()
        {
            var left = Populated();
            var right = Populated();

            Assert.IsTrue(
                ObservableStateCopier.StateEquals(left, right),
                "Two instances built the same way hold the same state, so refreshing one from " +
                "the other would notify a bound view for nothing.");

            right.Number = 43;
            Assert.IsFalse(ObservableStateCopier.StateEquals(left, right));
        }

        [TestMethod]
        public void StateEquals_ComparesTheSameFieldsTheCopyAssigns()
        {
            // The two must agree, or a row could compare equal and then be changed by the copy,
            // leaving the view showing a value nothing announced.
            var source = Populated();
            var target = new DerivedState();

            Assert.IsFalse(ObservableStateCopier.StateEquals(target, source));
            ObservableStateCopier.CopyState(target, source);
            Assert.IsTrue(
                ObservableStateCopier.StateEquals(target, source),
                "A copy must leave the two comparing equal; if it does not, the diff and the " +
                "copy disagree about what state is.");
        }

        [TestMethod]
        public void StateEquals_IgnoresEventSubscribersAndReadonlyFields()
        {
            var left = Populated();
            var right = Populated();

            right.Changed += (_, __) => { };
            right.ReadonlyList.Add("only-here");

            Assert.IsTrue(
                ObservableStateCopier.StateEquals(left, right),
                "Neither is state the copy would transfer, so neither may make two rows look " +
                "different and force a needless notification.");
        }

        [TestMethod]
        public void StateEquals_HandlesNullsAndMismatchedTypes()
        {
            var populated = Populated();

            Assert.IsTrue(ObservableStateCopier.StateEquals<object>(null, null));
            Assert.IsFalse(ObservableStateCopier.StateEquals<object>(populated, null));
            Assert.IsFalse(ObservableStateCopier.StateEquals<object>(null, populated));
            Assert.IsFalse(ObservableStateCopier.StateEquals<object>(populated, new OtherState()));
            Assert.IsTrue(ObservableStateCopier.StateEquals(populated, populated));
        }

        [TestMethod]
        public void TheEditorRow_UsesTheCopierAndAnnouncesEveryProperty()
        {
            // AchievementEditorRow is not linked into this project, so the delegation itself is
            // asserted against the source; the mechanism above is what carries the coverage.
            var source = ReadEditorViewModel();

            StringAssert.Contains(
                source,
                "Common.ObservableStateCopier.CopyState(this, source)",
                "The row must not grow its own hand-written field copy, which would drift from " +
                "its fields as they are added.");
            StringAssert.Contains(
                source,
                "OnPropertyChanged(string.Empty)",
                "An empty name is how WPF is told every property changed; without it the grid " +
                "keeps showing the pre-reset values.");
        }

        private static string ReadEditorViewModel()
        {
            var parts = new[]
            {
                "source", "ViewModels", "ManageAchievements", "ManageAchievementsEditorViewModel.cs"
            };

            var directory = new System.IO.DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var path = System.IO.Path.Combine(
                    new[] { directory.FullName }.Concat(parts).ToArray());
                if (System.IO.File.Exists(path))
                {
                    return System.IO.File.ReadAllText(path);
                }

                directory = directory.Parent;
            }

            Assert.Fail("Could not find ManageAchievementsEditorViewModel.cs.");
            return null;
        }
    }
}
