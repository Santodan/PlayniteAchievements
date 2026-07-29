using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Settings;

namespace PlayniteAchievements.Views.Settings.General
{
    /// <summary>
    /// General settings: Notification appearance section. Hosts the platform selector (global
    /// default vs per-provider whole-style copies), the toast and screenshot-frame editors with
    /// live mockups, the theme-template toggles, and the on-screen preview buttons.
    /// </summary>
    public partial class NotificationAppearanceSection : UserControl, IDisposable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly ILogger _logger;
        private readonly AchievementToastTemplateResolver _toastTemplateResolver;
        private readonly PersistedSettingsSubscription _persistedSubscription;
        private readonly NotificationAppearanceEditorViewModel _toastEditorViewModel;
        private readonly NotificationAppearanceEditorViewModel _frameEditorViewModel;

        private Window _framePreviewWindow;
        private string _selectedProviderKey;
        private readonly string _fallbackSampleProviderKey;
        private NotificationStyleSettings _currentStyle;
        private bool _suppressCustomizeEvents;

        public NotificationAppearanceSection()
        {
            InitializeComponent();
        }

        internal NotificationAppearanceSection(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ILogger logger)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _logger = logger;

            _toastTemplateResolver = new AchievementToastTemplateResolver(plugin.PlayniteApi, logger);

            // A sample provider so the mock and fire-tests always show a provider icon, even
            // when the global default (no platform selected) is being edited.
            _fallbackSampleProviderKey =
                (plugin.ProviderRegistry?.GetSettingsViewProviderKeys() ?? Enumerable.Empty<string>())
                .FirstOrDefault(key => !string.IsNullOrWhiteSpace(key));

            _toastEditorViewModel = new NotificationAppearanceEditorViewModel(
                settings, plugin, logger, isFrameSurface: false);
            _frameEditorViewModel = new NotificationAppearanceEditorViewModel(
                settings, plugin, logger, isFrameSurface: true);
            _toastEditorViewModel.StyleChanged += OnEditorStyleChanged;
            _frameEditorViewModel.StyleChanged += OnEditorStyleChanged;

            // The editors are DataContext islands over the editor view models, independent of
            // this section's inherited settings DataContext.
            ToastEditor.DataContext = _toastEditorViewModel;
            FrameEditor.DataContext = _frameEditorViewModel;
            ToastEditor.ColorPicker = (owner, current) => _plugin.PickColor(owner, current);
            FrameEditor.ColorPicker = (owner, current) => _plugin.PickColor(owner, current);

            PlatformSelector.ItemsSource = BuildPlatformOptions();
            PlatformSelector.SelectedIndex = 0;

            _persistedSubscription = new PersistedSettingsSubscription(
                _settings,
                OnPersistedPropertyChanged,
                ApplySelection);

