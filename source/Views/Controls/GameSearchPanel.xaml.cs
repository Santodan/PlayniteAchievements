using Playnite.SDK.Events;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// The search box, picked games and results of a <see cref="GameSearchViewModel"/>. Searches
    /// on load when the box has text. Raises <see cref="Accepted"/> when a row is chosen and
    /// <see cref="Cancelled"/> on Escape; the host decides what either does.
    /// </summary>
    public partial class GameSearchPanel : UserControl
    {
        private GameSearchViewModel _viewModel;

        public GameSearchPanel()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Loaded += OnLoaded;
        }

        public event EventHandler Accepted;

        public event EventHandler Cancelled;

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            }

            _viewModel = e.NewValue as GameSearchViewModel;
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            }

            UpdateColumns();
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(GameSearchViewModel.AllowsMultiple) ||
                e.PropertyName == nameof(GameSearchViewModel.ShowPlatformColumn))
            {
                UpdateColumns();
            }
            else if (e.PropertyName == nameof(GameSearchViewModel.SelectedItem) && _viewModel?.SelectedItem != null)
            {
                ResultsDataGrid.ScrollIntoView(_viewModel.SelectedItem);
            }
        }

        // DataGrid columns sit outside the visual tree, so their visibility is set here rather than bound.
        private void UpdateColumns()
        {
            PickColumn.Visibility = _viewModel?.AllowsMultiple == true ? Visibility.Visible : Visibility.Collapsed;
            PlatformColumn.Visibility = _viewModel?.ShowPlatformColumn == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            if (_viewModel != null &&
                !_viewModel.IsSearching &&
                _viewModel.Results.Count == 0 &&
                !string.IsNullOrWhiteSpace(_viewModel.SearchText))
            {
                _ = _viewModel.SearchAsync();
            }

            // Keyboard focus is taken back when a host window shows; asking again at input
            // priority runs after that.
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (SearchTextBox.IsVisible)
                    {
                        SearchTextBox.Focus();
                        Keyboard.Focus(SearchTextBox);
                        SearchTextBox.SelectAll();
                    }
                }),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        private void Accept()
        {
            if (_viewModel?.CanAccept == true)
            {
                Accepted?.Invoke(this, EventArgs.Empty);
            }
        }

        private void MoveSelection(int step)
        {
            if (_viewModel == null || _viewModel.Results.Count == 0)
            {
                return;
            }

            var index = _viewModel.SelectedItem == null ? -1 : _viewModel.Results.IndexOf(_viewModel.SelectedItem);
            index = Math.Max(0, Math.Min(_viewModel.Results.Count - 1, index + step));
            _viewModel.SelectedItem = _viewModel.Results[index];
        }

        private void SearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter when _viewModel?.SearchCommand.CanExecute(null) == true:
                    _viewModel.SearchCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Down:
                    MoveSelection(1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    MoveSelection(-1);
                    e.Handled = true;
                    break;
                case Key.Space when Keyboard.Modifiers == ModifierKeys.Control:
                    _viewModel?.Toggle();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    Cancelled?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    break;
            }
        }

        private void LinkTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (_viewModel?.AddLinkCommand.CanExecute(null) == true)
                {
                    _viewModel.AddLinkCommand.Execute(null);
                }

                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Cancelled?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
        }

        private void ResultsDataGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    Accept();
                    e.Handled = true;
                    break;
                case Key.Space when _viewModel?.AllowsMultiple == true:
                    _viewModel.Toggle();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    Cancelled?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    break;
            }
        }

        /// <summary>
        /// A double-click chooses the row. Once games are picked for a link that names several,
        /// it picks or unpicks the row instead, so the picks are not dropped by a stray click.
        /// </summary>
        private void ResultsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var row = VisualTreeHelpers.FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
            if (!(row?.Item is GameSearchResultItem item) || _viewModel == null)
            {
                return;
            }

            if (_viewModel.AllowsMultiple && _viewModel.HasPicked)
            {
                _viewModel.Toggle(item);
                return;
            }

            _viewModel.SelectedItem = item;
            Accept();
        }

        private void ResultsDataGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is DataGrid dataGrid))
            {
                return;
            }

            var row = VisualTreeHelpers.FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
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

        private void ClearSearchTextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.SearchText = string.Empty;
            }

            SearchTextBox.Focus();
        }

        /// <summary>Activates a focused column header from a controller; other input is left to the host.</summary>
        internal bool HandleControllerInput(ControllerInput input)
        {
            if (ResultsDataGrid.IsKeyboardFocusWithin &&
                FullscreenControllerNavigationService.IsFocusWithinDataGridColumnHeader(ResultsDataGrid) &&
                FullscreenControllerNavigationService.IsAcceptInput(input))
            {
                return FullscreenControllerNavigationService.ActivateFocusedDataGridColumnHeader(ResultsDataGrid);
            }

            return false;
        }

        internal IEnumerable<UIElement> GetControllerElements()
        {
            yield return SearchTextBox;
            yield return ClearSearchTextButton;
            yield return SearchButton;
            yield return ResultsDataGrid;
            yield return LinkTextBox;
            yield return AddLinkButton;
        }
    }
}
