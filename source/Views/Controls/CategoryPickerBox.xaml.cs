using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Picks an existing category or names a new one, in a single editable box.
    ///
    /// Selection is resolved on demand through <see cref="ResolveSelection"/> rather than tracked
    /// live. An editable ComboBox raises selection and text changes in an order that depends on
    /// whether the user clicked a row, typed, or typed then clicked; reading the final state once,
    /// when the caller is ready to commit, avoids having to unpick that ordering - and there is no
    /// intermediate state any caller wants.
    /// </summary>
    public partial class CategoryPickerBox : UserControl
    {
        private List<CategoryPickerOption> _options = new List<CategoryPickerOption>();

        public CategoryPickerBox()
        {
            InitializeComponent();
            PickerComboBox.SelectionChanged += (_, __) => UpdatePlaceholder();
            PickerComboBox.AddHandler(
                System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                new TextChangedEventHandler((_, __) => UpdatePlaceholder()));
        }

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
                box.RebuildOptions();
            }
        }

        /// <summary>
        /// Seeds the box with a category already in effect, matching it to an existing row where one
        /// exists so the initial state is a selection rather than text that merely looks like one.
        /// </summary>
        public void SetInitialCategory(string label)
        {
            var normalized = CategoryPathHelper.NormalizePath(label);
            foreach (var option in _options)
            {
                if (CategoryPathHelper.IsSame(option.Label, normalized))
                {
                    PickerComboBox.SelectedItem = option;
                    UpdatePlaceholder();
                    return;
                }
            }

            PickerComboBox.SelectedItem = null;
            PickerComboBox.Text = string.IsNullOrWhiteSpace(label)
                ? string.Empty
                : AchievementCategoryTypeHelper.ToCategoryLeafDisplayText(normalized);
            UpdatePlaceholder();
        }

        /// <summary>
        /// The category label to store, or null when the box is empty. A picked row keeps its full
        /// path; typed text resolves to an existing category with that leaf when exactly one has it,
        /// and otherwise names a new root category.
        /// </summary>
        public string ResolveSelection()
        {
            return CategoryPickerResolver.Resolve(
                PickerComboBox.Text,
                PickerComboBox.SelectedItem as CategoryPickerOption,
                _options);
        }

        public void FocusInput()
        {
            PickerComboBox.Focus();
        }

        private void RebuildOptions()
        {
            _options = CategoryPickerResolver.BuildOptions(Categories);
            var carriedText = PickerComboBox.Text;
            PickerComboBox.ItemsSource = _options;
            PickerComboBox.Text = carriedText;
            UpdatePlaceholder();
        }

        private void UpdatePlaceholder()
        {
            PlaceholderText.Visibility = string.IsNullOrEmpty(PickerComboBox.Text)
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
