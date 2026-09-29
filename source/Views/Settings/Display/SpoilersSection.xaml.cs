using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
// WinForms dialog: the WPF Microsoft.Win32 picker renders legacy-style on .NET Framework.
using DialogResult = System.Windows.Forms.DialogResult;
using OpenFileDialog = System.Windows.Forms.OpenFileDialog;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Settings.Display
{
    /// <summary>
    /// Display settings: Spoilers section. Hosts the hidden/locked reveal matrix over icon, name,
    /// description, trophy and points, the masked-state cover images, and the live preview of both.
    /// </summary>
    public partial class SpoilersSection : UserControl, IDisposable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly ILogger _logger;
        private readonly PersistedSettingsSubscription _persistedSubscription;
        private ModernThemeBindings _achievementVisibilityPreviewThemeData;

        public SpoilersSection()
        {
            InitializeComponent();
        }

        internal SpoilersSection(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ILogger logger)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _logger = logger;

            _persistedSubscription = new PersistedSettingsSubscription(
                _settings,
                OnPersistedPropertyChanged,
                OnSettingsReloaded);

            RefreshVisibilityPreview();
            UpdateFallbackThumbnails();
        }

        private void OnSettingsReloaded()
        {
            RefreshVisibilityPreview();
            UpdateFallbackThumbnails();
        }

        /// <summary>Effective locked fallback image, cache-busted, for the picker thumbnail.</summary>
        public static readonly DependencyProperty LockedFallbackThumbnailUriProperty =
            DependencyProperty.Register(nameof(LockedFallbackThumbnailUri), typeof(string),
                typeof(SpoilersSection), new PropertyMetadata(string.Empty));

        public string LockedFallbackThumbnailUri
        {
            get => (string)GetValue(LockedFallbackThumbnailUriProperty);
            set => SetValue(LockedFallbackThumbnailUriProperty, value);
        }

        /// <summary>Effective hidden fallback image, cache-busted, for the picker thumbnail.</summary>
        public static readonly DependencyProperty HiddenFallbackThumbnailUriProperty =
            DependencyProperty.Register(nameof(HiddenFallbackThumbnailUri), typeof(string),
                typeof(SpoilersSection), new PropertyMetadata(string.Empty));

        public string HiddenFallbackThumbnailUri
        {
            get => (string)GetValue(HiddenFallbackThumbnailUriProperty);
            set => SetValue(HiddenFallbackThumbnailUriProperty, value);
        }

        // Shows the image actually in effect, so an unset slot previews the built-in placeholder.
        private void UpdateFallbackThumbnails()
        {
            LockedFallbackThumbnailUri = AchievementIconResolver.GetLockedFallbackIcon();
            HiddenFallbackThumbnailUri = AchievementIconResolver.GetHiddenFallbackIcon();
        }

        private async void FallbackIconBrowse_Click(object sender, RoutedEventArgs e)
        {
            if (!TryResolveFallbackSlot(sender as FrameworkElement, out var slot))
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

            await ApplyFallbackIconAsync(slot, dialog.FileName);
        }

        private void FallbackIconClear_Click(object sender, RoutedEventArgs e)
        {
            var persisted = _settings?.Persisted;
            if (persisted == null || !TryResolveFallbackSlot(sender as FrameworkElement, out var slot))
            {
                return;
            }

            var previous = FallbackIconStore.GetPath(persisted, slot);
            _plugin?.FallbackIconStore?.DeleteSlot(slot);
            FallbackIconStore.SetPath(persisted, slot, null);

            // Drop the removed image so a later re-pick at the same managed path does not
            // resurface it from the memory cache.
            if (!string.IsNullOrEmpty(previous))
            {
                _plugin?.ImageService?.EvictByUriSegment(previous);
            }

            UpdateFallbackThumbnails();
        }

        private void FallbackIconTextBox_PreviewDragOver(object sender, DragEventArgs e)
        {
            var hasDropPayload = ImageDropHelper.TryGetFirstImageFilePath(e.Data, out _) ||
                                 ImageDropHelper.TryGetFirstBrowserUrl(e.Data, out _);
            e.Effects = hasDropPayload ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void FallbackIconTextBox_PreviewDrop(object sender, DragEventArgs e)
        {
            if (!TryResolveFallbackSlot(sender as FrameworkElement, out var slot))
            {
                return;
            }

            try
            {
                if (ImageDropHelper.TryGetFirstImageFilePath(e.Data, out var imagePath))
                {
                    e.Handled = true;
                    await ApplyFallbackIconAsync(slot, imagePath);
                    return;
                }

                // A global setting has no refresh pass that would materialize a URL later, so
                // download it now rather than persisting the link.
                if (ImageDropHelper.TryGetFirstBrowserUrl(e.Data, out var url))
                {
                    e.Handled = true;
                    await ApplyFallbackIconAsync(slot, url);
                }
            }
            catch
            {
                e.Handled = true;
            }
        }

        /// <summary>
        /// Copies or downloads the picked image into managed storage for the slot and stores the
        /// resulting path. No-ops when materialization fails.
        /// </summary>
        private async Task ApplyFallbackIconAsync(FallbackIconSlot slot, string sourcePathOrUrl)
        {
            var persisted = _settings?.Persisted;
            var store = _plugin?.FallbackIconStore;
            if (persisted == null || store == null)
            {
                return;
            }

            try
            {
                // Clear first so the UI releases the old file and the slot starts empty.
                store.DeleteSlot(slot);
                FallbackIconStore.SetPath(persisted, slot, null);

                var resolved = await store.MaterializeAsync(
                    sourcePathOrUrl, slot, CancellationToken.None);
                if (resolved != null)
                {
                    // The slot uses a fixed filename, so a different source resolves to the same
                    // path and would otherwise show the previously cached bitmap. Evict BEFORE
                    // setting the path: setting it synchronously triggers the preview/mockup
                    // reload, whose cache lookup must see an already-cleared cache.
                    _plugin?.ImageService?.EvictByUriSegment(resolved);
                    FallbackIconStore.SetPath(persisted, slot, resolved);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to apply fallback icon for slot {slot}.");
            }
            finally
            {
                UpdateFallbackThumbnails();
            }
        }

        private static bool TryResolveFallbackSlot(FrameworkElement element, out FallbackIconSlot slot)
        {
            slot = FallbackIconSlot.Locked;
            var token = (element?.Tag as string)?.Trim();
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            if (string.Equals(token, "Hidden", StringComparison.OrdinalIgnoreCase))
            {
                slot = FallbackIconSlot.Hidden;
                return true;
            }

            return string.Equals(token, "Locked", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Gets modern theme bindings with locked and hidden achievements for visibility preview.
        /// </summary>
        public ModernThemeBindings AchievementVisibilityPreviewThemeData
        {
            get
            {
                if (_achievementVisibilityPreviewThemeData == null)
                {
                    _achievementVisibilityPreviewThemeData = MockDataHelper.GetAchievementVisibilityPreviewThemeData();
                }
                return _achievementVisibilityPreviewThemeData;
            }
        }

        /// <summary>
        /// Refreshes the achievement visibility preview to reflect current settings.
        /// </summary>
        public void RefreshVisibilityPreview()
        {
            if (_settings?.Persisted == null) return;

            _achievementVisibilityPreviewThemeData?.RefreshDisplayItems(_settings);
        }

        private void OnPersistedPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (DisplayPreviewProperties.AffectsMockPreviews(e.PropertyName))
            {
                RefreshVisibilityPreview();
            }

            if (e.PropertyName == nameof(PersistedSettings.LockedFallbackIconPath) ||
                e.PropertyName == nameof(PersistedSettings.HiddenFallbackIconPath))
            {
                UpdateFallbackThumbnails();
            }
        }

        public void Dispose()
        {
            _persistedSubscription?.Dispose();
        }
    }
}
