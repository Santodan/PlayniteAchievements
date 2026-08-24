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
using PlayniteAchievements.Views.Settings.Controls;

namespace PlayniteAchievements.Views.Settings.Display
{
    /// <summary>
    /// Display settings: General section. Hosts grid defaults, achievement icon and visibility
    /// options plus the achievement visibility preview, and the reset-to-defaults action.
    /// </summary>
    public partial class DisplayGeneralSection : UserControl, IDisposable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly ILogger _logger;
        private readonly Action _onDisplaySettingsReset;
        private readonly PersistedSettingsSubscription _persistedSubscription;
        private ModernThemeBindings _achievementVisibilityPreviewThemeData;

        public DisplayGeneralSection()
        {
            InitializeComponent();
        }

        internal DisplayGeneralSection(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ILogger logger,
            Action onDisplaySettingsReset)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _logger = logger;
            _onDisplaySettingsReset = onDisplaySettingsReset;

            _persistedSubscription = new PersistedSettingsSubscription(
                _settings,
                OnPersistedPropertyChanged,
                OnSettingsReloaded);

            RefreshVisibilityPreview();
            UpdateGlowTierTexts();
            UpdateFallbackThumbnails();
        }

        /// <summary>Summary text for the soft-glow tier selector button.</summary>
        public static readonly DependencyProperty SoftGlowTiersTextProperty =
            DependencyProperty.Register(nameof(SoftGlowTiersText), typeof(string),
                typeof(DisplayGeneralSection), new PropertyMetadata(string.Empty));

        public string SoftGlowTiersText
        {
            get => (string)GetValue(SoftGlowTiersTextProperty);
            set => SetValue(SoftGlowTiersTextProperty, value);
        }

        /// <summary>Summary text for the ray tier selector button.</summary>
        public static readonly DependencyProperty RayGlowTiersTextProperty =
            DependencyProperty.Register(nameof(RayGlowTiersText), typeof(string),
                typeof(DisplayGeneralSection), new PropertyMetadata(string.Empty));

        public string RayGlowTiersText
        {
            get => (string)GetValue(RayGlowTiersTextProperty);
            set => SetValue(RayGlowTiersTextProperty, value);
        }

        private void SoftGlowTiersButton_Click(object sender, RoutedEventArgs e)
        {
            RaritySelectorMenu.Open(
                sender as Button,
                () => _settings?.Persisted?.RarityGlowSoftTiers ?? RaritySelectorMenu.GlowTiers,
                value => { if (_settings?.Persisted != null) { _settings.Persisted.RarityGlowSoftTiers = value; } },
                UpdateGlowTierTexts,
                includeCommon: false,
                includeCompleted: true);
        }

        private void RayGlowTiersButton_Click(object sender, RoutedEventArgs e)
        {
            RaritySelectorMenu.Open(
                sender as Button,
                () => _settings?.Persisted?.RarityGlowRayTiers ?? RaritySelection.None,
                value => { if (_settings?.Persisted != null) { _settings.Persisted.RarityGlowRayTiers = value; } },
                UpdateGlowTierTexts,
                includeCommon: false,
                includeCompleted: true);
        }

        private void UpdateGlowTierTexts()
        {
            var persisted = _settings?.Persisted;
            SoftGlowTiersText = RaritySelectorMenu.Format(
                persisted?.RarityGlowSoftTiers ?? RaritySelectorMenu.GlowTiers,
                includeCommon: false,
                includeCompleted: true);
            RayGlowTiersText = RaritySelectorMenu.Format(
                persisted?.RarityGlowRayTiers ?? RaritySelection.None,
                includeCommon: false,
                includeCompleted: true);
        }

        private void OnSettingsReloaded()
        {
            RefreshVisibilityPreview();
            UpdateGlowTierTexts();
            UpdateFallbackThumbnails();
        }

        /// <summary>Effective locked fallback image, cache-busted, for the picker thumbnail.</summary>
        public static readonly DependencyProperty LockedFallbackThumbnailUriProperty =
            DependencyProperty.Register(nameof(LockedFallbackThumbnailUri), typeof(string),
                typeof(DisplayGeneralSection), new PropertyMetadata(string.Empty));

        public string LockedFallbackThumbnailUri
        {
            get => (string)GetValue(LockedFallbackThumbnailUriProperty);
            set => SetValue(LockedFallbackThumbnailUriProperty, value);
        }

        /// <summary>Effective hidden fallback image, cache-busted, for the picker thumbnail.</summary>
        public static readonly DependencyProperty HiddenFallbackThumbnailUriProperty =
            DependencyProperty.Register(nameof(HiddenFallbackThumbnailUri), typeof(string),
                typeof(DisplayGeneralSection), new PropertyMetadata(string.Empty));

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
            var settings = _settings?.Persisted;
            if (settings == null) return;

            _achievementVisibilityPreviewThemeData?.RefreshDisplayItems(
                settings.ShowHiddenIcon, settings.ShowHiddenTitle, settings.ShowHiddenDescription,
                settings.ShowHiddenSuffix, settings.ShowLockedIcon, settings.UseSeparateLockedIconsWhenAvailable, settings.ShowCompactListRarityBar);
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

            if (e.PropertyName == nameof(PersistedSettings.ShowCompletedProgressColoring))
            {
                RarityAppearanceHelper.ApplyBadgeApplicationResources(_settings?.Persisted);
            }
        }

        private void ResetDisplaySettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _logger?.Info("Resetting Display tab settings to defaults.");

                _settings.Persisted.ResetDisplaySettingsToDefaults();
                RefreshVisibilityPreview();
                _onDisplaySettingsReset?.Invoke();

                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    L("LOCPlayAch_Status_Succeeded"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to reset Display tab settings.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        public void Dispose()
        {
            _persistedSubscription?.Dispose();
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }

        private static string LF(string key, params object[] args)
        {
            return string.Format(L(key), args);
        }
    }
}
