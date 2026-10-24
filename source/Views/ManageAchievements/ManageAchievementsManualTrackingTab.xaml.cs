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
        }

        /// <summary>A double-click or Enter on a search result goes on to linking it.</summary>
        private void SearchPanel_Accepted(object sender, EventArgs e)
        {
            if (_viewModel?.NextCommand.CanExecute(null) == true)
            {
                _viewModel.NextCommand.Execute(null);
            }
        }

        private void SearchPanel_Cancelled(object sender, EventArgs e)
        {
            if (_viewModel?.CancelCommand.CanExecute(null) == true)
            {
                _viewModel.CancelCommand.Execute(null);
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
            if (SearchPanel?.IsKeyboardFocusWithin == true)
            {
                return SearchPanel.HandleControllerInput(input);
            }

            if (ManualAchievementsDataGrid?.IsKeyboardFocusWithin == true)
            {
                return HandleManualAchievementsControllerInput(input);
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
                elements.AddRange(SearchPanel.GetControllerElements());
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
                elements.Add(DisplayPlatformComboBox);
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
