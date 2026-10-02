using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
// WinForms dialog: the WPF Microsoft.Win32 picker renders legacy-style on .NET Framework.
using DialogResult = System.Windows.Forms.DialogResult;
using OpenFileDialog = System.Windows.Forms.OpenFileDialog;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Views.Settings.Notifications
{
    /// <summary>
    /// Notification settings: the Behavior page. What fires (the four enable switches), how it
    /// fires (duration, delay, concurrency, screen corner), and what accompanies it (controller
    /// vibration and the per-tier unlock sounds). Capture settings live in
    /// <see cref="NotificationCapturesSection"/>, per-provider overrides in
    /// <see cref="NotificationPlatformsSection"/>, and styling in
    /// <see cref="General.NotificationAppearanceSection"/>.
    /// </summary>
    public partial class NotificationBehaviorSection : UserControl, IDisposable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly PersistedSettingsSubscription _persistedSubscription;
        private readonly UnlockSoundSettingsViewModel _unlockSoundsViewModel;
        private readonly ILogger _logger;

        public NotificationBehaviorSection()
        {
            InitializeComponent();
        }

        internal NotificationBehaviorSection(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ILogger logger)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));
            _logger = logger;

            _persistedSubscription = new PersistedSettingsSubscription(
                _settings,
                OnPersistedPropertyChanged);

            // DataContext island: the per-tier sound rows carry their own view model.
            _unlockSoundsViewModel = new UnlockSoundSettingsViewModel(settings, plugin.UnlockSounds, logger);
            UnlockSoundRows.DataContext = _unlockSoundsViewModel;
        }

        private void UnlockSoundBrowse_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is UnlockSoundRowItem row))
            {
                return;
            }

            var dialog = new OpenFileDialog
            {
                Filter = UnlockSoundResolver.BuildOpenFileDialogFilter(),
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                row.CustomPath = dialog.FileName;
            }
        }

        private void UnlockSoundClear_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is UnlockSoundRowItem row)
            {
                row.CustomPath = null;
            }
        }

        private void UnlockSoundTest_Click(object sender, RoutedEventArgs e)
        {
            Keyboard.ClearFocus();
            if ((sender as FrameworkElement)?.DataContext is UnlockSoundRowItem row)
            {
                _unlockSoundsViewModel?.Test(row);
            }
        }

        /// <summary>
        /// Plays what a theme ships for this tier. One candidate is not worth a menu, so the button
        /// plays it outright; two means the desktop and the fullscreen theme ship different files
        /// for the tier and the listener has to pick, which is the case this exists for. Either can
        /// be heard without restarting Playnite into the other mode.
        /// </summary>
        private void UnlockSoundTheme_Click(object sender, RoutedEventArgs e)
        {
            Keyboard.ClearFocus();
            if (!(sender is Button button) || !(button.DataContext is UnlockSoundRowItem row))
            {
                return;
            }

            if (row.ThemeCandidates.Count == 0)
            {
                return;
            }

            if (row.ThemeCandidates.Count == 1)
            {
                _unlockSoundsViewModel?.TestFile(row.ThemeCandidates[0].Path);
                return;
            }

            var menu = button.ContextMenu;
            if (menu == null)
            {
                return;
            }

            menu.Items.Clear();
            foreach (var candidate in row.ThemeCandidates)
            {
                var path = candidate.Path;
                var item = new MenuItem { Header = candidate.Label };
                item.Click += (s, args) => _unlockSoundsViewModel?.TestFile(path);
                menu.Items.Add(item);
            }

            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        /// <summary>
        /// Pulses the controllers at the currently configured strength and duration so the settings
        /// can be felt without unlocking an achievement.
        /// </summary>
        private void TestVibration_Click(object sender, RoutedEventArgs e)
        {
            // Nothing here takes focus away from the button the way the buttons that open a window
            // do, so it would keep the theme's focused look until something else was clicked.
            Keyboard.ClearFocus();

            var persisted = _settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            try
            {
                ControllerVibrationService.Pulse(
                    persisted.ControllerVibrationStrengthPercent,
                    persisted.ControllerVibrationDurationMs,
                    _logger);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Test controller vibration failed.");
            }
        }

        private void OnPersistedPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e?.PropertyName)
            {
                case null:
                case "":
                case nameof(PersistedSettings.UnlockSounds):
                    // The persisted instance was replaced (Cancel) or the slot object swapped.
                    _unlockSoundsViewModel?.Refresh();
                    break;
                case nameof(PersistedSettings.AllowThemeUnlockSounds):
                    // Changes which file each tier resolves to, so the rows are restated and the
                    // host's preloaded set is rebuilt.
                    _unlockSoundsViewModel?.Refresh();
                    _unlockSoundsViewModel?.ScheduleApply();
                    break;
                case nameof(PersistedSettings.UnlockSoundVolumePercent):
                case nameof(PersistedSettings.EnableUnlockSounds):
                    _unlockSoundsViewModel?.ScheduleApply();
                    break;
            }

            // The tier badges are drawn from the rarity appearance settings, so they have to be
            // rebuilt when those change while this page is open.
            if (!string.IsNullOrEmpty(e?.PropertyName) &&
                RarityAppearanceHelper.IsAppearanceSettingPropertyName(e.PropertyName))
            {
                _unlockSoundsViewModel?.Refresh();
            }
        }

        public void Dispose()
        {
            _persistedSubscription?.Dispose();
            _unlockSoundsViewModel?.Dispose();
        }
    }
}
