using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Playnite.SDK;
using Playnite.SDK.Events;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views
{
    public partial class ManageAchievementsNotesTab : UserControl, IFullscreenControllerNavigable
    {
        public ManageAchievementsNotesTab(ManageAchievementsNotesViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        }

        private ManageAchievementsNotesViewModel ViewModel => DataContext as ManageAchievementsNotesViewModel;

        public void RefreshData()
        {
            ViewModel?.ReloadData();
        }

        public bool HandleFullscreenControllerInput(ControllerInput input)
        {
            if (NotesDataGrid?.IsKeyboardFocusWithin != true)
            {
                return false;
            }

            if (FullscreenControllerNavigationService.IsFocusWithinDataGridColumnHeader(NotesDataGrid))
            {
                if (FullscreenControllerNavigationService.IsAcceptInput(input))
                {
                    return FullscreenControllerNavigationService.ActivateFocusedDataGridColumnHeader(NotesDataGrid);
                }

                return false;
            }

            return false;
        }

        public IList<UIElement> GetControllerElements()
        {
            var elements = new List<UIElement>
            {
                SearchTextBox,
                ClearSearchButton,
                StateFilterComboBox,
                NotesDataGrid
            };

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

        private void NotesDataGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (source == null)
            {
                return;
            }

            if (source is ButtonBase || VisualTreeHelpers.FindVisualParent<ButtonBase>(source) != null)
            {
                return;
            }

            var row = VisualTreeHelpers.FindVisualParent<DataGridRow>(source);
            if (!(row?.DataContext is ManageAchievementsNoteItem item))
            {
                return;
            }

            if (item.CanReveal)
            {
                item.ToggleReveal();
            }

            e.Handled = true;
        }

        private void ViewButton_Click(object sender, RoutedEventArgs e)
        {
            if (TryResolveRow(sender as FrameworkElement, out var item))
            {
                OpenNoteDialog(item, isEditMode: false);
            }

            e.Handled = true;
        }

        private void EditButton_Click(object sender, RoutedEventArgs e)
        {
            if (TryResolveRow(sender as FrameworkElement, out var item))
            {
                OpenNoteDialog(item, isEditMode: true);
            }

            e.Handled = true;
        }

        private bool TryResolveRow(FrameworkElement element, out ManageAchievementsNoteItem item)
        {
            item = element?.DataContext as ManageAchievementsNoteItem;
            return item != null;
        }

        private void OpenNoteDialog(ManageAchievementsNoteItem item, bool isEditMode)
        {
            if (item == null)
            {
                return;
            }

            var dialog = new AchievementNoteDialog(
                item.DisplayNameResolved,
                item.ApiNameResolved,
                item.AchievementNote,
                isReadOnly: !isEditMode,
                achievementIconSource: item.DisplayIcon);

            var title = isEditMode
                ? L("LOCPlayAch_NotesDialog_EditTitle", "Edit Note")
                : L("LOCPlayAch_NotesDialog_ViewTitle", "View Note");
            var window = PlayniteUiProvider.CreateExtensionWindow(
                title,
                dialog,
                new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 640,
                    Height = isEditMode ? 560 : 420
                });

            dialog.RequestClose += (s, e) => window.Close();
            window.ShowDialog();

            if (isEditMode && dialog.DialogResult == true)
            {
                ViewModel?.SetNote(item, dialog.SavedNote);
            }
        }

        private static string L(string key, string fallback)
        {
            var value = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }
}
