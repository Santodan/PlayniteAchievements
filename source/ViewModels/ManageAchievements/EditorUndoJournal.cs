using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// What kind of gesture a write belongs to. Writes carrying the same intent are one undo step.
    /// </summary>
    public enum EditorEditKind
    {
        /// <summary>A field edited in the grid or the details pane.</summary>
        FieldEdit,

        /// <summary>A command or menu pick, named by <see cref="EditorEditIntent.Name"/>.</summary>
        Command,

        /// <summary>
        /// A gesture that writes several facets in sequence and must stay one step, such as
        /// reverting or resetting. Never merges with the gesture before or after it.
        /// </summary>
        Atomic
    }

    /// <summary>
    /// The gesture a write belongs to: its kind, and a name that distinguishes one gesture of that
    /// kind from another.
    /// </summary>
    public sealed class EditorEditIntent : IEquatable<EditorEditIntent>
    {
        public EditorEditIntent(EditorEditKind kind, string name, string labelKey)
        {
            Kind = kind;
            Name = name ?? string.Empty;
            LabelKey = labelKey;
        }

        public EditorEditKind Kind { get; }

        /// <summary>
        /// What separates two gestures of the same kind - the property name for a field edit, the
        /// command for a command. Editing points and then trophy is two steps, not one.
        /// </summary>
        public string Name { get; }

        /// <summary>The localization key naming this gesture on the undo button.</summary>
        public string LabelKey { get; }

        public bool Equals(EditorEditIntent other)
        {
            return other != null &&
                Kind == other.Kind &&
                string.Equals(Name, other.Name, StringComparison.Ordinal);
        }

        public override bool Equals(object obj) => Equals(obj as EditorEditIntent);

        public override int GetHashCode()
        {
            return ((int)Kind * 397) ^ (Name?.GetHashCode() ?? 0);
        }

        public static EditorEditIntent FieldEdit(string propertyName, string labelKey)
        {
            return new EditorEditIntent(EditorEditKind.FieldEdit, propertyName, labelKey);
        }

        public static EditorEditIntent Command(string name, string labelKey)
        {
            return new EditorEditIntent(EditorEditKind.Command, name, labelKey);
        }

        public static EditorEditIntent Atomic(string name, string labelKey)
        {
            return new EditorEditIntent(EditorEditKind.Atomic, name, labelKey);
        }
    }

    /// <summary>
    /// One field on one achievement, and what it held on each side of a change.
    /// </summary>
    /// <remarks>
    /// The grain a field edit actually has. Reversing one is setting the property back through the
    /// same setter the edit used, which makes the undo an edit - the same cost, the same write
    /// path, and no way for it to disturb a field it never recorded.
    ///
    /// The values are whatever the row held: a string, a number, a bool, a date. Nothing here
    /// refers to a row or a view model, so a history still holds only data.
    /// </remarks>
    public sealed class EditorRowValueChange
    {
        public EditorRowValueChange(string apiName, string propertyName, object oldValue, object newValue)
        {
            ApiName = apiName;
            PropertyName = propertyName;
            OldValue = oldValue;
            NewValue = newValue;
        }

        public string ApiName { get; }

        public string PropertyName { get; }

        public object OldValue { get; }

        /// <summary>What the field became, for redo.</summary>
        public object NewValue { get; }

        internal EditorRowValueChange WithNewValue(object newValue)
        {
            return new EditorRowValueChange(ApiName, PropertyName, OldValue, newValue);
        }
    }

    /// <summary>
    /// One undoable step: the facets a gesture moved, and the flags to report when it is reversed.
    /// </summary>
    /// <remarks>
    /// Holds plain data only - a localization key, two booleans, deep-copied facet values and a
    /// set of ApiNames. No delegates and no reference to a row or a view model, which is what lets
    /// a history outlive a grid without keeping the grid alive. A command-object history could not
    /// make that promise, because its undo closures would capture the rows they were built from.
    /// </remarks>
    public sealed class EditorUndoEntry
    {
        internal EditorUndoEntry(
            string labelKey,
            IReadOnlyList<GameCustomDataFacetPatch> facets,
            bool affectsSummaryData,
            bool affectsOverrideMirror,
            IReadOnlyList<string> affectedApiNames,
            IReadOnlyList<EditorRowValueChange> rowValues = null)
        {
            LabelKey = labelKey;
            Facets = facets ?? new List<GameCustomDataFacetPatch>();
            AffectsSummaryData = affectsSummaryData;
            AffectsOverrideMirror = affectsOverrideMirror;
            AffectedApiNames = affectedApiNames ?? new List<string>();
            RowValues = rowValues ?? new List<EditorRowValueChange>();
        }

        /// <summary>
        /// The fields this step changed, when it was a field edit. Reversed by setting each one
        /// back, rather than by restoring a whole stored record and reloading the grid.
        /// </summary>
        public IReadOnlyList<EditorRowValueChange> RowValues { get; }

        /// <summary>Whether this step is reversed field by field rather than record by record.</summary>
        public bool IsRowValueStep => RowValues.Count > 0;

        /// <summary>The localization key naming the gesture, for the undo button's tooltip.</summary>
        public string LabelKey { get; }

        public IReadOnlyList<GameCustomDataFacetPatch> Facets { get; }

        /// <summary>
        /// The flags to repeat when this step is reversed. Folded across the writes in the step,
        /// so a reversal reports a change at least as significant as the original - never less,
        /// which would leave a mirror stale.
        /// </summary>
        public bool AffectsSummaryData { get; }

        public bool AffectsOverrideMirror { get; }

        /// <summary>
        /// The achievements this step touched, for reselecting them after it is reversed. Names
        /// rather than rows, so the entry holds nothing that could keep a grid alive.
        /// </summary>
        public IReadOnlyList<string> AffectedApiNames { get; }

        public int ElementCount => Facets.Sum(patch => patch.ElementCount) + RowValues.Count;

        /// <summary>The facets this step would rewrite, for deciding whether a foreign write invalidates it.</summary>
        internal IEnumerable<GameCustomDataFacet> TouchedFacets =>
            IsRowValueStep
                // A field edit lands in the per-achievement record, so a write from elsewhere that
                // moves it puts this step in doubt just the same.
                ? new[] { GameCustomDataFacet.AchievementOverrides }
                : Facets.Select(patch => patch.Facet);
    }

    /// <summary>
    /// The achievement editor's undo history.
    /// </summary>
    /// <remarks>
    /// The editor persists as it goes - there is no Save or Revert on this window - so undo here
    /// rewinds state that is already stored rather than discarding a buffer. Every write funnels
    /// through one place in the store, which hands over the record before and after it, so the
    /// history is built by recording those pairs rather than by each edit site describing its own
    /// inverse.
    ///
    /// Writes are grouped into steps by the gesture they came from: a fan-out edit writes once per
    /// selected row and a reset writes several facets in turn, and each has to undo in one press.
    /// A step stays open until a different gesture arrives or the caller closes it.
    ///
    /// Pure by design - no dispatcher, no WPF, no plugin services - so the grouping rules can be
    /// tested directly. The caller owns the timer that closes an idle step.
    /// </remarks>
    public sealed class EditorUndoJournal
    {
        /// <summary>
        /// How many steps to keep. Matches the dashboard's own history depth.
        /// </summary>
        public const int MaxDepth = 50;

        /// <summary>
        /// A ceiling on what the history holds, counted in list and map entries across both sides
        /// of every step. The depth cap alone is not a memory bound, because one step can carry a
        /// heavily customized game's whole override map.
        /// </summary>
        public const int MaxElements = 20000;

        private readonly List<EditorUndoEntry> _undo = new List<EditorUndoEntry>();
        private readonly List<EditorUndoEntry> _redo = new List<EditorUndoEntry>();

        /// <summary>
        /// The fields changed in the open step, keyed by achievement and property so the earliest
        /// value each one held survives a gesture that writes it more than once.
        /// </summary>
        private readonly Dictionary<string, EditorRowValueChange> _openRowValues =
            new Dictionary<string, EditorRowValueChange>(StringComparer.OrdinalIgnoreCase);

        private EditorEditIntent _openIntent;
        private GameCustomDataFile _openBefore;
        private GameCustomDataFile _openAfter;
        private bool _openAffectsSummaryData;
        private bool _openAffectsOverrideMirror;
        private readonly List<string> _openApiNames = new List<string>();

        public bool CanUndo => _undo.Count > 0;

        public bool CanRedo => _redo.Count > 0;

        /// <summary>Whether a step is being collected, so a caller knows an idle timer is wanted.</summary>
        public bool HasOpenStep => _openIntent != null;

        public string UndoLabelKey => _undo.Count > 0 ? _undo[_undo.Count - 1].LabelKey : null;

        public string RedoLabelKey => _redo.Count > 0 ? _redo[_redo.Count - 1].LabelKey : null;

        /// <summary>
        /// Records a write against the gesture it came from.
        /// </summary>
        /// <remarks>
        /// A null <paramref name="previous"/> means the state before the write is unknown rather
        /// than empty, so nothing can be said about how to reverse it: the history is dropped
        /// rather than left holding a step that would restore the wrong thing.
        /// </remarks>
        public void Record(
            GameCustomDataFile previous,
            GameCustomDataFile persisted,
            bool affectsSummaryData,
            bool affectsOverrideMirror,
            EditorEditIntent intent,
            IEnumerable<string> affectedApiNames = null)
        {
            if (persisted == null)
            {
                return;
            }

            if (previous == null)
            {
                Clear();
                return;
            }

            if (intent == null)
            {
                // A write nobody claimed: something outside the editor, or an edit site that was
                // never labelled. Either way it is not ours to undo.
                NoteForeignWrite(previous, persisted);
                return;
            }

            var opened = EnsureOpenStep(intent);
            if (opened || _openBefore == null)
            {
                // Cloned, not held: the store caches the record it just wrote and the next write
                // mutates that same instance in place, so a reference here would drift under us
                // before the step is closed. Also set when the step was opened by a field change,
                // which happens before any write.
                _openBefore = previous.Clone();
                _openAffectsSummaryData = affectsSummaryData;
                _openAffectsOverrideMirror = affectsOverrideMirror;
            }
            else
            {
                // Folded, never narrowed: a reversal has to resync everything the gesture did.
                _openAffectsSummaryData |= affectsSummaryData;
                _openAffectsOverrideMirror |= affectsOverrideMirror;
            }

            // The step spans from the first write's starting point to the latest write's result,
            // which is what makes a fan-out across many rows one entry.
            _openAfter = persisted.Clone();

            if (affectedApiNames != null)
            {
                foreach (var apiName in affectedApiNames)
                {
                    if (!string.IsNullOrWhiteSpace(apiName) &&
                        !_openApiNames.Contains(apiName, StringComparer.OrdinalIgnoreCase))
                    {
                        _openApiNames.Add(apiName);
                    }
                }
            }
        }

        /// <summary>
        /// Makes sure a step is open for this gesture, closing a different one first. Returns
        /// whether a new step was opened.
        /// </summary>
        /// <remarks>
        /// Either kind of recording can be the first thing a gesture does: a field change is seen
        /// in the setter, before the write it causes, while everything else is only seen when the
        /// write lands.
        /// </remarks>
        private bool EnsureOpenStep(EditorEditIntent intent)
        {
            // An atomic gesture stands alone, and any change of gesture closes what was open.
            if (_openIntent != null &&
                (!_openIntent.Equals(intent) ||
                    _openIntent.Kind == EditorEditKind.Atomic ||
                    intent.Kind == EditorEditKind.Atomic))
            {
                CommitOpenStep();
            }

            if (_openIntent != null)
            {
                return false;
            }

            _openIntent = intent;
            _openApiNames.Clear();
            return true;
        }

        /// <summary>
        /// Records one field's change against the open step.
        /// </summary>
        /// <remarks>
        /// Only for a field edit. The other gestures rewrite whole stored records - a reset clears
        /// several at once, a reorder rewrites a list - and are reversed from those records
        /// instead.
        ///
        /// The first value a field held in the step is the one kept, so a gesture that writes the
        /// same field repeatedly still reverses to where it started.
        /// </remarks>
        public void RecordRowValue(
            string apiName,
            string propertyName,
            object oldValue,
            object newValue,
            EditorEditIntent intent)
        {
            if (intent == null ||
                intent.Kind != EditorEditKind.FieldEdit ||
                string.IsNullOrWhiteSpace(apiName) ||
                string.IsNullOrWhiteSpace(propertyName))
            {
                return;
            }

            // Opens the step itself rather than waiting for one. The setter runs before the write
            // it causes, so this is the first the history hears of the gesture - requiring an open
            // step here is what made every field edit fall through to the record-level path.
            EnsureOpenStep(intent);

            var key = apiName + " " + propertyName;
            _openRowValues[key] = _openRowValues.TryGetValue(key, out var existing)
                ? existing.WithNewValue(newValue)
                : new EditorRowValueChange(apiName, propertyName, oldValue, newValue);
        }

        /// <summary>
        /// Closes the step being collected and puts it on the history. Called when the writes stop
        /// arriving, and before an undo or redo so the step just made is on the stack.
        /// </summary>
        public void CommitOpenStep()
        {
            if (_openIntent == null)
            {
                return;
            }

            var labelKey = _openIntent.LabelKey;
            var summary = _openAffectsSummaryData;
            var mirror = _openAffectsOverrideMirror;
            var apiNames = new List<string>(_openApiNames);

            // A field edit is held as the fields it moved. Reversing those is an edit, where
            // restoring the record they live in and reloading is not, and costs the same whether
            // one achievement changed or the whole game.
            var rowValues = _openRowValues.Count > 0
                ? _openRowValues.Values
                    .Where(change => !Equals(change.OldValue, change.NewValue))
                    .ToList()
                : new List<EditorRowValueChange>();

            // Only when the step is not already held as row values. The record diff walks both
            // sides of every facet and compares each entry by serializing it, so running it for
            // a step that is about to discard it charged every ordinary field edit for a
            // whole-record comparison nothing read.
            var facets = rowValues.Count > 0
                ? (IReadOnlyList<GameCustomDataFacetPatch>)Array.Empty<GameCustomDataFacetPatch>()
                : GameCustomDataFacetDiffer.Diff(_openBefore, _openAfter);

            ClearOpenStep();

            // A gesture that ended where it started is not a step. Typing a value and typing it
            // back is the ordinary way this happens.
            if (rowValues.Count == 0 && facets.Count == 0)
            {
                return;
            }

            _undo.Add(rowValues.Count > 0
                ? new EditorUndoEntry(labelKey, null, summary, mirror, apiNames, rowValues)
                : new EditorUndoEntry(labelKey, facets, summary, mirror, apiNames));

            // A new step makes the forward history unreachable, as it does in any editor.
            _redo.Clear();

            Trim();
        }

        /// <summary>
        /// Takes the most recent step off the history. The caller applies its before-values and
        /// then reports the write, and the step moves to the redo side.
        /// </summary>
        public EditorUndoEntry Undo()
        {
            CommitOpenStep();
            if (_undo.Count == 0)
            {
                return null;
            }

            var entry = _undo[_undo.Count - 1];
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(entry);
            return entry;
        }

        /// <summary>
        /// Takes the most recently undone step back off the redo side, for the caller to re-apply.
        /// </summary>
        public EditorUndoEntry Redo()
        {
            CommitOpenStep();
            if (_redo.Count == 0)
            {
                return null;
            }

            var entry = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(entry);
            return entry;
        }

        /// <summary>
        /// Accounts for a write this journal did not record - another tab, a refresh, the Overview.
        /// </summary>
        /// <remarks>
        /// Only the steps that overlap it are in doubt, because every step assumes it knows what
        /// the facets it holds contained. Clearing on any foreign write at all would empty the
        /// history on almost every refresh, since most writes touch nothing the editor edits.
        /// </remarks>
        public void NoteForeignWrite(GameCustomDataFile previous, GameCustomDataFile persisted)
        {
            if (previous == null || persisted == null)
            {
                Clear();
                return;
            }

            var moved = GameCustomDataFacetDiffer.Diff(previous, persisted)
                .Select(patch => patch.Facet)
                .ToList();
            if (moved.Count == 0)
            {
                return;
            }

            if (_undo.Any(entry => entry.TouchedFacets.Any(moved.Contains)) ||
                _redo.Any(entry => entry.TouchedFacets.Any(moved.Contains)))
            {
                Clear();
            }
        }

        public void Clear()
        {
            ClearOpenStep();
            _undo.Clear();
            _redo.Clear();
        }

        private void ClearOpenStep()
        {
            _openIntent = null;
            _openBefore = null;
            _openAfter = null;
            _openAffectsSummaryData = false;
            _openAffectsOverrideMirror = false;
            _openApiNames.Clear();
            _openRowValues.Clear();
        }

        /// <summary>
        /// Drops the oldest steps until the history is inside both caps.
        /// </summary>
        private void Trim()
        {
            while (_undo.Count > MaxDepth)
            {
                _undo.RemoveAt(0);
            }

            var elements = _undo.Sum(entry => entry.ElementCount);
            while (_undo.Count > 1 && elements > MaxElements)
            {
                elements -= _undo[0].ElementCount;
                _undo.RemoveAt(0);
            }
        }
    }
}
