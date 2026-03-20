using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;

namespace PlayniteAchievements.Views.ThemeIntegration.Base
{
    /// <summary>
    /// Base class for theme integration controls.
    /// Provides common initialization and game context change handling for all achievement controls.
    /// </summary>
    public abstract class ThemeControlBase : PluginUserControl
    {
        private bool _isAutoUpdateSubscribed;
        private bool _themeUpdateQueued;

        /// <summary>
        /// Gets the plugin instance for this control.
        /// </summary>
        protected PlayniteAchievementsPlugin Plugin { get; }

        #region ThemeDataOverride Dependency Property

        /// <summary>
        /// Identifies the SettingsOverride dependency property.
        /// When set, this settings instance is used instead of Plugin.Settings.
        /// Used by settings preview to bind preview controls to the editable settings object.
        /// </summary>
        public static readonly DependencyProperty SettingsOverrideProperty =
            DependencyProperty.Register(nameof(SettingsOverride), typeof(PlayniteAchievementsSettings),
                typeof(ThemeControlBase), new PropertyMetadata(null, OnSettingsOverrideChanged));

        /// <summary>
        /// Gets or sets a settings override for preview purposes.
        /// </summary>
        public PlayniteAchievementsSettings SettingsOverride
        {
            get => (PlayniteAchievementsSettings)GetValue(SettingsOverrideProperty);
            set => SetValue(SettingsOverrideProperty, value);
        }

