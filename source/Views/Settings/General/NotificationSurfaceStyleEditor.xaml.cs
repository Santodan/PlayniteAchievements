using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
// WinForms dialog: the WPF Microsoft.Win32 picker renders legacy-style on .NET Framework.
using DialogResult = System.Windows.Forms.DialogResult;
using OpenFileDialog = System.Windows.Forms.OpenFileDialog;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.ViewModels.Settings;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Settings.General
{
    /// <summary>
    /// Appearance editor for one notification surface (toast or screenshot frame): shown-field
    /// toggles, drag-reorderable text lines with per-line sizes, font family, and (on the toast
    /// surface only) the shared background image, badge image, and header text groups.
    /// DataContext is a <see cref="NotificationAppearanceEditorViewModel"/> set by the host
    /// section; the VM's surface must match this control's usage.
    /// </summary>
    public partial class NotificationSurfaceStyleEditor : UserControl
    {
        private const string DragDataFormat = "PlayniteAchievements.NotificationLineOrder";

        public NotificationSurfaceStyleEditor()
        {
            InitializeComponent();

            DataGridRowReorderBehavior.SetOptions(LineOrderGrid, new DataGridRowReorderOptions
            {
                DragDataFormat = DragDataFormat,
                DropIndicator = DropInsertLine,
                DragCountPopup = DragCountPopup,
                DragCountText = DragCountText,
                IsReorderableItem = item => item is NotificationLineRowItem,
                ExtractDragKeys = items => items
                    .OfType<NotificationLineRowItem>()
                    .Select(item => item.Kind)
                    .ToList(),
                MoveItemsRelativeToTarget = (kinds, target, insertAfter) =>
                    target is NotificationLineRowItem targetRow &&
                    ViewModel?.MoveLines(kinds, targetRow.Kind, insertAfter) == true,
                MoveItemsToEnd = kinds => ViewModel?.MoveLinesToEnd(kinds) == true
            });
        }

        private NotificationAppearanceEditorViewModel ViewModel =>
            DataContext as NotificationAppearanceEditorViewModel;

        /// <summary>
        /// Opens a color picker (owner window, current value) → chosen color, or null if
        /// cancelled. Set by the host section so the editor can reuse the plugin's picker.
        /// </summary>
        public Func<Window, string, string> ColorPicker { get; set; }

        private void PickCountdownColor_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = ViewModel;
            if (viewModel == null || ColorPicker == null)
            {
                return;
            }

            var picked = ColorPicker(Window.GetWindow(this), viewModel.CountdownBarColorText);
            if (!string.IsNullOrWhiteSpace(picked))
            {
                viewModel.CountdownBarColorText = picked;
            }
        }

        private void ResetCountdownColor_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.CountdownBarColorText = null;
            }
        }

        private void FitCardToImage_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.FitCardToBackgroundImage();
        }

        private async void ImageBrowse_Click(object sender, RoutedEventArgs e)
        {
            var viewModel = ViewModel;
            if (viewModel == null || !TryResolveSlot(sender as FrameworkElement, out var slot))
            {
                return;
            }

            var dialog = new OpenFileDialog
            {
                Filter = ImageFormats.BuildOpenFileDialogFilter(includeAllFiles: false),
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            await viewModel.ApplyImageAsync(slot, dialog.FileName);
        }

        private void ImageClear_Click(object sender, RoutedEventArgs e)
        {
            if (TryResolveSlot(sender as FrameworkElement, out var slot))
            {
                ViewModel?.ClearImage(slot);
            }
        }

        private void ImageTextBox_PreviewDragOver(object sender, DragEventArgs e)
        {
            var hasDropPayload = ImageDropHelper.TryGetFirstImageFilePath(e.Data, out _) ||
                                 ImageDropHelper.TryGetFirstBrowserUrl(e.Data, out _);
            e.Effects = hasDropPayload ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void ImageTextBox_PreviewDrop(object sender, DragEventArgs e)
        {
            var viewModel = ViewModel;
            if (viewModel == null || !TryResolveSlot(sender as FrameworkElement, out var slot))
            {
                return;
            }

            try
            {
                if (ImageDropHelper.TryGetFirstImageFilePath(e.Data, out var imagePath))
                {
                    e.Handled = true;
                    await viewModel.ApplyImageAsync(slot, imagePath);
                    return;
                }

                if (ImageDropHelper.TryGetFirstBrowserUrl(e.Data, out var url))
                {
                    e.Handled = true;
                    await viewModel.ApplyImageAsync(slot, url);
                }
            }
            catch
            {
                e.Handled = true;
            }
        }

        private void ApplyFontFamilyToAllLines_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.ApplyFontFamilyToAllLines();
        }

        // The header-text boxes keep Explicit bindings (format strings never persist per
        // keystroke) and commit on focus loss or Enter, so the binding update and the style
        // apply always run in one deterministic step.
        private void HeaderTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitHeaderText(sender as TextBox);
        }

        private void HeaderTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitHeaderText(sender as TextBox);
                e.Handled = true;
            }
        }

        private void CommitHeaderText(TextBox textBox)
        {
            if (textBox == null)
            {
                return;
            }

            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            ViewModel?.ApplyHeaderTexts();
        }

        private static bool TryResolveSlot(FrameworkElement element, out NotificationImageSlot slot)
        {
            slot = NotificationImageSlot.Background;
            var token = (element?.Tag as string)?.Trim();
            return !string.IsNullOrEmpty(token) && Enum.TryParse(token, ignoreCase: true, out slot);
        }
    }
}
