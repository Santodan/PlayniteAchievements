using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
// WinForms file dialogs: on .NET Framework the WPF Microsoft.Win32 dialogs render the legacy
// pre-Vista picker (their hook blocks the common-item-dialog upgrade); the WinForms ones
// auto-upgrade to the modern Explorer-style dialog.
using DialogResult = System.Windows.Forms.DialogResult;
using OpenFileDialog = System.Windows.Forms.OpenFileDialog;
using SaveFileDialog = System.Windows.Forms.SaveFileDialog;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.Library;
using PlayniteAchievements.Services.Notifications;
using PlayniteAchievements.Services.Sound;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.Services.Workshop;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Settings;
using PlayniteAchievements.Views.Dialogs;
using PlayniteAchievements.Views.Helpers;
using PlayniteAchievements.Views.Settings.Controls;

namespace PlayniteAchievements.Views.Settings.Notifications
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
        // The scope's own style (global / provider / game). _currentStyle narrows to a kind's
        // copy when one is active; this stays the object that owns the kind styles, which is
        // what a game snapshot has to persist.
        private NotificationStyleSettings _currentScopeStyle;
        private bool _currentToastUseThemeStyling = true;
        private bool _currentFrameUseThemeStyling = true;
        private bool _suppressCustomizeEvents;
        private bool _suppressThemeStylingEvents;
        private bool _suppressSelectionChanged;
        private bool _suppressSurfaceTabEvents;
        private AchievementToastViewModel _toastPreviewViewModel;
        private ToastPreviewBackdrop _toastPreviewBackdrop;
        private ItemsControl _toastPreviewSurface;

        // Whether the toast preview sits on game art: a view option of this editor, never saved.
        // Null follows the style (on while it has a blurred background); the toggle sets it until
        // the blurred background is switched, which hands it back to the style.
        private bool? _previewBackdropChoice;
        private bool? _previewBackdropBlurSeen;
        private bool _suppressPreviewBackdropToggle;
        private string _previewBackdropScope;
        private string _previewBackdropPath;
        private ImageSource _lastToastBackgroundRenderSource;

        private bool IsGameMode => _gameId != Guid.Empty;

        // Game mode: the game's own behavior and capture editors, created when their tab is opened.
        private NotificationBehaviorSection _gameBehaviorSection;
        private NotificationCapturesSection _gameCapturesSection;

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
            _toastEditorViewModel.KeyframePreviewChanged += OnEditorStyleChanged;
            _frameEditorViewModel.StyleChanged += OnEditorStyleChanged;

            // The editors are DataContext islands over the editor view models, independent of
            // this section's inherited settings DataContext.
            ToastEditor.DataContext = _toastEditorViewModel;
            FrameEditor.DataContext = _frameEditorViewModel;
            ToastEditor.ColorPicker = (owner, current) => _plugin.PickColor(owner, current);
            FrameEditor.ColorPicker = (owner, current) => _plugin.PickColor(owner, current);

            // A file edit can make the scope's pack differ from the preset it follows.
            SoundsEditor.Initialize(settings, plugin, logger);
            SoundsEditor.SoundsChanged += (s, e) => StylePresetPicker?.RefreshNow();

            AsyncImage.AddSourceReadyHandler(
                ToastBackgroundAnimationHost,
                OnToastBackgroundSourceChanged);

            if (IsGameMode)
            {
                PlatformSelector.Visibility = Visibility.Collapsed;
                PlatformSelectorPanel.Visibility = Visibility.Collapsed;
                GameSelectionPanel.Visibility = Visibility.Visible;
                BehaviorTabItem.Visibility = Visibility.Visible;
                CapturesTabItem.Visibility = Visibility.Visible;
            }
            else
            {
                // Setting SelectedIndex fires SelectionChanged synchronously; the ctor's single
                // ApplySelection below already covers the initial selection.
                _suppressSelectionChanged = true;
                PlatformSelector.ItemsSource = BuildPlatformOptions();
                PlatformSelector.SelectedIndex = 0;
                _suppressSelectionChanged = false;
            }

            _persistedSubscription = new PersistedSettingsSubscription(
                _settings,
                OnPersistedPropertyChanged,
                ApplySelection);

            StylePresetPicker.Initialize(new LibraryPresetPickerOptions
            {
                Plugin = plugin,
                Settings = settings,
                Target = CurrentPickerTarget,
                TargetChanged = OnPresetApplied,
                Logger = logger
            });

            // Selected here rather than left to the TabControl, which would pick the first tab
            // (Behavior, collapsed outside game mode). A game opens on its Behavior tab; Preselect
            // still moves to a named surface. The ctor's own calls below cover this selection.
            _suppressSurfaceTabEvents = true;
            SurfaceTabs.SelectedItem = IsGameMode ? BehaviorTabItem : ToastTabItem;
            _suppressSurfaceTabEvents = false;

            ApplyGameSettingsTab();
            ApplyPreviewColumns();
            ApplySelection();

            Loaded += (s, e) =>
            {
                // Nothing sits behind this handler but the dispatcher, so it absorbs its own
                // failures; each step is independent and already logs its own detail.
                try
                {
                    UpdateMockups();
                    StylePresetPicker.RefreshNow();
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed initializing the notification appearance section.");
                }
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

        /// <summary>
        /// The kind whose style the editors are pointed at: the one named by the active tab's
        /// sample dropdown, or Base for the plain rarity samples, which always follow the
        /// scope's shared style.
        /// </summary>
        private NotificationKind ActiveKind => ResolveSampleKind(ActiveSampleTag);

        private string ActiveSampleTag =>
            (FrameTabItem?.IsSelected == true ? FrameSampleSelector : NotificationSampleSelector)
                ?.SelectedValue as string;

        private static NotificationKind ResolveSampleKind(string sampleTag)
        {
            switch (sampleTag)
            {
                case "capstone":
                    return NotificationKind.Capstone;
                case "complete":
                    return NotificationKind.Completion;
                case "friend":
                    return NotificationKind.Friend;
                case "progress":
                    return NotificationKind.Progress;
                case "common":
                    return NotificationKind.Common;
                case "uncommon":
                    return NotificationKind.Uncommon;
                case "rare":
                    return NotificationKind.Rare;
                case "ultrarare":
                    return NotificationKind.UltraRare;
                default:
                    return NotificationKind.Base;
            }
        }

        private static string GetKindDisplayName(NotificationKind kind)
        {
            switch (kind)
            {
                case NotificationKind.Capstone:
                    return L("LOCPlayAch_Settings_ToastPreviewCapstone");
                case NotificationKind.Completion:
                    return L("LOCPlayAch_Settings_Style_HeaderCongratulations");
                case NotificationKind.Friend:
                    return L("LOCPlayAch_Settings_Style_HeaderFriendUnlock");
                case NotificationKind.Progress:
                    return L("LOCPlayAch_Toast_AchievementProgress");
                case NotificationKind.Common:
                    return L("LOCPlayAch_Rarity_Common");
                case NotificationKind.Uncommon:
                    return L("LOCPlayAch_Rarity_Uncommon");
                case NotificationKind.Rare:
                    return L("LOCPlayAch_Rarity_Rare");
                case NotificationKind.UltraRare:
                    return L("LOCPlayAch_Rarity_UltraRare");
                default:
                    return null;
            }
        }

        /// <summary>
        /// Narrows the scope's style to the active kind's own copy when it has one, and brings
        /// the kind row in line with what that means. Returns the style the editors should edit.
        /// </summary>
        private NotificationStyleSettings ApplyKindSelection(
            NotificationStyleSettings scopeStyle,
            bool scopeEditable)
        {
            var kind = ActiveKind;
            if (KindStylePanel == null)
            {
                return scopeStyle?.ResolveKind(kind) ?? scopeStyle;
            }

            if (kind == NotificationKind.Base || scopeStyle == null)
            {
                KindStylePanel.Visibility = Visibility.Collapsed;
                return scopeStyle;
            }

            var hasKindStyle = scopeStyle.HasKindStyle(kind);
            KindStylePanel.Visibility = Visibility.Visible;
            KindStyleCheckBox.Content = string.Format(
                L("LOCPlayAch_Settings_Style_Kind_Customize"),
                GetKindDisplayName(kind));
            KindStyleCheckBox.IsEnabled = scopeEditable;
            _suppressCustomizeEvents = true;
            KindStyleCheckBox.IsChecked = hasKindStyle;
            _suppressCustomizeEvents = false;
            ResetKindStyleButton.Visibility = hasKindStyle && scopeEditable
                ? Visibility.Visible
                : Visibility.Collapsed;

            return hasKindStyle ? scopeStyle.ResolveKind(kind) : scopeStyle;
        }

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

        /// <summary>
        /// Shows one scope: the platform (a provider key, or null for the default; a game's
        /// section has no platform choice and ignores it) and the notification, frame or sounds
        /// tab. A provider the selector does not list leaves the platform as it is.
        /// </summary>
        internal void Preselect(string providerKey, NotificationSurface surface)
        {
            if (!IsGameMode && PlatformSelector?.ItemsSource is IEnumerable<NotificationStylePlatformOption> options)
            {
                var key = string.IsNullOrWhiteSpace(providerKey) ? null : providerKey.Trim();
                var option = options.FirstOrDefault(candidate => string.Equals(candidate.Key, key, StringComparison.OrdinalIgnoreCase));
                if (option != null)
                {
                    PlatformSelector.SelectedItem = option;
                }
            }

            if (SurfaceTabs != null)
            {
                SurfaceTabs.SelectedItem = surface == NotificationSurface.Sounds
                    ? SoundsTabItem
                    : surface == NotificationSurface.Frame ? FrameTabItem : ToastTabItem;
            }
        }

        private void PlatformSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSelectionChanged)
            {
                return;
            }

            ApplySelection();
        }

        /// <summary>
        /// Points both surface editors at the style for the current platform selection: the
        /// global default, the provider's copy, or the default shown read-only when the
        /// provider is not customized yet.
        /// </summary>
        private void ApplySelection()
        {
            try
            {
                ApplySelectionCore();
            }
            catch (Exception ex)
            {
                // Reached from the ctor, the platform selector, and the persisted-settings
                // subscription; leaving the editors on the previous style beats taking Playnite
                // down with an unhandled dispatcher exception.
                _logger?.Error(ex, "Failed applying the notification appearance selection.");
            }
        }

        private void ApplySelectionCore()
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
                // Default has no platform scope to opt into, so its block in the scope column hides.
                PlatformSelectorPanel.Visibility = Visibility.Collapsed;
                CustomizeCheckBox.Visibility = Visibility.Collapsed;
            }
            else
            {
                var custom = persisted.GetProviderNotificationStyle(option.Key);
                editable = custom != null;
                style = custom ?? persisted.NotificationStyle;

                PlatformSelectorPanel.Visibility = Visibility.Visible;
                CustomizeCheckBox.Content = string.Format(
                    L("LOCPlayAch_Settings_Style_Kind_Customize"),
                    option.DisplayName);
                CustomizeCheckBox.Visibility = Visibility.Visible;
                _suppressCustomizeEvents = true;
                CustomizeCheckBox.IsChecked = editable;
                _suppressCustomizeEvents = false;
            }

            _currentScopeStyle = style;
            var kindStyle = ApplyKindSelection(style, editable);
            var editingKind = !ReferenceEquals(kindStyle, style);
            _currentStyle = kindStyle;
            _currentToastUseThemeStyling = persisted.ToastUseThemeStyling;
            _currentFrameUseThemeStyling = persisted.FrameUseThemeStyling;
            ApplyThemeStylingControls(editable: true);
            // A kind styled separately owns its own image slots, so the editors point at the
            // kind's own folder inside the scope.
            var imageOwner = NotificationImageOwner
                .ForProvider(editable ? option.Key : null)
                .ForNotificationKind(editingKind ? ActiveKind : NotificationKind.Base);
            // Captured rather than read at flush time: the editors flush a pending edit only
            // after this method has already moved the fields to the next selection.
            var scopeKey = option.Key;
            var scopeStyle = style;
            Action<NotificationStyleSettings> persist = editable && scopeKey != null
                ? _ => PersistProviderStyle(scopeKey, scopeStyle)
                : (Action<NotificationStyleSettings>)null;
            _toastEditorViewModel.SetStyle(
                kindStyle, imageOwner, editable, persist, providerKey: editable ? option.Key : null,
                isKindStyle: editingKind);
            _frameEditorViewModel.SetStyle(
                kindStyle, imageOwner, editable, persist, providerKey: editable ? option.Key : null);
            ApplySoundsSelection();
            UpdateMockups();
            RefreshPresetRow();
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

            ApplyThemeStylingControls(hasOverride);
            Action<NotificationStyleSettings> persist = hasOverride
                ? PersistGameStyle
                : (Action<NotificationStyleSettings>)null;
            var scopeStyle = _currentStyle;
            _currentScopeStyle = scopeStyle;
            var kindStyle = ApplyKindSelection(scopeStyle, hasOverride);
            var editingKind = !ReferenceEquals(kindStyle, scopeStyle);
            var owner = NotificationImageOwner
                .ForGame(_gameId)
                .ForNotificationKind(editingKind ? ActiveKind : NotificationKind.Base);
            _toastEditorViewModel.SetStyle(kindStyle, owner, hasOverride, persist, isKindStyle: editingKind);
            _frameEditorViewModel.SetStyle(kindStyle, owner, hasOverride, persist);
            ApplySoundsSelection();
            UpdateMockups();
            RefreshPresetRow();
        }

        /// <summary>
        /// Points the Sounds tab at the selected scope's pack: the global pack (with the switches
        /// every scope shares), a platform's own pack, or a game's, each shown read-only while it
        /// follows the pack it inherits. Then brings the scope column in line with the active tab.
        /// </summary>
        private void ApplySoundsSelection()
        {
            var persisted = _settings?.Persisted;
            if (persisted == null || SoundsEditor == null)
            {
                return;
            }

            UnlockSoundScope scope;
            bool editable;
            string testProviderKey;
            if (IsGameMode)
            {
                var store = _plugin?.GameCustomDataStore;
                if (store == null)
                {
                    return;
                }

                scope = UnlockSoundScope.ForGame(store, _gameId, () => _settings?.Persisted, ResolveGameProviderKey);
                editable = scope.OwnSounds != null;
                testProviderKey = ResolveGameProviderKey();
                CustomizeGameSoundsCheckBox.Content = string.Format(
                    L("LOCPlayAch_Settings_Sounds_Customize"),
                    _plugin.PlayniteApi?.Database?.Games?.Get(_gameId)?.Name ?? string.Empty);
                _suppressCustomizeEvents = true;
                CustomizeGameSoundsCheckBox.IsChecked = editable;
                _suppressCustomizeEvents = false;
            }
            else
            {
                scope = UnlockSoundScope.ForSettings(persisted, _selectedProviderKey);
                editable = _selectedProviderKey == null || scope.OwnSounds != null;
                testProviderKey = _selectedProviderKey;
                if (_selectedProviderKey != null)
                {
                    CustomizeSoundsCheckBox.Content = string.Format(
                        L("LOCPlayAch_Settings_Sounds_Customize"),
                        (PlatformSelector?.SelectedItem as NotificationStylePlatformOption)?.DisplayName ?? _selectedProviderKey);
                    _suppressCustomizeEvents = true;
                    CustomizeSoundsCheckBox.IsChecked = editable;
                    _suppressCustomizeEvents = false;
                }
            }

            SoundsEditor.SetScope(
                scope,
                editable,
                showGlobalSwitches: !IsGameMode && _selectedProviderKey == null,
                testProviderKey,
                ScopeGameId);
            ApplySurfaceVisibility();
        }

        /// <summary>
        /// Shows the scope column's checkboxes for the active tab: the style ones on the
        /// notification and frame tabs, the sound ones on the Sounds tab, where the per-kind
        /// styling has no meaning.
        /// </summary>
        private void ApplySurfaceVisibility()
        {
            var sounds = IsSoundsTabActive;
            if (IsGameMode)
            {
                CustomizeGameCheckBox.Visibility = sounds ? Visibility.Collapsed : Visibility.Visible;
                CustomizeGameSoundsCheckBox.Visibility = sounds ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                var platform = _selectedProviderKey != null;
                PlatformSelectorPanel.Visibility = platform ? Visibility.Visible : Visibility.Collapsed;
                CustomizeCheckBox.Visibility = platform && !sounds ? Visibility.Visible : Visibility.Collapsed;
                CustomizeSoundsCheckBox.Visibility = platform && sounds ? Visibility.Visible : Visibility.Collapsed;
            }

            if (sounds)
            {
                KindStylePanel.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>
        /// Gives the selected platform its own sound pack, copied from the global one, or after
        /// confirmation makes it play the global pack again.
        /// </summary>
        private void CustomizeSoundsCheckBox_Click(object sender, RoutedEventArgs e)
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
                if (CustomizeSoundsCheckBox.IsChecked == true)
                {
                    persisted.SetProviderUnlockSounds(providerKey, persisted.UnlockSounds.Clone());
                }
                else
                {
                    if (!Confirm(L("LOCPlayAch_Settings_Sounds_StopSeparateConfirm")))
                    {
                        _suppressCustomizeEvents = true;
                        CustomizeSoundsCheckBox.IsChecked = true;
                        _suppressCustomizeEvents = false;
                        return;
                    }

                    persisted.SetProviderUnlockSounds(providerKey, null);
                    // The platform plays the global pack again, so it no longer follows a library item.
                    persisted.SetLibraryLink(LibraryTargetKeys.SoundsProvider(providerKey), null);
                }

                _plugin.PersistSettingsForUi();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to toggle separate unlock sounds for {providerKey}.");
            }

            ApplySelection();
            SoundsEditor.RefreshAndApply();
        }

        /// <summary>
        /// Gives the game its own sound pack, copied from the one it plays now, or after
        /// confirmation makes it play its platform's or the global pack again.
        /// </summary>
        private void CustomizeGameSoundsCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressCustomizeEvents || !IsGameMode)
            {
                return;
            }

            var store = _plugin?.GameCustomDataStore;
            if (store == null || _settings?.Persisted == null)
            {
                return;
            }

            try
            {
                var scope = UnlockSoundScope.ForGame(store, _gameId, () => _settings?.Persisted, ResolveGameProviderKey);
                if (CustomizeGameSoundsCheckBox.IsChecked == true)
                {
                    scope.Write(scope.EffectiveSounds.Clone());
                }
                else
                {
                    if (!Confirm(L("LOCPlayAch_Settings_Sounds_StopSeparateConfirm")))
                    {
                        _suppressCustomizeEvents = true;
                        CustomizeGameSoundsCheckBox.IsChecked = true;
                        _suppressCustomizeEvents = false;
                        return;
                    }

                    scope.Write(null);
                    // The game plays the inherited pack again, so it no longer follows a library item.
                    _plugin.GameLinkStore.Remove(LibraryTargetKeys.SoundsGame(_gameId));
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to toggle separate unlock sounds for game {_gameId}.");
            }

            ApplySelection();
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
        /// Splits the preview row for the active tab: the toast card keeps its own width and the
        /// scope column takes the rest, while the frame strip has no natural width, so it takes
        /// the rest and the scope column sizes to its content.
        /// </summary>
        private void ApplyPreviewColumns()
        {
            var frame = FrameTabItem?.IsSelected == true;
            PreviewColumn.Width = frame
                ? new GridLength(1, GridUnitType.Star)
                : GridLength.Auto;
            ScopeColumn.Width = frame
                ? GridLength.Auto
                : new GridLength(1, GridUnitType.Star);
        }

        /// <summary>
        /// Writes the game snapshot. The editor hands back whichever style it holds, which is a
        /// kind's copy while one is active; the snapshot always stores the scope style that owns
        /// the kind copies, so persisting a kind edit cannot flatten the snapshot onto it.
        /// </summary>
        private void PersistGameStyle(NotificationStyleSettings style)
        {
            style = _currentScopeStyle ?? style;
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
        /// Stores the platform's style. Like <see cref="PersistGameStyle"/>, it stores the scope
        /// style that owns the kind copies rather than the kind copy the editor may hold, so a
        /// kind edit cannot replace the platform's whole style with that kind's.
        /// </summary>
        private void PersistProviderStyle(string providerKey, NotificationStyleSettings scopeStyle)
        {
            var persisted = _settings?.Persisted;
            if (IsGameMode || persisted == null || scopeStyle == null || providerKey == null ||
                persisted.GetProviderNotificationStyle(providerKey) == null)
            {
                return;
            }

            persisted.SetProviderNotificationStyle(providerKey, scopeStyle);
            _plugin.PersistSettingsForUi();
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
                        L("LOCPlayAch_Settings_Style_StopSeparateConfirm"),
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
                    // The platform follows the global look again, so it no longer follows a library item.
                    persisted.SetLibraryLink(LibraryTargetKeys.ToastProvider(providerKey), null);
                    persisted.SetLibraryLink(LibraryTargetKeys.FrameProvider(providerKey), null);
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
                        L("LOCPlayAch_Settings_Style_StopSeparateConfirm"),
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
                    // The game follows the inherited look again, so it no longer follows a library item.
                    _plugin.GameLinkStore.Remove(LibraryTargetKeys.ToastGame(_gameId));
                    _plugin.GameLinkStore.Remove(LibraryTargetKeys.FrameGame(_gameId));
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
                    // Equivalent to ApplyGameSelection in game mode, but guarded.
                    ApplySelection();
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

        private System.Windows.Threading.DispatcherTimer _mockupRefreshTimer;
        private bool _mockupRefreshPending;

        private void OnEditorStyleChanged(object sender, EventArgs e)
        {
            // Throttle mockup rebuilds during rapid style edits (slider drags fire one change
            // per tick): the first edit rebuilds immediately so the preview follows the drag,
            // further edits within the window coalesce, and a trailing tick applies the final
            // value. Animations survive the rebuilds via the shared phase-lock epoch.
            if (_mockupRefreshTimer == null)
            {
                _mockupRefreshTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(200)
                };
                _mockupRefreshTimer.Tick += (s, args) =>
                {
                    if (_mockupRefreshPending)
                    {
                        _mockupRefreshPending = false;
                        UpdateMockups();
                    }
                    else
                    {
                        _mockupRefreshTimer.Stop();
                    }
                };
            }

            if (_mockupRefreshTimer.IsEnabled)
            {
                _mockupRefreshPending = true;
            }
            else
            {
                UpdateMockups();
                _mockupRefreshTimer.Start();
            }
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
        // selected provider (null = global). Mirrors how the style package scope is chosen.
        private string ScopeProviderKey => IsGameMode ? null : _selectedProviderKey;

        private Guid ScopeGameId => IsGameMode ? _gameId : Guid.Empty;

        /// <summary>
        /// Rebuilds the inline mockups, degrading to empty hosts instead of letting a failure reach
        /// the WPF dispatcher: an imported style can carry values the preview pipeline rejects, and
        /// there is no application-level exception handler behind this.
        /// </summary>
        /// <remarks>
        /// This cannot catch a template that fails to realize: the content tree is built during the
        /// following layout pass, after this method returns. Install-time validation
        /// (AchievementToastTemplateResolver.TryValidateTemplateXaml) is what covers that.
        /// </remarks>
        private void UpdateMockups()
        {
            try
            {
                UpdateMockupsCore();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed building the notification appearance mockups.");
                try
                {
                    if (ToastMockupHost != null)
                    {
                        ToastMockupHost.ContentTemplate = null;
                        ToastMockupHost.Content = null;
                    }

                    if (FrameMockupHost != null)
                    {
                        FrameMockupHost.ContentTemplate = null;
                        FrameMockupHost.Content = null;
                    }
                }
                catch (Exception clearEx)
                {
                    _logger?.Debug(clearEx, "Failed clearing the notification appearance mockups.");
                }
            }
        }

        private void UpdateMockupsCore()
        {
            // Mockups are visual-only; while the control is not in the visual tree, the Loaded
            // handler's rebuild covers every change made in the meantime.
            if (!IsLoaded)
            {
                return;
            }

            var persisted = _settings?.Persisted;
            if (persisted == null || ToastMockupHost == null || FrameMockupHost == null ||
                _toastTemplateResolver == null)
            {
                return;
            }

            UpdateToastBackgroundAnimationHost();

            // The toast mockup is built through the same ToastSurfaceFactory the live toast wave
            // uses (single-item list), so the inline preview and the fired notification cannot
            // drift. The sample kind mirrors the fire-test dropdown so the preview shows whatever
            // firing would produce; a null preview source keeps ResolveTemplate parity with a real
            // unlock.
            var toastKind = NotificationSampleSelector?.SelectedValue as string ?? "rare";
            _toastPreviewViewModel = new AchievementToastViewModel(
                BuildPreviewArgs(toastKind),
                persisted,
                _currentStyle,
                gameCustomDataStore: null,
                toastUseThemeStylingOverride: _currentToastUseThemeStyling,
                frameUseThemeStylingOverride: _currentFrameUseThemeStyling,
                toastBackgroundRenderSourceOverride: ToastBackgroundAnimationHost?.Source,
                useToastBackgroundRenderSourceOverride: true)
            {
                // Paused: the slot images show their keyframe stills instead of animating.
                HoldToastImagesAtKeyframe = _toastEditorViewModel?.IsKeyframePreviewPaused == true
            };
            var toastItems = new[] { _toastPreviewViewModel };
            var toastTemplate = ToastSurfaceFactory.ResolveToastTemplate(
                _toastTemplateResolver, toastItems, _currentToastUseThemeStyling, ScopeProviderKey, ScopeGameId);
            ToastMockupHost.ContentTemplate = null;
            var toastSurface = ToastSurfaceFactory.BuildToastSurface(toastItems, toastTemplate);
            ToastMockupHost.Content = toastSurface;

            _toastPreviewSurface = toastSurface;
            ApplyPreviewBackdrop();

            // The host may already have published its animated source — before this control
            // loaded, or while this very method was setting the URI — in which case no further
            // SourceReady is coming and the VM above captured a stale object.
            SyncToastBackgroundRenderSource();

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

        private void UpdateToastBackgroundAnimationHost()
        {
            if (ToastBackgroundAnimationHost == null)
            {
                return;
            }

            var requested = PlayniteAchievements.Models.Achievements.AchievementIconResolver.ApplyCacheBust(
                _currentStyle?.ToastBackgroundImagePath);
            if (!Equals(AsyncImage.GetUri(ToastBackgroundAnimationHost), requested))
            {
                _lastToastBackgroundRenderSource = null;
                AsyncImage.SetUri(ToastBackgroundAnimationHost, requested);
            }
        }

        private void OnToastBackgroundSourceChanged(object sender, RoutedEventArgs e)
        {
            // AsyncImage raises this only when the source object is replaced. Listening to the
            // raw Source dependency property here would also receive every mutable GIF frame and
            // rebuild the whole toast repeatedly, which presents as flicker and severe stutter.
            //
            // Deliberately not gated on IsLoaded: with warm image caches the whole load completes
            // inline, so this can arrive before the control is loaded. Dropping it then left the
            // mockup on whatever it had captured, which is why the background animated on the
            // first settings open of a session and never again.
            SyncToastBackgroundRenderSource();
        }

        /// <summary>
        /// Points the toast mockup at the animation host's current render source.
        ///
        /// The mockup's brush reuses the host's <c>Source</c> object, which the native GIF decoder
        /// mutates in place — so holding the live object is what makes the mockup animate, and
        /// holding a stale one freezes it. The host publishes a new object at most once per URI,
        /// and that can land either side of the mockup being rebuilt, so both sides call this.
        /// </summary>
        private void SyncToastBackgroundRenderSource()
        {
            var renderSource = ToastBackgroundAnimationHost?.Source;
            if (renderSource == null || _toastPreviewViewModel == null ||
                ReferenceEquals(_lastToastBackgroundRenderSource, renderSource))
            {
                return;
            }

            _lastToastBackgroundRenderSource = renderSource;
            _toastPreviewViewModel.SetToastBackgroundRenderSourceOverride(renderSource);
        }

        // Both sample-kind dropdowns refresh the inline mockups through one handler so the preview
        // mirrors whatever a fire-test would show.
        private void SampleSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // The sample also names the kind being styled, so a change can swap which style
            // object the editors edit; ApplySelection refreshes the mockups on its way out.
            ApplySelection();
        }

        /// <summary>
        /// Opts the active kind out of the scope's shared style into its own copy, or drops
        /// that copy after confirmation. The copy is seeded from the scope style as it stands,
        /// so the kind starts out looking exactly as it did.
        /// </summary>
        private async void KindStyleCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressCustomizeEvents)
            {
                return;
            }

            var kind = ActiveKind;
            var scopeStyle = _currentScopeStyle;
            if (kind == NotificationKind.Base || scopeStyle == null)
            {
                return;
            }

            try
            {
                _toastEditorViewModel?.FlushPendingPersist();
                _frameEditorViewModel?.FlushPendingPersist();

                if (KindStyleCheckBox.IsChecked == true)
                {
                    await SeedKindStyleAsync(scopeStyle, kind);
                }
                else if (!ConfirmDropKindStyle())
                {
                    _suppressCustomizeEvents = true;
                    KindStyleCheckBox.IsChecked = true;
                    _suppressCustomizeEvents = false;
                    return;
                }
                else
                {
                    scopeStyle.ClearKindStyle(kind);
                    _plugin.NotificationImageStore.DeleteKindImages(ScopeImageOwner, kind);
                }

                PersistScopeStyle();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to toggle the {kind} notification style.");
            }

            ApplySelection();
        }

        /// <summary>
        /// Re-seeds the active kind's style from the scope's shared style, discarding the
        /// separate design while keeping the kind opted out.
        /// </summary>
        private async void ResetKindStyle_Click(object sender, RoutedEventArgs e)
        {
            var kind = ActiveKind;
            var scopeStyle = _currentScopeStyle;
            if (kind == NotificationKind.Base || scopeStyle == null || !ConfirmDropKindStyle())
            {
                return;
            }

            try
            {
                _toastEditorViewModel?.FlushPendingPersist();
                _frameEditorViewModel?.FlushPendingPersist();
                scopeStyle.ClearKindStyle(kind);
                _plugin.NotificationImageStore.DeleteKindImages(ScopeImageOwner, kind);
                await SeedKindStyleAsync(scopeStyle, kind);
                PersistScopeStyle();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to reset the {kind} notification style.");
            }

            ApplySelection();
        }

        /// <summary>
        /// The image slot owner for the current scope, before any kind narrowing.
        /// </summary>
        private NotificationImageOwner ScopeImageOwner =>
            IsGameMode
                ? NotificationImageOwner.ForGame(_gameId)
                : NotificationImageOwner.ForProvider(_selectedProviderKey);

        /// <summary>
        /// Seeds a kind's own style from the scope's shared one and gives it its own copies of
        /// the shared images, so it starts out identical and can then be changed — images
        /// included — without touching the shared style.
        /// </summary>
        private async Task SeedKindStyleAsync(NotificationStyleSettings scopeStyle, NotificationKind kind)
        {
            var seeded = scopeStyle.EnableKindStyle(kind);
            await _plugin.NotificationImageStore.CopyImagesAsync(
                seeded,
                ScopeImageOwner.ForNotificationKind(kind),
                CancellationToken.None);
        }

        private bool ConfirmDropKindStyle()
        {
            return _plugin.PlayniteApi.Dialogs.ShowMessage(
                L("LOCPlayAch_Settings_Style_StopSeparateConfirm"),
                L("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        /// <summary>
        /// Commits a change made to the scope style itself (rather than through the surface
        /// editors), which for a game snapshot means rewriting it in custom data.
        /// </summary>
        private void PersistScopeStyle()
        {
            if (IsGameMode)
            {
                PersistGameStyle(_currentScopeStyle);
                return;
            }

            if (_selectedProviderKey != null)
            {
                // The store holds a clone of the working style, so the change is stored again.
                PersistProviderStyle(_selectedProviderKey, _currentScopeStyle);
                return;
            }

            _plugin.PersistSettingsForUi();
        }

        private void FireNotification_Click(object sender, RoutedEventArgs e)
        {
            var source = ResolvePreviewSource(_currentToastUseThemeStyling);
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

            args.PreviewStyleOverride = _currentScopeStyle ?? _currentStyle;

            PlayniteAchievementsPlugin.NotifyAchievementUnlocked(args);
        }

        /// <summary>
        /// The design a test fire uses: the active theme's (falling back as a real notification
        /// would) when the surface's "Use theme's template" checkbox is on, else the plugin's.
        /// </summary>
        private static NotificationTemplatePreviewSource ResolvePreviewSource(bool useThemeStyling)
        {
            return useThemeStyling
                ? NotificationTemplatePreviewSource.ActiveTheme
                : NotificationTemplatePreviewSource.PluginStyle;
        }

        /// <summary>
        /// Shows the screenshot frame full-monitor over Playnite through
        /// <see cref="FramePreviewOverlay"/> so themes can be checked at real scale.
        /// </summary>
        private void FireFrame_Click(object sender, RoutedEventArgs e)
        {
            var source = ResolvePreviewSource(_currentFrameUseThemeStyling);
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

            var window = FramePreviewOverlay.Show(
                _plugin.PlayniteApi,
                Window.GetWindow(this),
                template,
                new AchievementToastViewModel(
                    BuildPreviewArgs(kind),
                    persisted,
                    _currentStyle,
                    gameCustomDataStore: null,
                    toastUseThemeStylingOverride: _currentToastUseThemeStyling,
                    frameUseThemeStylingOverride: _currentFrameUseThemeStyling));
            if (window == null)
            {
                return;
            }

            window.Closed += (s, args) =>
            {
                if (ReferenceEquals(_framePreviewWindow, window))
                {
                    _framePreviewWindow = null;
                }
            };
            _framePreviewWindow = window;
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

        /// <summary>
        /// Shows or hides the game behind the toast preview per <see cref="_previewBackdropChoice"/>,
        /// and keeps the toolbar toggle in step.
        /// </summary>
        private void ApplyPreviewBackdrop()
        {
            var vm = _toastPreviewViewModel;
            if (vm == null || _toastPreviewSurface == null || ToastMockupFrame == null)
            {
                return;
            }

            if (_previewBackdropBlurSeen != vm.HasToastBlur)
            {
                _previewBackdropBlurSeen = vm.HasToastBlur;
                _previewBackdropChoice = null;
            }

            var show = _previewBackdropChoice ?? vm.HasToastBlur;
            if (PreviewBackdropToggle != null && PreviewBackdropToggle.IsChecked != show)
            {
                _suppressPreviewBackdropToggle = true;
                PreviewBackdropToggle.IsChecked = show;
                _suppressPreviewBackdropToggle = false;
            }

            _toastPreviewBackdrop = _toastPreviewBackdrop ?? new ToastPreviewBackdrop(ToastMockupFrame, ResolvePreviewBackdropPath);
            _toastPreviewBackdrop.Apply(_toastPreviewSurface, vm, show);
        }

        private void PreviewBackdropToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_suppressPreviewBackdropToggle)
            {
                return;
            }

            _previewBackdropChoice = PreviewBackdropToggle.IsChecked == true;
            ApplyPreviewBackdrop();
        }

        /// <summary>
        /// The image the toast preview sits on: the edited game's background art, or
        /// outside game mode the most recently played game's; null (a generated stand-in) when there
        /// is none. Resolved once per scope.
        /// </summary>
        private string ResolvePreviewBackdropPath()
        {
            var scope = IsGameMode ? _gameId.ToString() : string.Empty;
            if (string.Equals(_previewBackdropScope, scope, StringComparison.Ordinal))
            {
                return _previewBackdropPath;
            }

            _previewBackdropScope = scope;
            _previewBackdropPath = null;
            try
            {
                var games = _plugin.PlayniteApi?.Database?.Games;
                var game = games == null
                    ? null
                    : IsGameMode
                        ? games.Get(_gameId)
                        : games.Where(g => !string.IsNullOrWhiteSpace(g.BackgroundImage))
                            .OrderByDescending(g => g.LastActivity ?? DateTime.MinValue)
                            .FirstOrDefault();
                var path = ResolveGameImagePath(game?.BackgroundImage);
                _previewBackdropPath = !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed resolving the blurred-background preview image.");
            }

            return _previewBackdropPath;
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
        /// Exports the active tab's surface for the current platform selection to a shareable
        /// .panotif (notification) or .paframe (screenshot frame) package, bundling the surface's
        /// images and optionally its authored custom template. Debounced edits are flushed first
        /// so the file matches what the editors show.
        /// </summary>
        private void ExportStyle_Click(object sender, RoutedEventArgs e)
        {
            if (IsSoundsTabActive)
            {
                // Only the pack's own and theme files travel, so all-default tiers have nothing to share.
                WorkshopMenus.OpenExport(
                    sender as Button,
                    ExportSoundsFile,
                    ShareSoundsToWorkshop,
                    workshopEnabled: SoundsEditor.HasShareableSounds);
                return;
            }

            // Both scopes share the menu: a file export of what the tab shows, or sharing it to the
            // Workshop as a style of that surface (a game's own look travels like any other),
            // then the surface's default template as a loose .xaml starting point.
            var isFrame = FrameTabItem?.IsSelected == true;
            var menu = new ContextMenu();
            foreach (var item in WorkshopMenus.ExportItems(
                         () => ExportStyleFile_Click(sender, e),
                         () => ShareStyleToWorkshop(isFrame)))
            {
                menu.Items.Add(item);
            }

            menu.Items.Add(new Separator());
            var defaultTemplate = new MenuItem
            {
                Header = L("LOCPlayAch_Settings_Style_ExportDefaultTemplate")
            };
            defaultTemplate.Click += (s, args) => ExportDefaultTemplate(isFrame);
            menu.Items.Add(defaultTemplate);
            SelectorContextMenuHelper.Open(sender as Button, menu);
        }

        /// <summary>
        /// Packages the surface shown in the active tab for the current scope (global, platform
        /// or game) into a scratch .panotif or .paframe and opens the share dialog on it; the
        /// dialog previews that package. The scratch folder outlives the modal dialog only.
        /// </summary>
        private void ShareStyleToWorkshop(bool isFrame)
        {
            var style = _currentScopeStyle ?? _currentStyle;
            var store = _plugin?.NotificationStylePortableStore;
            if (style == null || store == null)
            {
                return;
            }

            _toastEditorViewModel?.FlushPendingPersist();
            _frameEditorViewModel?.FlushPendingPersist();

            var scratch = PortablePackage.CreateScratchDirectory("StyleShare");
            try
            {
                var name = BuildDefaultStyleFileName();
                var path = Path.Combine(scratch, name + NotificationStylePortableStore.SurfaceExtension(isFrame));
                var templateXaml = _toastTemplateResolver?.ReadCustomTemplateXaml(isFrame, ScopeProviderKey, ScopeGameId);
                store.ExportSurfacePackage(isFrame, style, path, templateXaml);
                _plugin.OpenWorkshopShare(
                    isFrame ? WorkshopItemKind.ScreenshotFrame : WorkshopItemKind.NotificationStyle,
                    Window.GetWindow(this),
                    packagePath: path,
                    defaultName: name);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed sharing the notification style.");
                Inform(string.Format(L("LOCPlayAch_Status_Failed"), ex.Message), MessageBoxImage.Error);
            }
            finally
            {
                PortablePackage.TryDeleteDirectory(scratch);
            }
        }

        private void ExportStyleFile_Click(object sender, RoutedEventArgs e)
        {
            var style = _currentScopeStyle ?? _currentStyle;
            var store = _plugin?.NotificationStylePortableStore;
            if (style == null || store == null)
            {
                return;
            }

            try
            {
                _toastEditorViewModel?.FlushPendingPersist();
                _frameEditorViewModel?.FlushPendingPersist();

                var isFrame = FrameTabItem?.IsSelected == true;
                var extension = isFrame
                    ? NotificationStylePortableStore.FramePackageFileExtension
                    : NotificationStylePortableStore.ToastPackageFileExtension;
                var dialog = new SaveFileDialog
                {
                    Filter = isFrame
                        ? "Playnite Achievements Frame Style (*.paframe)|*.paframe"
                        : "Playnite Achievements Notification Style (*.panotif)|*.panotif",
                    AddExtension = true,
                    DefaultExt = extension,
                    FileName = BuildDefaultStyleFileName()
                };

                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                var destinationPath = NotificationStylePortableStore.NormalizeExportPath(
                    dialog.FileName, extension);

                // Bundle only a template the user actually authored for this scope: portable
                // loose XAML that passed validation on install. The active theme's override is
                // never bundled (it is theme-coupled and would import broken).
                string templateXaml = null;
                var resolver = _toastTemplateResolver;
                if (resolver != null)
                {
                    var custom = resolver.ReadCustomTemplateXaml(isFrame, ScopeProviderKey, ScopeGameId);
                    if (custom != null &&
                        Confirm(L("LOCPlayAch_Settings_Style_ExportIncludeTemplate")))
                    {
                        templateXaml = custom;
                    }
                }

                store.ExportSurfacePackage(isFrame, style, destinationPath, templateXaml);

                _plugin.PlayniteApi?.Dialogs?.ShowMessage(
                    L("LOCPlayAch_Status_Succeeded") + "\n" + destinationPath,
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed exporting notification surface style.");
                _plugin.PlayniteApi?.Dialogs?.ShowMessage(
                    string.Format(L("LOCPlayAch_Status_Failed"), ex.Message),
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Imports a style file onto the current platform selection, replacing the surfaces the
        /// file carries (a .panotif replaces the notification, a .paframe the frame, a
        /// a retired .pastyle with both) and creating the provider's whole-style copy if it was
        /// following the default. Any file type imports from either tab; a file that does not
        /// cover the active tab's surface warns first. Bundled images are re-materialized into
        /// managed storage.
        /// </summary>
        private void ImportStyle_Click(object sender, RoutedEventArgs e)
        {
            if (IsSoundsTabActive)
            {
                WorkshopMenus.OpenImport(
                    sender as Button,
                    ImportSoundsFile,
                    () => _plugin.OpenWorkshopWindow(focusKind: WorkshopItemKind.UnlockSounds));
                return;
            }

            // From file: the global page saves a preset, a game tab applies straight onto the game.
            // From Workshop: the browser scoped to this surface; installs land in the presets.
            var kind = FrameTabItem?.IsSelected == true
                ? WorkshopItemKind.ScreenshotFrame
                : WorkshopItemKind.NotificationStyle;
            WorkshopMenus.OpenImport(
                sender as Button,
                () =>
                {
                    if (IsGameMode)
                    {
                        ImportStyleIntoGame_Click(sender, e);
                    }
                    else
                    {
                        ImportStyleFile_Click(sender, e);
                    }
                },
                () => _plugin.OpenWorkshopWindow(focusKind: kind));
        }

        /// <summary>
        /// The per-game tab imports a file through the library: each surface the file carries
        /// becomes a library item named after the file, and the game follows it, so a later
        /// re-save of that preset reaches the game like any other follower. A template the file
        /// carries comes with the item.
        /// </summary>
        private void ImportStyleIntoGame_Click(object sender, RoutedEventArgs e)
        {
            var store = _plugin?.NotificationStylePortableStore;
            var presets = _plugin?.NotificationStylePresetStore;
            if (!IsGameMode || store == null || presets == null)
            {
                return;
            }

            try
            {
                var dialog = new OpenFileDialog
                {
                    // The retired .pastyle spelling stays accepted in the first filter so files
                    // exported before 4.1 still import, without advertising it as a format.
                    Filter =
                        "Playnite Achievements Style Files (*.panotif;*.paframe)|*.panotif;*.paframe;*.panotif.zip;*.paframe.zip;*.pastyle;*.pastyle.zip|" +
                        "Playnite Achievements Notification Style (*.panotif)|*.panotif;*.panotif.zip|" +
                        "Playnite Achievements Frame Style (*.paframe)|*.paframe;*.paframe.zip",
                    CheckFileExists = true,
                    Multiselect = false
                };

                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                var contents = store.InspectPackage(dialog.FileName);
                if (!contents.HasStyle)
                {
                    Inform(L("LOCPlayAch_Settings_Style_ImportUnsupportedFile"), MessageBoxImage.Warning);
                    return;
                }

                // Importing is allowed from either tab, but a file that does not cover the
                // active tab's surface is easy to pick by accident, so it warns first. The
                // mismatch prompt doubles as the import confirmation.
                var activeIsFrame = IsFrameTabActive;
                var coversActiveSurface = activeIsFrame ? contents.HasFrameStyle : contents.HasToastStyle;
                if (!coversActiveSurface)
                {
                    var carried = L(contents.HasFrameStyle
                        ? "LOCPlayAch_Settings_FrameHeader"
                        : "LOCPlayAch_Settings_Style_ToastTab");
                    var active = L(activeIsFrame
                        ? "LOCPlayAch_Settings_FrameHeader"
                        : "LOCPlayAch_Settings_Style_ToastTab");
                    if (!Confirm(string.Format(
                            L("LOCPlayAch_Settings_Style_ImportSurfaceMismatch"), carried, active)))
                    {
                        return;
                    }
                }
                else if (!Confirm(L("LOCPlayAch_Settings_Style_ImportConfirm")))
                {
                    return;
                }

                FlushEditors();
                var stem = NotificationStylePortableStore.StripRecognizedSuffix(Path.GetFileName(dialog.FileName));
                foreach (var isFrame in new[] { false, true })
                {
                    if (isFrame ? !contents.HasFrameStyle : !contents.HasToastStyle)
                    {
                        continue;
                    }

                    var preset = presets.SavePresetFromPackage(isFrame, presets.UniqueName(isFrame, stem), dialog.FileName);
                    var item = StylePresetPicker.AddLocalPreset(
                        preset.FilePath,
                        preset.Name,
                        isFrame ? LibraryItemKind.Frame : LibraryItemKind.Toast);
                    _plugin.LibraryUpdateService.ApplyToGameTarget(
                        LibraryTargetKeys.NotificationScope(isFrame, null, _gameId),
                        item);
                }

                ApplySelection();
                if (!ReportTemplateErrors())
                {
                    Inform(L("LOCPlayAch_Status_Succeeded"), MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed importing notification style.");
                Inform(string.Format(L("LOCPlayAch_Status_Failed"), ex.Message), MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Adds a .panotif or .paframe file to the presets of the surface it carries, named
        /// after the file. Applying it to a platform or game is the preset list's job, so the
        /// current look does not change here. Files from before 4.1 that carry both surfaces
        /// become one preset per surface.
        /// </summary>
        private void ImportStyleFile_Click(object sender, RoutedEventArgs e)
        {
            var store = _plugin?.NotificationStylePortableStore;
            var presets = _plugin?.NotificationStylePresetStore;
            if (store == null || presets == null)
            {
                return;
            }

            try
            {
                var dialog = new OpenFileDialog
                {
                    // The retired .pastyle spelling stays accepted in the first filter so files
                    // exported before 4.1 still import, without advertising it as a format.
                    Filter =
                        "Playnite Achievements Style Files (*.panotif;*.paframe)|*.panotif;*.paframe;*.panotif.zip;*.paframe.zip;*.pastyle;*.pastyle.zip|" +
                        "Playnite Achievements Notification Style (*.panotif)|*.panotif;*.panotif.zip|" +
                        "Playnite Achievements Frame Style (*.paframe)|*.paframe;*.paframe.zip",
                    CheckFileExists = true,
                    Multiselect = false
                };

                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                var contents = store.InspectPackage(dialog.FileName);
                if (!contents.HasStyle)
                {
                    Inform(L("LOCPlayAch_Settings_Style_ImportUnsupportedFile"), MessageBoxImage.Warning);
                    return;
                }

                var stem = NotificationStylePortableStore.StripRecognizedSuffix(Path.GetFileName(dialog.FileName));
                string selectName = null;
                foreach (var isFrame in new[] { false, true })
                {
                    if (isFrame ? !contents.HasFrameStyle : !contents.HasToastStyle)
                    {
                        continue;
                    }

                    var preset = presets.SavePresetFromPackage(isFrame, presets.UniqueName(isFrame, stem), dialog.FileName);
                    StylePresetPicker.AddLocalPreset(preset.FilePath, preset.Name, isFrame ? LibraryItemKind.Frame : LibraryItemKind.Toast);
                    if (selectName == null || isFrame == IsFrameTabActive)
                    {
                        selectName = preset.Name;
                    }
                }

                Inform(string.Format(L("LOCPlayAch_Workshop_SavedAsPreset"), selectName), MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed importing notification style.");
                Inform(string.Format(L("LOCPlayAch_Status_Failed"), ex.Message), MessageBoxImage.Error);
            }
        }

        /// <summary>Writes what each tier of the shown pack plays, minus the bundled defaults, to a .pasounds file.</summary>
        private void ExportSoundsFile()
        {
            Keyboard.ClearFocus();
            var store = _plugin?.UnlockSoundPortableStore;
            if (store == null)
            {
                return;
            }

            try
            {
                var dialog = new SaveFileDialog
                {
                    Filter = UnlockSoundPortableStore.BuildFileDialogFilter(),
                    AddExtension = true,
                    DefaultExt = UnlockSoundPortableStore.PackageFileExtension,
                    FileName = BuildDefaultStyleFileName() + UnlockSoundPortableStore.PackageFileExtension
                };

                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                SoundsEditor.ExportCurrent(UnlockSoundPortableStore.NormalizeExportPath(dialog.FileName), store);
                Inform(L("LOCPlayAch_Status_Succeeded"), MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed exporting unlock sound pack.");
                Inform(string.Format(L("LOCPlayAch_Status_Failed"), ex.Message), MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Packages the shown pack into a scratch .pasounds and opens the share dialog on it. The
        /// scratch folder outlives the modal dialog only.
        /// </summary>
        private void ShareSoundsToWorkshop()
        {
            var store = _plugin?.UnlockSoundPortableStore;
            if (store == null)
            {
                return;
            }

            var scratch = PortablePackage.CreateScratchDirectory("SoundsShare");
            try
            {
                var name = BuildDefaultStyleFileName();
                var path = Path.Combine(scratch, name + UnlockSoundPortableStore.PackageFileExtension);
                SoundsEditor.ExportCurrent(path, store);
                _plugin.OpenWorkshopShare(
                    WorkshopItemKind.UnlockSounds,
                    Window.GetWindow(this),
                    packagePath: path,
                    defaultName: name);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed sharing the unlock sound pack.");
                Inform(string.Format(L("LOCPlayAch_Status_Failed"), ex.Message), MessageBoxImage.Error);
            }
            finally
            {
                PortablePackage.TryDeleteDirectory(scratch);
            }
        }

        /// <summary>
        /// Adds a .pasounds file to the sound presets, named after the file. On the global page
        /// applying it is the preset list's job; a game tab applies it straight onto the game,
        /// which then follows it, as a style file does.
        /// </summary>
        private void ImportSoundsFile()
        {
            Keyboard.ClearFocus();
            var presets = _plugin?.UnlockSoundPresetStore;
            if (presets == null)
            {
                return;
            }

            try
            {
                var dialog = new OpenFileDialog
                {
                    Filter = UnlockSoundPortableStore.BuildFileDialogFilter(),
                    CheckFileExists = true,
                    Multiselect = false
                };

                if (dialog.ShowDialog() != DialogResult.OK)
                {
                    return;
                }

                var saved = presets.SaveFrom(presets.UniqueName(SoundPackageStem(dialog.FileName)), dialog.FileName);
                var item = StylePresetPicker.AddLocalPreset(saved.FilePath, saved.Name, LibraryItemKind.Sounds);
                if (IsGameMode && item != null)
                {
                    _plugin.LibraryUpdateService.ApplyToGameTarget(LibraryTargetKeys.SoundsGame(_gameId), item);
                    ApplySelection();
                    SoundsEditor.RefreshAndApply();
                    Inform(L("LOCPlayAch_Status_Succeeded"), MessageBoxImage.Information);
                    return;
                }

                Inform(string.Format(L("LOCPlayAch_Workshop_SavedAsPreset"), saved.Name), MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed importing unlock sound pack.");
                Inform(string.Format(L("LOCPlayAch_Status_Failed"), ex.Message), MessageBoxImage.Error);
            }
        }

        /// <summary>The file name without its package extension, including a trailing .zip.</summary>
        private static string SoundPackageStem(string path)
        {
            var name = Path.GetFileName(path) ?? string.Empty;
            foreach (var suffix in new[] { ".zip", UnlockSoundPortableStore.PackageFileExtension })
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(0, name.Length - suffix.Length);
                }
            }

            return name;
        }

        private void Inform(string message, MessageBoxImage image)
        {
            _plugin?.PlayniteApi?.Dialogs?.ShowMessage(
                message,
                L("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                image);
        }

        private bool Confirm(string message)
        {
            return _plugin.PlayniteApi.Dialogs.ShowMessage(
                message,
                L("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;
        }

        private bool IsFrameTabActive => FrameTabItem?.IsSelected == true;

        private bool IsSoundsTabActive => SoundsTabItem?.IsSelected == true;

        /// <summary>True while the editors show one kind's own copy rather than the scope's shared style.</summary>
        private bool IsEditingKind => _currentStyle != null && !ReferenceEquals(_currentStyle, _currentScopeStyle);

        /// <summary>
        /// The preset row's target for the active tab: the scope the selector names (global, a
        /// platform, or the game), which follows the item applied to it; or, while one kind is
        /// being styled on its own, that kind's copy, which takes an item as a one-off and
        /// follows nothing.
        /// </summary>
        private ILibraryPickerTarget CurrentPickerTarget()
        {
            if (IsSoundsTabActive)
            {
                return CurrentSoundsPickerTarget();
            }

            var targets = _plugin?.NotificationLibraryTargets;
            if (targets == null)
            {
                return null;
            }

            var isFrame = IsFrameTabActive;
            var adapter = targets.AdapterFor(isFrame);
            var target = new DelegatePickerTarget
            {
                Kind = adapter.Kind,
                Folder = new NotificationStylePresetFolder(_plugin.NotificationStylePresetStore, isFrame),
                Export = path => ExportCurrentSurface(isFrame, path)
            };

            if (IsEditingKind)
            {
                var kind = ActiveKind;
                var scope = IsGameMode
                    ? targets.GameScope(_gameId)
                    : NotificationStyleScope.ForSettings(_settings.Persisted, _selectedProviderKey);
                target.ApplyItem = item =>
                {
                    FlushEditors();
                    adapter.ApplyToKind(_plugin.LibraryStore.FullPath(item), scope, kind);
                    PersistIfSettingsScope();
                };
                return target;
            }

            var key = LibraryTargetKeys.NotificationScope(isFrame, IsGameMode ? null : _selectedProviderKey, ScopeGameId);
            var updates = _plugin.LibraryUpdateService;
            if (IsGameMode)
            {
                target.State = () => updates.GetGameTargetState(key);
                target.ApplyItem = item =>
                {
                    FlushEditors();
                    updates.ApplyToGameTarget(key, item);
                };
                target.LinkItem = item => updates.LinkGameTarget(key, item);
                return target;
            }

            var settingsAdapter = targets.SettingsAdapter(key);
            var apply = _plugin.LibraryApplyService;
            var providerKey = _selectedProviderKey;
            target.State = () => apply.GetSettingsState(settingsAdapter, _settings.Persisted);
            target.ApplyItem = item =>
            {
                FlushEditors();
                apply.ApplyToSettings(settingsAdapter, item, _settings.Persisted);
                PersistIfSettingsScope();
            };
            target.LinkItem = item =>
            {
                // A platform that shows the global look has no look of its own to follow the preset.
                if (providerKey == null || _settings.Persisted.GetProviderNotificationStyle(providerKey) != null)
                {
                    apply.LinkSettings(settingsAdapter, item, _settings.Persisted);
                }
            };
            return target;
        }

        /// <summary>
        /// The preset row's target on the Sounds tab: the selected scope's pack (global, a
        /// platform, or the game), which follows the sound pack applied to it.
        /// </summary>
        private ILibraryPickerTarget CurrentSoundsPickerTarget()
        {
            var targets = _plugin?.SoundsLibraryTargets;
            var presets = _plugin?.UnlockSoundPresetStore;
            if (targets == null || presets == null)
            {
                return null;
            }

            var target = new DelegatePickerTarget
            {
                Kind = LibraryItemKind.Sounds,
                Folder = new PackagePresetFolder(presets),
                Export = path => SoundsEditor.ExportCurrent(path, _plugin.UnlockSoundPortableStore)
            };

            var providerKey = IsGameMode ? null : _selectedProviderKey;
            var key = LibraryTargetKeys.SoundsScope(providerKey, ScopeGameId);
            var updates = _plugin.LibraryUpdateService;
            if (IsGameMode)
            {
                target.State = () => updates.GetGameTargetState(key);
                target.ApplyItem = item => updates.ApplyToGameTarget(key, item);
                target.LinkItem = item => updates.LinkGameTarget(key, item);
                return target;
            }

            var settingsAdapter = targets.SettingsAdapter(key);
            var apply = _plugin.LibraryApplyService;
            target.State = () => apply.GetSettingsState(settingsAdapter, _settings.Persisted);
            target.ApplyItem = item =>
            {
                apply.ApplyToSettings(settingsAdapter, item, _settings.Persisted);
                PersistIfSettingsScope();
            };
            target.LinkItem = item =>
            {
                // A platform that plays the global pack has no pack of its own to follow the preset.
                if (providerKey == null || _settings.Persisted.GetProviderUnlockSounds(providerKey) != null)
                {
                    apply.LinkSettings(settingsAdapter, item, _settings.Persisted);
                }
            };
            return target;
        }

        /// <summary>Writes the scope's surface, with every separately styled kind and the scope's custom template, as a package.</summary>
        private void ExportCurrentSurface(bool isFrame, string path)
        {
            var style = _currentScopeStyle ?? _currentStyle ?? throw new InvalidOperationException("No notification style is selected.");
            FlushEditors();
            var templateXaml = _toastTemplateResolver?.ReadCustomTemplateXaml(isFrame, ScopeProviderKey, ScopeGameId);
            _plugin.NotificationStylePortableStore.ExportSurfacePackage(isFrame, style, path, templateXaml);
        }

        private void FlushEditors()
        {
            _toastEditorViewModel?.FlushPendingPersist();
            _frameEditorViewModel?.FlushPendingPersist();
        }

        private void PersistIfSettingsScope()
        {
            if (!IsGameMode)
            {
                _plugin.PersistSettingsForUi();
            }
        }

        /// <summary>After the preset row applied an item: the editors and mockups show the new look.</summary>
        private void OnPresetApplied()
        {
            ApplySelection();
            if (IsSoundsTabActive)
            {
                // The host's preloaded set follows the new files.
                SoundsEditor.RefreshAndApply();
                return;
            }

            UpdateMockups();
            ReportTemplateErrors();
        }

        /// <summary>Shows the templates an apply could not install, if any; true when there were some.</summary>
        private bool ReportTemplateErrors()
        {
            var targets = _plugin?.NotificationLibraryTargets;
            if (targets == null)
            {
                return false;
            }

            var errors = targets.AdapterFor(isFrame: false).TakeTemplateErrors()
                .Concat(targets.AdapterFor(isFrame: true).TakeTemplateErrors())
                .ToList();
            if (errors.Count == 0)
            {
                return false;
            }

            Inform(string.Format(L("LOCPlayAch_Status_Failed"), string.Join("\n", errors)), MessageBoxImage.Warning);
            return true;
        }

        /// <summary>
        /// Brings the preset row in line with the selection: its list and the item the target
        /// follows. On a game tab everything that would write a style is idle until the game is
        /// styled separately; an inherited look has nowhere to put an import or a preset.
        /// </summary>
        private void RefreshPresetRow()
        {
            if (StylePresetPicker == null)
            {
                return;
            }

            var gameCustomized = IsSoundsTabActive ? CustomizeGameSoundsCheckBox : CustomizeGameCheckBox;
            StylePresetPicker.IsEnabled = !IsGameMode || gameCustomized?.IsChecked == true;
            StylePresetPicker.RefreshNow();
        }

        private void SurfaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // SelectionChanged bubbles up from ComboBoxes inside the tabs; only a tab switch
            // should swap the preset list.
            if (_suppressSurfaceTabEvents || !ReferenceEquals(e.OriginalSource, SurfaceTabs))
            {
                return;
            }

            ApplyGameSettingsTab();
            ApplyPreviewColumns();

            // Each tab carries its own sample dropdown, so the tab switch can change the kind
            // being styled along with the surface; the preset row follows on the way out.
            ApplySelection();
        }

        private bool IsGameSettingsTabActive =>
            BehaviorTabItem?.IsSelected == true || CapturesTabItem?.IsSelected == true;

        /// <summary>
        /// Shows the game's Behavior or Captures section in place of the style preview, presets
        /// and editors while one of those tabs is active, creating it on first use.
        /// </summary>
        private void ApplyGameSettingsTab()
        {
            // The tabs raise SelectionChanged while InitializeComponent is still building the tree.
            if (SurfaceToolbar == null || PreviewScopeGrid == null || StyleScroller == null || GameSettingsHost == null)
            {
                return;
            }

            var active = IsGameMode && IsGameSettingsTabActive;
            var styleVisibility = active ? Visibility.Collapsed : Visibility.Visible;
            SurfaceToolbar.Visibility = styleVisibility;
            PreviewScopeGrid.Visibility = styleVisibility;
            StyleScroller.Visibility = styleVisibility;
            GameSettingsHost.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            if (!active)
            {
                GameSettingsHost.Content = null;
                return;
            }

            if (BehaviorTabItem.IsSelected)
            {
                GameSettingsHost.Content = _gameBehaviorSection ?? (_gameBehaviorSection =
                    new NotificationBehaviorSection(_settings, _plugin, _logger, _gameId, ResolveGameProviderKey()));
            }
            else
            {
                GameSettingsHost.Content = _gameCapturesSection ?? (_gameCapturesSection =
                    new NotificationCapturesSection(_settings, _plugin, _logger, _gameId, ResolveGameProviderKey()));
            }
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

                if (dialog.ShowDialog() != DialogResult.OK)
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

        private void ResetStyle_Click(object sender, RoutedEventArgs e)
        {
            ResetStyle(isFrame: FrameTabItem?.IsSelected == true);
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

            _gameBehaviorSection?.RefreshGameData();
            _gameCapturesSection?.RefreshGameData();
            ApplySelection();
        }

        public void Dispose()
        {
            if (ToastBackgroundAnimationHost != null)
            {
                AsyncImage.RemoveSourceReadyHandler(
                    ToastBackgroundAnimationHost,
                    OnToastBackgroundSourceChanged);
                AsyncImage.SetUri(ToastBackgroundAnimationHost, null);
            }

            _toastPreviewViewModel = null;

            StylePresetPicker?.Dispose();
            _persistedSubscription?.Dispose();
            _toastEditorViewModel?.Dispose();
            _frameEditorViewModel?.Dispose();
            SoundsEditor?.Dispose();
            _gameBehaviorSection?.Dispose();
            _gameCapturesSection?.Dispose();
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