        private static void OnSettingsOverrideChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ThemeControlBase control)
            {
                control.UpdateDataContext();

                if (control._isAutoUpdateSubscribed)
                {
                    control.UnsubscribeFromThemeDataUpdates();
                    control.SubscribeToThemeDataUpdates();
                }

                if (control.IsLoaded)
                {
                    control.OnThemeDataUpdated();
                }
            }
        }

        /// <summary>
        /// Identifies the ThemeDataOverride dependency property.
        /// When set, this override is used instead of Plugin.Settings.Theme for data binding.
        /// Used by settings preview to inject mock data.
        /// </summary>
        public static readonly DependencyProperty ThemeDataOverrideProperty =
            DependencyProperty.Register(nameof(ThemeDataOverride), typeof(ModernThemeBindings),
                typeof(ThemeControlBase), new PropertyMetadata(null, OnThemeDataOverrideChanged));

        /// <summary>
        /// Gets or sets a modern theme binding override for preview purposes.
        /// When null (default), uses Plugin.Settings.Theme.
        /// When set, uses this instance instead (for settings preview).
        /// </summary>
        public ModernThemeBindings ThemeDataOverride
        {
            get => (ModernThemeBindings)GetValue(ThemeDataOverrideProperty);
            set => SetValue(ThemeDataOverrideProperty, value);
        }

        private static void OnThemeDataOverrideChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ThemeControlBase control)
            {
                control.UpdateDataContext();
                control.OnThemeDataOverrideChangedInternal();

                // Re-subscribe to theme data updates if auto-update is enabled
                if (control._isAutoUpdateSubscribed)
                {
                    control.UnsubscribeFromThemeDataUpdates();
                    control.SubscribeToThemeDataUpdates();
                }

                if (control.IsLoaded)
                {
                    control.OnThemeDataUpdated();
                }
            }
        }

        public static readonly DependencyProperty LegacyThemeOverrideProperty =
            DependencyProperty.Register(nameof(LegacyThemeOverride), typeof(LegacyThemeBindings),
                typeof(ThemeControlBase), new PropertyMetadata(null, OnLegacyThemeOverrideChanged));

        /// <summary>
        /// Gets or sets a legacy theme binding override for preview purposes.
        /// When null (default), uses Plugin.Settings.LegacyTheme.
        /// </summary>
        public LegacyThemeBindings LegacyThemeOverride
        {
            get => (LegacyThemeBindings)GetValue(LegacyThemeOverrideProperty);
            set => SetValue(LegacyThemeOverrideProperty, value);
        }

        private static void OnLegacyThemeOverrideChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ThemeControlBase control)
            {
                control.UpdateDataContext();

                if (control._isAutoUpdateSubscribed)
                {
                    control.UnsubscribeFromThemeDataUpdates();
                    control.SubscribeToThemeDataUpdates();
                }

                if (control.IsLoaded)
                {
                    control.OnThemeDataUpdated();
                }
            }
        }

        /// <summary>
        /// Called when ThemeDataOverride changes. Derived classes can override to perform
        /// additional actions like clearing caches.
        /// </summary>
        protected virtual void OnThemeDataOverrideChangedInternal()
        {
        }

        /// <summary>
        /// Gets the effective settings object to use for binding and update subscriptions.
        /// Returns SettingsOverride if set, otherwise Plugin.Settings.
        /// </summary>
        protected PlayniteAchievementsSettings EffectiveSettings => SettingsOverride ?? Plugin?.Settings;

        /// <summary>
        /// Gets the effective modern theme bindings to use for binding.
        /// Returns ThemeDataOverride if set, otherwise Plugin.Settings.Theme.
        /// </summary>
        protected ModernThemeBindings EffectiveTheme => ThemeDataOverride ?? EffectiveSettings?.Theme;

        /// <summary>
        /// Gets the effective legacy theme bindings to use for binding.
        /// Returns LegacyThemeOverride if set, otherwise Plugin.Settings.LegacyTheme.
        /// </summary>
        protected LegacyThemeBindings EffectiveLegacyTheme => LegacyThemeOverride ?? EffectiveSettings?.LegacyTheme;

        private void UpdateDataContext()
        {
            var settings = EffectiveSettings;
            if (ThemeDataOverride != null || LegacyThemeOverride != null)
            {
                DataContext = new ThemePreviewContext(
                    settings,
                    ThemeDataOverride ?? settings?.Theme,
                    LegacyThemeOverride ?? settings?.LegacyTheme);
            }
            else
            {
                DataContext = settings;
            }
        }

        #endregion

        /// <summary>
        /// Gets a value indicating whether this control should subscribe to theme data change notifications.
        /// </summary>
        protected virtual bool EnableAutomaticThemeDataUpdates => false;

        /// <summary>
        /// Gets a value indicating whether this control consumes modern theme bindings.
        /// </summary>
        protected virtual bool UsesThemeBindings => false;

        /// <summary>
        /// Gets a value indicating whether this control consumes legacy theme bindings.
        /// </summary>
        protected virtual bool UsesLegacyThemeBindings => false;

        /// <summary>
        /// Determines whether a change raised from <see cref="PlayniteAchievementsSettings"/> should trigger a refresh.
        /// </summary>
        protected virtual bool ShouldHandleSettingsDataChange(string propertyName) => false;

        /// <summary>
        /// Determines whether a change raised from <see cref="Models.ThemeIntegration.ModernThemeBindings"/> should trigger a refresh.
        /// </summary>
        protected virtual bool ShouldHandleThemeDataChange(string propertyName) => false;

        /// <summary>
        /// Determines whether a change raised from <see cref="Models.ThemeIntegration.LegacyThemeBindings"/> should trigger a refresh.
        /// </summary>
        protected virtual bool ShouldHandleLegacyThemeDataChange(string propertyName) => false;

        /// <summary>
        /// Called when watched theme data changes and refresh work should be performed.
        /// </summary>
        protected virtual void OnThemeDataUpdated()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ThemeControlBase"/> class.
        /// Derived classes must call InitializeComponent in their constructors.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the PlayniteAchievementsPlugin instance is not available.
        /// </exception>
        protected ThemeControlBase()
        {
            Plugin = PlayniteAchievementsPlugin.Instance
                ?? throw new InvalidOperationException("Plugin instance not available");

            DataContext = EffectiveSettings;
            Loaded += ThemeControlBase_Loaded;
            Unloaded += ThemeControlBase_Unloaded;
        }

        private void ThemeControlBase_Loaded(object sender, RoutedEventArgs e)
        {
            if (!EnableAutomaticThemeDataUpdates)
            {
                return;
            }

            SubscribeToThemeDataUpdates();
            QueueThemeDataUpdate();
        }

        private void ThemeControlBase_Unloaded(object sender, RoutedEventArgs e)
        {
            if (!EnableAutomaticThemeDataUpdates)
            {
                return;
            }

            UnsubscribeFromThemeDataUpdates();
        }

        private void SubscribeToThemeDataUpdates()
        {
            if (_isAutoUpdateSubscribed)
            {
                return;
            }

            var settings = EffectiveSettings;
            if (settings == null)
            {
                return;
            }

            settings.PropertyChanged -= Settings_PropertyChanged;
            settings.PropertyChanged += Settings_PropertyChanged;

            if (settings.Persisted != null)
            {
                settings.Persisted.PropertyChanged -= Persisted_PropertyChanged;
                settings.Persisted.PropertyChanged += Persisted_PropertyChanged;
            }

            if (UsesThemeBindings)
            {
                var effectiveTheme = EffectiveTheme;
                if (effectiveTheme != null)
                {
                    effectiveTheme.PropertyChanged -= Theme_PropertyChanged;
                    effectiveTheme.PropertyChanged += Theme_PropertyChanged;
                }
            }

            if (UsesLegacyThemeBindings)
            {
                var effectiveLegacyTheme = EffectiveLegacyTheme;
                if (effectiveLegacyTheme != null)
                {
                    effectiveLegacyTheme.PropertyChanged -= LegacyTheme_PropertyChanged;
                    effectiveLegacyTheme.PropertyChanged += LegacyTheme_PropertyChanged;
                }
            }

            _isAutoUpdateSubscribed = true;
        }

        private void UnsubscribeFromThemeDataUpdates()
        {
            if (!_isAutoUpdateSubscribed)
            {
                return;
            }

            var settings = EffectiveSettings;
            if (settings != null)
            {
                settings.PropertyChanged -= Settings_PropertyChanged;

                if (settings.Persisted != null)
                {
                    settings.Persisted.PropertyChanged -= Persisted_PropertyChanged;
                }

                if (UsesThemeBindings)
                {
                    var effectiveTheme = EffectiveTheme;
                    if (effectiveTheme != null)
                    {
                        effectiveTheme.PropertyChanged -= Theme_PropertyChanged;
                    }
                }

                if (UsesLegacyThemeBindings)
                {
                    var effectiveLegacyTheme = EffectiveLegacyTheme;
                    if (effectiveLegacyTheme != null)
                    {
                        effectiveLegacyTheme.PropertyChanged -= LegacyTheme_PropertyChanged;
                    }
                }
            }

            _isAutoUpdateSubscribed = false;
            _themeUpdateQueued = false;
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            var propertyName = e?.PropertyName;
            if (propertyName == nameof(PlayniteAchievementsSettings.Theme) ||
                propertyName == nameof(PlayniteAchievementsSettings.LegacyTheme) ||
                propertyName == nameof(PlayniteAchievementsSettings.Persisted))
            {
                UpdateDataContext();

                // Keep nested subscriptions valid if either child object is replaced.
                UnsubscribeFromThemeDataUpdates();
                SubscribeToThemeDataUpdates();
            }

            if (ShouldHandleChange(propertyName, ShouldHandleSettingsDataChange))
            {
                QueueThemeDataUpdate();
            }
        }

        private void Persisted_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (ShouldHandleChange(e?.PropertyName, ShouldHandleSettingsDataChange))
            {
                QueueThemeDataUpdate();
            }
        }

        private void Theme_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (ShouldHandleChange(e?.PropertyName, ShouldHandleThemeDataChange))
            {
                QueueThemeDataUpdate();
            }
        }

        private void LegacyTheme_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (ShouldHandleChange(e?.PropertyName, ShouldHandleLegacyThemeDataChange))
            {
                QueueThemeDataUpdate();
            }
        }

        private static bool ShouldHandleChange(string propertyName, Func<string, bool> predicate)
        {
            if (string.IsNullOrEmpty(propertyName))
            {
                return true;
            }

            return predicate?.Invoke(propertyName) == true;
        }

        private void QueueThemeDataUpdate()
        {
            if (!IsLoaded || _themeUpdateQueued)
            {
                return;
            }

            _themeUpdateQueued = true;
            var dispatcher = Dispatcher;
            if (dispatcher == null)
            {
                ExecuteQueuedThemeDataUpdate();
                return;
            }

            var priority = ThemeDataOverride != null || LegacyThemeOverride != null
                ? DispatcherPriority.DataBind
                : DispatcherPriority.Background;

            dispatcher.BeginInvoke(
                new Action(ExecuteQueuedThemeDataUpdate),
                priority);
        }

        private void ExecuteQueuedThemeDataUpdate()
        {
            _themeUpdateQueued = false;
            if (!IsLoaded || !EnableAutomaticThemeDataUpdates)
            {
                return;
            }

            OnThemeDataUpdated();
        }

        /// <summary>
        /// Called when the game context changes for this control.
        /// Requests a theme update for the new game context.
        /// </summary>
        /// <param name="oldContext">The previous game context.</param>
        /// <param name="newContext">The new game context.</param>
        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            Plugin.RequestThemeUpdate(newContext);
        }
    }

    /// <summary>
    /// DataContext wrapper that substitutes custom modern and legacy theme bindings for preview.
    /// </summary>
    internal class ThemePreviewContext
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ModernThemeBindings _themeOverride;
        private readonly LegacyThemeBindings _legacyThemeOverride;

        public ThemePreviewContext(
            PlayniteAchievementsSettings settings,
            ModernThemeBindings themeOverride,
            LegacyThemeBindings legacyThemeOverride)
        {
            _settings = settings;
            _themeOverride = themeOverride;
            _legacyThemeOverride = legacyThemeOverride;
        }

        /// <summary>
        /// Returns the override modern theme bindings instead of the settings' Theme.
        /// </summary>
        public ModernThemeBindings Theme => _themeOverride;

        /// <summary>
        /// Returns the override legacy theme bindings instead of the settings' LegacyTheme.
        /// </summary>
        public LegacyThemeBindings LegacyTheme => _legacyThemeOverride;

        /// <summary>
        /// Returns the effective settings object backing this preview context.
        /// </summary>
        public PlayniteAchievementsSettings Settings => _settings;

        // Forward other common settings properties
        public PersistedSettings Persisted => _settings?.Persisted;
    }
}

