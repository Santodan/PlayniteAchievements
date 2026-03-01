using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Views
{
    /// <summary>
    /// Interaction logic for ManualAchievementsWizardControl.xaml
    /// </summary>
    public partial class ManualAchievementsWizardControl : UserControl
    {
        private readonly ManualAchievementsWizardViewModel _viewModel;

        public ManualAchievementsWizardControl(ManualAchievementsWizardViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataContext = _viewModel;

            _viewModel.RequestClose += ViewModel_RequestClose;

            // Auto-trigger search when control loads if search text is pre-filled
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;

            if (!_viewModel.IsEditingStage &&
                !string.IsNullOrWhiteSpace(_viewModel.SearchText))
            {
                try
                {
                    await _viewModel.ExecuteSearchAsync();
                }
                catch
                {
                    // Error is handled by ViewModel
                }
            }
        }

        private void ViewModel_RequestClose(object sender, EventArgs e)
        {
            // Handled by parent window
        }

        private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && _viewModel?.SearchCommand.CanExecute(null) == true)
            {
                _viewModel.SearchCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (_viewModel?.CancelCommand.CanExecute(null) == true)
                {
                    _viewModel.CancelCommand.Execute(null);
                }
                e.Handled = true;
            }
        }

        private void SearchDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel?.SelectedResult != null && _viewModel.NextCommand.CanExecute(null))
            {
                _viewModel.NextCommand.Execute(null);
            }
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel?.NextCommand?.CanExecute(null) == true)
            {
                _viewModel.NextCommand.Execute(null);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel?.SaveCommand?.CanExecute(null) == true)
            {
                _viewModel.SaveCommand.Execute(null);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel?.CancelCommand?.CanExecute(null) == true)
            {
                _viewModel.CancelCommand.Execute(null);
            }
        }

        private void HiddenText_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is ManualAchievementEditItem item)
            {
                if (item.CanReveal)
                {
                    _viewModel?.RevealAchievementCommand.Execute(item);
                    e.Handled = true;
                }
            }
        }

        public void Cleanup()
        {
            if (_viewModel != null)
            {
                _viewModel.RequestClose -= ViewModel_RequestClose;
                _viewModel.Cleanup();
            }
        }
    }
}
