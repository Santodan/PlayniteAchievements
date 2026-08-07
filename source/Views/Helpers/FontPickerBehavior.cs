using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Turns a font-family <see cref="ComboBox"/> into a type-to-filter picker: the user types into
    /// the editable box and the drop-down narrows to matching family names.
    /// </summary>
    /// <remarks>
    /// Implemented as an attached behaviour rather than a <see cref="ComboBox"/> subclass because
    /// WPF implicit styles key on the exact type, so a derived control would silently lose
    /// PlayAch.ComboBox.BaseStyle (which is also what supplies PART_EditableTextBox).
    ///
    /// Filtering goes through <see cref="ItemCollection.Filter"/> rather than by swapping in a
    /// private <see cref="System.Windows.Data.CollectionViewSource"/>: reassigning ItemsSource
    /// re-evaluates selection, and a transient null SelectedItem would travel back through the
    /// TwoWay binding and erase the stored font. For the same reason the predicate always keeps the
    /// selected item visible — a Selector whose SelectedItem leaves its items writes back null.
    /// </remarks>
    internal static class FontPickerBehavior
    {
        public static readonly DependencyProperty EnableSearchProperty =
            DependencyProperty.RegisterAttached(
                "EnableSearch",
                typeof(bool),
                typeof(FontPickerBehavior),
                new PropertyMetadata(false, OnEnableSearchChanged));

        public static void SetEnableSearch(DependencyObject element, bool value)
        {
            element.SetValue(EnableSearchProperty, value);
        }

        public static bool GetEnableSearch(DependencyObject element)
        {
            return (bool)element.GetValue(EnableSearchProperty);
        }

        private static readonly DependencyProperty StateProperty =
            DependencyProperty.RegisterAttached(
                "State",
                typeof(SearchState),
                typeof(FontPickerBehavior));

        /// <summary>
        /// Per-ComboBox filter state. The option lists are shared (both surface editors and every
        /// recycled DataGrid cell read the same cached list), so the query has to live with the
        /// control, not with the list.
        /// </summary>
        private sealed class SearchState
        {
            public string Query = string.Empty;

            /// <summary>
            /// Set while the behaviour itself is refreshing the view, so the resulting
            /// TextChanged/SelectionChanged echoes are not treated as the user typing.
            /// </summary>
            public bool Suppress;

            /// <summary>
            /// Set while handling DropDownClosed. Reopening the popup from inside its own closed
            /// handler throws, so no code path may set IsDropDownOpen while this is set.
            /// </summary>
            public bool IsClosing;
        }

        private static void OnEnableSearchChanged(
            DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is ComboBox combo))
            {
                return;
            }

            if (!(e.NewValue is bool enabled) || !enabled)
            {
                Detach(combo);
                return;
            }

            var state = new SearchState();
            combo.SetValue(StateProperty, state);

            combo.IsEditable = true;
            combo.StaysOpenOnEdit = true;
            // The built-in type-ahead would jump the selection to the nearest match on every
            // keystroke, which writes that font to the settings while the user is still typing.
            combo.IsTextSearchEnabled = false;
            TextSearch.SetTextPath(combo, nameof(FontFamilyOption.DisplayName));

            combo.AddHandler(
                TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnTextChanged));
            combo.DropDownClosed += OnDropDownClosed;
            combo.SelectionChanged += OnSelectionChanged;
            combo.Loaded += OnLoaded;

            ApplySelectedPreviewFont(combo);
            RestoreSelectionText(combo, state);
        }

        private static void Detach(ComboBox combo)
        {
            combo.RemoveHandler(
                TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnTextChanged));
            combo.DropDownClosed -= OnDropDownClosed;
            combo.SelectionChanged -= OnSelectionChanged;
            combo.Loaded -= OnLoaded;
            ClearFilter(combo);
            combo.ClearValue(Control.FontFamilyProperty);
            combo.SetValue(StateProperty, null);
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!(sender is ComboBox combo)
                || !(combo.GetValue(StateProperty) is SearchState state))
            {
                return;
            }

            // Recycled DataGrid cells re-run Loaded with a different row's selection.
            ApplySelectedPreviewFont(combo);
            RestoreSelectionText(combo, state);
        }

        /// <summary>
        /// Renders the closed picker's text in the font it names. Making the ComboBox editable hides
        /// the templated selection box, which is what previewed the selected font before, so the
        /// preview is reapplied here as a local FontFamily value.
        /// </summary>
        private static void ApplySelectedPreviewFont(ComboBox combo)
        {
            var preview = (combo.SelectedItem as FontFamilyOption)?.PreviewFamily;
            if (preview == null)
            {
                combo.ClearValue(Control.FontFamilyProperty);
                return;
            }

            combo.FontFamily = preview;
        }

        private static void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (!(sender is ComboBox combo)
                || !(combo.GetValue(StateProperty) is SearchState state)
                || state.Suppress)
            {
                return;
            }

            var query = combo.Text ?? string.Empty;
            if (string.Equals(query, state.Query, StringComparison.Ordinal))
            {
                return;
            }

            // Only the user typing counts as a search. The ComboBox writes into the box itself too -
            // when it turns editable, and after a commit - and treating that as a query filtered the
            // list down to just the already-selected entry, so opening a font dropdown showed one
            // row until it was cleared by hand. Those writes arrive without keyboard focus, or carry
            // exactly the selected item's name.
            var selectedName = (combo.SelectedItem as FontFamilyOption)?.DisplayName;
            if (!combo.IsKeyboardFocusWithin
                || (selectedName != null
                    && string.Equals(query, selectedName, StringComparison.Ordinal)))
            {
                state.Query = string.Empty;
                ClearFilter(combo);
                return;
            }

            state.Query = query;
            ApplyFilter(combo, state);
            OpenDropDownForQuery(combo, state);
        }

        /// <summary>
        /// Shows the narrowed list when the user types with the drop-down closed.
        /// </summary>
        /// <remarks>
        /// Posted rather than set inline. WPF throws "Cannot reopen a popup in the closed event
        /// handler" if IsDropDownOpen is set anywhere inside the popup's close call stack, and a
        /// TextChanged can be raised from within that stack, so the open has to wait until it has
        /// unwound. The re-check is what keeps a just-committed selection from springing back open:
        /// committing clears the query, so a stale post finds nothing to do.
        /// </remarks>
        private static void OpenDropDownForQuery(ComboBox combo, SearchState state)
        {
            if (state.IsClosing || combo.IsDropDownOpen || !combo.IsKeyboardFocusWithin)
            {
                return;
            }

            combo.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (state.IsClosing
                        || combo.IsDropDownOpen
                        || !combo.IsKeyboardFocusWithin
                        || string.IsNullOrEmpty(state.Query)
                        || !string.Equals(state.Query, combo.Text, StringComparison.Ordinal))
                    {
                        return;
                    }

                    combo.IsDropDownOpen = true;
                }),
                DispatcherPriority.Input);
        }

        private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(sender is ComboBox combo)
                || !(combo.GetValue(StateProperty) is SearchState state))
            {
                return;
            }

            ApplySelectedPreviewFont(combo);

            if (state.Suppress)
            {
                return;
            }

            // Committing a font ends the search, so the next open starts from the whole list.
            state.Query = string.Empty;
            ClearFilter(combo);
        }

        private static void OnDropDownClosed(object sender, EventArgs e)
        {
            if (!(sender is ComboBox combo)
                || !(combo.GetValue(StateProperty) is SearchState state))
            {
                return;
            }

            // Everything here runs inside the popup's closed handler, where reopening the popup
            // throws, so the flag keeps any re-entrant TextChanged from doing that.
            state.IsClosing = true;
            try
            {
                state.Query = string.Empty;
                ClearFilter(combo);

                // An abandoned partial query would otherwise sit in the box looking like the chosen
                // font; restore the text the selection implies.
                RestoreSelectionText(combo, state);
            }
            finally
            {
                state.IsClosing = false;
            }
        }

        private static void ApplyFilter(ComboBox combo, SearchState state)
        {
            var query = state.Query;
            var selected = combo.SelectedItem;

            RunSuppressed(combo, state, () =>
            {
                if (string.IsNullOrEmpty(query))
                {
                    combo.Items.Filter = null;
                    return;
                }

                combo.Items.Filter = item =>
                {
                    // Keeping the selection in the view is what prevents a null write-back.
                    if (ReferenceEquals(item, selected))
                    {
                        return true;
                    }

                    var name = (item as FontFamilyOption)?.DisplayName;
                    return !string.IsNullOrEmpty(name)
                           && name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                };
            }, preserveTypedText: true);
        }

        private static void ClearFilter(ComboBox combo)
        {
            if (!(combo.GetValue(StateProperty) is SearchState state))
            {
                return;
            }

            RunSuppressed(combo, state, () => combo.Items.Filter = null, preserveTypedText: false);
        }

        /// <summary>
        /// Runs a view change without the behaviour reacting to the TextChanged and SelectionChanged
        /// echoes it causes, and puts the half-typed query back afterwards.
        /// </summary>
        /// <remarks>
        /// Refreshing the view makes the ComboBox re-derive its editable text from the selection,
        /// which otherwise wipes what the user has typed the moment the filter is applied. The
        /// restore must happen while suppression is still in effect: writing the text raises
        /// TextChanged again, and once that was seen as fresh typing it re-entered the handler and
        /// reopened the drop-down, which throws when the change came from DropDownClosed.
        /// </remarks>
        /// <param name="preserveTypedText">
        /// True while narrowing the list, where the query must survive the refresh. False when
        /// clearing the filter on commit or close, where the ComboBox should be left to put the
        /// chosen font's name in the box itself.
        /// </param>
        private static void RunSuppressed(
            ComboBox combo, SearchState state, Action action, bool preserveTypedText)
        {
            var editBox = preserveTypedText
                ? combo.Template?.FindName("PART_EditableTextBox", combo) as TextBox
                : null;
            var text = editBox?.Text;
            var caret = editBox?.CaretIndex ?? 0;

            var wasSuppressed = state.Suppress;
            state.Suppress = true;
            try
            {
                action();

                if (editBox != null
                    && text != null
                    && !string.Equals(editBox.Text, text, StringComparison.Ordinal))
                {
                    editBox.Text = text;
                    editBox.CaretIndex = Math.Min(caret, editBox.Text.Length);
                }
            }
            catch (InvalidOperationException)
            {
                // The view does not support filtering; the picker still works unfiltered.
            }
            finally
            {
                state.Suppress = wasSuppressed;
            }
        }

        /// <summary>
        /// Puts the selected font's name in the box, discarding any abandoned partial query.
        /// </summary>
        /// <remarks>
        /// Also what gets the name there in the first place: an editable ComboBox derives its text
        /// through <see cref="TextSearch"/>, and when the text is derived before this behaviour sets
        /// the text path the box shows the option object's ToString. Setting it explicitly keeps the
        /// displayed name independent of the order XAML applies the attached property in.
        /// </remarks>
        private static void RestoreSelectionText(ComboBox combo, SearchState state)
        {
            var name = (combo.SelectedItem as FontFamilyOption)?.DisplayName ?? string.Empty;
            if (string.Equals(combo.Text, name, StringComparison.Ordinal))
            {
                return;
            }

            var wasSuppressed = state.Suppress;
            state.Suppress = true;
            try
            {
                combo.Text = name;
            }
            finally
            {
                state.Suppress = wasSuppressed;
            }
        }
    }
}
