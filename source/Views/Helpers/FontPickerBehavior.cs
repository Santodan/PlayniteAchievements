using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

            combo.SetValue(StateProperty, new SearchState());

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
        }

        private static void Detach(ComboBox combo)
        {
            combo.RemoveHandler(
                TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnTextChanged));
            combo.DropDownClosed -= OnDropDownClosed;
            combo.SelectionChanged -= OnSelectionChanged;
            ClearFilter(combo);
            combo.SetValue(StateProperty, null);
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

            state.Query = query;
            ApplyFilter(combo, state);

            // Typing with the list closed would filter invisibly.
            if (!combo.IsDropDownOpen && combo.IsKeyboardFocusWithin)
            {
                combo.IsDropDownOpen = true;
            }
        }

        private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(sender is ComboBox combo)
                || !(combo.GetValue(StateProperty) is SearchState state)
                || state.Suppress)
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

            state.Query = string.Empty;
            ClearFilter(combo);

            // An abandoned partial query would otherwise sit in the box looking like the chosen
            // font; restore the text the selection implies.
            RestoreSelectionText(combo, state);
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
            });
        }

        private static void ClearFilter(ComboBox combo)
        {
            if (!(combo.GetValue(StateProperty) is SearchState state))
            {
                return;
            }

            RunSuppressed(combo, state, () => combo.Items.Filter = null);
        }

        /// <summary>
        /// Refreshing the view makes the ComboBox re-derive its editable text from the selection,
        /// which would wipe the half-typed query, so the caret and text are put back afterwards.
        /// </summary>
        private static void RunSuppressed(ComboBox combo, SearchState state, Action action)
        {
            var editBox = combo.Template?.FindName("PART_EditableTextBox", combo) as TextBox;
            var text = editBox?.Text;
            var caret = editBox?.CaretIndex ?? 0;

            var wasSuppressed = state.Suppress;
            state.Suppress = true;
            try
            {
                action();
            }
            catch (InvalidOperationException)
            {
                // The view does not support filtering; the picker still works unfiltered.
            }
            finally
            {
                state.Suppress = wasSuppressed;
            }

            if (editBox != null && text != null && !string.Equals(editBox.Text, text, StringComparison.Ordinal))
            {
                editBox.Text = text;
                editBox.CaretIndex = Math.Min(caret, editBox.Text.Length);
            }
        }

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
