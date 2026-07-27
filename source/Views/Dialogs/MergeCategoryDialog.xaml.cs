using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PlayniteAchievements.Views.Dialogs
{
    public partial class MergeCategoryDialog : UserControl
    {
        public string SourceLabel
        {
            get => (string)GetValue(SourceLabelProperty);
            set => SetValue(SourceLabelProperty, value);
        }

        public static readonly DependencyProperty SourceLabelProperty =
            DependencyProperty.Register(
                nameof(SourceLabel),
                typeof(string),
                typeof(MergeCategoryDialog),
                new PropertyMetadata(string.Empty));

        public IEnumerable<string> TargetOptions
        {
            get => (IEnumerable<string>)GetValue(TargetOptionsProperty);
            set => SetValue(TargetOptionsProperty, value);
        }

        public static readonly DependencyProperty TargetOptionsProperty =
            DependencyProperty.Register(
                nameof(TargetOptions),
                typeof(IEnumerable<string>),
                typeof(MergeCategoryDialog),
                new PropertyMetadata(Array.Empty<string>()));

        public string SelectedTarget
        {
            get => (string)GetValue(SelectedTargetProperty);
            set => SetValue(SelectedTargetProperty, value);
        }

        public static readonly DependencyProperty SelectedTargetProperty =
            DependencyProperty.Register(
                nameof(SelectedTarget),
                typeof(string),
                typeof(MergeCategoryDialog),
                new PropertyMetadata(string.Empty));

        public bool? DialogResult { get; private set; }

        public event EventHandler RequestClose;

        public MergeCategoryDialog(string sourceLabel, IEnumerable<string> targetOptions)
        {
            InitializeComponent();

            var options = (targetOptions ?? Enumerable.Empty<string>())
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Where(label => !string.Equals(label, sourceLabel, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
                .ToList();

            SourceLabel = sourceLabel ?? string.Empty;
            TargetOptions = options;
            SelectedTarget = options.FirstOrDefault() ?? string.Empty;

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
