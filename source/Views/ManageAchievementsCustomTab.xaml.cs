using Playnite.SDK;
using PlayniteAchievements.Views.Dialogs;
using Microsoft.Win32;
using Playnite.SDK.Events;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace PlayniteAchievements.Views
{
    public partial class ManageAchievementsCustomTab : UserControl, IFullscreenControllerNavigable
    {
        private static readonly Regex HttpUrlRegex = new Regex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly string[] SupportedImageExtensions =
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".bmp",
            ".gif",
            ".tif",
            ".tiff"
        };

        private CustomAchievementEditItem _categoryPickerRow;

        public ManageAchievementsCustomTab(ManageAchievementsCustomViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

            // The picker resolves on demand (Enter or focus loss); it is seeded for the row that
            // was selected when editing began, so a click onto another row commits to the right one.
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
            viewModel.AssignmentsChanged += (_, __) => SeedCategoryPicker();
            CategoryPicker.Committed += (_, __) => ApplyCategoryFromPicker();
            CategoryPicker.IsKeyboardFocusWithinChanged += (_, e) =>
            {
                if (!(bool)e.NewValue)
                {
                    ApplyCategoryFromPicker();
                }
            };
            SeedCategoryPicker();
        }

        private ManageAchievementsCustomViewModel ViewModel => DataContext as ManageAchievementsCustomViewModel;

        private void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e?.PropertyName == nameof(ManageAchievementsCustomViewModel.SelectedRow))
            {
                SeedCategoryPicker();
            }
        }

        private void SeedCategoryPicker()
        {
            _categoryPickerRow = ViewModel?.SelectedRow;
            CategoryPicker.SetInitialCategory(_categoryPickerRow?.CategoryLabel);
        }

        private void ApplyCategoryFromPicker()
        {
            var row = _categoryPickerRow;
            if (row == null || ViewModel == null || !row.CanEditAssignments)
            {
                return;
            }

            ViewModel.ApplyCategoryToRow(row, CategoryPicker.ResolveSelection());
        }

        /// <summary>
        /// Opens the same note editor the Notes tab uses, so a note written here is written the
        /// same way and gets the markdown-capable editor rather than a bare cell.
        /// </summary>
        private void EditNoteButton_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is CustomAchievementEditItem row))
            {
                return;
            }

            var dialog = new AchievementNoteDialog(
                row.DisplayName,
                row.OriginalApiName,
                row.AchievementNote,
                isReadOnly: false,
                achievementIconSource: row.DisplayIcon);

            var window = PlayniteUiProvider.CreateExtensionWindow(
                ResourceProvider.GetString("LOCPlayAch_NotesDialog_EditTitle"),
                dialog,
                new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 640,
                    Height = 560
                });

            WindowPlacementPersistenceService.Attach(window, "AchievementNoteEdit");
            dialog.RequestClose += (s, args) => window.Close();
            window.ShowDialog();

            if (dialog.DialogResult == true)
            {
                // Assigning the row's note raises the change that persists it, the same path a
                // checkbox or committed text box takes.
                row.AchievementNote = dialog.SavedNote;
            }
        }

        /// <summary>
        /// Text editors bind on focus loss so a half-typed value is not persisted; Enter commits
        /// the same way the other Manage tabs do.
        /// </summary>
        private void EditorTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || !(sender is TextBox textBox))
            {
                return;
            }

            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }

        private void TypeSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null || TypeSelectionContextMenu == null || TypeSelectionButton == null)
            {
                return;
            }

            SelectorContextMenuHelper.OpenCategoryTypeMenu(
                TypeSelectionButton,
                TypeSelectionContextMenu,
                ViewModel.TypeSelectionOptions);
        }

        public void RefreshData()
        {
            ViewModel?.RefreshData();
        }

        private void ContextMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || button.ContextMenu == null)
            {
                return;
            }

            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
        }

        private void IconImage_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is CustomAchievementEditItem row) || !row.CanReveal)
            {
                return;
            }

            row.ToggleReveal();
            e.Handled = true;
        }

        private void RarityMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!(e.OriginalSource is MenuItem menuItem) ||
                !(menuItem.DataContext is CustomAchievementSelectionOption option))
            {
                return;
            }

            var menu = ItemsControl.ItemsControlFromItemContainer(menuItem) as ContextMenu;
            if ((menu?.PlacementTarget as FrameworkElement)?.DataContext is CustomAchievementEditItem row)
            {
                row.RarityInput = option.DisplayName;
            }
        }

        private void BrowseIconButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryResolveRowAndVariant(sender as FrameworkElement, out var row, out var variant))
            {
                return;
            }

            var dialog = new OpenFileDialog
            {
                Filter = "Image Files (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All Files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            SetIconPath(row, variant, dialog.FileName);
        }

        private void ClearIconButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryResolveRowAndVariant(sender as FrameworkElement, out var row, out var variant))
            {
                return;
            }

            SetIconPath(row, variant, null);
            e.Handled = true;
        }

        private void IconTextBox_PreviewDragOver(object sender, DragEventArgs e)
        {
            var hasDropPayload = TryGetFirstImageFilePath(e.Data, out _) || TryGetFirstBrowserUrl(e.Data, out _);
            e.Effects = hasDropPayload ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void IconTextBox_Drop(object sender, DragEventArgs e)
        {
            if (!TryResolveRowAndVariant(sender as FrameworkElement, out var row, out var variant))
            {
                return;
            }

            try
            {
                if (TryGetFirstImageFilePath(e.Data, out var imagePath))
                {
                    SetIconPath(row, variant, imagePath);
                    e.Handled = true;
                    return;
                }

                if (TryGetFirstBrowserUrl(e.Data, out var url))
                {
                    SetIconPath(row, variant, url);
                    e.Handled = true;
                }
            }
            catch
            {
                e.Handled = true;
            }
        }

        private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var textBox = sender as TextBox;
            var candidate = BuildCandidateText(textBox, e?.Text);
            e.Handled = !IsValidNumericCandidate(candidate, textBox?.Tag as string);
        }

        private void NumericTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            var textBox = sender as TextBox;
            var pastedText = e.DataObject?.GetData(typeof(string)) as string;
            if (!IsValidNumericCandidate(BuildCandidateText(textBox, pastedText), textBox?.Tag as string))
            {
                e.CancelCommand();
            }
        }

        public bool HandleFullscreenControllerInput(ControllerInput input)
        {
            if (CustomAchievementsGrid?.IsKeyboardFocusWithin != true)
            {
                return false;
            }

            if (FullscreenControllerNavigationService.IsFocusWithinDataGridColumnHeader(CustomAchievementsGrid))
            {
                if (FullscreenControllerNavigationService.IsAcceptInput(input))
                {
                    return FullscreenControllerNavigationService.ActivateFocusedDataGridColumnHeader(CustomAchievementsGrid);
                }
            }

            return false;
        }

        public IList<UIElement> GetControllerElements()
        {
            var elements = new List<UIElement>
            {
                // Header provider row first (top-left); the IsVisible filter below drops it for
                // games that have real provider data.
                CustomProviderComboBox,
                AddCustomProviderButton,
                EditCustomProviderButton,
                AddButton,
                DuplicateButton,
                DeleteButton,
                ImportFileButton,
                ExportButton,
                ClearButton,
                CustomAchievementsGrid,
                AddRowFooterButton,
                CapstoneCheckBox,
                CategoryPicker,
                TypeSelectionButton
            };

            return elements
                .Where(element => element != null && element.IsVisible && element.IsEnabled)
                .ToList();
        }

        private static bool TryResolveRowAndVariant(
            FrameworkElement element,
            out CustomAchievementEditItem row,
            out AchievementIconVariant variant)
        {
            row = element?.DataContext as CustomAchievementEditItem;
            variant = AchievementIconVariant.Unlocked;
            if (row == null)
            {
                return false;
            }

            var variantToken = (element as ButtonBase)?.CommandParameter as string;
            if (string.IsNullOrWhiteSpace(variantToken))
            {
                variantToken = element?.Tag as string;
            }

            if (string.Equals((variantToken ?? string.Empty).Trim(), "Locked", StringComparison.OrdinalIgnoreCase))
            {
                variant = AchievementIconVariant.Locked;
            }

            return true;
        }

        private static void SetIconPath(
            CustomAchievementEditItem row,
            AchievementIconVariant variant,
            string value)
        {
            if (row == null)
            {
                return;
            }

            if (variant == AchievementIconVariant.Locked)
            {
                row.LockedIconPath = value;
            }
            else
            {
                row.UnlockedIconPath = value;
            }
        }

        private static string BuildCandidateText(TextBox textBox, string input)
        {
            var current = textBox?.Text ?? string.Empty;
            input ??= string.Empty;
            var start = Math.Max(0, textBox?.SelectionStart ?? current.Length);
            var length = Math.Max(0, textBox?.SelectionLength ?? 0);
            if (start > current.Length)
            {
                start = current.Length;
            }

            if (start + length > current.Length)
            {
                length = current.Length - start;
            }

            return current.Remove(start, length).Insert(start, input);
        }

        private static bool IsValidNumericCandidate(string value, string mode)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return true;
            }

            if (string.Equals(mode, "Percent", StringComparison.OrdinalIgnoreCase))
            {
                if (normalized.Count(c => c == '.') > 1 ||
                    !double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                {
                    return false;
                }

                return percent >= 0 && percent <= 100;
            }

            return normalized.All(char.IsDigit);
        }

        private static bool TryGetFirstImageFilePath(IDataObject data, out string imagePath)
        {
            imagePath = null;
            if (data == null)
            {
                return false;
            }

            try
            {
                if (!data.GetDataPresent(DataFormats.FileDrop))
                {
                    return false;
                }

                var files = data.GetData(DataFormats.FileDrop) as string[];
                imagePath = files?.FirstOrDefault(IsSupportedImageFile);
                return !string.IsNullOrWhiteSpace(imagePath);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetFirstBrowserUrl(IDataObject data, out string url)
        {
            url = null;
            if (data == null)
            {
                return false;
            }

            try
            {
                var text = ReadDroppedText(data, DataFormats.UnicodeText) ??
                           ReadDroppedText(data, DataFormats.Text) ??
                           ReadDroppedText(data, DataFormats.Html);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return false;
                }

                var match = HttpUrlRegex.Match(text);
                if (!match.Success)
                {
                    return false;
                }

                url = TrimTrailingUrlPunctuation(match.Value);
                return !string.IsNullOrWhiteSpace(url);
            }
            catch
            {
                return false;
            }
        }

        private static string ReadDroppedText(IDataObject data, string format)
        {
            if (data == null || string.IsNullOrWhiteSpace(format))
            {
                return null;
            }

            try
            {
                if (!data.GetDataPresent(format))
                {
                    return null;
                }

                return data.GetData(format) as string;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsSupportedImageFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            var extension = Path.GetExtension(path) ?? string.Empty;
            if (!SupportedImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string TrimTrailingUrlPunctuation(string value)
        {
            return (value ?? string.Empty).Trim().TrimEnd('.', ',', ';', ')', ']', '}');
        }
    }
}
