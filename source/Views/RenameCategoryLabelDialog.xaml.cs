using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PlayniteAchievements.Views
{
    public partial class RenameCategoryLabelDialog : UserControl
    {
        public IEnumerable<string> LabelOptions
        {
            get => (IEnumerable<string>)GetValue(LabelOptionsProperty);
            set => SetValue(LabelOptionsProperty, value);
        }

        public static readonly DependencyProperty LabelOptionsProperty =
            DependencyProperty.Register(
                nameof(LabelOptions),
                typeof(IEnumerable<string>),
                typeof(RenameCategoryLabelDialog),
                new PropertyMetadata(Array.Empty<string>()));

        public string SelectedSourceLabel
        {
            get => (string)GetValue(SelectedSourceLabelProperty);
            set => SetValue(SelectedSourceLabelProperty, value);
        }

        public static readonly DependencyProperty SelectedSourceLabelProperty =
            DependencyProperty.Register(
                nameof(SelectedSourceLabel),
                typeof(string),
                typeof(RenameCategoryLabelDialog),
                new PropertyMetadata(string.Empty));

        public string TargetLabel
        {
            get => (string)GetValue(TargetLabelProperty);
            set => SetValue(TargetLabelProperty, value);
        }

        public static readonly DependencyProperty TargetLabelProperty =
            DependencyProperty.Register(
                nameof(TargetLabel),
                typeof(string),
                typeof(RenameCategoryLabelDialog),
                new PropertyMetadata(string.Empty));

        public bool? DialogResult { get; private set; }

        public event EventHandler RequestClose;

        public RenameCategoryLabelDialog(IEnumerable<string> labelOptions, string preferredSourceLabel = null)
        {
            InitializeComponent();

            var options = (labelOptions ?? Enumerable.Empty<string>())
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(label => label, StringComparer.OrdinalIgnoreCase)
                .ToList();

            LabelOptions = options;
            SelectedSourceLabel = options.FirstOrDefault(label =>
                                     string.Equals(label, preferredSourceLabel, StringComparison.OrdinalIgnoreCase))
                                 ?? options.FirstOrDefault()
                                 ?? string.Empty;
            TargetLabel = SelectedSourceLabel;

            DataContext = this;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void TargetLabelTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
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