            Loaded += (s, e) =>
            {
                UpdateMockups();
                RefreshFireButtons();
            };
        }

        /// <summary>
        /// The provider key used for sample/fire content: the selected platform, or a fallback
        /// sample provider so the global-default preview still shows a provider icon.
        /// </summary>
        private string EffectiveSampleProviderKey =>
            _selectedProviderKey ?? _fallbackSampleProviderKey;

        /// <summary>
        /// Disables the desktop/fullscreen theme fire-test buttons when the corresponding active
        /// theme ships no template for the surface (they would just fall back to plugin style).
        /// </summary>
        private void RefreshFireButtons()
        {
            if (NotificationThemeButton == null)
            {
                return;
            }

            NotificationThemeButton.IsEnabled =
                _toastTemplateResolver.ThemeProvidesTemplate(NotificationTemplatePreviewSource.ActiveTheme, isFrame: false);
            FrameThemeButton.IsEnabled =
                _toastTemplateResolver.ThemeProvidesTemplate(NotificationTemplatePreviewSource.ActiveTheme, isFrame: true);
        }

        private List<NotificationStylePlatformOption> BuildPlatformOptions()
        {
            var options = new List<NotificationStylePlatformOption>
            {
                NotificationStylePlatformOption.CreateDefault()
            };

            options.AddRange(
                (_plugin.ProviderRegistry?.GetSettingsViewProviderKeys() ?? Enumerable.Empty<string>())
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => new NotificationStylePlatformOption(key)));

            return options;
        }

        private void PlatformSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplySelection();
        }

        /// <summary>
        /// Points both surface editors at the style for the current platform selection: the
        /// global default, the provider's copy, or the default shown read-only when the
        /// provider is not customized yet.
        /// </summary>
        private void ApplySelection()
        {
            var option = PlatformSelector?.SelectedItem as NotificationStylePlatformOption;
            var persisted = _settings?.Persisted;
            if (option == null || persisted == null ||
                _toastEditorViewModel == null || _frameEditorViewModel == null)
            {
                return;
            }

            _selectedProviderKey = option.Key;

            NotificationStyleSettings style;
            bool editable;
            if (option.Key == null)
            {
                style = persisted.NotificationStyle;
                editable = true;
                CustomizeCheckBox.Visibility = Visibility.Collapsed;
                FollowDefaultHint.Visibility = Visibility.Collapsed;
            }
            else
            {
                var custom = persisted.GetProviderNotificationStyle(option.Key);
                editable = custom != null;
                style = custom ?? persisted.NotificationStyle;

                CustomizeCheckBox.Visibility = Visibility.Visible;
                _suppressCustomizeEvents = true;
                CustomizeCheckBox.IsChecked = editable;
                _suppressCustomizeEvents = false;
                FollowDefaultHint.Visibility = editable ? Visibility.Collapsed : Visibility.Visible;
            }

            _currentStyle = style;
            _toastEditorViewModel.SetStyle(style, editable ? option.Key : null, editable);
            _frameEditorViewModel.SetStyle(style, editable ? option.Key : null, editable);
            UpdateMockups();
        }

        /// <summary>
        /// Creates or removes the selected platform's whole-style copy. Checking clones the
        /// current default (including its images, re-materialized into the provider's own
        /// folder); unchecking reverts to the default after confirmation and deletes the
        /// provider's images.
        /// </summary>
        private async void CustomizeCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressCustomizeEvents)
            {
                return;
            }

            var providerKey = _selectedProviderKey;
            var persisted = _settings?.Persisted;
            if (providerKey == null || persisted == null)
            {
                return;
            }

            try
            {
                if (CustomizeCheckBox.IsChecked == true)
                {
                    var copy = persisted.NotificationStyle.Clone();
                    await _plugin.NotificationImageStore.CopyImagesForProviderAsync(
                        copy, providerKey, CancellationToken.None);
                    persisted.SetProviderNotificationStyle(providerKey, copy);
                    _plugin.PersistSettingsForUi();
                }
                else
                {
                    var result = _plugin.PlayniteApi.Dialogs.ShowMessage(
                        L("LOCPlayAch_Settings_Style_RevertConfirm"),
                        L("LOCPlayAch_Title_PluginName"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    if (result != MessageBoxResult.Yes)
                    {
                        _suppressCustomizeEvents = true;
                        CustomizeCheckBox.IsChecked = true;
                        _suppressCustomizeEvents = false;
                        return;
                    }

                    persisted.SetProviderNotificationStyle(providerKey, null);
                    _plugin.NotificationImageStore.DeleteProviderImages(providerKey);
                    _plugin.PersistSettingsForUi();
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to toggle notification style customization for {providerKey}.");
            }

            ApplySelection();
        }

        private void OnEditorStyleChanged(object sender, EventArgs e)
        {
            UpdateMockups();
        }

        private void OnPersistedPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            var name = e?.PropertyName;
            if (string.IsNullOrEmpty(name) ||
                name == nameof(PersistedSettings.NotificationStyle))
            {
                // The default style instance was replaced wholesale; re-resolve the editors.
                ApplySelection();
                return;
            }

            if (name == nameof(PersistedSettings.ProviderNotificationStyles))
            {
                // Raised by every debounced flush of a provider copy (the store re-clones);
                // keep the editors on their working instance and only refresh derived UI.
                UpdateMockups();
                return;
            }

            if (name == nameof(PersistedSettings.ToastUseThemeStyling) ||
                name == nameof(PersistedSettings.FrameUseThemeStyling) ||
                name == nameof(PersistedSettings.RarityColors) ||
                name == nameof(PersistedSettings.ProviderColorOverrides) ||
                name == nameof(PersistedSettings.UseUniformRarityBadges))
            {
                UpdateMockups();
            }
        }

        /// <summary>
        /// Rebuilds both inline mockups from the resolved templates and the style being edited
        /// so every toggle, reorder, image, and font change previews live.
        /// </summary>
        private void UpdateMockups()
        {
            var persisted = _settings?.Persisted;
            if (persisted == null || ToastMockupHost == null || FrameMockupHost == null ||
                _toastTemplateResolver == null)
            {
                return;
            }

            ToastMockupHost.ContentTemplate =
                _toastTemplateResolver.ResolveTemplate(persisted.ToastUseThemeStyling);
            ToastMockupHost.Content = new AchievementToastViewModel(
                ToastPreviewFactory.BuildPreviewArgs("mockup", EffectiveSampleProviderKey),
                persisted,
                _currentStyle);

            FrameMockupHost.ContentTemplate =
                _toastTemplateResolver.ResolveFrameTemplate(persisted.FrameUseThemeStyling);
            FrameMockupHost.Content = new AchievementToastViewModel(
                ToastPreviewFactory.BuildPreviewArgs("mockup", EffectiveSampleProviderKey),
                persisted,
                _currentStyle);
        }

        private void FireNotification_Click(object sender, RoutedEventArgs e)
        {
            if (!TryResolvePreviewSource(sender, out var source))
            {
                return;
            }

            var kind = NotificationSampleSelector?.SelectedValue as string ?? "rare";

            // Flush any debounced edits so the real notification pipeline resolves the same
            // style the mockup shows.
            _toastEditorViewModel?.FlushPendingPersist();
            _frameEditorViewModel?.FlushPendingPersist();
            PlayniteAchievementsPlugin.NotifyAchievementUnlocked(
                ToastPreviewFactory.BuildPreviewArgs(kind, EffectiveSampleProviderKey, source));
        }

        private static bool TryResolvePreviewSource(object sender, out NotificationTemplatePreviewSource source)
        {
            source = NotificationTemplatePreviewSource.PluginStyle;
            return sender is Button { Tag: string tag } &&
                   Enum.TryParse(tag, ignoreCase: true, out source);
        }

        /// <summary>
        /// Shows the screenshot frame full-monitor over Playnite so themes can be checked at
        /// real scale. Reproduces the compositor's 1080-DIP virtual canvas exactly (Viewbox
        /// Fill onto the monitor), so what is shown matches what gets stamped onto saved
        /// images. Dismissed by click, Escape, or a 10s auto-close timer.
        /// </summary>
        private void FireFrame_Click(object sender, RoutedEventArgs e)
        {
            if (!TryResolvePreviewSource(sender, out var source))
            {
                return;
            }

            var kind = FrameSampleSelector?.SelectedValue as string ?? "rare";
            var persisted = _settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            _toastEditorViewModel?.FlushPendingPersist();
            _frameEditorViewModel?.FlushPendingPersist();

            CloseFramePreview();

            var template = _toastTemplateResolver.ResolvePreviewTemplate(source, isFrame: true);
            if (template == null)
            {
                return;
            }

            var window = Views.Helpers.PlayniteUiProvider.CreateBorderlessTopmostWindow(
                _plugin.PlayniteApi,
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"));
            window.SizeToContent = SizeToContent.Manual;
            window.ShowActivated = true;
            window.Focusable = true;

            var reference = _plugin.PlayniteApi?.Dialogs?.GetCurrentAppWindow() ?? Window.GetWindow(this);
            var monitorPixels = Views.Helpers.PlayniteUiProvider.PlaceOnWindowMonitor(window, reference);
            if (monitorPixels == null)
            {
                return;
            }

            var (canvasWidth, canvasHeight, _) = ScreenshotFrameCompositor.ComputeCanvas(
                monitorPixels.Value.Width,
                monitorPixels.Value.Height);
            var canvas = new Grid
            {
                Width = canvasWidth,
                Height = canvasHeight,
                // Almost-transparent so the live screen shows through while the window still
                // receives the dismissing click (fully transparent pixels are not hit-testable).
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(1, 0, 0, 0)),
            };
            canvas.Children.Add(new ContentControl
            {
                Content = new AchievementToastViewModel(
                    ToastPreviewFactory.BuildPreviewArgs(kind, EffectiveSampleProviderKey),
                    persisted,
                    _currentStyle),
                ContentTemplate = template,
            });
            window.Content = new System.Windows.Controls.Viewbox
            {
                Stretch = System.Windows.Media.Stretch.Fill,
                Child = canvas,
            };

            var autoClose = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10),
            };
            autoClose.Tick += (s, args) => window.Close();
            window.PreviewMouseDown += (s, args) => window.Close();
            window.PreviewKeyDown += (s, args) =>
            {
                if (args.Key == System.Windows.Input.Key.Escape)
                {
                    args.Handled = true;
                    window.Close();
                }
            };
            window.Closed += (s, args) =>
            {
                autoClose.Stop();
                if (ReferenceEquals(_framePreviewWindow, window))
                {
                    _framePreviewWindow = null;
                }
            };

            _framePreviewWindow = window;
            window.Show();
            window.Focus();
            autoClose.Start();
        }

        private void CloseFramePreview()
        {
            try
            {
                _framePreviewWindow?.Close();
            }
            catch
            {
            }

            _framePreviewWindow = null;
        }

        public void Dispose()
        {
            _persistedSubscription?.Dispose();
            _toastEditorViewModel?.Dispose();
            _frameEditorViewModel?.Dispose();
            CloseFramePreview();
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }

    /// <summary>
    /// One entry of the appearance platform selector: the global default (null key) or a
    /// provider, with the same icon+name visuals as the overrides grid platform cell.
    /// </summary>
    internal sealed class NotificationStylePlatformOption
    {
        public NotificationStylePlatformOption(string providerKey)
        {
            Key = providerKey;
            DisplayName = ProviderRegistry.GetLocalizedName(providerKey);
            ProviderRegistry.TryResolveProviderVisuals(providerKey, out var iconKey, out _);
            ProviderIconKey = iconKey;
        }

        private NotificationStylePlatformOption()
        {
        }

        public static NotificationStylePlatformOption CreateDefault()
        {
            return new NotificationStylePlatformOption
            {
                DisplayName = ResourceProvider.GetString("LOCPlayAch_Common_Default")
            };
        }

        /// <summary>Provider key, or null for the global default entry.</summary>
        public string Key { get; private set; }

        public string DisplayName { get; private set; }

        public string ProviderIconKey { get; private set; }

        public string ProviderColorHex => Key == null ? null : ProviderRegistry.GetProviderColorHex(Key);

        public bool HasProviderIcon => !string.IsNullOrWhiteSpace(ProviderIconKey);
    }
}
