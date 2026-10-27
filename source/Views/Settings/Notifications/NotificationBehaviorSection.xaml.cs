using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Views.Settings.Notifications
{
    /// <summary>
    /// Notification settings: the Behavior page. What fires (the four enable switches) and how it
    /// fires (duration, delay, concurrency, screen corner, controller vibration). Capture settings
    /// live in <see cref="NotificationCapturesSection"/>, per-provider overrides in
    /// <see cref="NotificationPlatformsSection"/>, and styling and unlock sounds in
    /// <see cref="NotificationAppearanceSection"/>.
    /// </summary>
    /// <remarks>
    /// In game mode (Manage Achievements) the section edits one game's
    /// <see cref="GameNotificationBehaviorOverride"/> instead, and hides the rows that stay
    /// app-wide (duration, concurrency, render mode, vibration) and position, which a game sets
    /// through its own style.
    /// </remarks>
    public partial class NotificationBehaviorSection : UserControl, IDisposable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private readonly GameNotificationOverrideScope<GameNotificationBehaviorOverride> _gameScope;

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
            _logger = logger;
        }

        /// <summary>Game mode: edits the game's own behavior for <paramref name="gameId"/>.</summary>
        internal NotificationBehaviorSection(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ILogger logger,
            Guid gameId,
            string providerKey)
            : this(settings, plugin, logger)
        {
            _gameScope = new GameNotificationOverrideScope<GameNotificationBehaviorOverride>(
                plugin?.GameCustomDataStore,
                gameId,
                data => data.NotificationBehaviorOverride,
                (data, value) => data.NotificationBehaviorOverride = value,
                () => GameNotificationBehaviorOverride.FromEffective(
                    settings.Persisted,
                    settings.Persisted?.GetProviderNotificationOverride(providerKey)),
                value => value.Clone(),
                plugin?.PlayniteApi,
                logger);
            _gameScope.Changed += (s, e) => ApplyGameScope();

            ContentPanel.Margin = new Thickness(0);
            GameCustomizeCheckBox.Visibility = Visibility.Visible;
            foreach (var globalOnly in new FrameworkElement[]
            {
                SkipCompletionGlobalRow, DurationGlobalRow, MaxConcurrentGlobalRow, PositionGlobalRow, RenderModeGlobalRow, VibrationGlobalRow, VibrationGlobalPanel
            })
            {
                globalOnly.Visibility = Visibility.Collapsed;
            }

            // The snapshot has no friends switch of its own; the global feature toggle still decides.
            BindingOperations.ClearBinding(FriendUnlockToastsRow, VisibilityProperty);
            FriendUnlockToastsRow.Visibility = settings.Persisted?.EnableFriendsFeatures == true
                ? Visibility.Visible
                : Visibility.Collapsed;

            _gameScope.Load();
        }

        /// <summary>Re-reads the game's behavior after its custom data changed elsewhere.</summary>
        internal void RefreshGameData()
        {
            _gameScope?.Load();
        }

        private void ApplyGameScope()
        {
            SettingsBody.DataContext = new GameNotificationOverrideContext<GameNotificationBehaviorOverride>(_gameScope.Current);
            SettingsBody.IsEnabled = _gameScope.IsCustomized;
            GameCustomizeCheckBox.IsChecked = _gameScope.IsCustomized;
        }

        private void GameCustomizeCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (_gameScope != null && !_gameScope.SetCustomized(GameCustomizeCheckBox.IsChecked == true))
            {
                GameCustomizeCheckBox.IsChecked = _gameScope.IsCustomized;
            }
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

        public void Dispose()
        {
            _gameScope?.Dispose();
        }
    }
}
