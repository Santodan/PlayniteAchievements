using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using Playnite.SDK.Events;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Helpers;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Views.ManageAchievements
{
    public partial class ManageAchievementsManualTrackingTab : UserControl, IFullscreenControllerNavigable
    {
        public static readonly DependencyProperty UnlinkCommandProperty =
            DependencyProperty.Register(
                nameof(UnlinkCommand),
                typeof(ICommand),
                typeof(ManageAchievementsManualTrackingTab),
                new PropertyMetadata(null));

        public ICommand UnlinkCommand
        {
            get => (ICommand)GetValue(UnlinkCommandProperty);
            set => SetValue(UnlinkCommandProperty, value);
        }

        private readonly ManualAchievementsViewModel _viewModel;

        public ManageAchievementsManualTrackingTab(ManualAchievementsViewModel viewModel)
        {
            InitializeComponent();
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataContext = _viewModel;

            // Auto-trigger search when control loads if search text is pre-filled.
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
                    // Error is handled by ViewModel.
                }
            }
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

        private void SearchDataGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is DataGrid dataGrid))
            {
                return;
            }

            var source = e.OriginalSource as DependencyObject;
            var row = VisualTreeHelpers.FindVisualParent<DataGridRow>(source);
            if (row?.Item == null)
            {
                return;
            }

            if (!row.IsSelected)
            {
                dataGrid.SelectedItem = row.Item;
                row.Focus();
            }
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel?.NextCommand?.CanExecute(null) == true)
            {
                _viewModel.NextCommand.Execute(null);
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

        private void ClearSearchTextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.SearchText = string.Empty;
            }
        }

        private void ClearEditFilterButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.EditSearchFilter = string.Empty;
            }
        }

        private void TimeModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(sender is ComboBox comboBox))
            {
                return;
            }

            var binding = BindingOperations.GetBindingExpression(comboBox, ComboBox.SelectedItemProperty);
            binding?.UpdateSource();
        }

        public bool HandleFullscreenControllerInput(ControllerInput input)
        {
            if (SearchResultsDataGrid?.IsKeyboardFocusWithin == true)
            {
                return HandleSearchResultsControllerInput(input);
            }

            if (ManualAchievementsDataGrid?.IsKeyboardFocusWithin == true)
            {
                return HandleManualAchievementsControllerInput(input);
            }

            return false;
        }

        private bool HandleSearchResultsControllerInput(ControllerInput input)
        {
            if (FullscreenControllerNavigationService.IsFocusWithinDataGridColumnHeader(SearchResultsDataGrid))
            {
                if (FullscreenControllerNavigationService.IsAcceptInput(input))
                {
                    return FullscreenControllerNavigationService.ActivateFocusedDataGridColumnHeader(SearchResultsDataGrid);
                }

                return false;
            }

            return false;
        }

        private bool HandleManualAchievementsControllerInput(ControllerInput input)
        {
            if (FullscreenControllerNavigationService.IsFocusWithinDataGridColumnHeader(ManualAchievementsDataGrid))
            {
                if (FullscreenControllerNavigationService.IsAcceptInput(input))
                {
                    return FullscreenControllerNavigationService.ActivateFocusedDataGridColumnHeader(ManualAchievementsDataGrid);
                }

                return false;
            }

            return false;
        }

        public IList<UIElement> GetControllerElements()
        {
            if (_viewModel == null)
            {
                return new List<UIElement>();
            }

            var elements = new List<UIElement>();
            if (_viewModel.IsSearchStage)
            {
                elements.Add(SourceComboBox);
                elements.Add(SearchTextBox);
                elements.Add(ClearSearchTextButton);
                elements.Add(SearchButton);
                elements.Add(SearchResultsDataGrid);
                elements.Add(NextButton);
                elements.Add(SearchCancelButton);
            }
            else if (_viewModel.IsRefreshingStage)
            {
                elements.Add(RefreshCancelButton);
            }
            else if (_viewModel.IsEditingStage)
            {
                elements.Add(EditSearchTextBox);
                elements.Add(ClearEditFilterButton);
                elements.Add(UnlockAllButton);
                elements.Add(LockAllButton);
                elements.Add(ManualAchievementsDataGrid);
                elements.Add(UnlinkButton);
                elements.Add(SaveButton);
                elements.Add(EditCancelButton);
            }

            return elements
                .Where(IsControllerElementAvailable)
                .ToList();
        }

        private static bool IsControllerElementAvailable(UIElement element)
        {
            if (element == null || !element.IsVisible || !element.IsEnabled)
            {
                return false;
            }

            if (element is Button button &&
                ReferenceEquals(button.Style, button.TryFindResource("ClearSearchButtonStyle")))
            {
                return !string.IsNullOrEmpty(button.Tag as string);
            }

            return true;
        }

        public void Cleanup()
        {
            _viewModel?.Cleanup();
        }
    }
}
