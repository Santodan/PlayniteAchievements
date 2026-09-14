using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Picks one of a game's categories, and optionally offers a row that asks the host to create a
    /// new one.
    ///
    /// The box is select-only. Naming a category by typing into it was both the slow path - WPF runs
    /// its text-search pipeline over every option on each keystroke of an editable ComboBox - and a
    /// silent one, since anything that did not match an existing leaf became a new root category. A
    /// category is now either picked from the list or created deliberately through the create row.
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
        /// Raised when the user picks the create row. The host names and creates the category; the
        /// box has already put the previous pick back, so a cancelled prompt changes nothing.
        /// </summary>
        public event EventHandler CreateRequested;

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

        public static readonly DependencyProperty AllowCreateNewProperty =
            DependencyProperty.Register(
                nameof(AllowCreateNew),
                typeof(bool),
                typeof(CategoryPickerBox),
                new PropertyMetadata(false, OnCreateRowChanged));

        /// <summary>
        /// Whether the list opens with a row that requests a new category. Off by default: a picker
        /// whose host cannot create one would offer a row nothing answers.
        /// </summary>
        public bool AllowCreateNew
        {
            get => (bool)GetValue(AllowCreateNewProperty);
            set => SetValue(AllowCreateNewProperty, value);
        }

        public static readonly DependencyProperty CreateNewTextProperty =
            DependencyProperty.Register(
                nameof(CreateNewText),
                typeof(string),
                typeof(CategoryPickerBox),
                new PropertyMetadata(string.Empty, OnCreateRowChanged));

        /// <summary>What the create row reads.</summary>
        public string CreateNewText
        {
            get => (string)GetValue(CreateNewTextProperty);
            set => SetValue(CreateNewTextProperty, value);
        }

        private static void OnCategoriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CategoryPickerBox box)
            {
                box.AttachCategoriesWatcher(e.NewValue as INotifyCollectionChanged);
                box.RebuildOptions();
            }
        }

        private static void OnCreateRowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            (d as CategoryPickerBox)?.RebuildOptions();
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
                if (!option.IsCreateNew && CategoryPathHelper.IsSame(option.Label, normalized))
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
            return picked == null || picked.IsCreateNew ? null : picked.Label;
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

            if (AllowCreateNew)
            {
                options.Insert(0, CategoryPickerOption.CreateNewRow(CreateNewText));
            }

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
                if (left[i].IsCreateNew != right[i].IsCreateNew ||
                    left[i].IsSelectable != right[i].IsSelectable ||
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
            Services.Logging.PluginLogger.GetLogger("CategoryDiag").Debug(
                $"[CategoryDiag] Picker selection changed to '{selected?.Label ?? "<null>"}' " +
                $"(createRow={selected?.IsCreateNew == true}, lastPick='{_lastPickedOption?.Label}', options={_options.Count}).");
            if (selected == null)
            {
                // Nothing in a select-only box can unselect it, so an empty selection is always the
                // option list being rebuilt underneath it. The selector can report that after the
                // fact, once the guard around the rebuild has closed, so the pick that was showing
                // is put back here rather than being read as the user clearing the category.
                SetInitialCategory(_lastPickedOption?.Label);
                return;
            }

            if (selected.IsCreateNew)
            {
                // The create row is an action, not a category: the previous pick goes back before
                // the host is asked, so cancelling the prompt leaves the box as it was.
                SelectWithoutCommitting(_lastPickedOption);
                PickerComboBox.IsDropDownOpen = false;
                CreateRequested?.Invoke(this, EventArgs.Empty);
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
