using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Dialogs
{
    /// <summary>
    /// Set-category dialog: pick one the game already has, or name a new one. Replaces the plain
    /// text prompt this used to be, which could only ever create a category and gave no way to see
    /// what already existed.
    /// </summary>
    public partial class CategoryPickerDialog : UserControl
    {
        public string Hint
        {
            get => (string)GetValue(HintProperty);
            set => SetValue(HintProperty, value);
        }

        public static readonly DependencyProperty HintProperty =
            DependencyProperty.Register(nameof(Hint), typeof(string), typeof(CategoryPickerDialog), new PropertyMetadata(string.Empty));

        public IEnumerable<string> Categories
        {
            get => (IEnumerable<string>)GetValue(CategoriesProperty);
            set => SetValue(CategoriesProperty, value);
        }

        public static readonly DependencyProperty CategoriesProperty =
            DependencyProperty.Register(nameof(Categories), typeof(IEnumerable<string>), typeof(CategoryPickerDialog), new PropertyMetadata(null));

        /// <summary>The chosen category in storage form, or null when nothing was entered.</summary>
        public string SelectedCategory { get; private set; }

        public bool? DialogResult { get; private set; }

        public event EventHandler RequestClose;

        public CategoryPickerDialog()
        {
            InitializeComponent();
            DataContext = this;
            PickerBox.Committed += (_, __) => Accept();
            PickerBox.Cancelled += (_, __) => Cancel();
            Loaded += (_, __) => PickerBox.FocusInput();
        }

        public CategoryPickerDialog(string hint, IEnumerable<string> categories, string currentCategory) : this()
        {
            Hint = hint ?? string.Empty;
            Categories = categories;
            PickerBox.SetInitialCategory(currentCategory);
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Accept();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Cancel();
        }

        private void Accept()
        {
            SelectedCategory = PickerBox.ResolveSelection();
            DialogResult = true;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void Cancel()
        {
            DialogResult = false;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
    }
}
