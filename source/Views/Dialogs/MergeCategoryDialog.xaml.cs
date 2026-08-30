using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Views.Dialogs
{
    public partial class MergeCategoryDialog : UserControl
    {
        /// <summary>Leaf of the category being merged away, which is what the dialog shows.</summary>
        public string SourceDisplay
        {
            get => (string)GetValue(SourceDisplayProperty);
            set => SetValue(SourceDisplayProperty, value);
        }

        public static readonly DependencyProperty SourceDisplayProperty =
            DependencyProperty.Register(
                nameof(SourceDisplay),
                typeof(string),
                typeof(MergeCategoryDialog),
                new PropertyMetadata(string.Empty));

        /// <summary>Full display path of the source, offered on hover.</summary>
        public string SourcePathDisplay
        {
            get => (string)GetValue(SourcePathDisplayProperty);
            set => SetValue(SourcePathDisplayProperty, value);
        }

        public static readonly DependencyProperty SourcePathDisplayProperty =
            DependencyProperty.Register(
                nameof(SourcePathDisplay),
                typeof(string),
                typeof(MergeCategoryDialog),
                new PropertyMetadata(string.Empty));

        public IReadOnlyList<CategoryPickerOption> TargetOptions
        {
            get => (IReadOnlyList<CategoryPickerOption>)GetValue(TargetOptionsProperty);
            set => SetValue(TargetOptionsProperty, value);
        }

        public static readonly DependencyProperty TargetOptionsProperty =
            DependencyProperty.Register(
                nameof(TargetOptions),
                typeof(IReadOnlyList<CategoryPickerOption>),
                typeof(MergeCategoryDialog),
                new PropertyMetadata(Array.Empty<CategoryPickerOption>()));

        public CategoryPickerOption SelectedOption
        {
            get => (CategoryPickerOption)GetValue(SelectedOptionProperty);
            set => SetValue(SelectedOptionProperty, value);
        }

        public static readonly DependencyProperty SelectedOptionProperty =
            DependencyProperty.Register(
                nameof(SelectedOption),
                typeof(CategoryPickerOption),
                typeof(MergeCategoryDialog),
                new PropertyMetadata(null));

        /// <summary>
        /// Storage label of the chosen target, for the caller to merge into. Read off the selected
        /// option rather than bound to the box: the box shows leaves, and two categories can share
        /// one, so the displayed text is not enough to identify the target.
        /// </summary>
        public string SelectedTarget => SelectedOption?.Label;

        public bool? DialogResult { get; private set; }

        public event EventHandler RequestClose;

        public MergeCategoryDialog(string sourceLabel, IEnumerable<string> targetOptions)
        {
            InitializeComponent();

            // The source is withheld from the input rather than filtered out of the result, so
            // the tree rebuilds it as structure: its children keep a parent to hang off, and it is
            // drawn in place without being offered as its own merge target.
            //
            // No sort: the caller passes the rows in the order the category grid shows them, and
            // the tree build keeps that order within each level.
            var options = CategoryPickerResolver.BuildOptions(
                (targetOptions ?? Enumerable.Empty<string>())
                    .Where(label => !string.IsNullOrWhiteSpace(label))
                    .Where(label => !CategoryPathHelper.IsSame(label, sourceLabel)),
                synthesizedAreSelectable: false);

            // Display forms only. The stored label carries the internal path separator, which is
            // never shown to a user.
            SourceDisplay = AchievementCategoryTypeHelper.ToCategoryLeafDisplayText(sourceLabel);
            SourcePathDisplay = AchievementCategoryTypeHelper.ToCategoryLabelDisplayText(sourceLabel);
            TargetOptions = options;
            SelectedOption = options.FirstOrDefault(option => option.IsSelectable);

            DataContext = this;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SelectedTarget))
            {
                return;
            }

            DialogResult = true;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void TargetComboBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(SelectedTarget))
            {
                DialogResult = true;
                RequestClose?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                DialogResult = false;
                RequestClose?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        }
    }
}
