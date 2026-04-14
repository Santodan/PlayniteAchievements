using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
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
        /// When set, this override is used instead of Plugin.Settings.ModernTheme for data binding.
        /// Used by settings preview to inject mock data.
        /// </summary>
        public static readonly DependencyProperty ThemeDataOverrideProperty =
            DependencyProperty.Register(nameof(ThemeDataOverride), typeof(ModernThemeBindings),
                typeof(ThemeControlBase), new PropertyMetadata(null, OnThemeDataOverrideChanged));

        /// <summary>
        /// Gets or sets a modern theme binding override for preview purposes.
        /// When null (default), uses Plugin.Settings.ModernTheme.
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
        /// Gets the effective persisted settings object for visibility and option bindings.
        /// </summary>
        public PersistedSettings Persisted => EffectiveSettings?.Persisted;

        /// <summary>
        /// Gets the effective modern theme bindings to use for binding.
        /// Returns ThemeDataOverride if set, otherwise Plugin.Settings.ModernTheme.
        /// </summary>
        protected ModernThemeBindings EffectiveTheme => ThemeDataOverride ?? EffectiveSettings?.ModernTheme;

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
                    ThemeDataOverride ?? settings?.ModernTheme,
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
            if (propertyName == nameof(PlayniteAchievementsSettings.ModernTheme) ||
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
    internal class ThemePreviewContext : PlayniteAchievements.Common.ObservableObject
    {
        private static readonly List<AchievementDetail> EmptyAchievementList = new List<AchievementDetail>();
        private static readonly AchievementRarityStats EmptyRarityStats = new AchievementRarityStats();

        private static readonly IReadOnlyDictionary<string, string> SettingsForwardMap =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [nameof(PlayniteAchievementsSettings.DynamicAchievements)] = nameof(DynamicAchievements),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsFilterKey)] = nameof(DynamicAchievementsFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsFilterLabel)] = nameof(DynamicAchievementsFilterLabel),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsSortKey)] = nameof(DynamicAchievementsSortKey),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsSortLabel)] = nameof(DynamicAchievementsSortLabel),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsSortDirectionKey)] = nameof(DynamicAchievementsSortDirectionKey),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsSortDirectionLabel)] = nameof(DynamicAchievementsSortDirectionLabel),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummaries)] = nameof(DynamicGameSummaries),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesProviderKey)] = nameof(DynamicGameSummariesProviderKey),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesProviderLabel)] = nameof(DynamicGameSummariesProviderLabel),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesSortKey)] = nameof(DynamicGameSummariesSortKey),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesSortLabel)] = nameof(DynamicGameSummariesSortLabel),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesSortDirectionKey)] = nameof(DynamicGameSummariesSortDirectionKey),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesSortDirectionLabel)] = nameof(DynamicGameSummariesSortDirectionLabel),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievements)] = nameof(DynamicLibraryAchievements),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsProviderKey)] = nameof(DynamicLibraryAchievementsProviderKey),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsProviderLabel)] = nameof(DynamicLibraryAchievementsProviderLabel),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsSortKey)] = nameof(DynamicLibraryAchievementsSortKey),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsSortLabel)] = nameof(DynamicLibraryAchievementsSortLabel),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsSortDirectionKey)] = nameof(DynamicLibraryAchievementsSortDirectionKey),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsSortDirectionLabel)] = nameof(DynamicLibraryAchievementsSortDirectionLabel),
                [nameof(PlayniteAchievementsSettings.SetDynamicAchievementsFilterCommand)] = nameof(SetDynamicAchievementsFilterCommand),
                [nameof(PlayniteAchievementsSettings.SortDynamicAchievementsCommand)] = nameof(SortDynamicAchievementsCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicAchievementsSortDirectionCommand)] = nameof(SetDynamicAchievementsSortDirectionCommand),
                [nameof(PlayniteAchievementsSettings.FilterDynamicLibraryAchievementsByProviderCommand)] = nameof(FilterDynamicLibraryAchievementsByProviderCommand),
                [nameof(PlayniteAchievementsSettings.SortDynamicLibraryAchievementsCommand)] = nameof(SortDynamicLibraryAchievementsCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicLibraryAchievementsSortDirectionCommand)] = nameof(SetDynamicLibraryAchievementsSortDirectionCommand),
                [nameof(PlayniteAchievementsSettings.FilterDynamicGameSummariesByProviderCommand)] = nameof(FilterDynamicGameSummariesByProviderCommand),
                [nameof(PlayniteAchievementsSettings.SortDynamicGameSummariesCommand)] = nameof(SortDynamicGameSummariesCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicGameSummariesSortDirectionCommand)] = nameof(SetDynamicGameSummariesSortDirectionCommand)
            };

        private static readonly IReadOnlyDictionary<string, string> ModernThemeForwardMap =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [nameof(ModernThemeBindings.HasAchievements)] = nameof(HasAchievements),
                [nameof(ModernThemeBindings.AchievementCount)] = nameof(AchievementCount),
                [nameof(ModernThemeBindings.UnlockedCount)] = nameof(UnlockedCount),
                [nameof(ModernThemeBindings.LockedCount)] = nameof(LockedCount),
                [nameof(ModernThemeBindings.ProgressPercentage)] = nameof(ProgressPercentage),
                [nameof(ModernThemeBindings.IsCompleted)] = nameof(IsCompleted),
                [nameof(ModernThemeBindings.AllAchievements)] = nameof(Achievements),
                [nameof(ModernThemeBindings.AchievementsNewestFirst)] = nameof(AchievementsNewestFirst),
                [nameof(ModernThemeBindings.AchievementsOldestFirst)] = nameof(AchievementsOldestFirst),
                [nameof(ModernThemeBindings.AchievementsRarityAsc)] = nameof(AchievementsRarityAsc),
                [nameof(ModernThemeBindings.AchievementsRarityDesc)] = nameof(AchievementsRarityDesc),
                [nameof(ModernThemeBindings.DynamicAchievements)] = nameof(DynamicAchievements),
                [nameof(ModernThemeBindings.DynamicAchievementsFilterKey)] = nameof(DynamicAchievementsFilterKey),
                [nameof(ModernThemeBindings.DynamicAchievementsFilterLabel)] = nameof(DynamicAchievementsFilterLabel),
                [nameof(ModernThemeBindings.DynamicAchievementsSortKey)] = nameof(DynamicAchievementsSortKey),
                [nameof(ModernThemeBindings.DynamicAchievementsSortLabel)] = nameof(DynamicAchievementsSortLabel),
                [nameof(ModernThemeBindings.DynamicAchievementsSortDirectionKey)] = nameof(DynamicAchievementsSortDirectionKey),
                [nameof(ModernThemeBindings.DynamicAchievementsSortDirectionLabel)] = nameof(DynamicAchievementsSortDirectionLabel),
                [nameof(ModernThemeBindings.DynamicGameSummaries)] = nameof(DynamicGameSummaries),
                [nameof(ModernThemeBindings.DynamicGameSummariesProviderKey)] = nameof(DynamicGameSummariesProviderKey),
                [nameof(ModernThemeBindings.DynamicGameSummariesProviderLabel)] = nameof(DynamicGameSummariesProviderLabel),
                [nameof(ModernThemeBindings.DynamicGameSummariesSortKey)] = nameof(DynamicGameSummariesSortKey),
                [nameof(ModernThemeBindings.DynamicGameSummariesSortLabel)] = nameof(DynamicGameSummariesSortLabel),
                [nameof(ModernThemeBindings.DynamicGameSummariesSortDirectionKey)] = nameof(DynamicGameSummariesSortDirectionKey),
                [nameof(ModernThemeBindings.DynamicGameSummariesSortDirectionLabel)] = nameof(DynamicGameSummariesSortDirectionLabel),
                [nameof(ModernThemeBindings.DynamicLibraryAchievements)] = nameof(DynamicLibraryAchievements),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementsProviderKey)] = nameof(DynamicLibraryAchievementsProviderKey),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementsProviderLabel)] = nameof(DynamicLibraryAchievementsProviderLabel),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementsSortKey)] = nameof(DynamicLibraryAchievementsSortKey),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementsSortLabel)] = nameof(DynamicLibraryAchievementsSortLabel),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementsSortDirectionKey)] = nameof(DynamicLibraryAchievementsSortDirectionKey),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementsSortDirectionLabel)] = nameof(DynamicLibraryAchievementsSortDirectionLabel),
                [nameof(ModernThemeBindings.Common)] = nameof(Common),
                [nameof(ModernThemeBindings.Uncommon)] = nameof(Uncommon),
                [nameof(ModernThemeBindings.Rare)] = nameof(Rare),
                [nameof(ModernThemeBindings.UltraRare)] = nameof(UltraRare),
                [nameof(ModernThemeBindings.RareAndUltraRare)] = nameof(RareAndUltraRare)
            };

        private static readonly string[] ForwardedModernProperties =
        {
            nameof(HasAchievements),
            nameof(AchievementCount),
            nameof(UnlockedCount),
            nameof(LockedCount),
            nameof(ProgressPercentage),
            nameof(IsCompleted),
            nameof(Achievements),
            nameof(AchievementsNewestFirst),
            nameof(AchievementsOldestFirst),
            nameof(AchievementsRarityAsc),
            nameof(AchievementsRarityDesc),
            nameof(DynamicAchievements),
            nameof(DynamicAchievementsFilterKey),
            nameof(DynamicAchievementsFilterLabel),
            nameof(DynamicAchievementsSortKey),
            nameof(DynamicAchievementsSortLabel),
            nameof(DynamicAchievementsSortDirectionKey),
            nameof(DynamicAchievementsSortDirectionLabel),
            nameof(DynamicGameSummaries),
            nameof(DynamicGameSummariesProviderKey),
            nameof(DynamicGameSummariesProviderLabel),
            nameof(DynamicGameSummariesSortKey),
            nameof(DynamicGameSummariesSortLabel),
            nameof(DynamicGameSummariesSortDirectionKey),
            nameof(DynamicGameSummariesSortDirectionLabel),
            nameof(DynamicLibraryAchievements),
            nameof(DynamicLibraryAchievementsProviderKey),
            nameof(DynamicLibraryAchievementsProviderLabel),
            nameof(DynamicLibraryAchievementsSortKey),
            nameof(DynamicLibraryAchievementsSortLabel),
            nameof(DynamicLibraryAchievementsSortDirectionKey),
            nameof(DynamicLibraryAchievementsSortDirectionLabel),
            nameof(Common),
            nameof(Uncommon),
            nameof(Rare),
            nameof(UltraRare),
            nameof(RareAndUltraRare)
        };

        private readonly PlayniteAchievementsSettings _settings;
        private readonly ModernThemeBindings _modernThemeOverride;
        private readonly LegacyThemeBindings _legacyThemeOverride;

        public ThemePreviewContext(
            PlayniteAchievementsSettings settings,
            ModernThemeBindings modernThemeOverride,
            LegacyThemeBindings legacyThemeOverride)
        {
            _settings = settings;
            _modernThemeOverride = modernThemeOverride;
            _legacyThemeOverride = legacyThemeOverride;

            if (_settings != null)
            {
                PropertyChangedEventManager.AddHandler(_settings, Settings_PropertyChanged, string.Empty);
            }

            if (_modernThemeOverride != null)
            {
                PropertyChangedEventManager.AddHandler(_modernThemeOverride, ModernThemeOverride_PropertyChanged, string.Empty);
            }
        }

        /// <summary>
        /// Returns the override modern theme bindings instead of the settings' Theme.
        /// </summary>
        public ModernThemeBindings ModernTheme => _modernThemeOverride;

        /// <summary>
        /// Returns the override legacy theme bindings instead of the settings' LegacyTheme.
        /// </summary>
        public LegacyThemeBindings LegacyTheme => _legacyThemeOverride;

        /// <summary>
        /// Returns the effective settings object backing this preview context.
        /// </summary>
        public PlayniteAchievementsSettings Settings => _settings;

        public bool HasAchievements => _modernThemeOverride?.HasAchievements ?? _settings?.HasAchievements ?? false;

        public int AchievementCount => _modernThemeOverride?.AchievementCount ?? _settings?.AchievementCount ?? 0;

        public int UnlockedCount => _modernThemeOverride?.UnlockedCount ?? _settings?.UnlockedCount ?? 0;

        public int LockedCount => _modernThemeOverride?.LockedCount ?? _settings?.LockedCount ?? 0;

        public double ProgressPercentage => _modernThemeOverride?.ProgressPercentage ?? _settings?.ProgressPercentage ?? 0;

        public bool IsCompleted => _modernThemeOverride?.IsCompleted ?? _settings?.IsCompleted ?? false;

        public List<AchievementDetail> Achievements => _modernThemeOverride?.AllAchievements ?? _settings?.Achievements ?? EmptyAchievementList;

        public List<AchievementDetail> AchievementsNewestFirst => _modernThemeOverride?.AchievementsNewestFirst ?? _settings?.AchievementsNewestFirst ?? EmptyAchievementList;

        public List<AchievementDetail> AchievementsOldestFirst => _modernThemeOverride?.AchievementsOldestFirst ?? _settings?.AchievementsOldestFirst ?? EmptyAchievementList;

        public List<AchievementDetail> AchievementsRarityAsc => _modernThemeOverride?.AchievementsRarityAsc ?? _settings?.AchievementsRarityAsc ?? EmptyAchievementList;

        public List<AchievementDetail> AchievementsRarityDesc => _modernThemeOverride?.AchievementsRarityDesc ?? _settings?.AchievementsRarityDesc ?? EmptyAchievementList;

        public List<AchievementDetail> DynamicAchievements => _modernThemeOverride?.DynamicAchievements ?? _settings?.DynamicAchievements ?? EmptyAchievementList;

        public string DynamicAchievementsFilterKey => _modernThemeOverride?.DynamicAchievementsFilterKey ?? _settings?.DynamicAchievementsFilterKey ?? DynamicThemeViewKeys.All;

        public string DynamicAchievementsFilterLabel => _modernThemeOverride?.DynamicAchievementsFilterLabel ?? _settings?.DynamicAchievementsFilterLabel ?? DynamicThemeViewKeys.All;

        public string DynamicAchievementsSortKey => _modernThemeOverride?.DynamicAchievementsSortKey ?? _settings?.DynamicAchievementsSortKey ?? DynamicThemeViewKeys.Default;

        public string DynamicAchievementsSortLabel => _modernThemeOverride?.DynamicAchievementsSortLabel ?? _settings?.DynamicAchievementsSortLabel ?? DynamicThemeViewKeys.Default;

        public string DynamicAchievementsSortDirectionKey => _modernThemeOverride?.DynamicAchievementsSortDirectionKey ?? _settings?.DynamicAchievementsSortDirectionKey ?? DynamicThemeViewKeys.Descending;

        public string DynamicAchievementsSortDirectionLabel => _modernThemeOverride?.DynamicAchievementsSortDirectionLabel ?? _settings?.DynamicAchievementsSortDirectionLabel ?? DynamicThemeViewKeys.Descending;

        public ObservableCollection<GameAchievementSummary> DynamicGameSummaries => _modernThemeOverride?.DynamicGameSummaries ?? _settings?.DynamicGameSummaries;

        public string DynamicGameSummariesProviderKey => _modernThemeOverride?.DynamicGameSummariesProviderKey ?? _settings?.DynamicGameSummariesProviderKey ?? DynamicThemeViewKeys.All;

        public string DynamicGameSummariesProviderLabel => _modernThemeOverride?.DynamicGameSummariesProviderLabel ?? _settings?.DynamicGameSummariesProviderLabel ?? DynamicThemeViewKeys.All;

        public string DynamicGameSummariesSortKey => _modernThemeOverride?.DynamicGameSummariesSortKey ?? _settings?.DynamicGameSummariesSortKey ?? DynamicThemeViewKeys.LastUnlock;

        public string DynamicGameSummariesSortLabel => _modernThemeOverride?.DynamicGameSummariesSortLabel ?? _settings?.DynamicGameSummariesSortLabel ?? DynamicThemeViewKeys.LastUnlock;

        public string DynamicGameSummariesSortDirectionKey => _modernThemeOverride?.DynamicGameSummariesSortDirectionKey ?? _settings?.DynamicGameSummariesSortDirectionKey ?? DynamicThemeViewKeys.Descending;

        public string DynamicGameSummariesSortDirectionLabel => _modernThemeOverride?.DynamicGameSummariesSortDirectionLabel ?? _settings?.DynamicGameSummariesSortDirectionLabel ?? DynamicThemeViewKeys.Descending;

        public List<AchievementDetail> DynamicLibraryAchievements => _modernThemeOverride?.DynamicLibraryAchievements ?? _settings?.DynamicLibraryAchievements ?? EmptyAchievementList;

        public string DynamicLibraryAchievementsProviderKey => _modernThemeOverride?.DynamicLibraryAchievementsProviderKey ?? _settings?.DynamicLibraryAchievementsProviderKey ?? DynamicThemeViewKeys.All;

        public string DynamicLibraryAchievementsProviderLabel => _modernThemeOverride?.DynamicLibraryAchievementsProviderLabel ?? _settings?.DynamicLibraryAchievementsProviderLabel ?? DynamicThemeViewKeys.All;

        public string DynamicLibraryAchievementsSortKey => _modernThemeOverride?.DynamicLibraryAchievementsSortKey ?? _settings?.DynamicLibraryAchievementsSortKey ?? DynamicThemeViewKeys.UnlockTime;

        public string DynamicLibraryAchievementsSortLabel => _modernThemeOverride?.DynamicLibraryAchievementsSortLabel ?? _settings?.DynamicLibraryAchievementsSortLabel ?? DynamicThemeViewKeys.UnlockTime;

        public string DynamicLibraryAchievementsSortDirectionKey => _modernThemeOverride?.DynamicLibraryAchievementsSortDirectionKey ?? _settings?.DynamicLibraryAchievementsSortDirectionKey ?? DynamicThemeViewKeys.Descending;

        public string DynamicLibraryAchievementsSortDirectionLabel => _modernThemeOverride?.DynamicLibraryAchievementsSortDirectionLabel ?? _settings?.DynamicLibraryAchievementsSortDirectionLabel ?? DynamicThemeViewKeys.Descending;

        public AchievementRarityStats Common => _modernThemeOverride?.Common ?? _settings?.Common ?? EmptyRarityStats;

        public AchievementRarityStats Uncommon => _modernThemeOverride?.Uncommon ?? _settings?.Uncommon ?? EmptyRarityStats;

        public AchievementRarityStats Rare => _modernThemeOverride?.Rare ?? _settings?.Rare ?? EmptyRarityStats;

        public AchievementRarityStats UltraRare => _modernThemeOverride?.UltraRare ?? _settings?.UltraRare ?? EmptyRarityStats;

        public AchievementRarityStats RareAndUltraRare => _modernThemeOverride?.RareAndUltraRare ?? _settings?.RareAndUltraRare ?? EmptyRarityStats;

        public System.Windows.Input.ICommand SetDynamicAchievementsFilterCommand => _settings?.SetDynamicAchievementsFilterCommand;

        public System.Windows.Input.ICommand SortDynamicAchievementsCommand => _settings?.SortDynamicAchievementsCommand;

        public System.Windows.Input.ICommand SetDynamicAchievementsSortDirectionCommand => _settings?.SetDynamicAchievementsSortDirectionCommand;

        public System.Windows.Input.ICommand FilterDynamicLibraryAchievementsByProviderCommand => _settings?.FilterDynamicLibraryAchievementsByProviderCommand;

        public System.Windows.Input.ICommand SortDynamicLibraryAchievementsCommand => _settings?.SortDynamicLibraryAchievementsCommand;

        public System.Windows.Input.ICommand SetDynamicLibraryAchievementsSortDirectionCommand => _settings?.SetDynamicLibraryAchievementsSortDirectionCommand;

        public System.Windows.Input.ICommand FilterDynamicGameSummariesByProviderCommand => _settings?.FilterDynamicGameSummariesByProviderCommand;

        public System.Windows.Input.ICommand SortDynamicGameSummariesCommand => _settings?.SortDynamicGameSummariesCommand;

        public System.Windows.Input.ICommand SetDynamicGameSummariesSortDirectionCommand => _settings?.SetDynamicGameSummariesSortDirectionCommand;

        // Forward other common settings properties.
        public PersistedSettings Persisted => _settings?.Persisted;

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            var propertyName = e?.PropertyName;
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                OnPropertyChanged(nameof(Persisted));
                return;
            }

            if (propertyName == nameof(PlayniteAchievementsSettings.Persisted))
            {
                OnPropertyChanged(nameof(Persisted));
                return;
            }

            if (SettingsForwardMap.TryGetValue(propertyName, out var mappedProperty))
            {
                OnPropertyChanged(mappedProperty);
            }
        }

        private void ModernThemeOverride_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            var propertyName = e?.PropertyName;
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                NotifyForwardedModernProperties();
                return;
            }

            if (ModernThemeForwardMap.TryGetValue(propertyName, out var mappedProperty))
            {
                OnPropertyChanged(mappedProperty);
            }
        }

        private void NotifyForwardedModernProperties()
        {
            foreach (var propertyName in ForwardedModernProperties)
            {
                OnPropertyChanged(propertyName);
            }
        }
    }
}

