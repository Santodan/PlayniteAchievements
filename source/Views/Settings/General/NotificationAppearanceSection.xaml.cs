using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.Notifications;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Settings;
using PlayniteAchievements.Views.Dialogs;
using PlayniteAchievements.Views.Helpers;

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
        private readonly Guid _gameId;
        private readonly string _gameProviderKey;
        private string _selectedProviderKey;
        private readonly string _fallbackSampleProviderKey;
        private NotificationStyleSettings _currentStyle;
        private bool _currentToastUseThemeStyling = true;
        private bool _currentFrameUseThemeStyling = true;
        private bool _suppressCustomizeEvents;
        private bool _suppressThemeStylingEvents;

        private bool IsGameMode => _gameId != Guid.Empty;

        public NotificationAppearanceSection()
        {
            InitializeComponent();
        }

        internal NotificationAppearanceSection(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ILogger logger,
            Guid gameId = default,
            string gameProviderKey = null)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _logger = logger;
            _gameId = gameId;
            _gameProviderKey = string.IsNullOrWhiteSpace(gameProviderKey)
                ? null
                : gameProviderKey.Trim();

            _toastTemplateResolver = new AchievementToastTemplateResolver(
                plugin.PlayniteApi,
                logger,
                customTemplatesDirectory: AchievementToastTemplateResolver.GetCustomTemplatesDirectory(
                    plugin.GetPluginUserDataPath()));

            // A sample provider so the mock and fire-tests always show a provider icon, even
            // when the global default (no platform selected) is being edited.
            _fallbackSampleProviderKey = _gameProviderKey ??
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

            if (IsGameMode)
            {
                PlatformHeader.Visibility = Visibility.Collapsed;
                PlatformSelectorPanel.Visibility = Visibility.Collapsed;
                FollowDefaultHint.Visibility = Visibility.Collapsed;
                GameSelectionPanel.Visibility = Visibility.Visible;
            }
            else
            {
                PlatformSelector.ItemsSource = BuildPlatformOptions();
                PlatformSelector.SelectedIndex = 0;
            }

            _persistedSubscription = new PersistedSettingsSubscription(
                _settings,
                OnPersistedPropertyChanged,
                ApplySelection);

            ApplySelection();
            Loaded += (s, e) =>
            {
                UpdateMockups();
                RefreshFireButtons();
                RefreshPresetOptions();
            };
        }

        /// <summary>
        /// The provider key used for sample/fire content: the selected platform, or a fallback
        /// sample provider so the global-default preview still shows a provider icon.
        /// </summary>
        private string EffectiveSampleProviderKey =>
            IsGameMode
                ? ResolveGameProviderKey() ?? _fallbackSampleProviderKey
                : _selectedProviderKey ?? _fallbackSampleProviderKey;

        private string ResolveGameProviderKey()
        {
            if (!IsGameMode)
            {
                return null;
            }

            return _plugin?.AchievementDataService
                       ?.GetGameAchievementData(_gameId)
                       ?.EffectiveProviderKey ??
                   _gameProviderKey;
        }

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
            if (IsGameMode)
            {
                ApplyGameSelection();
                return;
            }

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
            _currentToastUseThemeStyling = persisted.ToastUseThemeStyling;
            _currentFrameUseThemeStyling = persisted.FrameUseThemeStyling;
            ApplyThemeStylingControls(editable: true);
            _toastEditorViewModel.SetStyle(style, editable ? option.Key : null, editable);
            _frameEditorViewModel.SetStyle(style, editable ? option.Key : null, editable);
            UpdateMockups();
        }

        private void ApplyGameSelection()
        {
            var persisted = _settings?.Persisted;
            var store = _plugin?.GameCustomDataStore;
            if (persisted == null || store == null ||
                _toastEditorViewModel == null || _frameEditorViewModel == null)
            {
                return;
            }

            var hasOverride = store.TryLoad(_gameId, out var customData) &&
                              customData?.NotificationAppearanceOverride?.Style != null;
            var appearance = customData?.NotificationAppearanceOverride;
            var providerKey = ResolveGameProviderKey();
            _currentStyle = hasOverride
                ? appearance.Style
                : NotificationStyleResolver.Resolve(persisted, providerKey);
            _currentToastUseThemeStyling = hasOverride
                ? appearance.ToastUseThemeStyling
                : persisted.ToastUseThemeStyling;
            _currentFrameUseThemeStyling = hasOverride
                ? appearance.FrameUseThemeStyling
                : persisted.FrameUseThemeStyling;

            _suppressCustomizeEvents = true;
            CustomizeGameCheckBox.IsChecked = hasOverride;
            _suppressCustomizeEvents = false;

            var providerName = !string.IsNullOrWhiteSpace(providerKey)
                ? ProviderRegistry.GetLocalizedName(providerKey)
                : L("LOCPlayAch_Common_Default");
            GameInheritanceHint.Text = hasOverride
                ? L("LOCPlayAch_ManageAchievements_Notifications_SnapshotHint")
                : string.Format(
                    L("LOCPlayAch_ManageAchievements_Notifications_InheritHint"),
                    providerName);

            ApplyThemeStylingControls(hasOverride);
            var owner = NotificationImageOwner.ForGame(_gameId);
            Action<NotificationStyleSettings> persist = hasOverride
                ? PersistGameStyle
                : (Action<NotificationStyleSettings>)null;
            _toastEditorViewModel.SetStyle(_currentStyle, owner, hasOverride, persist);
            _frameEditorViewModel.SetStyle(_currentStyle, owner, hasOverride, persist);
            UpdateMockups();
        }

        private void ApplyThemeStylingControls(bool editable)
        {
            _suppressThemeStylingEvents = true;
            ToastThemeStylingCheckBox.IsChecked = _currentToastUseThemeStyling;
            FrameThemeStylingCheckBox.IsChecked = _currentFrameUseThemeStyling;
            ToastThemeStylingCheckBox.IsEnabled = editable;
            FrameThemeStylingCheckBox.IsEnabled = editable;
            _suppressThemeStylingEvents = false;
        }

        /// <summary>
        /// Sets the plain-language line under each surface's tester naming, in words, where the
        /// shown style and look come from right now: the current scope (global / a platform / this
        /// game) and the look (the plugin, the active theme, or an imported template). Recomputed
        /// on every selection, toggle, import, and revert so it always matches the preview.
        /// </summary>
        private void RefreshSourceSummary()
        {
            if (ToastSourceSummary == null || FrameSourceSummary == null)
            {
                return;
            }

            ToastSourceSummary.Text = BuildSourceSummary(isFrame: false, _currentToastUseThemeStyling);
            FrameSourceSummary.Text = BuildSourceSummary(isFrame: true, _currentFrameUseThemeStyling);
        }

        private string BuildSourceSummary(bool isFrame, bool useThemeStyling)
        {
            string scope;
            if (IsGameMode)
            {
                scope = L("LOCPlayAch_Settings_Style_SourceScope_Game");
            }
            else if (string.IsNullOrWhiteSpace(_selectedProviderKey))
            {
                scope = L("LOCPlayAch_Settings_Style_SourceScope_Global");
            }
            else
            {
                scope = string.Format(
                    L("LOCPlayAch_Settings_Style_SourceScope_Platform"),
                    ProviderRegistry.GetLocalizedName(_selectedProviderKey));
            }

            string look;
            if (useThemeStyling && _toastTemplateResolver != null &&
                _toastTemplateResolver.ThemeProvidesTemplate(NotificationTemplatePreviewSource.ActiveTheme, isFrame))
            {
                look = L("LOCPlayAch_Settings_Style_SourceLook_Theme");
            }
            else if (_toastTemplateResolver != null &&
                     !string.IsNullOrWhiteSpace(
                         _toastTemplateResolver.ResolveCustomTemplatePath(isFrame, ScopeProviderKey, ScopeGameId)))
            {
                look = L("LOCPlayAch_Settings_Style_SourceLook_Imported");
            }
            else
            {
                look = L("LOCPlayAch_Settings_Style_SourceLook_Plugin");
            }

            return string.Format(L("LOCPlayAch_Settings_Style_SourceSummary"), scope, look);
        }

        private void PersistGameStyle(NotificationStyleSettings style)
        {
            if (!IsGameMode || style == null)
            {
                return;
            }

            _plugin.GameCustomDataStore?.Update(_gameId, data =>
            {
                var appearance = data.NotificationAppearanceOverride;
                if (appearance == null)
                {
                    return;
                }

                appearance.Style = style.Clone();
            });
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

        private async void CustomizeGameCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressCustomizeEvents || !IsGameMode)
            {
                return;
            }

            var persisted = _settings?.Persisted;
            var customDataStore = _plugin?.GameCustomDataStore;
            if (persisted == null || customDataStore == null)
            {
                return;
            }

            try
            {
                _toastEditorViewModel?.FlushPendingPersist();
                _frameEditorViewModel?.FlushPendingPersist();

                if (CustomizeGameCheckBox.IsChecked == true)
                {
                    var copy = NotificationStyleResolver
                        .Resolve(persisted, ResolveGameProviderKey())
                        .Clone();
                    await _plugin.NotificationImageStore.CopyImagesForGameAsync(
                        copy,
                        _gameId,
                        CancellationToken.None);
                    customDataStore.Update(_gameId, data =>
                    {
                        data.NotificationAppearanceOverride =
                            new GameNotificationAppearanceOverride
                            {
                                Style = copy,
                                ToastUseThemeStyling = persisted.ToastUseThemeStyling,
                                FrameUseThemeStyling = persisted.FrameUseThemeStyling
                            };
                    });
                }
                else
                {
                    var result = _plugin.PlayniteApi.Dialogs.ShowMessage(
                        L("LOCPlayAch_ManageAchievements_Notifications_RevertConfirm"),
                        L("LOCPlayAch_Title_PluginName"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);
                    if (result != MessageBoxResult.Yes)
                    {
                        _suppressCustomizeEvents = true;
                        CustomizeGameCheckBox.IsChecked = true;
                        _suppressCustomizeEvents = false;
                        return;
                    }

                    customDataStore.Update(
                        _gameId,
                        data => data.NotificationAppearanceOverride = null);
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to toggle notification style customization for game {_gameId}.");
            }

            ApplySelection();
        }

        private void ThemeStylingCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressThemeStylingEvents)
            {
                return;
            }

            var toastValue = ToastThemeStylingCheckBox.IsChecked == true;
            var frameValue = FrameThemeStylingCheckBox.IsChecked == true;
            if (IsGameMode)
            {
                if (CustomizeGameCheckBox.IsChecked != true)
                {
                    ApplyGameSelection();
                    return;
                }

                _currentToastUseThemeStyling = toastValue;
                _currentFrameUseThemeStyling = frameValue;
                _plugin.GameCustomDataStore?.Update(_gameId, data =>
                {
                    var appearance = data.NotificationAppearanceOverride;
                    if (appearance == null)
                    {
                        return;
                    }

                    appearance.ToastUseThemeStyling = toastValue;
                    appearance.FrameUseThemeStyling = frameValue;
                });
            }
            else
            {
                var persisted = _settings?.Persisted;
                if (persisted == null)
                {
                    return;
                }

                persisted.ToastUseThemeStyling = toastValue;
                persisted.FrameUseThemeStyling = frameValue;
                _currentToastUseThemeStyling = toastValue;
                _currentFrameUseThemeStyling = frameValue;
                _plugin.PersistSettingsForUi();
            }

            UpdateMockups();
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
                if (IsGameMode && CustomizeGameCheckBox?.IsChecked != true)
                {
                    ApplySelection();
                    return;
                }

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
                if (name == nameof(PersistedSettings.ToastUseThemeStyling) ||
                    name == nameof(PersistedSettings.FrameUseThemeStyling))
                {
                    if (!IsGameMode || CustomizeGameCheckBox?.IsChecked != true)
                    {
                        _currentToastUseThemeStyling =
                            _settings?.Persisted?.ToastUseThemeStyling ?? true;
                        _currentFrameUseThemeStyling =
                            _settings?.Persisted?.FrameUseThemeStyling ?? true;
                        ApplyThemeStylingControls(editable: !IsGameMode);
                    }
                }

                UpdateMockups();
            }
        }

        /// <summary>
        /// Rebuilds both inline mockups from the resolved templates and the style being edited
        /// so every toggle, reorder, image, and font change previews live.
        /// </summary>
        // The custom-template scope for the current selection: a game in game mode, else the
        // selected provider (null = global). Mirrors how the .pastyle style scope is chosen.
        private string ScopeProviderKey => IsGameMode ? null : _selectedProviderKey;

        private Guid ScopeGameId => IsGameMode ? _gameId : Guid.Empty;

        private void UpdateMockups()
        {
            // The source summary needs no mockup hosts, so refresh it before the host guard.
            RefreshSourceSummary();

            var persisted = _settings?.Persisted;
            if (persisted == null || ToastMockupHost == null || FrameMockupHost == null ||
                _toastTemplateResolver == null)
            {
                return;
            }

            // The toast mockup is built through the same ToastSurfaceFactory the live toast wave
            // uses (single-item list), so the inline preview and the fired notification cannot
            // drift. The sample kind mirrors the fire-test dropdown so the preview shows whatever
            // firing would produce; a null preview source keeps ResolveTemplate parity with a real
            // unlock.
            var toastKind = NotificationSampleSelector?.SelectedValue as string ?? "rare";
            var toastItems = new[]
            {
                new AchievementToastViewModel(
                    BuildPreviewArgs(toastKind),
                    persisted,
                    _currentStyle,
                    gameCustomDataStore: null,
                    toastUseThemeStylingOverride: _currentToastUseThemeStyling,
                    frameUseThemeStylingOverride: _currentFrameUseThemeStyling),
            };
            var toastTemplate = ToastSurfaceFactory.ResolveToastTemplate(
                _toastTemplateResolver, toastItems, _currentToastUseThemeStyling, ScopeProviderKey, ScopeGameId);
            ToastMockupHost.ContentTemplate = null;
            ToastMockupHost.Content = ToastSurfaceFactory.BuildToastSurface(toastItems, toastTemplate);

            // The frame surface already shares one ContentControl path with its offscreen capture
            // pipeline, so it stays a single-VM host; only the sample kind is mirrored here.
            var frameKind = FrameSampleSelector?.SelectedValue as string ?? "rare";
            FrameMockupHost.ContentTemplate =
                _toastTemplateResolver.ResolveFrameTemplate(_currentFrameUseThemeStyling, ScopeProviderKey, ScopeGameId);
            FrameMockupHost.Content = new AchievementToastViewModel(
                BuildPreviewArgs(frameKind),
                persisted,
                _currentStyle,
                gameCustomDataStore: null,
                toastUseThemeStylingOverride: _currentToastUseThemeStyling,
                frameUseThemeStylingOverride: _currentFrameUseThemeStyling);
        }

        // Both sample-kind dropdowns refresh the inline mockups through one handler so the preview
        // mirrors whatever a fire-test would show.
        private void SampleSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateMockups();
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

            // Tag the sample unlock with the scope being edited (per-provider via ProviderKey,
            // per-game via PlayniteGameId) so the provider icon / game art match the mockup, and
            // carry the exact edited style so the fired notification renders IDENTICALLY to the
            // inline mockup instead of re-resolving (which could pick up the sample provider's own
            // per-provider override and differ, e.g. the description's 1- vs 2-line budget).
            var args = BuildPreviewArgs(kind, providerKey: ScopeProviderKey, previewSource: source);
            if (IsGameMode)
            {
                args.PlayniteGameId = _gameId;
            }

            args.PreviewStyleOverride = _currentStyle;

            PlayniteAchievementsPlugin.NotifyAchievementUnlocked(args);
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

            var template = _toastTemplateResolver.ResolvePreviewTemplate(source, isFrame: true, ScopeProviderKey, ScopeGameId);
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
                    BuildPreviewArgs(kind),
                    persisted,
                    _currentStyle,
                    gameCustomDataStore: null,
                    toastUseThemeStylingOverride: _currentToastUseThemeStyling,
                    frameUseThemeStylingOverride: _currentFrameUseThemeStyling),
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

        private AchievementUnlockedEventArgs BuildPreviewArgs(
            string kind,
            string providerKey = null,
            NotificationTemplatePreviewSource? previewSource = null)
        {
            var args = ToastPreviewFactory.BuildPreviewArgs(
                kind,
                providerKey ?? EffectiveSampleProviderKey,
                previewSource);
            if (!IsGameMode)
            {
                return args;
            }

            args.PlayniteGameId = _gameId;
            try
            {
                var game = _plugin.PlayniteApi?.Database?.Games?.Get(_gameId);
                if (game != null)
                {
                    args.GameName = game.Name;
                    args.GameIconPath = ResolveGameImagePath(game.Icon);
                    args.GameCoverPath = ResolveGameImagePath(game.CoverImage);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed resolving game art for notification preview {_gameId}.");
            }

            return args;
        }

        private string ResolveGameImagePath(string imagePath)
        {
            return string.IsNullOrWhiteSpace(imagePath)
                ? null
                : _plugin.PlayniteApi?.Database?.GetFullFilePath(imagePath);
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

        /// <summary>
        /// Exports the style for the current platform selection to a shareable .pastyle (JSON) or
        /// .pastyle.zip (JSON + bundled images) file. Debounced edits are flushed first so the file
        /// matches what the editors show.
        /// </summary>
        private void ExportStyle_Click(object sender, RoutedEventArgs e)
        {
            var style = _currentStyle;
            var store = _plugin?.NotificationStylePortableStore;
            if (style == null || store == null)
            {
                return;
            }

            try
            {
                _toastEditorViewModel?.FlushPendingPersist();
                _frameEditorViewModel?.FlushPendingPersist();

                var dialog = new SaveFileDialog
                {
                    Filter =
                        "Playnite Achievements Style Package (*.pastyle.zip)|*.pastyle.zip|" +
                        "Playnite Achievements Style (*.pastyle)|*.pastyle",
                    AddExtension = true,
                    DefaultExt = ".zip",
                    FileName = BuildDefaultStyleFileName()
                };

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                // FilterIndex is 1-based: 1 = package (images), 2 = plain JSON.
                var usePackage = dialog.FilterIndex != 2;
                var extension = usePackage
                    ? NotificationStylePortableStore.PackageFileExtension
                    : NotificationStylePortableStore.FileExtension;
                var destinationPath = NotificationStylePortableStore.NormalizeExportPath(
                    dialog.FileName, extension);

                if (usePackage)
                {
                    // A package can also carry the toast and/or frame template, but only a template
                    // the user actually authored for this scope: that is portable loose XAML that
                    // passed validation on install. The active theme's override is never bundled
                    // (it is theme-coupled and would import broken), so when nothing is authored the
                    // prompt is skipped and the package is data-only. Users who want a working
                    // template to start from use the separate "export default template" action.
                    string toastTemplateXaml = null;
                    string frameTemplateXaml = null;
                    var resolver = _toastTemplateResolver;
                    if (resolver != null)
                    {
                        var customToast = resolver.ReadCustomTemplateXaml(isFrame: false, ScopeProviderKey, ScopeGameId);
                        if (customToast != null &&
                            Confirm(L("LOCPlayAch_Settings_Style_ExportIncludeToastTemplate")))
                        {
                            toastTemplateXaml = customToast;
                        }

                        var customFrame = resolver.ReadCustomTemplateXaml(isFrame: true, ScopeProviderKey, ScopeGameId);
                        if (customFrame != null &&
                            Confirm(L("LOCPlayAch_Settings_Style_ExportIncludeFrameTemplate")))
                        {
                            frameTemplateXaml = customFrame;
                        }
                    }

                    store.ExportPackage(style, destinationPath, toastTemplateXaml, frameTemplateXaml);
                }
                else
                {
                    store.ExportPa(style, destinationPath);
                }

                _plugin.PlayniteApi?.Dialogs?.ShowMessage(
                    L("LOCPlayAch_Status_Succeeded") + "\n" + destinationPath,
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed exporting notification style.");
                _plugin.PlayniteApi?.Dialogs?.ShowMessage(
                    string.Format(L("LOCPlayAch_Status_Failed"), ex.Message),
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Imports a .pastyle/.pastyle.zip onto the current platform selection, replacing the
        /// selected style (creating the provider's whole-style copy if it was following the
        /// default). Bundled images are re-materialized into managed storage.
        /// </summary>
        private async void ImportStyle_Click(object sender, RoutedEventArgs e)
        {
            var persisted = _settings?.Persisted;
            var store = _plugin?.NotificationStylePortableStore;
            if (persisted == null || store == null)
            {
                return;
            }

            try
            {
                var dialog = new OpenFileDialog
                {
                    Filter =
                        "Playnite Achievements Style Files (*.pastyle;*.pastyle.zip)|*.pastyle;*.pastyle.zip|" +
                        "Playnite Achievements Style Package (*.pastyle.zip)|*.pastyle.zip|" +
                        "Playnite Achievements Style (*.pastyle)|*.pastyle",
                    CheckFileExists = true,
                    Multiselect = false
                };

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                var contents = store.InspectPackage(dialog.FileName);
                var resolver = _toastTemplateResolver;
                var offerTemplates = resolver != null &&
                    (contents.HasToastTemplate || contents.HasFrameTemplate);

                bool applyStyle;
                var installToast = false;
                var installFrame = false;

                if (!offerTemplates)
                {
                    // Style-only file: single confirmation, apply the style (unchanged behavior).
                    if (!Confirm(L("LOCPlayAch_Settings_Style_ImportConfirm")))
                    {
                        return;
                    }

                    applyStyle = true;
                }
                else
                {
                    // The package carries one or both templates: let the user pick any combination
                    // of the available parts to apply.
                    applyStyle = contents.HasStyle &&
                        Confirm(L("LOCPlayAch_Settings_Style_ImportApplyStyle"));
                    installToast = contents.HasToastTemplate &&
                        Confirm(L("LOCPlayAch_Settings_Style_ImportInstallToastTemplate"));
                    installFrame = contents.HasFrameTemplate &&
                        Confirm(L("LOCPlayAch_Settings_Style_ImportInstallFrameTemplate"));
                    if (!applyStyle && !installToast && !installFrame)
                    {
                        return;
                    }
                }

                _toastEditorViewModel?.FlushPendingPersist();
                _frameEditorViewModel?.FlushPendingPersist();

                if (applyStyle)
                {
                    var providerKey = _selectedProviderKey;
                    var owner = IsGameMode
                        ? NotificationImageOwner.ForGame(_gameId)
                        : NotificationImageOwner.ForProvider(providerKey);
                    var imported = await store.ImportAsync(
                        dialog.FileName,
                        owner,
                        CancellationToken.None);
                    if (imported == null)
                    {
                        throw new InvalidOperationException("Imported notification style was empty.");
                    }

                    ApplyImportedStyle(persisted, providerKey, imported);

                    if (!IsGameMode)
                    {
                        _plugin.PersistSettingsForUi();
                    }

                    // Drop slot files the replaced style no longer references.
                    _plugin.NotificationImageStore.PruneOrphans(
                        persisted,
                        _plugin.GameCustomDataStore?.LoadAll());
                }

                var templateErrors = new List<string>();
                if (installToast)
                {
                    InstallImportedTemplate(store, resolver, dialog.FileName, isFrame: false, templateErrors);
                }

                if (installFrame)
                {
                    InstallImportedTemplate(store, resolver, dialog.FileName, isFrame: true, templateErrors);
                }

                ApplySelection();
                UpdateMockups();

                if (templateErrors.Count > 0)
                {
                    _plugin.PlayniteApi?.Dialogs?.ShowMessage(
                        string.Format(L("LOCPlayAch_Status_Failed"), string.Join("\n", templateErrors)),
                        L("LOCPlayAch_Title_PluginName"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                else
                {
                    _plugin.PlayniteApi?.Dialogs?.ShowMessage(
                        L("LOCPlayAch_Status_Succeeded"),
                        L("LOCPlayAch_Title_PluginName"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed importing notification style.");
                _plugin.PlayniteApi?.Dialogs?.ShowMessage(
                    string.Format(L("LOCPlayAch_Status_Failed"), ex.Message),
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private bool Confirm(string message)
        {
            return _plugin.PlayniteApi.Dialogs.ShowMessage(
                message,
                L("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        /// <summary>
        /// Applies an imported appearance style to the current platform/game target, creating a
        /// provider whole-style copy or per-game override as needed.
        /// </summary>
        private void ApplyImportedStyle(
            Models.Settings.PersistedSettings persisted,
            string providerKey,
            NotificationStyleSettings imported)
        {
            if (IsGameMode)
            {
                _plugin.GameCustomDataStore.Update(_gameId, data =>
                {
                    var existing = data.NotificationAppearanceOverride;
                    data.NotificationAppearanceOverride =
                        new GameNotificationAppearanceOverride
                        {
                            Style = imported,
                            ToastUseThemeStyling =
                                existing?.ToastUseThemeStyling ?? persisted.ToastUseThemeStyling,
                            FrameUseThemeStyling =
                                existing?.FrameUseThemeStyling ?? persisted.FrameUseThemeStyling
                        };
                });
            }
            else if (providerKey == null)
            {
                persisted.NotificationStyle = imported;
            }
            else
            {
                persisted.SetProviderNotificationStyle(providerKey, imported);
            }
        }

        /// <summary>
        /// Reads a surface's embedded template from the package and installs it into the plugin-owned
        /// custom-template tier. Validation lives in the resolver; a failure is collected (not thrown)
        /// so one bad template does not abort the rest of the import.
        /// </summary>
        private void InstallImportedTemplate(
            NotificationStylePortableStore store,
            AchievementToastTemplateResolver resolver,
            string sourcePath,
            bool isFrame,
            List<string> errors)
        {
            try
            {
                var xaml = store.ReadTemplateXaml(sourcePath, isFrame);
                if (string.IsNullOrWhiteSpace(xaml))
                {
                    return;
                }

                resolver.SaveCustomTemplate(isFrame, xaml, ScopeProviderKey, ScopeGameId);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed installing custom {(isFrame ? "frame" : "toast")} template.");
                errors.Add(ex.Message);
            }
        }

        private bool IsFrameTabActive => FrameTabItem?.IsSelected == true;

        private NotificationStylePresetInfo SelectedPreset =>
            PresetSelector?.SelectedItem as NotificationStylePresetInfo;

        /// <summary>
        /// Repopulates the preset dropdown with the active surface tab's saved presets behind a
        /// "None" placeholder, selecting <paramref name="selectName"/> when given (e.g. right
        /// after saving) and the placeholder otherwise.
        /// </summary>
        private void RefreshPresetOptions(string selectName = null)
        {
            var store = _plugin?.NotificationStylePresetStore;
            if (PresetSelector == null || store == null)
            {
                return;
            }

            var items = new List<object> { L("LOCPlayAch_Common_None") };
            try
            {
                items.AddRange(store.ListPresets(IsFrameTabActive));
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed listing notification appearance presets.");
            }

            PresetSelector.ItemsSource = items;
            PresetSelector.SelectedItem = string.IsNullOrWhiteSpace(selectName)
                ? items[0]
                : items.OfType<NotificationStylePresetInfo>().FirstOrDefault(preset =>
                      string.Equals(preset.Name, selectName, StringComparison.OrdinalIgnoreCase)) ??
                  items[0];
            RefreshPresetButtons();
        }

        private void RefreshPresetButtons()
        {
            if (ApplyPresetButton == null || DeletePresetButton == null)
            {
                return;
            }

            ApplyPresetButton.IsEnabled = DeletePresetButton.IsEnabled = SelectedPreset != null;
        }

        private void SurfaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // SelectionChanged bubbles up from ComboBoxes inside the tabs; only a tab switch
            // should swap the preset list.
            if (!ReferenceEquals(e.OriginalSource, SurfaceTabs))
            {
                return;
            }

            RefreshPresetOptions();
        }

        private void PresetSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshPresetButtons();
        }

        /// <summary>
        /// Saves the active surface tab's appearance as a named preset: the surface style, its
        /// images (toast only), and the current scope's custom template when one is installed.
        /// Debounced edits are flushed first so the preset matches what the editors show.
        /// </summary>
        private void SavePreset_Click(object sender, RoutedEventArgs e)
        {
            var style = _currentStyle;
            var store = _plugin?.NotificationStylePresetStore;
            if (style == null || store == null)
            {
                return;
            }

            try
            {
                _toastEditorViewModel?.FlushPendingPersist();
                _frameEditorViewModel?.FlushPendingPersist();

                var isFrame = IsFrameTabActive;
                if (!TryPromptPresetName(SelectedPreset?.Name, out var name))
                {
                    return;
                }

                var exists = store.PresetExists(isFrame, name);
                if (!exists &&
                    store.CountPresets(isFrame) >= NotificationStylePresetStore.MaxPresetCount)
                {
                    _plugin.PlayniteApi?.Dialogs?.ShowMessage(
                        string.Format(
                            L("LOCPlayAch_Presets_MaxReached"),
                            NotificationStylePresetStore.MaxPresetCount),
                        L("LOCPlayAch_Title_PluginName"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                if (exists &&
                    !Confirm(string.Format(L("LOCPlayAch_Presets_OverwriteConfirm"), name)))
                {
                    return;
                }

                var templateXaml = _toastTemplateResolver?.ReadCustomTemplateXaml(
                    isFrame, ScopeProviderKey, ScopeGameId);
                store.SavePreset(isFrame, name, style, templateXaml);
                RefreshPresetOptions(selectName: name);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed saving notification appearance preset.");
                _plugin.PlayniteApi?.Dialogs?.ShowMessage(
                    string.Format(L("LOCPlayAch_Status_Failed"), ex.Message),
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private bool TryPromptPresetName(string defaultName, out string presetName)
        {
            presetName = null;

            var inputDialog = new TextInputDialog(
                L("LOCPlayAch_Presets_NameDialogHint"),
                defaultName ?? string.Empty);

            var window = PlayniteUiProvider.CreateExtensionWindow(
                L("LOCPlayAch_Presets_NameDialogTitle"),
                inputDialog,
                new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    ShowCloseButton = true,
                    CanBeResizable = false,
                    Width = 460,
                    Height = 200
                });

            try
            {
                if (window.Owner == null)
                {
                    window.Owner = _plugin.PlayniteApi?.Dialogs?.GetCurrentAppWindow();
                }
            }
            catch
            {
            }

            inputDialog.RequestClose += (s, e) => window.Close();
            window.ShowDialog();

            if (inputDialog.DialogResult != true)
            {
                return false;
            }

            var sanitized = NotificationStylePresetStore.SanitizeName(inputDialog.InputText);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                _plugin.PlayniteApi?.Dialogs?.ShowMessage(
                    string.Format(
                        L("LOCPlayAch_Presets_NameInvalid"),
                        NotificationStylePresetStore.MaxNameLength),
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            presetName = sanitized;
            return true;
        }

        /// <summary>
        /// Applies the selected preset to the current platform/game selection, replacing only
        /// the preset's surface: its style, its images (toast only), and its custom template.
        /// A preset saved without a template removes the target scope's template so the applied
        /// look always matches what was saved.
        /// </summary>
        private async void ApplyPreset_Click(object sender, RoutedEventArgs e)
        {
            var preset = SelectedPreset;
            var persisted = _settings?.Persisted;
            var store = _plugin?.NotificationStylePresetStore;
            var style = _currentStyle;
            if (preset == null || persisted == null || store == null || style == null)
            {
                return;
            }

            try
            {
                var isFrame = preset.IsFrame;
                if (!Confirm(string.Format(L("LOCPlayAch_Presets_ApplyConfirm"), preset.Name)))
                {
                    return;
                }

                _toastEditorViewModel?.FlushPendingPersist();
                _frameEditorViewModel?.FlushPendingPersist();

                var providerKey = _selectedProviderKey;
                var owner = IsGameMode
                    ? NotificationImageOwner.ForGame(_gameId)
                    : NotificationImageOwner.ForProvider(providerKey);

                // The merge base keeps the untouched surface intact. When the target scope is
                // still following an inherited style, snapshot the inherited images into the
                // scope first so the new copy never references another owner's slot files.
                var merged = style.Clone();
                if (!IsGameMode && providerKey != null &&
                    persisted.GetProviderNotificationStyle(providerKey) == null)
                {
                    await _plugin.NotificationImageStore.CopyImagesForProviderAsync(
                        merged, providerKey, CancellationToken.None);
                }
                else if (IsGameMode && CustomizeGameCheckBox?.IsChecked != true)
                {
                    await _plugin.NotificationImageStore.CopyImagesForGameAsync(
                        merged, _gameId, CancellationToken.None);
                }

                var imported = await store.LoadPresetStyleAsync(preset, owner, CancellationToken.None);
                if (imported == null)
                {
                    throw new InvalidOperationException("Preset notification style was empty.");
                }

                // The preset's surface replaces the target surface wholesale, badge images and
                // header texts included; a toast preset also carries the toast-only background.
                if (isFrame)
                {
                    merged.Frame = imported.Frame;
                }
                else
                {
                    merged.Toast = imported.Toast;
                    merged.ToastBackgroundImagePath = imported.ToastBackgroundImagePath;
                }

                ApplyImportedStyle(persisted, providerKey, merged);

                if (!IsGameMode)
                {
                    _plugin.PersistSettingsForUi();
                }

                // Drop slot files the replaced style no longer references.
                _plugin.NotificationImageStore.PruneOrphans(
                    persisted,
                    _plugin.GameCustomDataStore?.LoadAll());

                var templateErrors = new List<string>();
                try
                {
                    var xaml = store.ReadPresetTemplateXaml(preset);
                    if (!string.IsNullOrWhiteSpace(xaml))
                    {
                        _toastTemplateResolver.SaveCustomTemplate(
                            isFrame, xaml, ScopeProviderKey, ScopeGameId);
                    }
                    else if (_toastTemplateResolver.HasCustomTemplate(
                                 isFrame, ScopeProviderKey, ScopeGameId))
                    {
                        _toastTemplateResolver.DeleteCustomTemplate(
                            isFrame, ScopeProviderKey, ScopeGameId);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, $"Failed applying preset {(isFrame ? "frame" : "toast")} template.");
                    templateErrors.Add(ex.Message);
                }

                ApplySelection();
                UpdateMockups();

                if (templateErrors.Count > 0)
                {
                    _plugin.PlayniteApi?.Dialogs?.ShowMessage(
                        string.Format(L("LOCPlayAch_Status_Failed"), string.Join("\n", templateErrors)),
                        L("LOCPlayAch_Title_PluginName"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed applying notification appearance preset.");
                _plugin.PlayniteApi?.Dialogs?.ShowMessage(
                    string.Format(L("LOCPlayAch_Status_Failed"), ex.Message),
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void DeletePreset_Click(object sender, RoutedEventArgs e)
        {
            var preset = SelectedPreset;
            var store = _plugin?.NotificationStylePresetStore;
            if (preset == null || store == null)
            {
                return;
            }

            if (!Confirm(string.Format(L("LOCPlayAch_Presets_DeleteConfirm"), preset.Name)))
            {
                return;
            }

            try
            {
                store.DeletePreset(preset);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed deleting notification appearance preset.");
                _plugin.PlayniteApi?.Dialogs?.ShowMessage(
                    string.Format(L("LOCPlayAch_Status_Failed"), ex.Message),
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            RefreshPresetOptions();
        }

        private void ExportDefaultToastTemplate_Click(object sender, RoutedEventArgs e)
        {
            ExportDefaultTemplate(isFrame: false);
        }

        private void ExportDefaultFrameTemplate_Click(object sender, RoutedEventArgs e)
        {
            ExportDefaultTemplate(isFrame: true);
        }

        /// <summary>
        /// Writes the default template for the surface to a loose <c>.xaml</c> file as a working,
        /// theme-independent starting point the user can edit and re-import. Deliberately separate
        /// from style export: the package never carries this, and this never carries a theme
        /// override, so an imported template always renders.
        /// </summary>
        private void ExportDefaultTemplate(bool isFrame)
        {
            var resolver = _toastTemplateResolver;
            if (resolver == null)
            {
                return;
            }

            try
            {
                var xaml = resolver.ReadDefaultTemplateXaml(isFrame);
                var dialog = new SaveFileDialog
                {
                    Filter = "XAML template (*.xaml)|*.xaml",
                    AddExtension = true,
                    DefaultExt = ".xaml",
                    FileName = isFrame
                        ? AchievementToastTemplateResolver.CustomFrameTemplateFileName
                        : AchievementToastTemplateResolver.CustomToastTemplateFileName
                };

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                File.WriteAllText(
                    dialog.FileName,
                    xaml,
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                _plugin.PlayniteApi?.Dialogs?.ShowMessage(
                    L("LOCPlayAch_Status_Succeeded") + "\n" + dialog.FileName,
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed exporting default notification template.");
                _plugin.PlayniteApi?.Dialogs?.ShowMessage(
                    string.Format(L("LOCPlayAch_Status_Failed"), ex.Message),
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ResetToastStyle_Click(object sender, RoutedEventArgs e)
        {
            ResetStyle(isFrame: false);
        }

        private void ResetFrameStyle_Click(object sender, RoutedEventArgs e)
        {
            ResetStyle(isFrame: true);
        }

        /// <summary>
        /// Resets the surface to its built-in default: clears the editable style fields and
        /// removes any installed custom template, reverting live notifications and the mockup.
        /// Confirmed first, since it discards the user's edits for the surface. A no-op when the
        /// current selection is read-only (a platform without a custom style).
        /// </summary>
        private void ResetStyle(bool isFrame)
        {
            var editor = isFrame ? _frameEditorViewModel : _toastEditorViewModel;
            if (editor == null || !editor.IsEditable)
            {
                return;
            }

            var confirm = _plugin.PlayniteApi.Dialogs.ShowMessage(
                L("LOCPlayAch_Settings_Style_ResetConfirm"),
                L("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                editor.ResetSurfaceToDefault();

                var resolver = _toastTemplateResolver;
                if (resolver != null &&
                    resolver.HasCustomTemplate(isFrame, ScopeProviderKey, ScopeGameId))
                {
                    resolver.DeleteCustomTemplate(isFrame, ScopeProviderKey, ScopeGameId);
                }

                UpdateMockups();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed resetting notification appearance to default.");
                _plugin.PlayniteApi?.Dialogs?.ShowMessage(
                    string.Format(L("LOCPlayAch_Status_Failed"), ex.Message),
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// A file-name-safe default based on the selected platform's display name.
        /// </summary>
        private string BuildDefaultStyleFileName()
        {
            var option = PlatformSelector?.SelectedItem as NotificationStylePlatformOption;
            var name = IsGameMode
                ? _plugin?.PlayniteApi?.Database?.Games?.Get(_gameId)?.Name
                : option?.DisplayName;
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "notification-style";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var sanitized = new string(name.Trim().Where(c => !invalid.Contains(c)).ToArray());
            return string.IsNullOrWhiteSpace(sanitized) ? "notification-style" : sanitized;
        }

        public void RefreshData(bool discardPending = false)
        {
            if (discardPending)
            {
                _toastEditorViewModel?.DiscardPendingPersist();
                _frameEditorViewModel?.DiscardPendingPersist();
            }

            ApplySelection();
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
