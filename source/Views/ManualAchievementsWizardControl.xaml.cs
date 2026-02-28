using System;
using System.Windows;
using System.Windows.Controls;
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

            if (_viewModel.SearchVm != null &&
                !_viewModel.IsEditingStage &&
                !string.IsNullOrWhiteSpace(_viewModel.SearchVm.SearchText))
            {
                try
                {
                    await _viewModel.SearchVm.SearchAsync();
                }
                catch
                {
                    // Error is handled by SearchViewModel
                }
            }
        }

        private void ViewModel_RequestClose(object sender, EventArgs e)
        {
            // Handled by parent window
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
