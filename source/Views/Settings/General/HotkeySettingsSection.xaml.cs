using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Views.Settings.General
{
    /// <summary>
    /// General settings: Hotkeys section. Hosts the hotkey enable toggles and the capture
    /// buttons that write captured gestures directly into the persisted settings.
    /// </summary>
    public partial class HotkeySettingsSection : UserControl, IDisposable
    {
        public static readonly DependencyProperty ViewAchievementsHotkeyButtonTextProperty =
            DependencyProperty.Register(
                nameof(ViewAchievementsHotkeyButtonText),
                typeof(string),
                typeof(HotkeySettingsSection),
                new PropertyMetadata(string.Empty));

        public string ViewAchievementsHotkeyButtonText
        {
            get => (string)GetValue(ViewAchievementsHotkeyButtonTextProperty);
            set => SetValue(ViewAchievementsHotkeyButtonTextProperty, value);
        }

        public static readonly DependencyProperty ManageAchievementsHotkeyButtonTextProperty =
            DependencyProperty.Register(
                nameof(ManageAchievementsHotkeyButtonText),
                typeof(string),
                typeof(HotkeySettingsSection),
                new PropertyMetadata(string.Empty));

        public string ManageAchievementsHotkeyButtonText
        {
            get => (string)GetValue(ManageAchievementsHotkeyButtonTextProperty);
            set => SetValue(ManageAchievementsHotkeyButtonTextProperty, value);
        }

        public static readonly DependencyProperty OverviewHotkeyButtonTextProperty =
            DependencyProperty.Register(
                nameof(OverviewHotkeyButtonText),
                typeof(string),
                typeof(HotkeySettingsSection),
                new PropertyMetadata(string.Empty));

        public string OverviewHotkeyButtonText
        {
            get => (string)GetValue(OverviewHotkeyButtonTextProperty);
            set => SetValue(OverviewHotkeyButtonTextProperty, value);
        }

        public static readonly DependencyProperty OpenSettingsHotkeyButtonTextProperty =
            DependencyProperty.Register(
                nameof(OpenSettingsHotkeyButtonText),
                typeof(string),
                typeof(HotkeySettingsSection),
                new PropertyMetadata(string.Empty));

        public string OpenSettingsHotkeyButtonText
        {
            get => (string)GetValue(OpenSettingsHotkeyButtonTextProperty);
            set => SetValue(OpenSettingsHotkeyButtonTextProperty, value);
        }

        public static readonly DependencyProperty CategoryModeHotkeyButtonTextProperty =
            DependencyProperty.Register(
                nameof(CategoryModeHotkeyButtonText),
                typeof(string),
                typeof(HotkeySettingsSection),
                new PropertyMetadata(string.Empty));

        public string CategoryModeHotkeyButtonText
        {
            get => (string)GetValue(CategoryModeHotkeyButtonTextProperty);
            set => SetValue(CategoryModeHotkeyButtonTextProperty, value);
        }

        public static readonly DependencyProperty HotkeyCaptureStatusTextProperty =
            DependencyProperty.Register(
                nameof(HotkeyCaptureStatusText),
                typeof(string),
                typeof(HotkeySettingsSection),
                new PropertyMetadata(string.Empty));

        public string HotkeyCaptureStatusText
        {
            get => (string)GetValue(HotkeyCaptureStatusTextProperty);
            set => SetValue(HotkeyCaptureStatusTextProperty, value);
        }

        private enum HotkeyCaptureTarget
        {
            ViewAchievements,
            ManageAchievements,
            Overview,
            OpenSettings,
            CategoryMode
        }

        private static readonly HotkeyCaptureTarget[] AllTargets =
            (HotkeyCaptureTarget[])Enum.GetValues(typeof(HotkeyCaptureTarget));

        private readonly PlayniteAchievementsSettings _settings;
        private readonly PersistedSettingsSubscription _persistedSubscription;
        private HotkeyCaptureTarget? _capturingHotkey;

        public HotkeySettingsSection()
        {
            InitializeComponent();
        }

        internal HotkeySettingsSection(PlayniteAchievementsSettings settings)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            _persistedSubscription = new PersistedSettingsSubscription(
                _settings,
                OnPersistedPropertyChanged,
                UpdateHotkeyButtonTexts);

            UpdateHotkeyButtonTexts();
        }

        private void OnPersistedPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PersistedSettings.ViewAchievementsHotkey) ||
                e.PropertyName == nameof(PersistedSettings.ManageAchievementsHotkey) ||
                e.PropertyName == nameof(PersistedSettings.OverviewHotkey) ||
                e.PropertyName == nameof(PersistedSettings.OpenSettingsHotkey) ||
                e.PropertyName == nameof(PersistedSettings.CategoryModeHotkey))
            {
                UpdateHotkeyButtonTexts();
            }
        }

        private void HotkeyCapture_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button &&
                button.CommandParameter is string targetName &&
                Enum.TryParse(targetName, out HotkeyCaptureTarget target))
            {
                StartHotkeyCapture(target, button);
            }
        }

        private void ResetHotkey_Click(object sender, RoutedEventArgs e)
        {
            var persisted = _settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            if (!(sender is Button { CommandParameter: string targetName }) ||
                !Enum.TryParse(targetName, out HotkeyCaptureTarget target))
            {
                return;
            }

            SetPersistedHotkey(target, GetDefaultHotkey(target));
            EndHotkeyCapture();
        }

        private void HotkeyCapture_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_capturingHotkey.HasValue)
            {
                return;
            }

            var target = _capturingHotkey.Value;
            if (!ReferenceEquals(sender, GetCaptureButton(target)))
            {
                return;
            }

            e.Handled = true;
            var key = GetEffectiveHotkeyCaptureKey(e);
            if (key == Key.Escape)
            {
                EndHotkeyCapture();
                return;
            }

            if (key == Key.Back || key == Key.Delete)
            {
                SetPersistedHotkey(target, string.Empty);
                EndHotkeyCapture();
                return;
            }

            if (!AchievementHotkeyGesture.TryCreate(key, Keyboard.Modifiers, out var gesture))
            {
                HotkeyCaptureStatusText = L("LOCPlayAch_Hotkeys_InvalidShortcut");
                return;
            }

            if (IsDuplicateHotkey(target, gesture))
            {
                HotkeyCaptureStatusText = L("LOCPlayAch_Hotkeys_DuplicateShortcut");
                return;
            }

            SetPersistedHotkey(target, gesture.ToString());
            EndHotkeyCapture();
        }

        private void StartHotkeyCapture(HotkeyCaptureTarget target, Button button)
        {
            // Refresh every button from persisted state first, then mark only the captured one.
            _capturingHotkey = null;
            UpdateHotkeyButtonTexts();

            _capturingHotkey = target;
            HotkeyCaptureStatusText = L("LOCPlayAch_Hotkeys_CapturePrompt");
            SetHotkeyButtonText(target, L("LOCPlayAch_Hotkeys_CaptureButton"));

            button?.Focus();
            Keyboard.Focus(button);
        }

        private void EndHotkeyCapture()
        {
            _capturingHotkey = null;
            HotkeyCaptureStatusText = string.Empty;
            UpdateHotkeyButtonTexts();
        }

        private string GetPersistedHotkey(HotkeyCaptureTarget target)
        {
            var persisted = _settings?.Persisted;
            switch (target)
            {
                case HotkeyCaptureTarget.ViewAchievements: return persisted?.ViewAchievementsHotkey;
                case HotkeyCaptureTarget.ManageAchievements: return persisted?.ManageAchievementsHotkey;
                case HotkeyCaptureTarget.Overview: return persisted?.OverviewHotkey;
                case HotkeyCaptureTarget.OpenSettings: return persisted?.OpenSettingsHotkey;
                case HotkeyCaptureTarget.CategoryMode: return persisted?.CategoryModeHotkey;
                default: return null;
            }
        }

        private void SetPersistedHotkey(HotkeyCaptureTarget target, string hotkey)
        {
            var persisted = _settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            switch (target)
            {
                case HotkeyCaptureTarget.ViewAchievements: persisted.ViewAchievementsHotkey = hotkey; break;
                case HotkeyCaptureTarget.ManageAchievements: persisted.ManageAchievementsHotkey = hotkey; break;
                case HotkeyCaptureTarget.Overview: persisted.OverviewHotkey = hotkey; break;
                case HotkeyCaptureTarget.OpenSettings: persisted.OpenSettingsHotkey = hotkey; break;
                case HotkeyCaptureTarget.CategoryMode: persisted.CategoryModeHotkey = hotkey; break;
            }
        }

        private static string GetDefaultHotkey(HotkeyCaptureTarget target)
        {
            switch (target)
            {
                case HotkeyCaptureTarget.ViewAchievements: return PersistedSettings.DefaultViewAchievementsHotkey;
                case HotkeyCaptureTarget.ManageAchievements: return PersistedSettings.DefaultManageAchievementsHotkey;
                case HotkeyCaptureTarget.Overview: return PersistedSettings.DefaultOverviewHotkey;
                case HotkeyCaptureTarget.OpenSettings: return PersistedSettings.DefaultOpenSettingsHotkey;
                case HotkeyCaptureTarget.CategoryMode: return PersistedSettings.DefaultCategoryModeHotkey;
                default: return string.Empty;
            }
        }

        private Button GetCaptureButton(HotkeyCaptureTarget target)
        {
            switch (target)
            {
                case HotkeyCaptureTarget.ViewAchievements: return ViewAchievementsHotkeyCaptureButton;
                case HotkeyCaptureTarget.ManageAchievements: return ManageAchievementsHotkeyCaptureButton;
                case HotkeyCaptureTarget.Overview: return OverviewHotkeyCaptureButton;
                case HotkeyCaptureTarget.OpenSettings: return OpenSettingsHotkeyCaptureButton;
                case HotkeyCaptureTarget.CategoryMode: return CategoryModeHotkeyCaptureButton;
                default: return null;
            }
        }

        private void SetHotkeyButtonText(HotkeyCaptureTarget target, string text)
        {
            switch (target)
            {
                case HotkeyCaptureTarget.ViewAchievements: ViewAchievementsHotkeyButtonText = text; break;
                case HotkeyCaptureTarget.ManageAchievements: ManageAchievementsHotkeyButtonText = text; break;
                case HotkeyCaptureTarget.Overview: OverviewHotkeyButtonText = text; break;
                case HotkeyCaptureTarget.OpenSettings: OpenSettingsHotkeyButtonText = text; break;
                case HotkeyCaptureTarget.CategoryMode: CategoryModeHotkeyButtonText = text; break;
            }
        }

        private bool IsDuplicateHotkey(HotkeyCaptureTarget target, AchievementHotkeyGesture gesture)
        {
            if (gesture == null || gesture.IsEmpty || _settings?.Persisted == null)
            {
                return false;
            }

            foreach (var other in AllTargets)
            {
                if (other == target)
                {
                    continue;
                }

                if (AchievementHotkeyGesture.TryParse(GetPersistedHotkey(other), out var otherGesture) &&
                    otherGesture != null &&
                    !otherGesture.IsEmpty &&
                    gesture.Equals(otherGesture))
                {
                    return true;
                }
            }

            return false;
        }

        private void UpdateHotkeyButtonTexts()
        {
            if (_capturingHotkey.HasValue)
            {
                return;
            }

            foreach (var target in AllTargets)
            {
                SetHotkeyButtonText(target, FormatHotkeyButtonText(GetPersistedHotkey(target)));
            }
        }

        private string FormatHotkeyButtonText(string hotkey)
        {
            return string.IsNullOrWhiteSpace(hotkey)
                ? L("LOCPlayAch_Common_None")
                : hotkey;
        }

        private static Key GetEffectiveHotkeyCaptureKey(KeyEventArgs e)
        {
            if (e == null)
            {
                return Key.None;
            }

            if (e.Key == Key.System)
            {
                return e.SystemKey;
            }

            if (e.Key == Key.ImeProcessed)
            {
                return e.ImeProcessedKey;
            }

            return e.Key;
        }

        public void Dispose()
        {
            _persistedSubscription?.Dispose();
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }
}
