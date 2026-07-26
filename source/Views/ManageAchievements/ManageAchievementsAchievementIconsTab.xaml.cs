using Microsoft.Win32;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.ViewModels;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Playnite.SDK.Events;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels.ManageAchievements;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.ManageAchievements
{
    public partial class ManageAchievementsAchievementIconsTab : UserControl, IFullscreenControllerNavigable
    {
        private const double SmoothMouseWheelDivisor = 3.0;
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

        private readonly ManageAchievementsAchievementIconsViewModel _viewModel;
        private ScrollViewer _achievementCardsScrollViewer;

        private enum IconEditorControlKind
        {
            TextBox,
            ClearButton,
            BrowseButton
        }

        public ManageAchievementsAchievementIconsTab(ManageAchievementsAchievementIconsViewModel viewModel)
        {
            _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataContext = _viewModel;
            InitializeComponent();
            _viewModel.IconOverridesSaved += ViewModel_IconOverridesSaved;
        }

        public event EventHandler<IconOverridesSavedEventArgs> IconOverridesSaved;

        public void RefreshData()
        {
            _viewModel?.RefreshData();
        }

        public void Cleanup()
        {
            if (_viewModel != null)
            {
                _viewModel.IconOverridesSaved -= ViewModel_IconOverridesSaved;
                _viewModel.Cleanup();
            }
        }

        private async void BrowseButton_Click(object sender, RoutedEventArgs e)
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

            await _viewModel.ApplyLocalFileOverrideAsync(row, variant, dialog.FileName);
        }

        private void ClearOverrideButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryResolveRowAndVariant(sender as FrameworkElement, out var row, out var variant))
            {
                return;
            }

            row.ClearOverride(variant);
            e.Handled = true;
        }

        private void AchievementCardsList_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (IsInteractiveElementHit(source))
            {
                return;
            }

            var itemContainer = VisualTreeHelpers.FindVisualParent<ListBoxItem>(source);
            var row = itemContainer?.DataContext as AchievementIconOverrideItem;
            if (row?.CanReveal != true)
            {
                return;
            }

            row.ToggleReveal();
            e.Handled = true;
        }

        private void AchievementCardsList_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            EnsureAchievementCardsScrollViewer();
            if (_achievementCardsScrollViewer == null || _achievementCardsScrollViewer.ScrollableHeight <= 0)
            {
                return;
            }

            var nextOffset = _achievementCardsScrollViewer.VerticalOffset - (e.Delta / SmoothMouseWheelDivisor);
            nextOffset = Math.Max(0, Math.Min(_achievementCardsScrollViewer.ScrollableHeight, nextOffset));
            _achievementCardsScrollViewer.ScrollToVerticalOffset(nextOffset);
            e.Handled = true;
        }

        private void OverrideTextBox_PreviewDragOver(object sender, DragEventArgs e)
        {
            var hasDropPayload = TryGetFirstImageFilePath(e.Data, out _) || TryGetFirstBrowserUrl(e.Data, out _);
            e.Effects = hasDropPayload ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void OverrideTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || !(sender is TextBox textBox))
            {
                return;
            }

            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            Keyboard.ClearFocus();
            e.Handled = true;
        }

        private async void OverrideTextBox_Drop(object sender, DragEventArgs e)
        {
            if (!TryResolveRowAndVariant(sender as FrameworkElement, out var row, out var variant))
            {
                return;
            }

            try
            {
                if (TryGetFirstImageFilePath(e.Data, out var imagePath))
                {
                    e.Handled = true;
                    await _viewModel.ApplyLocalFileOverrideAsync(row, variant, imagePath);
                    return;
                }

                if (TryGetFirstBrowserUrl(e.Data, out var url))
                {
                    e.Handled = true;
                    if (variant == AchievementIconVariant.Locked)
                    {
                        row.LockedOverrideValue = url;
                    }
                    else
                    {
                        row.UnlockedOverrideValue = url;
                    }
                }
            }
            catch
            {
                e.Handled = true;
            }
        }

        private void ViewModel_IconOverridesSaved(object sender, IconOverridesSavedEventArgs e)
        {
            IconOverridesSaved?.Invoke(this, e);
        }

        private void EnsureAchievementCardsScrollViewer()
        {
            if (_achievementCardsScrollViewer != null)
            {
                return;
            }

            _achievementCardsScrollViewer = VisualTreeHelpers.FindVisualChild<ScrollViewer>(AchievementCardsList);
        }

        private static bool TryResolveRowAndVariant(
            FrameworkElement element,
            out AchievementIconOverrideItem row,
            out AchievementIconVariant variant)
        {
            row = element?.DataContext as AchievementIconOverrideItem;
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

        private static bool IsInteractiveElementHit(DependencyObject source)
        {
            return source is ButtonBase ||
                   source is TextBoxBase ||
                   source is ScrollBar ||
                   VisualTreeHelpers.FindVisualParent<ButtonBase>(source) != null ||
                   VisualTreeHelpers.FindVisualParent<TextBoxBase>(source) != null ||
                   VisualTreeHelpers.FindVisualParent<ScrollBar>(source) != null;
        }

        public bool HandleFullscreenControllerInput(ControllerInput input)
        {
            if (!FullscreenControllerNavigationService.IsKeyboardFocusWithin(this))
            {
                return false;
            }

            if (FullscreenControllerNavigationService.TryGetHorizontalDelta(input, out var horizontalDelta))
            {
                return TryMoveIconEditorFocusHorizontal(horizontalDelta);
            }

            if (FullscreenControllerNavigationService.TryGetVerticalDelta(input, out var verticalDelta))
            {
                return TryMoveIconEditorFocusVertical(verticalDelta);
            }

            return false;
        }

        private bool TryMoveIconEditorFocusHorizontal(int delta)
        {
            if (delta == 0 ||
                AchievementCardsList?.IsVisible != true ||
                !TryGetFocusedIconEditor(out var rowItem, out var variant, out var kind))
            {
                return false;
            }

            var candidates = GetIconEditorCandidates(rowItem, variant);
            if (candidates.Count == 0)
            {
                return false;
            }

            var currentIndex = candidates.FindIndex(candidate => candidate.Kind == kind);
            if (currentIndex < 0)
            {
                return false;
            }

            var nextIndex = currentIndex + delta;
            if (nextIndex < 0 || nextIndex >= candidates.Count)
            {
                return false;
            }

            return FullscreenControllerNavigationService.FocusElement(candidates[nextIndex].Element);
        }

        private bool TryMoveIconEditorFocusVertical(int delta)
        {
            if (delta == 0 ||
                AchievementCardsList?.IsVisible != true)
            {
                return false;
            }

            if (!TryGetFocusedIconEditor(out var rowItem, out var variant, out var kind))
            {
                return delta > 0 && TryFocusFirstVisibleIconEditor();
            }

            if (delta > 0 && variant == AchievementIconVariant.Unlocked)
            {
                return FocusIconEditor(rowItem, AchievementIconVariant.Locked, kind);
            }

            if (delta < 0 && variant == AchievementIconVariant.Locked)
            {
                return FocusIconEditor(rowItem, AchievementIconVariant.Unlocked, kind);
            }

            var nextRow = GetAdjacentGeneratedRow(rowItem, delta);
            if (nextRow == null)
            {
                return false;
            }

            var nextVariant = delta > 0 ? AchievementIconVariant.Unlocked : AchievementIconVariant.Locked;
            return FocusIconEditor(nextRow, nextVariant, kind);
        }

        private bool TryGetFocusedIconEditor(
            out ListBoxItem rowItem,
            out AchievementIconVariant variant,
            out IconEditorControlKind kind)
        {
            rowItem = null;
            variant = AchievementIconVariant.Unlocked;
            kind = IconEditorControlKind.TextBox;

            var focused = Keyboard.FocusedElement as DependencyObject;
            rowItem = FullscreenControllerNavigationService.FindAncestor<ListBoxItem>(focused);
            return rowItem != null &&
                   TryGetIconEditorIdentity(focused, rowItem, out variant, out kind);
        }

        private bool TryGetIconEditorIdentity(
            DependencyObject source,
            DependencyObject rowRoot,
            out AchievementIconVariant variant,
            out IconEditorControlKind kind)
        {
            variant = AchievementIconVariant.Unlocked;
            kind = IconEditorControlKind.TextBox;

            var textBox = FullscreenControllerNavigationService.FindAncestor<TextBox>(source) ?? source as TextBox;
            if (textBox != null && FullscreenControllerNavigationService.IsDescendantOf(textBox, rowRoot))
            {
                return TryParseVariant(textBox.Tag as string, out variant);
            }

            var button = FullscreenControllerNavigationService.FindAncestor<ButtonBase>(source) ?? source as ButtonBase;
            if (button == null || !FullscreenControllerNavigationService.IsDescendantOf(button, rowRoot))
            {
                return false;
            }

            var variantToken = button.CommandParameter as string ?? button.Tag as string;
            if (!TryParseVariant(variantToken, out variant))
            {
                return false;
            }

            kind = IsClearButton(button)
                ? IconEditorControlKind.ClearButton
                : IconEditorControlKind.BrowseButton;
            return true;
        }

        private ListBoxItem GetAdjacentGeneratedRow(ListBoxItem rowItem, int delta)
        {
            var index = AchievementCardsList.ItemContainerGenerator.IndexFromContainer(rowItem);
            var nextIndex = index + delta;
            if (nextIndex < 0 || nextIndex >= AchievementCardsList.Items.Count)
            {
                return null;
            }

            AchievementCardsList.ScrollIntoView(AchievementCardsList.Items[nextIndex]);
            AchievementCardsList.UpdateLayout();
            return AchievementCardsList.ItemContainerGenerator.ContainerFromIndex(nextIndex) as ListBoxItem;
        }

        private bool TryFocusFirstVisibleIconEditor()
        {
            if (AchievementCardsList?.IsVisible != true || AchievementCardsList.Items.Count == 0)
            {
                return false;
            }

            AchievementCardsList.UpdateLayout();
            var rowItem = GetFirstGeneratedRow();
            if (rowItem == null)
            {
                AchievementCardsList.ScrollIntoView(AchievementCardsList.Items[0]);
                AchievementCardsList.UpdateLayout();
                rowItem = AchievementCardsList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
            }

            return rowItem != null &&
                   FocusIconEditor(rowItem, AchievementIconVariant.Unlocked, IconEditorControlKind.TextBox);
        }

        private ListBoxItem GetFirstGeneratedRow()
        {
            for (var index = 0; index < AchievementCardsList.Items.Count; index++)
            {
                var rowItem = AchievementCardsList.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
                if (rowItem?.IsVisible == true)
                {
                    return rowItem;
                }
            }

            return null;
        }

        private bool FocusIconEditor(
            ListBoxItem rowItem,
            AchievementIconVariant variant,
            IconEditorControlKind preferredKind)
        {
            var candidates = GetIconEditorCandidates(rowItem, variant);

            var target = candidates.FirstOrDefault(candidate => candidate.Kind == preferredKind).Element
                         ?? candidates.FirstOrDefault(candidate => candidate.Kind == IconEditorControlKind.TextBox).Element
                         ?? candidates.FirstOrDefault(candidate => candidate.Kind == IconEditorControlKind.BrowseButton).Element
                         ?? candidates.FirstOrDefault().Element;

            return target != null && FullscreenControllerNavigationService.FocusElement(target);
        }

        private List<IconEditorCandidate> GetIconEditorCandidates(
            ListBoxItem rowItem,
            AchievementIconVariant variant)
        {
            return FullscreenControllerNavigationService.GetVisibleFocusableElements(rowItem)
                .Where(IsControllerElementAvailable)
                .Select(element =>
                {
                    if (!TryGetIconEditorIdentity(element, rowItem, out var elementVariant, out var kind) ||
                        elementVariant != variant)
                    {
                        return null;
                    }

                    return new IconEditorCandidate(element, kind);
                })
                .Where(candidate => candidate != null)
                .OrderBy(candidate => GetIconEditorKindOrder(candidate.Kind))
                .ToList();
        }

        private static int GetIconEditorKindOrder(IconEditorControlKind kind)
        {
            switch (kind)
            {
                case IconEditorControlKind.TextBox:
                    return 0;
                case IconEditorControlKind.ClearButton:
                    return 1;
                case IconEditorControlKind.BrowseButton:
                    return 2;
                default:
                    return 99;
            }
        }

        private static bool TryParseVariant(string value, out AchievementIconVariant variant)
        {
            variant = AchievementIconVariant.Unlocked;
            if (string.Equals((value ?? string.Empty).Trim(), "Locked", StringComparison.OrdinalIgnoreCase))
            {
                variant = AchievementIconVariant.Locked;
                return true;
            }

            if (string.Equals((value ?? string.Empty).Trim(), "Unlocked", StringComparison.OrdinalIgnoreCase))
            {
                variant = AchievementIconVariant.Unlocked;
                return true;
            }

            return false;
        }

        public IList<UIElement> GetControllerElements()
        {
            var elements = new List<UIElement>
            {
                OpenIconsFolderButton
            };

            if (AchievementCardsList?.IsVisible == true)
            {
                AchievementCardsList.UpdateLayout();
                elements.AddRange(FullscreenControllerNavigationService.GetVisibleFocusableElements(AchievementCardsList));
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

        private static bool IsClearButton(ButtonBase button)
        {
            return button is Button clearButton &&
                   ReferenceEquals(clearButton.Style, clearButton.TryFindResource("ClearSearchButtonStyle"));
        }

        private sealed class IconEditorCandidate
        {
            public IconEditorCandidate(UIElement element, IconEditorControlKind kind)
            {
                Element = element;
                Kind = kind;
            }

            public UIElement Element { get; }

            public IconEditorControlKind Kind { get; }
        }
    }
}
