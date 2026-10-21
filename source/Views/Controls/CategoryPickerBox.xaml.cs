using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Picks one of a game's categories, and, when the host handles <see cref="CreateRequested"/>,
    /// offers to create the one a search found nothing for.
    ///
    /// The box is select-only. Naming a category by typing into it was both the slow path - WPF runs
    /// its text-search pipeline over every option on each keystroke of an editable ComboBox - and a
    /// silent one, since anything that did not match an existing leaf became a new root category. A
    /// category is now either picked from the list or created deliberately through the drop-down's
    /// search, whose create result names it.
    ///
    /// Selection is still read on demand through <see cref="ResolveSelection"/>: the host decides
    /// when a pick is worth committing, and there is no intermediate state any caller wants.
    /// </summary>
    public partial class CategoryPickerBox : UserControl
    {
        private List<CategoryPickerOption> _options = new List<CategoryPickerOption>();
        private INotifyCollectionChanged _observedCategories;
        private CategoryPickerOption _lastPickedOption;
        private bool _isSyncingSelection;

        public CategoryPickerBox()
        {
            InitializeComponent();
            PickerComboBox.SelectionChanged += PickerComboBox_SelectionChanged;
            Helpers.SelectorSearch.SetCreateAction(PickerComboBox, RequestCreate);
            Helpers.SelectorSearch.SetSearchHandler(PickerComboBox, ShowSearchResults);

            // A TabControl unloads the tab this box sits on, so the watcher has to come back with
            // it: the bound collection is delivered once and never replaced, which left a box that
            // had been unloaded showing the categories that existed when the user last left it.
            Loaded += (_, __) =>
            {
                AttachCategoriesWatcher(Categories as INotifyCollectionChanged);
                RebuildOptions();
            };
            Unloaded += (_, __) => DetachCategoriesWatcher();
        }

        /// <summary>Raised when the user picks a category, so a host can apply it straight away.</summary>
        public event EventHandler SelectionCommitted;

        /// <summary>
        /// Raised with the name the user searched for when they pick the create result. Without a
        /// handler the result is not offered.
        /// </summary>
        public event EventHandler<string> CreateRequested;

        /// <summary>Raised when the user commits with Enter, so a hosting dialog can accept.</summary>
        public event EventHandler Committed;

        /// <summary>Raised when the user presses Escape, so a hosting dialog can cancel.</summary>
        public event EventHandler Cancelled;

        public static readonly DependencyProperty CategoriesProperty =
            DependencyProperty.Register(
                nameof(Categories),
                typeof(IEnumerable<string>),
                typeof(CategoryPickerBox),
                new PropertyMetadata(null, OnCategoriesChanged));

        /// <summary>Existing category labels in storage form; order is preserved in the list.</summary>
        public IEnumerable<string> Categories
        {
            get => (IEnumerable<string>)GetValue(CategoriesProperty);
            set => SetValue(CategoriesProperty, value);
        }

        public static readonly DependencyProperty PlaceholderProperty =
            DependencyProperty.Register(
                nameof(Placeholder),
                typeof(string),
                typeof(CategoryPickerBox),
                new PropertyMetadata(string.Empty));

        public string Placeholder
        {
            get => (string)GetValue(PlaceholderProperty);
            set => SetValue(PlaceholderProperty, value);
        }

        private static void OnCategoriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CategoryPickerBox box)
            {
                box.AttachCategoriesWatcher(e.NewValue as INotifyCollectionChanged);
                box.RebuildOptions();
            }
        }

        // Read when the search finds nothing, so a host that subscribes later is still offered.
        private void RequestCreate(string name)
        {
            CreateRequested?.Invoke(this, name);
        }

        /// <summary>
        /// Lists the categories whose name holds <paramref name="query"/>, each under the categories
        /// above it, as a tree of its own so the connectors join what is shown; "" puts the full
        /// list back. Returns whether any category matched.
        /// </summary>
        private bool ShowSearchResults(string query)
        {
            List<CategoryPickerOption> shown;
            var matched = true;
            if (string.IsNullOrEmpty(query))
            {
                shown = _options;
            }
            else
            {
                var labels = _options
                    .Where(option => option.IsSelectable &&
                                     (option.LeafDisplay ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(option => option.Label)
                    .ToList();
                matched = labels.Count > 0;

                // The parents come back from the tree build; the full list's order keeps them where
                // they were.
                shown = CategoryPickerResolver.BuildOptions(
                    labels,
                    _options.Select(option => option.Label).ToList(),
                    synthesizedAreSelectable: true);
            }

            if (!ReferenceEquals(PickerComboBox.ItemsSource, shown))
            {
                var carriedLabel = _lastPickedOption?.Label;
                _isSyncingSelection = true;
                try
                {
                    PickerComboBox.ItemsSource = shown;
                    PickerComboBox.SelectedItem = carriedLabel == null
                        ? null
                        : shown.FirstOrDefault(option => CategoryPathHelper.IsSame(option.Label, carriedLabel));
                }
                finally
                {
                    _isSyncingSelection = false;
                }
            }

            return matched;
        }

        /// <summary>
        /// Follows the bound collection contents, not just the reference it was set to. Hosts fill
        /// their options list in place, so the property-changed callback fires once with an empty
        /// collection and never again - which left a category created while the pane was open
        /// missing from the list until the window was reopened.
        /// </summary>
        private void AttachCategoriesWatcher(INotifyCollectionChanged categories)
        {
            DetachCategoriesWatcher();
            _observedCategories = categories;
            if (_observedCategories != null)
            {
                _observedCategories.CollectionChanged += Categories_CollectionChanged;
            }
        }

        private void DetachCategoriesWatcher()
        {
            if (_observedCategories != null)
            {
                _observedCategories.CollectionChanged -= Categories_CollectionChanged;
                _observedCategories = null;
            }
        }

        private void Categories_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            RebuildOptions();
        }

        /// <summary>
        /// Seeds the box with a category already in effect. Nothing is selected when the label has no
        /// row - a bulk selection whose rows disagree has none - which is what shows the placeholder.
        /// </summary>
        public void SetInitialCategory(string label)
        {
            // Checked before normalizing: an empty label normalizes to the Default bucket, and
            // clearing the box would otherwise read as filing everything under Default.
            if (string.IsNullOrWhiteSpace(label))
            {
                SelectWithoutCommitting(null);
                return;
            }

            var normalized = CategoryPathHelper.NormalizePath(label);
            CategoryPickerOption match = null;
            foreach (var option in _options)
            {
                if (CategoryPathHelper.IsSame(option.Label, normalized))
                {
                    match = option;
                    break;
                }
            }

            SelectWithoutCommitting(match);
        }

        /// <summary>
        /// The category label to store, or null when nothing is picked. Only a row can be picked, so
        /// this is always an existing category.
        /// </summary>
        public string ResolveSelection()
        {
            var picked = PickerComboBox.SelectedItem as CategoryPickerOption;
            return picked?.Label;
        }

        public void FocusInput()
        {
            PickerComboBox.Focus();
        }

        private void RebuildOptions()
        {
            // Tree order, so the connectors the item template draws line up with the list: an
            // ancestor synthesised to complete the tree stays selectable here, because a category
            // holding no achievements of its own is still somewhere to file one.
            var options = CategoryPickerResolver.BuildOptions(
                Categories,
                synthesizedAreSelectable: true);

            // Nothing to do when the list is the same one: swapping an identical ItemsSource still
            // costs the drop-down every container it had realized, and every assignment refreshes
            // these options whether or not the categories moved.
            if (SameOptions(_options, options))
            {
                return;
            }

            _options = options;
            var carriedLabel = _lastPickedOption?.Label;

            // Reassigning the source drops the selection - the rebuilt rows are new instances - and
            // the selector reports that as a selection change. It is bookkeeping, not a pick, so it
            // is made under the guard: left uncovered it committed an empty category over whatever
            // the user had just filed, which is exactly what a freshly created category triggers.
            _isSyncingSelection = true;
            try
            {
                PickerComboBox.ItemsSource = _options;
            }
            finally
            {
                _isSyncingSelection = false;
            }

            // The label the box was showing goes back when the rebuilt list still has a row for it.
            SetInitialCategory(carriedLabel);
        }

        /// <summary>
        /// Whether two option lists describe the same rows. Shapes are derived from the labels and
        /// their order, so an equal label sequence draws an equal tree.
        /// </summary>
        private static bool SameOptions(List<CategoryPickerOption> left, List<CategoryPickerOption> right)
        {
            if (left == null || right == null || left.Count != right.Count)
            {
                return false;
            }

            for (var i = 0; i < left.Count; i++)
            {
                if (left[i].IsSelectable != right[i].IsSelectable ||
                    !string.Equals(left[i].Label, right[i].Label, StringComparison.Ordinal) ||
                    !string.Equals(left[i].LeafDisplay, right[i].LeafDisplay, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private void SelectWithoutCommitting(CategoryPickerOption option)
        {
            _isSyncingSelection = true;
            try
            {
                PickerComboBox.SelectedItem = option;
            }
            finally
            {
                _isSyncingSelection = false;
            }

            _lastPickedOption = option;
            UpdatePlaceholder();
        }

        private void PickerComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingSelection)
            {
                return;
            }

            var selected = PickerComboBox.SelectedItem as CategoryPickerOption;
            if (selected == null)
            {
                // Nothing in a select-only box can unselect it, so an empty selection is always the
                // option list being rebuilt underneath it. The selector can report that after the
                // fact, once the guard around the rebuild has closed, so the pick that was showing
                // is put back here rather than being read as the user clearing the category.
                SetInitialCategory(_lastPickedOption?.Label);
                return;
            }

            _lastPickedOption = selected;
            UpdatePlaceholder();
            SelectionCommitted?.Invoke(this, EventArgs.Empty);
        }

        private void UpdatePlaceholder()
        {
            PlaceholderText.Visibility = PickerComboBox.SelectedItem == null
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void PickerComboBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Enter while the drop-down is open belongs to the list - it picks the highlighted row.
            if (e.Key == Key.Enter && !PickerComboBox.IsDropDownOpen)
            {
                Committed?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape && !PickerComboBox.IsDropDownOpen)
            {
                Cancelled?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        }
    }
}
