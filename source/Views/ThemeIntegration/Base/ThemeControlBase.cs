using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services.Logging;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Views.ThemeIntegration.Base
{
    /// <summary>
    /// Base class for theme integration controls.
    /// Provides common initialization and game context change handling for all achievement controls.
    /// </summary>
    public abstract class ThemeControlBase : PluginUserControl
    {
        private static readonly ILogger _logger = PluginLogger.GetLogger(nameof(ThemeControlBase));

        private bool _isAutoUpdateSubscribed;
        private bool _themeUpdateQueued;

        /// <summary>
        /// Gets the most recent Playnite game context associated with this control.
        /// </summary>
        protected Guid? CurrentGameContextId { get; private set; }

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

        public PlayniteAchievementsSettings ThemeBindings => EffectiveSettings;

        public ModernThemeBindings ModernBindings => EffectiveTheme;

        public LegacyThemeBindings LegacyBindings => EffectiveLegacyTheme;

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

        protected void UpdateCurrentGameContext(Game gameContext)
        {
            CurrentGameContextId = gameContext != null && gameContext.Id != Guid.Empty
                ? gameContext.Id
                : (Guid?)null;
        }

        protected Guid? GetExpectedSelectedGameId()
        {
            if (CurrentGameContextId.HasValue)
            {
                return CurrentGameContextId;
            }

            var selectedGame = EffectiveSettings?.SelectedGame;
            return selectedGame != null && selectedGame.Id != Guid.Empty
                ? selectedGame.Id
                : (Guid?)null;
        }

        /// <summary>
        /// Opens the View Achievements window for the given game (falling back to the
        /// control's current game context) focused on the given achievement.
        /// Used by compact list controls when an achievement row is clicked.
        /// </summary>
        /// <param name="gameId">The Playnite game id, or null to use the current game context.</param>
        /// <param name="apiName">The achievement ApiName used to locate the row.</param>
        /// <param name="displayName">Fallback identifier when the achievement has no ApiName.</param>
        protected void OpenViewAchievementsWindowFocused(Guid? gameId, string apiName, string displayName)
        {
            if (ThemeDataOverride != null || LegacyThemeOverride != null)
            {
                // Preview/mock data contexts have no real game to open.
                _logger.Debug("Compact click ignored: preview/mock data context.");
                return;
            }

            var targetGameId = gameId ?? GetExpectedSelectedGameId();
            if (targetGameId == null || targetGameId == Guid.Empty)
            {
                _logger.Debug($"Compact click ignored: no game id for achievement '{displayName}'.");
                return;
            }

            if (Plugin == null)
            {
                _logger.Debug("Compact click ignored: no plugin reference on theme control.");
                return;
            }

            var focusAchievementId = string.IsNullOrWhiteSpace(apiName) ? displayName : apiName;
            Plugin.OpenViewAchievementsWindow(targetGameId.Value, focusAchievementId);
        }

        protected bool IsEffectiveModernThemeCurrentForContext()
        {
            if (ThemeDataOverride != null)
            {
                return true;
            }

            var expectedGameId = GetExpectedSelectedGameId();
            if (!expectedGameId.HasValue)
            {
                return true;
            }

            var actualGameId = EffectiveTheme?.SelectedGameId;
            return actualGameId.HasValue && actualGameId.Value == expectedGameId.Value;
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
        /// Gets a value indicating whether this control needs all-games/library theme data.
        /// </summary>
        protected virtual bool RequiresLibraryThemeData => false;

        /// <summary>
        /// Gets a value indicating whether this control needs full all-games achievement lists.
        /// </summary>
        protected virtual bool RequiresHeavyLibraryThemeData => false;

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

        private static ResourceDictionary _themeNativeTokens;

        /// <summary>
        /// Gets a shared dictionary of PlayAch.* tokens resolved from the active Playnite theme
        /// without user appearance overrides. Merging it into a theme control shadows the
        /// app-level tokens for the control's subtree (including hosted shared controls), so
        /// controls embedded in themes render pure theme values while plugin windows keep the
        /// override-aware app-level tokens.
        /// </summary>
        private static ResourceDictionary GetThemeNativeTokens()
        {
            if (_themeNativeTokens == null)
            {
                var dictionary = new ResourceDictionary();
                PlayAchResourceService.Apply(dictionary, null);
                _themeNativeTokens = dictionary;
            }

            return _themeNativeTokens;
        }

        /// <summary>
        /// Merges the theme-native token dictionary after XAML initialization. This must not run
        /// in the constructor: a &lt;UserControl.Resources&gt; element replaces the Resources
        /// instance during InitializeComponent, which would discard a constructor-time merge.
        /// </summary>
        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);

            if (Application.Current == null || DesignerProperties.GetIsInDesignMode(this))
            {
                return;
            }

            FormattingCulture.Apply(this);

            try
            {
                Resources.MergedDictionaries.Add(GetThemeNativeTokens());
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to merge theme-native token dictionary.");
            }
        }

        private void ThemeControlBase_Loaded(object sender, RoutedEventArgs e)
        {
            if (RequiresLibraryThemeData || RequiresHeavyLibraryThemeData)
            {
                Plugin?.ThemeIntegrationService?.EnsureAllGamesThemeDataLoaded(
                    includeHeavyAchievementLists: RequiresHeavyLibraryThemeData);
            }

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
            UpdateCurrentGameContext(newContext);
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
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsGameKey)] = nameof(DynamicAchievementsGameKey),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsGameLabel)] = nameof(DynamicAchievementsGameLabel),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsFilterKey)] = nameof(DynamicAchievementsFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsFilterLabel)] = nameof(DynamicAchievementsFilterLabel),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsCategoryLabelFilterKey)] = nameof(DynamicAchievementsCategoryLabelFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsCategoryLabelFilterLabel)] = nameof(DynamicAchievementsCategoryLabelFilterLabel),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsStatusFilterKey)] = nameof(DynamicAchievementsStatusFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsProgressFilterKey)] = nameof(DynamicAchievementsProgressFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsRarityFilterKey)] = nameof(DynamicAchievementsRarityFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsTrophyFilterKey)] = nameof(DynamicAchievementsTrophyFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsCategoryTypeFilterKey)] = nameof(DynamicAchievementsCategoryTypeFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsCustomizationFilterKey)] = nameof(DynamicAchievementsCustomizationFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsSortKey)] = nameof(DynamicAchievementsSortKey),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsSortLabel)] = nameof(DynamicAchievementsSortLabel),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsSortDirectionKey)] = nameof(DynamicAchievementsSortDirectionKey),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsSortDirectionLabel)] = nameof(DynamicAchievementsSortDirectionLabel),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsDefaultFilterKey)] = nameof(DynamicAchievementsDefaultFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsDefaultSortKey)] = nameof(DynamicAchievementsDefaultSortKey),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsDefaultSortDirectionKey)] = nameof(DynamicAchievementsDefaultSortDirectionKey),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsFilterOptions)] = nameof(DynamicAchievementsFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsSortOptions)] = nameof(DynamicAchievementsSortOptions),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementsSortDirectionOptions)] = nameof(DynamicAchievementsSortDirectionOptions),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementGameOptions)] = nameof(DynamicAchievementGameOptions),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementStatusFilterOptions)] = nameof(DynamicAchievementStatusFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementProgressFilterOptions)] = nameof(DynamicAchievementProgressFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementRarityFilterOptions)] = nameof(DynamicAchievementRarityFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementTrophyFilterOptions)] = nameof(DynamicAchievementTrophyFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementCategoryTypeFilterOptions)] = nameof(DynamicAchievementCategoryTypeFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementCategoryLabelFilterOptions)] = nameof(DynamicAchievementCategoryLabelFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicAchievementCustomizationFilterOptions)] = nameof(DynamicAchievementCustomizationFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummaries)] = nameof(DynamicGameSummaries),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesProviderKey)] = nameof(DynamicGameSummariesProviderKey),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesProviderLabel)] = nameof(DynamicGameSummariesProviderLabel),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesGameKey)] = nameof(DynamicGameSummariesGameKey),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesGameLabel)] = nameof(DynamicGameSummariesGameLabel),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesFilterKey)] = nameof(DynamicGameSummariesFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesFilterLabel)] = nameof(DynamicGameSummariesFilterLabel),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesProgressFilterKey)] = nameof(DynamicGameSummariesProgressFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesActivityFilterKey)] = nameof(DynamicGameSummariesActivityFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesSortKey)] = nameof(DynamicGameSummariesSortKey),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesSortLabel)] = nameof(DynamicGameSummariesSortLabel),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesSortDirectionKey)] = nameof(DynamicGameSummariesSortDirectionKey),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesSortDirectionLabel)] = nameof(DynamicGameSummariesSortDirectionLabel),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesDefaultProviderKey)] = nameof(DynamicGameSummariesDefaultProviderKey),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesDefaultFilterKey)] = nameof(DynamicGameSummariesDefaultFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesDefaultSortKey)] = nameof(DynamicGameSummariesDefaultSortKey),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesDefaultSortDirectionKey)] = nameof(DynamicGameSummariesDefaultSortDirectionKey),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesProviderOptions)] = nameof(DynamicGameSummariesProviderOptions),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesFilterOptions)] = nameof(DynamicGameSummariesFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesSortOptions)] = nameof(DynamicGameSummariesSortOptions),
                [nameof(PlayniteAchievementsSettings.DynamicGameSummariesSortDirectionOptions)] = nameof(DynamicGameSummariesSortDirectionOptions),
                [nameof(PlayniteAchievementsSettings.DynamicCategorySummaries)] = nameof(DynamicCategorySummaries),
                [nameof(PlayniteAchievementsSettings.DynamicCategorySummariesFilterKey)] = nameof(DynamicCategorySummariesFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicCategorySummariesFilterLabel)] = nameof(DynamicCategorySummariesFilterLabel),
                [nameof(PlayniteAchievementsSettings.DynamicCategorySummariesSortKey)] = nameof(DynamicCategorySummariesSortKey),
                [nameof(PlayniteAchievementsSettings.DynamicCategorySummariesSortLabel)] = nameof(DynamicCategorySummariesSortLabel),
                [nameof(PlayniteAchievementsSettings.DynamicCategorySummariesSortDirectionKey)] = nameof(DynamicCategorySummariesSortDirectionKey),
                [nameof(PlayniteAchievementsSettings.DynamicCategorySummariesSortDirectionLabel)] = nameof(DynamicCategorySummariesSortDirectionLabel),
                [nameof(PlayniteAchievementsSettings.DynamicCategorySummariesDefaultFilterKey)] = nameof(DynamicCategorySummariesDefaultFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicCategorySummariesDefaultSortKey)] = nameof(DynamicCategorySummariesDefaultSortKey),
                [nameof(PlayniteAchievementsSettings.DynamicCategorySummariesDefaultSortDirectionKey)] = nameof(DynamicCategorySummariesDefaultSortDirectionKey),
                [nameof(PlayniteAchievementsSettings.DynamicCategorySummariesFilterOptions)] = nameof(DynamicCategorySummariesFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicCategorySummariesSortOptions)] = nameof(DynamicCategorySummariesSortOptions),
                [nameof(PlayniteAchievementsSettings.DynamicCategorySummariesSortDirectionOptions)] = nameof(DynamicCategorySummariesSortDirectionOptions),
                [nameof(PlayniteAchievementsSettings.DynamicGameProgressFilterOptions)] = nameof(DynamicGameProgressFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicGameActivityFilterOptions)] = nameof(DynamicGameActivityFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicFriendSummaries)] = nameof(DynamicFriendSummaries),
                [nameof(PlayniteAchievementsSettings.DynamicFriendGameSummaries)] = nameof(DynamicFriendGameSummaries),
                [nameof(PlayniteAchievementsSettings.DynamicFriendAchievements)] = nameof(DynamicFriendAchievements),
                [nameof(PlayniteAchievementsSettings.DynamicFriendScopeProviderKey)] = nameof(DynamicFriendScopeProviderKey),
                [nameof(PlayniteAchievementsSettings.DynamicFriendScopeProviderLabel)] = nameof(DynamicFriendScopeProviderLabel),
                [nameof(PlayniteAchievementsSettings.DynamicFriendScopeUserKey)] = nameof(DynamicFriendScopeUserKey),
                [nameof(PlayniteAchievementsSettings.DynamicFriendScopeUserLabel)] = nameof(DynamicFriendScopeUserLabel),
                [nameof(PlayniteAchievementsSettings.DynamicFriendScopeGameKey)] = nameof(DynamicFriendScopeGameKey),
                [nameof(PlayniteAchievementsSettings.DynamicFriendScopeGameLabel)] = nameof(DynamicFriendScopeGameLabel),
                [nameof(PlayniteAchievementsSettings.DynamicFriendSummariesFilterKey)] = nameof(DynamicFriendSummariesFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicFriendSummariesFilterLabel)] = nameof(DynamicFriendSummariesFilterLabel),
                [nameof(PlayniteAchievementsSettings.DynamicFriendSummariesLastUnlockFilterKey)] = nameof(DynamicFriendSummariesLastUnlockFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicFriendSummariesSortKey)] = nameof(DynamicFriendSummariesSortKey),
                [nameof(PlayniteAchievementsSettings.DynamicFriendSummariesSortLabel)] = nameof(DynamicFriendSummariesSortLabel),
                [nameof(PlayniteAchievementsSettings.DynamicFriendSummariesSortDirectionKey)] = nameof(DynamicFriendSummariesSortDirectionKey),
                [nameof(PlayniteAchievementsSettings.DynamicFriendSummariesSortDirectionLabel)] = nameof(DynamicFriendSummariesSortDirectionLabel),
                [nameof(PlayniteAchievementsSettings.DynamicFriendGameSummariesFilterKey)] = nameof(DynamicFriendGameSummariesFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicFriendGameSummariesFilterLabel)] = nameof(DynamicFriendGameSummariesFilterLabel),
                [nameof(PlayniteAchievementsSettings.DynamicFriendGameSummariesProgressFilterKey)] = nameof(DynamicFriendGameSummariesProgressFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicFriendGameSummariesActivityFilterKey)] = nameof(DynamicFriendGameSummariesActivityFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicFriendGameSummariesSortKey)] = nameof(DynamicFriendGameSummariesSortKey),
                [nameof(PlayniteAchievementsSettings.DynamicFriendGameSummariesSortLabel)] = nameof(DynamicFriendGameSummariesSortLabel),
                [nameof(PlayniteAchievementsSettings.DynamicFriendGameSummariesSortDirectionKey)] = nameof(DynamicFriendGameSummariesSortDirectionKey),
                [nameof(PlayniteAchievementsSettings.DynamicFriendGameSummariesSortDirectionLabel)] = nameof(DynamicFriendGameSummariesSortDirectionLabel),
                [nameof(PlayniteAchievementsSettings.DynamicFriendAchievementsFilterKey)] = nameof(DynamicFriendAchievementsFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicFriendAchievementsFilterLabel)] = nameof(DynamicFriendAchievementsFilterLabel),
                [nameof(PlayniteAchievementsSettings.DynamicFriendAchievementsStatusFilterKey)] = nameof(DynamicFriendAchievementsStatusFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicFriendAchievementsProgressFilterKey)] = nameof(DynamicFriendAchievementsProgressFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicFriendAchievementsRarityFilterKey)] = nameof(DynamicFriendAchievementsRarityFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicFriendAchievementsTrophyFilterKey)] = nameof(DynamicFriendAchievementsTrophyFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicFriendAchievementsCategoryTypeFilterKey)] = nameof(DynamicFriendAchievementsCategoryTypeFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicFriendAchievementsCustomizationFilterKey)] = nameof(DynamicFriendAchievementsCustomizationFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicFriendAchievementsSortKey)] = nameof(DynamicFriendAchievementsSortKey),
                [nameof(PlayniteAchievementsSettings.DynamicFriendAchievementsSortLabel)] = nameof(DynamicFriendAchievementsSortLabel),
                [nameof(PlayniteAchievementsSettings.DynamicFriendAchievementsSortDirectionKey)] = nameof(DynamicFriendAchievementsSortDirectionKey),
                [nameof(PlayniteAchievementsSettings.DynamicFriendAchievementsSortDirectionLabel)] = nameof(DynamicFriendAchievementsSortDirectionLabel),
                [nameof(PlayniteAchievementsSettings.DynamicFriendScopeProviderOptions)] = nameof(DynamicFriendScopeProviderOptions),
                [nameof(PlayniteAchievementsSettings.DynamicFriendScopeUserOptions)] = nameof(DynamicFriendScopeUserOptions),
                [nameof(PlayniteAchievementsSettings.DynamicFriendScopeGameOptions)] = nameof(DynamicFriendScopeGameOptions),
                [nameof(PlayniteAchievementsSettings.DynamicFriendSummariesFilterOptions)] = nameof(DynamicFriendSummariesFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicFriendSummariesSortOptions)] = nameof(DynamicFriendSummariesSortOptions),
                [nameof(PlayniteAchievementsSettings.DynamicFriendSummariesSortDirectionOptions)] = nameof(DynamicFriendSummariesSortDirectionOptions),
                [nameof(PlayniteAchievementsSettings.DynamicFriendSummaryLastUnlockFilterOptions)] = nameof(DynamicFriendSummaryLastUnlockFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicFriendGameSummariesFilterOptions)] = nameof(DynamicFriendGameSummariesFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicFriendGameSummariesSortOptions)] = nameof(DynamicFriendGameSummariesSortOptions),
                [nameof(PlayniteAchievementsSettings.DynamicFriendGameSummariesSortDirectionOptions)] = nameof(DynamicFriendGameSummariesSortDirectionOptions),
                [nameof(PlayniteAchievementsSettings.DynamicFriendGameProgressFilterOptions)] = nameof(DynamicFriendGameProgressFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicFriendGameActivityFilterOptions)] = nameof(DynamicFriendGameActivityFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicFriendAchievementsFilterOptions)] = nameof(DynamicFriendAchievementsFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicFriendAchievementsSortOptions)] = nameof(DynamicFriendAchievementsSortOptions),
                [nameof(PlayniteAchievementsSettings.DynamicFriendAchievementsSortDirectionOptions)] = nameof(DynamicFriendAchievementsSortDirectionOptions),
                [nameof(PlayniteAchievementsSettings.DynamicFriendAchievementStatusFilterOptions)] = nameof(DynamicFriendAchievementStatusFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicFriendAchievementProgressFilterOptions)] = nameof(DynamicFriendAchievementProgressFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicFriendAchievementRarityFilterOptions)] = nameof(DynamicFriendAchievementRarityFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicFriendAchievementTrophyFilterOptions)] = nameof(DynamicFriendAchievementTrophyFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicFriendAchievementCategoryTypeFilterOptions)] = nameof(DynamicFriendAchievementCategoryTypeFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicFriendAchievementCustomizationFilterOptions)] = nameof(DynamicFriendAchievementCustomizationFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievements)] = nameof(DynamicLibraryAchievements),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsProviderKey)] = nameof(DynamicLibraryAchievementsProviderKey),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsProviderLabel)] = nameof(DynamicLibraryAchievementsProviderLabel),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsGameKey)] = nameof(DynamicLibraryAchievementsGameKey),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsGameLabel)] = nameof(DynamicLibraryAchievementsGameLabel),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsFilterKey)] = nameof(DynamicLibraryAchievementsFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsFilterLabel)] = nameof(DynamicLibraryAchievementsFilterLabel),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsStatusFilterKey)] = nameof(DynamicLibraryAchievementsStatusFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsProgressFilterKey)] = nameof(DynamicLibraryAchievementsProgressFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsRarityFilterKey)] = nameof(DynamicLibraryAchievementsRarityFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsTrophyFilterKey)] = nameof(DynamicLibraryAchievementsTrophyFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsCategoryTypeFilterKey)] = nameof(DynamicLibraryAchievementsCategoryTypeFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsCustomizationFilterKey)] = nameof(DynamicLibraryAchievementsCustomizationFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsSortKey)] = nameof(DynamicLibraryAchievementsSortKey),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsSortLabel)] = nameof(DynamicLibraryAchievementsSortLabel),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsSortDirectionKey)] = nameof(DynamicLibraryAchievementsSortDirectionKey),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsSortDirectionLabel)] = nameof(DynamicLibraryAchievementsSortDirectionLabel),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsDefaultProviderKey)] = nameof(DynamicLibraryAchievementsDefaultProviderKey),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsDefaultFilterKey)] = nameof(DynamicLibraryAchievementsDefaultFilterKey),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsDefaultSortKey)] = nameof(DynamicLibraryAchievementsDefaultSortKey),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsDefaultSortDirectionKey)] = nameof(DynamicLibraryAchievementsDefaultSortDirectionKey),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsProviderOptions)] = nameof(DynamicLibraryAchievementsProviderOptions),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsFilterOptions)] = nameof(DynamicLibraryAchievementsFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsSortOptions)] = nameof(DynamicLibraryAchievementsSortOptions),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsSortDirectionOptions)] = nameof(DynamicLibraryAchievementsSortDirectionOptions),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementStatusFilterOptions)] = nameof(DynamicLibraryAchievementStatusFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementProgressFilterOptions)] = nameof(DynamicLibraryAchievementProgressFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementRarityFilterOptions)] = nameof(DynamicLibraryAchievementRarityFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementTrophyFilterOptions)] = nameof(DynamicLibraryAchievementTrophyFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementCategoryTypeFilterOptions)] = nameof(DynamicLibraryAchievementCategoryTypeFilterOptions),
                [nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementCustomizationFilterOptions)] = nameof(DynamicLibraryAchievementCustomizationFilterOptions),
                [nameof(PlayniteAchievementsSettings.SetDynamicAchievementsGameCommand)] = nameof(SetDynamicAchievementsGameCommand),
                [nameof(PlayniteAchievementsSettings.FilterDynamicAchievementsByRunningGameCommand)] = nameof(FilterDynamicAchievementsByRunningGameCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicAchievementsFilterCommand)] = nameof(SetDynamicAchievementsFilterCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicAchievementsCategoryLabelFilterCommand)] = nameof(SetDynamicAchievementsCategoryLabelFilterCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicAchievementsStatusFilterCommand)] = nameof(SetDynamicAchievementsStatusFilterCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicAchievementsProgressFilterCommand)] = nameof(SetDynamicAchievementsProgressFilterCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicAchievementsRarityFilterCommand)] = nameof(SetDynamicAchievementsRarityFilterCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicAchievementsTrophyFilterCommand)] = nameof(SetDynamicAchievementsTrophyFilterCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicAchievementsCategoryTypeFilterCommand)] = nameof(SetDynamicAchievementsCategoryTypeFilterCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicAchievementsCustomizationFilterCommand)] = nameof(SetDynamicAchievementsCustomizationFilterCommand),
                [nameof(PlayniteAchievementsSettings.SortDynamicAchievementsCommand)] = nameof(SortDynamicAchievementsCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicAchievementsSortDirectionCommand)] = nameof(SetDynamicAchievementsSortDirectionCommand),
                [nameof(PlayniteAchievementsSettings.OpenViewAchievementsWindow)] = nameof(OpenViewAchievementsWindow),
                [nameof(PlayniteAchievementsSettings.OpenManageAchievementsWindow)] = nameof(OpenManageAchievementsWindow),
                [nameof(PlayniteAchievementsSettings.FilterDynamicLibraryAchievementsByProviderCommand)] = nameof(FilterDynamicLibraryAchievementsByProviderCommand),
                [nameof(PlayniteAchievementsSettings.FilterDynamicLibraryAchievementsByRunningGameCommand)] = nameof(FilterDynamicLibraryAchievementsByRunningGameCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicLibraryAchievementsFilterCommand)] = nameof(SetDynamicLibraryAchievementsFilterCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicLibraryAchievementsStatusFilterCommand)] = nameof(SetDynamicLibraryAchievementsStatusFilterCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicLibraryAchievementsProgressFilterCommand)] = nameof(SetDynamicLibraryAchievementsProgressFilterCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicLibraryAchievementsRarityFilterCommand)] = nameof(SetDynamicLibraryAchievementsRarityFilterCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicLibraryAchievementsTrophyFilterCommand)] = nameof(SetDynamicLibraryAchievementsTrophyFilterCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicLibraryAchievementsCategoryTypeFilterCommand)] = nameof(SetDynamicLibraryAchievementsCategoryTypeFilterCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicLibraryAchievementsCustomizationFilterCommand)] = nameof(SetDynamicLibraryAchievementsCustomizationFilterCommand),
                [nameof(PlayniteAchievementsSettings.SortDynamicLibraryAchievementsCommand)] = nameof(SortDynamicLibraryAchievementsCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicLibraryAchievementsSortDirectionCommand)] = nameof(SetDynamicLibraryAchievementsSortDirectionCommand),
                [nameof(PlayniteAchievementsSettings.FilterDynamicGameSummariesByProviderCommand)] = nameof(FilterDynamicGameSummariesByProviderCommand),
                [nameof(PlayniteAchievementsSettings.FilterDynamicGameSummariesByRunningGameCommand)] = nameof(FilterDynamicGameSummariesByRunningGameCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicGameSummariesFilterCommand)] = nameof(SetDynamicGameSummariesFilterCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicGameSummariesProgressFilterCommand)] = nameof(SetDynamicGameSummariesProgressFilterCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicGameSummariesActivityFilterCommand)] = nameof(SetDynamicGameSummariesActivityFilterCommand),
                [nameof(PlayniteAchievementsSettings.SortDynamicGameSummariesCommand)] = nameof(SortDynamicGameSummariesCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicGameSummariesSortDirectionCommand)] = nameof(SetDynamicGameSummariesSortDirectionCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicCategorySummariesFilterCommand)] = nameof(SetDynamicCategorySummariesFilterCommand),
                [nameof(PlayniteAchievementsSettings.SortDynamicCategorySummariesCommand)] = nameof(SortDynamicCategorySummariesCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicCategorySummariesSortDirectionCommand)] = nameof(SetDynamicCategorySummariesSortDirectionCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicFriendScopeProviderCommand)] = nameof(SetDynamicFriendScopeProviderCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicFriendScopeUserCommand)] = nameof(SetDynamicFriendScopeUserCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicFriendScopeGameCommand)] = nameof(SetDynamicFriendScopeGameCommand),
                [nameof(PlayniteAchievementsSettings.ResetDynamicFriendScopeCommand)] = nameof(ResetDynamicFriendScopeCommand),
                [nameof(PlayniteAchievementsSettings.FilterDynamicFriendSummariesByRunningGameCommand)] = nameof(FilterDynamicFriendSummariesByRunningGameCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicFriendSummariesFilterCommand)] = nameof(SetDynamicFriendSummariesFilterCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicFriendSummariesLastUnlockFilterCommand)] = nameof(SetDynamicFriendSummariesLastUnlockFilterCommand),
                [nameof(PlayniteAchievementsSettings.SortDynamicFriendSummariesCommand)] = nameof(SortDynamicFriendSummariesCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicFriendSummariesSortDirectionCommand)] = nameof(SetDynamicFriendSummariesSortDirectionCommand),
                [nameof(PlayniteAchievementsSettings.FilterDynamicFriendGameSummariesByRunningGameCommand)] = nameof(FilterDynamicFriendGameSummariesByRunningGameCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicFriendGameSummariesFilterCommand)] = nameof(SetDynamicFriendGameSummariesFilterCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicFriendGameSummariesProgressFilterCommand)] = nameof(SetDynamicFriendGameSummariesProgressFilterCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicFriendGameSummariesActivityFilterCommand)] = nameof(SetDynamicFriendGameSummariesActivityFilterCommand),
                [nameof(PlayniteAchievementsSettings.SortDynamicFriendGameSummariesCommand)] = nameof(SortDynamicFriendGameSummariesCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicFriendGameSummariesSortDirectionCommand)] = nameof(SetDynamicFriendGameSummariesSortDirectionCommand),
                [nameof(PlayniteAchievementsSettings.FilterDynamicFriendAchievementsByRunningGameCommand)] = nameof(FilterDynamicFriendAchievementsByRunningGameCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicFriendAchievementsFilterCommand)] = nameof(SetDynamicFriendAchievementsFilterCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicFriendAchievementsStatusFilterCommand)] = nameof(SetDynamicFriendAchievementsStatusFilterCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicFriendAchievementsProgressFilterCommand)] = nameof(SetDynamicFriendAchievementsProgressFilterCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicFriendAchievementsRarityFilterCommand)] = nameof(SetDynamicFriendAchievementsRarityFilterCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicFriendAchievementsTrophyFilterCommand)] = nameof(SetDynamicFriendAchievementsTrophyFilterCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicFriendAchievementsCategoryTypeFilterCommand)] = nameof(SetDynamicFriendAchievementsCategoryTypeFilterCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicFriendAchievementsCustomizationFilterCommand)] = nameof(SetDynamicFriendAchievementsCustomizationFilterCommand),
                [nameof(PlayniteAchievementsSettings.SortDynamicFriendAchievementsCommand)] = nameof(SortDynamicFriendAchievementsCommand),
                [nameof(PlayniteAchievementsSettings.SetDynamicFriendAchievementsSortDirectionCommand)] = nameof(SetDynamicFriendAchievementsSortDirectionCommand),
                [nameof(PlayniteAchievementsSettings.ResetDynamicAchievementsCommand)] = nameof(ResetDynamicAchievementsCommand),
                [nameof(PlayniteAchievementsSettings.ResetDynamicLibraryAchievementsCommand)] = nameof(ResetDynamicLibraryAchievementsCommand),
                [nameof(PlayniteAchievementsSettings.ResetDynamicGameSummariesCommand)] = nameof(ResetDynamicGameSummariesCommand),
                [nameof(PlayniteAchievementsSettings.ResetDynamicCategorySummariesCommand)] = nameof(ResetDynamicCategorySummariesCommand)
            };

        private static readonly IReadOnlyDictionary<string, string> ModernThemeForwardMap =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [nameof(ModernThemeBindings.HasAchievements)] = nameof(HasAchievements),
                [nameof(ModernThemeBindings.HasCustomAchievementOrder)] = nameof(HasCustomAchievementOrder),
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
                [nameof(ModernThemeBindings.DynamicAchievementsGameKey)] = nameof(DynamicAchievementsGameKey),
                [nameof(ModernThemeBindings.DynamicAchievementsGameLabel)] = nameof(DynamicAchievementsGameLabel),
                [nameof(ModernThemeBindings.DynamicAchievementsFilterKey)] = nameof(DynamicAchievementsFilterKey),
                [nameof(ModernThemeBindings.DynamicAchievementsFilterLabel)] = nameof(DynamicAchievementsFilterLabel),
                [nameof(ModernThemeBindings.DynamicAchievementsCategoryLabelFilterKey)] = nameof(DynamicAchievementsCategoryLabelFilterKey),
                [nameof(ModernThemeBindings.DynamicAchievementsCategoryLabelFilterLabel)] = nameof(DynamicAchievementsCategoryLabelFilterLabel),
                [nameof(ModernThemeBindings.DynamicAchievementsSortKey)] = nameof(DynamicAchievementsSortKey),
                [nameof(ModernThemeBindings.DynamicAchievementsSortLabel)] = nameof(DynamicAchievementsSortLabel),
                [nameof(ModernThemeBindings.DynamicAchievementsSortDirectionKey)] = nameof(DynamicAchievementsSortDirectionKey),
                [nameof(ModernThemeBindings.DynamicAchievementsSortDirectionLabel)] = nameof(DynamicAchievementsSortDirectionLabel),
                [nameof(ModernThemeBindings.DynamicAchievementsDefaultFilterKey)] = nameof(DynamicAchievementsDefaultFilterKey),
                [nameof(ModernThemeBindings.DynamicAchievementsDefaultSortKey)] = nameof(DynamicAchievementsDefaultSortKey),
                [nameof(ModernThemeBindings.DynamicAchievementsDefaultSortDirectionKey)] = nameof(DynamicAchievementsDefaultSortDirectionKey),
                [nameof(ModernThemeBindings.DynamicAchievementsFilterOptions)] = nameof(DynamicAchievementsFilterOptions),
                [nameof(ModernThemeBindings.DynamicAchievementsSortOptions)] = nameof(DynamicAchievementsSortOptions),
                [nameof(ModernThemeBindings.DynamicAchievementsSortDirectionOptions)] = nameof(DynamicAchievementsSortDirectionOptions),
                [nameof(ModernThemeBindings.DynamicAchievementGameOptions)] = nameof(DynamicAchievementGameOptions),
                [nameof(ModernThemeBindings.DynamicAchievementStatusFilterOptions)] = nameof(DynamicAchievementStatusFilterOptions),
                [nameof(ModernThemeBindings.DynamicAchievementProgressFilterOptions)] = nameof(DynamicAchievementProgressFilterOptions),
                [nameof(ModernThemeBindings.DynamicAchievementRarityFilterOptions)] = nameof(DynamicAchievementRarityFilterOptions),
                [nameof(ModernThemeBindings.DynamicAchievementTrophyFilterOptions)] = nameof(DynamicAchievementTrophyFilterOptions),
                [nameof(ModernThemeBindings.DynamicAchievementCategoryTypeFilterOptions)] = nameof(DynamicAchievementCategoryTypeFilterOptions),
                [nameof(ModernThemeBindings.DynamicAchievementCategoryLabelFilterOptions)] = nameof(DynamicAchievementCategoryLabelFilterOptions),
                [nameof(ModernThemeBindings.DynamicAchievementCustomizationFilterOptions)] = nameof(DynamicAchievementCustomizationFilterOptions),
                [nameof(ModernThemeBindings.DynamicGameSummaries)] = nameof(DynamicGameSummaries),
                [nameof(ModernThemeBindings.DynamicGameSummariesProviderKey)] = nameof(DynamicGameSummariesProviderKey),
                [nameof(ModernThemeBindings.DynamicGameSummariesProviderLabel)] = nameof(DynamicGameSummariesProviderLabel),
                [nameof(ModernThemeBindings.DynamicGameSummariesGameKey)] = nameof(DynamicGameSummariesGameKey),
                [nameof(ModernThemeBindings.DynamicGameSummariesGameLabel)] = nameof(DynamicGameSummariesGameLabel),
                [nameof(ModernThemeBindings.DynamicGameSummariesFilterKey)] = nameof(DynamicGameSummariesFilterKey),
                [nameof(ModernThemeBindings.DynamicGameSummariesFilterLabel)] = nameof(DynamicGameSummariesFilterLabel),
                [nameof(ModernThemeBindings.DynamicGameSummariesSortKey)] = nameof(DynamicGameSummariesSortKey),
                [nameof(ModernThemeBindings.DynamicGameSummariesSortLabel)] = nameof(DynamicGameSummariesSortLabel),
                [nameof(ModernThemeBindings.DynamicGameSummariesSortDirectionKey)] = nameof(DynamicGameSummariesSortDirectionKey),
                [nameof(ModernThemeBindings.DynamicGameSummariesSortDirectionLabel)] = nameof(DynamicGameSummariesSortDirectionLabel),
                [nameof(ModernThemeBindings.DynamicGameSummariesDefaultProviderKey)] = nameof(DynamicGameSummariesDefaultProviderKey),
                [nameof(ModernThemeBindings.DynamicGameSummariesDefaultFilterKey)] = nameof(DynamicGameSummariesDefaultFilterKey),
                [nameof(ModernThemeBindings.DynamicGameSummariesDefaultSortKey)] = nameof(DynamicGameSummariesDefaultSortKey),
                [nameof(ModernThemeBindings.DynamicGameSummariesDefaultSortDirectionKey)] = nameof(DynamicGameSummariesDefaultSortDirectionKey),
                [nameof(ModernThemeBindings.DynamicGameSummariesProviderOptions)] = nameof(DynamicGameSummariesProviderOptions),
                [nameof(ModernThemeBindings.DynamicGameSummariesFilterOptions)] = nameof(DynamicGameSummariesFilterOptions),
                [nameof(ModernThemeBindings.DynamicGameSummariesSortOptions)] = nameof(DynamicGameSummariesSortOptions),
                [nameof(ModernThemeBindings.DynamicGameSummariesSortDirectionOptions)] = nameof(DynamicGameSummariesSortDirectionOptions),
                [nameof(ModernThemeBindings.DynamicCategorySummaries)] = nameof(DynamicCategorySummaries),
                [nameof(ModernThemeBindings.HasCategorySummaries)] = nameof(HasCategorySummaries),
                [nameof(ModernThemeBindings.DynamicCategorySummariesFilterKey)] = nameof(DynamicCategorySummariesFilterKey),
                [nameof(ModernThemeBindings.DynamicCategorySummariesFilterLabel)] = nameof(DynamicCategorySummariesFilterLabel),
                [nameof(ModernThemeBindings.DynamicCategorySummariesSortKey)] = nameof(DynamicCategorySummariesSortKey),
                [nameof(ModernThemeBindings.DynamicCategorySummariesSortLabel)] = nameof(DynamicCategorySummariesSortLabel),
                [nameof(ModernThemeBindings.DynamicCategorySummariesSortDirectionKey)] = nameof(DynamicCategorySummariesSortDirectionKey),
                [nameof(ModernThemeBindings.DynamicCategorySummariesSortDirectionLabel)] = nameof(DynamicCategorySummariesSortDirectionLabel),
                [nameof(ModernThemeBindings.DynamicCategorySummariesDefaultFilterKey)] = nameof(DynamicCategorySummariesDefaultFilterKey),
                [nameof(ModernThemeBindings.DynamicCategorySummariesDefaultSortKey)] = nameof(DynamicCategorySummariesDefaultSortKey),
                [nameof(ModernThemeBindings.DynamicCategorySummariesDefaultSortDirectionKey)] = nameof(DynamicCategorySummariesDefaultSortDirectionKey),
                [nameof(ModernThemeBindings.DynamicCategorySummariesFilterOptions)] = nameof(DynamicCategorySummariesFilterOptions),
                [nameof(ModernThemeBindings.DynamicCategorySummariesSortOptions)] = nameof(DynamicCategorySummariesSortOptions),
                [nameof(ModernThemeBindings.DynamicCategorySummariesSortDirectionOptions)] = nameof(DynamicCategorySummariesSortDirectionOptions),
                [nameof(ModernThemeBindings.DynamicGameProgressFilterOptions)] = nameof(DynamicGameProgressFilterOptions),
                [nameof(ModernThemeBindings.DynamicGameActivityFilterOptions)] = nameof(DynamicGameActivityFilterOptions),
                [nameof(ModernThemeBindings.DynamicFriendSummaries)] = nameof(DynamicFriendSummaries),
                [nameof(ModernThemeBindings.DynamicFriendGameSummaries)] = nameof(DynamicFriendGameSummaries),
                [nameof(ModernThemeBindings.DynamicFriendAchievements)] = nameof(DynamicFriendAchievements),
                [nameof(ModernThemeBindings.DynamicFriendScopeProviderKey)] = nameof(DynamicFriendScopeProviderKey),
                [nameof(ModernThemeBindings.DynamicFriendScopeProviderLabel)] = nameof(DynamicFriendScopeProviderLabel),
                [nameof(ModernThemeBindings.DynamicFriendScopeUserKey)] = nameof(DynamicFriendScopeUserKey),
                [nameof(ModernThemeBindings.DynamicFriendScopeUserLabel)] = nameof(DynamicFriendScopeUserLabel),
                [nameof(ModernThemeBindings.DynamicFriendScopeGameKey)] = nameof(DynamicFriendScopeGameKey),
                [nameof(ModernThemeBindings.DynamicFriendScopeGameLabel)] = nameof(DynamicFriendScopeGameLabel),
                [nameof(ModernThemeBindings.DynamicFriendSummariesFilterKey)] = nameof(DynamicFriendSummariesFilterKey),
                [nameof(ModernThemeBindings.DynamicFriendSummariesFilterLabel)] = nameof(DynamicFriendSummariesFilterLabel),
                [nameof(ModernThemeBindings.DynamicFriendSummariesSortKey)] = nameof(DynamicFriendSummariesSortKey),
                [nameof(ModernThemeBindings.DynamicFriendSummariesSortLabel)] = nameof(DynamicFriendSummariesSortLabel),
                [nameof(ModernThemeBindings.DynamicFriendSummariesSortDirectionKey)] = nameof(DynamicFriendSummariesSortDirectionKey),
                [nameof(ModernThemeBindings.DynamicFriendSummariesSortDirectionLabel)] = nameof(DynamicFriendSummariesSortDirectionLabel),
                [nameof(ModernThemeBindings.DynamicFriendGameSummariesFilterKey)] = nameof(DynamicFriendGameSummariesFilterKey),
                [nameof(ModernThemeBindings.DynamicFriendGameSummariesFilterLabel)] = nameof(DynamicFriendGameSummariesFilterLabel),
                [nameof(ModernThemeBindings.DynamicFriendGameSummariesSortKey)] = nameof(DynamicFriendGameSummariesSortKey),
                [nameof(ModernThemeBindings.DynamicFriendGameSummariesSortLabel)] = nameof(DynamicFriendGameSummariesSortLabel),
                [nameof(ModernThemeBindings.DynamicFriendGameSummariesSortDirectionKey)] = nameof(DynamicFriendGameSummariesSortDirectionKey),
                [nameof(ModernThemeBindings.DynamicFriendGameSummariesSortDirectionLabel)] = nameof(DynamicFriendGameSummariesSortDirectionLabel),
                [nameof(ModernThemeBindings.DynamicFriendAchievementsFilterKey)] = nameof(DynamicFriendAchievementsFilterKey),
                [nameof(ModernThemeBindings.DynamicFriendAchievementsFilterLabel)] = nameof(DynamicFriendAchievementsFilterLabel),
                [nameof(ModernThemeBindings.DynamicFriendAchievementsSortKey)] = nameof(DynamicFriendAchievementsSortKey),
                [nameof(ModernThemeBindings.DynamicFriendAchievementsSortLabel)] = nameof(DynamicFriendAchievementsSortLabel),
                [nameof(ModernThemeBindings.DynamicFriendAchievementsSortDirectionKey)] = nameof(DynamicFriendAchievementsSortDirectionKey),
                [nameof(ModernThemeBindings.DynamicFriendAchievementsSortDirectionLabel)] = nameof(DynamicFriendAchievementsSortDirectionLabel),
                [nameof(ModernThemeBindings.DynamicFriendScopeProviderOptions)] = nameof(DynamicFriendScopeProviderOptions),
                [nameof(ModernThemeBindings.DynamicFriendScopeUserOptions)] = nameof(DynamicFriendScopeUserOptions),
                [nameof(ModernThemeBindings.DynamicFriendScopeGameOptions)] = nameof(DynamicFriendScopeGameOptions),
                [nameof(ModernThemeBindings.DynamicFriendSummariesFilterOptions)] = nameof(DynamicFriendSummariesFilterOptions),
                [nameof(ModernThemeBindings.DynamicFriendSummariesSortOptions)] = nameof(DynamicFriendSummariesSortOptions),
                [nameof(ModernThemeBindings.DynamicFriendSummariesSortDirectionOptions)] = nameof(DynamicFriendSummariesSortDirectionOptions),
                [nameof(ModernThemeBindings.DynamicFriendSummaryLastUnlockFilterOptions)] = nameof(DynamicFriendSummaryLastUnlockFilterOptions),
                [nameof(ModernThemeBindings.DynamicFriendGameSummariesFilterOptions)] = nameof(DynamicFriendGameSummariesFilterOptions),
                [nameof(ModernThemeBindings.DynamicFriendGameSummariesSortOptions)] = nameof(DynamicFriendGameSummariesSortOptions),
                [nameof(ModernThemeBindings.DynamicFriendGameSummariesSortDirectionOptions)] = nameof(DynamicFriendGameSummariesSortDirectionOptions),
                [nameof(ModernThemeBindings.DynamicFriendGameProgressFilterOptions)] = nameof(DynamicFriendGameProgressFilterOptions),
                [nameof(ModernThemeBindings.DynamicFriendGameActivityFilterOptions)] = nameof(DynamicFriendGameActivityFilterOptions),
                [nameof(ModernThemeBindings.DynamicFriendAchievementsFilterOptions)] = nameof(DynamicFriendAchievementsFilterOptions),
                [nameof(ModernThemeBindings.DynamicFriendAchievementsSortOptions)] = nameof(DynamicFriendAchievementsSortOptions),
                [nameof(ModernThemeBindings.DynamicFriendAchievementsSortDirectionOptions)] = nameof(DynamicFriendAchievementsSortDirectionOptions),
                [nameof(ModernThemeBindings.DynamicFriendAchievementStatusFilterOptions)] = nameof(DynamicFriendAchievementStatusFilterOptions),
                [nameof(ModernThemeBindings.DynamicFriendAchievementProgressFilterOptions)] = nameof(DynamicFriendAchievementProgressFilterOptions),
                [nameof(ModernThemeBindings.DynamicFriendAchievementRarityFilterOptions)] = nameof(DynamicFriendAchievementRarityFilterOptions),
                [nameof(ModernThemeBindings.DynamicFriendAchievementTrophyFilterOptions)] = nameof(DynamicFriendAchievementTrophyFilterOptions),
                [nameof(ModernThemeBindings.DynamicFriendAchievementCategoryTypeFilterOptions)] = nameof(DynamicFriendAchievementCategoryTypeFilterOptions),
                [nameof(ModernThemeBindings.DynamicFriendAchievementCustomizationFilterOptions)] = nameof(DynamicFriendAchievementCustomizationFilterOptions),
                [nameof(ModernThemeBindings.DynamicLibraryAchievements)] = nameof(DynamicLibraryAchievements),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementsProviderKey)] = nameof(DynamicLibraryAchievementsProviderKey),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementsProviderLabel)] = nameof(DynamicLibraryAchievementsProviderLabel),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementsGameKey)] = nameof(DynamicLibraryAchievementsGameKey),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementsGameLabel)] = nameof(DynamicLibraryAchievementsGameLabel),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementsFilterKey)] = nameof(DynamicLibraryAchievementsFilterKey),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementsFilterLabel)] = nameof(DynamicLibraryAchievementsFilterLabel),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementsSortKey)] = nameof(DynamicLibraryAchievementsSortKey),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementsSortLabel)] = nameof(DynamicLibraryAchievementsSortLabel),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementsSortDirectionKey)] = nameof(DynamicLibraryAchievementsSortDirectionKey),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementsSortDirectionLabel)] = nameof(DynamicLibraryAchievementsSortDirectionLabel),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementsDefaultProviderKey)] = nameof(DynamicLibraryAchievementsDefaultProviderKey),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementsDefaultFilterKey)] = nameof(DynamicLibraryAchievementsDefaultFilterKey),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementsDefaultSortKey)] = nameof(DynamicLibraryAchievementsDefaultSortKey),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementsDefaultSortDirectionKey)] = nameof(DynamicLibraryAchievementsDefaultSortDirectionKey),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementsProviderOptions)] = nameof(DynamicLibraryAchievementsProviderOptions),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementsFilterOptions)] = nameof(DynamicLibraryAchievementsFilterOptions),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementsSortOptions)] = nameof(DynamicLibraryAchievementsSortOptions),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementsSortDirectionOptions)] = nameof(DynamicLibraryAchievementsSortDirectionOptions),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementStatusFilterOptions)] = nameof(DynamicLibraryAchievementStatusFilterOptions),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementProgressFilterOptions)] = nameof(DynamicLibraryAchievementProgressFilterOptions),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementRarityFilterOptions)] = nameof(DynamicLibraryAchievementRarityFilterOptions),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementTrophyFilterOptions)] = nameof(DynamicLibraryAchievementTrophyFilterOptions),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementCategoryTypeFilterOptions)] = nameof(DynamicLibraryAchievementCategoryTypeFilterOptions),
                [nameof(ModernThemeBindings.DynamicLibraryAchievementCustomizationFilterOptions)] = nameof(DynamicLibraryAchievementCustomizationFilterOptions),
                [nameof(ModernThemeBindings.Common)] = nameof(Common),
                [nameof(ModernThemeBindings.Uncommon)] = nameof(Uncommon),
                [nameof(ModernThemeBindings.Rare)] = nameof(Rare),
                [nameof(ModernThemeBindings.UltraRare)] = nameof(UltraRare),
                [nameof(ModernThemeBindings.RareAndUltraRare)] = nameof(RareAndUltraRare)
            };

        private static readonly string[] ForwardedModernProperties =
        {
            nameof(HasAchievements),
            nameof(HasCustomAchievementOrder),
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
            nameof(DynamicAchievementsGameKey),
            nameof(DynamicAchievementsGameLabel),
            nameof(DynamicAchievementsFilterKey),
            nameof(DynamicAchievementsFilterLabel),
            nameof(DynamicAchievementsCategoryLabelFilterKey),
            nameof(DynamicAchievementsCategoryLabelFilterLabel),
            nameof(DynamicAchievementsStatusFilterKey),
            nameof(DynamicAchievementsProgressFilterKey),
            nameof(DynamicAchievementsRarityFilterKey),
            nameof(DynamicAchievementsTrophyFilterKey),
            nameof(DynamicAchievementsCategoryTypeFilterKey),
            nameof(DynamicAchievementsCustomizationFilterKey),
            nameof(DynamicAchievementsSortKey),
            nameof(DynamicAchievementsSortLabel),
            nameof(DynamicAchievementsSortDirectionKey),
            nameof(DynamicAchievementsSortDirectionLabel),
            nameof(DynamicAchievementsDefaultFilterKey),
            nameof(DynamicAchievementsDefaultSortKey),
            nameof(DynamicAchievementsDefaultSortDirectionKey),
            nameof(DynamicAchievementsFilterOptions),
            nameof(DynamicAchievementsSortOptions),
            nameof(DynamicAchievementsSortDirectionOptions),
            nameof(DynamicAchievementGameOptions),
            nameof(DynamicAchievementStatusFilterOptions),
            nameof(DynamicAchievementProgressFilterOptions),
            nameof(DynamicAchievementRarityFilterOptions),
            nameof(DynamicAchievementTrophyFilterOptions),
            nameof(DynamicAchievementCategoryTypeFilterOptions),
            nameof(DynamicAchievementCategoryLabelFilterOptions),
            nameof(DynamicAchievementCustomizationFilterOptions),
            nameof(DynamicLibraryAchievementStatusFilterOptions),
            nameof(DynamicLibraryAchievementProgressFilterOptions),
            nameof(DynamicLibraryAchievementRarityFilterOptions),
            nameof(DynamicLibraryAchievementTrophyFilterOptions),
            nameof(DynamicLibraryAchievementCategoryTypeFilterOptions),
            nameof(DynamicLibraryAchievementCustomizationFilterOptions),
            nameof(DynamicGameSummaries),
            nameof(DynamicGameSummariesProviderKey),
            nameof(DynamicGameSummariesProviderLabel),
            nameof(DynamicGameSummariesGameKey),
            nameof(DynamicGameSummariesGameLabel),
            nameof(DynamicGameSummariesFilterKey),
            nameof(DynamicGameSummariesFilterLabel),
            nameof(DynamicGameSummariesProgressFilterKey),
            nameof(DynamicGameSummariesActivityFilterKey),
            nameof(DynamicGameSummariesSortKey),
            nameof(DynamicGameSummariesSortLabel),
            nameof(DynamicGameSummariesSortDirectionKey),
            nameof(DynamicGameSummariesSortDirectionLabel),
            nameof(DynamicGameSummariesDefaultProviderKey),
            nameof(DynamicGameSummariesDefaultFilterKey),
            nameof(DynamicGameSummariesDefaultSortKey),
            nameof(DynamicGameSummariesDefaultSortDirectionKey),
            nameof(DynamicGameSummariesProviderOptions),
            nameof(DynamicGameSummariesFilterOptions),
            nameof(DynamicGameSummariesSortOptions),
            nameof(DynamicGameSummariesSortDirectionOptions),
            nameof(DynamicCategorySummaries),
            nameof(HasCategorySummaries),
            nameof(DynamicCategorySummariesFilterKey),
            nameof(DynamicCategorySummariesFilterLabel),
            nameof(DynamicCategorySummariesSortKey),
            nameof(DynamicCategorySummariesSortLabel),
            nameof(DynamicCategorySummariesSortDirectionKey),
            nameof(DynamicCategorySummariesSortDirectionLabel),
            nameof(DynamicCategorySummariesDefaultFilterKey),
            nameof(DynamicCategorySummariesDefaultSortKey),
            nameof(DynamicCategorySummariesDefaultSortDirectionKey),
            nameof(DynamicCategorySummariesFilterOptions),
            nameof(DynamicCategorySummariesSortOptions),
            nameof(DynamicCategorySummariesSortDirectionOptions),
            nameof(DynamicGameProgressFilterOptions),
            nameof(DynamicGameActivityFilterOptions),
            nameof(DynamicFriendSummaries),
            nameof(DynamicFriendGameSummaries),
            nameof(DynamicFriendAchievements),
            nameof(DynamicFriendScopeProviderKey),
            nameof(DynamicFriendScopeProviderLabel),
            nameof(DynamicFriendScopeUserKey),
            nameof(DynamicFriendScopeUserLabel),
            nameof(DynamicFriendScopeGameKey),
            nameof(DynamicFriendScopeGameLabel),
            nameof(DynamicFriendSummariesFilterKey),
            nameof(DynamicFriendSummariesFilterLabel),
            nameof(DynamicFriendSummariesLastUnlockFilterKey),
            nameof(DynamicFriendSummariesSortKey),
            nameof(DynamicFriendSummariesSortLabel),
            nameof(DynamicFriendSummariesSortDirectionKey),
            nameof(DynamicFriendSummariesSortDirectionLabel),
            nameof(DynamicFriendGameSummariesFilterKey),
            nameof(DynamicFriendGameSummariesFilterLabel),
            nameof(DynamicFriendGameSummariesProgressFilterKey),
            nameof(DynamicFriendGameSummariesActivityFilterKey),
            nameof(DynamicFriendGameSummariesSortKey),
            nameof(DynamicFriendGameSummariesSortLabel),
            nameof(DynamicFriendGameSummariesSortDirectionKey),
            nameof(DynamicFriendGameSummariesSortDirectionLabel),
            nameof(DynamicFriendAchievementsFilterKey),
            nameof(DynamicFriendAchievementsFilterLabel),
            nameof(DynamicFriendAchievementsStatusFilterKey),
            nameof(DynamicFriendAchievementsProgressFilterKey),
            nameof(DynamicFriendAchievementsRarityFilterKey),
            nameof(DynamicFriendAchievementsTrophyFilterKey),
            nameof(DynamicFriendAchievementsCategoryTypeFilterKey),
            nameof(DynamicFriendAchievementsCustomizationFilterKey),
            nameof(DynamicFriendAchievementsSortKey),
            nameof(DynamicFriendAchievementsSortLabel),
            nameof(DynamicFriendAchievementsSortDirectionKey),
            nameof(DynamicFriendAchievementsSortDirectionLabel),
            nameof(DynamicFriendScopeProviderOptions),
            nameof(DynamicFriendScopeUserOptions),
            nameof(DynamicFriendScopeGameOptions),
            nameof(DynamicFriendSummariesFilterOptions),
            nameof(DynamicFriendSummariesSortOptions),
            nameof(DynamicFriendSummariesSortDirectionOptions),
            nameof(DynamicFriendSummaryLastUnlockFilterOptions),
            nameof(DynamicFriendGameSummariesFilterOptions),
            nameof(DynamicFriendGameSummariesSortOptions),
            nameof(DynamicFriendGameSummariesSortDirectionOptions),
            nameof(DynamicFriendGameProgressFilterOptions),
            nameof(DynamicFriendGameActivityFilterOptions),
            nameof(DynamicFriendAchievementsFilterOptions),
            nameof(DynamicFriendAchievementsSortOptions),
            nameof(DynamicFriendAchievementsSortDirectionOptions),
            nameof(DynamicFriendAchievementStatusFilterOptions),
            nameof(DynamicFriendAchievementProgressFilterOptions),
            nameof(DynamicFriendAchievementRarityFilterOptions),
            nameof(DynamicFriendAchievementTrophyFilterOptions),
            nameof(DynamicFriendAchievementCategoryTypeFilterOptions),
            nameof(DynamicFriendAchievementCustomizationFilterOptions),
            nameof(DynamicLibraryAchievements),
            nameof(DynamicLibraryAchievementsProviderKey),
            nameof(DynamicLibraryAchievementsProviderLabel),
            nameof(DynamicLibraryAchievementsGameKey),
            nameof(DynamicLibraryAchievementsGameLabel),
            nameof(DynamicLibraryAchievementsFilterKey),
            nameof(DynamicLibraryAchievementsFilterLabel),
            nameof(DynamicLibraryAchievementsStatusFilterKey),
            nameof(DynamicLibraryAchievementsProgressFilterKey),
            nameof(DynamicLibraryAchievementsRarityFilterKey),
            nameof(DynamicLibraryAchievementsTrophyFilterKey),
            nameof(DynamicLibraryAchievementsCategoryTypeFilterKey),
            nameof(DynamicLibraryAchievementsCustomizationFilterKey),
            nameof(DynamicLibraryAchievementsSortKey),
            nameof(DynamicLibraryAchievementsSortLabel),
            nameof(DynamicLibraryAchievementsSortDirectionKey),
            nameof(DynamicLibraryAchievementsSortDirectionLabel),
            nameof(DynamicLibraryAchievementsDefaultProviderKey),
            nameof(DynamicLibraryAchievementsDefaultFilterKey),
            nameof(DynamicLibraryAchievementsDefaultSortKey),
            nameof(DynamicLibraryAchievementsDefaultSortDirectionKey),
            nameof(DynamicLibraryAchievementsProviderOptions),
            nameof(DynamicLibraryAchievementsFilterOptions),
            nameof(DynamicLibraryAchievementsSortOptions),
            nameof(DynamicLibraryAchievementsSortDirectionOptions),
            nameof(DynamicLibraryAchievementStatusFilterOptions),
            nameof(DynamicLibraryAchievementProgressFilterOptions),
            nameof(DynamicLibraryAchievementRarityFilterOptions),
            nameof(DynamicLibraryAchievementTrophyFilterOptions),
            nameof(DynamicLibraryAchievementCategoryTypeFilterOptions),
            nameof(DynamicLibraryAchievementCustomizationFilterOptions),
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

        private static string GetAchievementGroupFilter(string filterKey, string groupKey)
        {
            return GetAchievementGroupFilter(filterKey, new[] { groupKey });
        }

        private static string GetAchievementGroupFilter(string filterKey, IEnumerable<string> groupKeys)
        {
            return DynamicThemeOptionGroups.GetGroupSelection(
                filterKey,
                groupKeys,
                DynamicThemeOptionGroups.AchievementFilterGroupMap);
        }

        private static string GetGameSummaryGroupFilter(string filterKey, IEnumerable<string> groupKeys)
        {
            return DynamicThemeOptionGroups.GetGroupSelection(
                filterKey,
                groupKeys,
                DynamicThemeOptionGroups.GameSummaryFilterGroupMap);
        }

        private static string GetFriendSummaryGroupFilter(string filterKey, string groupKey)
        {
            return DynamicThemeOptionGroups.GetGroupSelection(
                filterKey,
                new[] { groupKey },
                DynamicThemeOptionGroups.FriendSummaryFilterGroupMap);
        }

        private void SetDynamicAchievementsGroupFilter(IEnumerable<string> groupKeys, object value)
        {
            DynamicAchievementsFilterKey = DynamicThemeOptionGroups.SetGroupSelection(
                DynamicAchievementsFilterKey,
                groupKeys,
                value is DynamicThemeOption option ? option.Key : value?.ToString(),
                DynamicThemeOptionGroups.AchievementFilterKeyMap,
                DynamicThemeOptionGroups.AchievementFilterGroupMap);
        }

        private void SetDynamicLibraryAchievementsGroupFilter(IEnumerable<string> groupKeys, object value)
        {
            DynamicLibraryAchievementsFilterKey = DynamicThemeOptionGroups.SetGroupSelection(
                DynamicLibraryAchievementsFilterKey,
                groupKeys,
                value is DynamicThemeOption option ? option.Key : value?.ToString(),
                DynamicThemeOptionGroups.AchievementFilterKeyMap,
                DynamicThemeOptionGroups.AchievementFilterGroupMap);
        }

        private void SetDynamicGameSummariesGroupFilter(IEnumerable<string> groupKeys, object value)
        {
            DynamicGameSummariesFilterKey = DynamicThemeOptionGroups.SetGroupSelection(
                DynamicGameSummariesFilterKey,
                groupKeys,
                value is DynamicThemeOption option ? option.Key : value?.ToString(),
                DynamicThemeOptionGroups.GameSummaryFilterKeyMap,
                DynamicThemeOptionGroups.GameSummaryFilterGroupMap);
        }

        private void SetDynamicFriendAchievementsGroupFilter(IEnumerable<string> groupKeys, object value)
        {
            DynamicFriendAchievementsFilterKey = DynamicThemeOptionGroups.SetGroupSelection(
                DynamicFriendAchievementsFilterKey,
                groupKeys,
                value is DynamicThemeOption option ? option.Key : value?.ToString(),
                DynamicThemeOptionGroups.AchievementFilterKeyMap,
                DynamicThemeOptionGroups.AchievementFilterGroupMap);
        }

        private void SetDynamicFriendGameSummariesGroupFilter(IEnumerable<string> groupKeys, object value)
        {
            DynamicFriendGameSummariesFilterKey = DynamicThemeOptionGroups.SetGroupSelection(
                DynamicFriendGameSummariesFilterKey,
                groupKeys,
                value is DynamicThemeOption option ? option.Key : value?.ToString(),
                DynamicThemeOptionGroups.GameSummaryFilterKeyMap,
                DynamicThemeOptionGroups.GameSummaryFilterGroupMap);
        }

        private void SetDynamicFriendSummariesGroupFilter(string groupKey, object value)
        {
            DynamicFriendSummariesFilterKey = DynamicThemeOptionGroups.SetGroupSelection(
                DynamicFriendSummariesFilterKey,
                groupKey,
                value is DynamicThemeOption option ? option.Key : value?.ToString(),
                DynamicThemeOptionGroups.FriendSummaryFilterKeyMap,
                DynamicThemeOptionGroups.FriendSummaryFilterGroupMap);
        }

        public bool HasAchievements => _modernThemeOverride?.HasAchievements ?? _settings?.HasAchievements ?? false;

        public bool HasCustomAchievementOrder => _modernThemeOverride?.HasCustomAchievementOrder ?? false;

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

        public string DynamicAchievementsGameKey
        {
            get => _modernThemeOverride?.DynamicAchievementsGameKey ?? _settings?.DynamicAchievementsGameKey ?? string.Empty;
            set
            {
                if (_settings != null) _settings.DynamicAchievementsGameKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicAchievementsGameKey = value;
            }
        }

        public string DynamicAchievementsGameLabel => _modernThemeOverride?.DynamicAchievementsGameLabel ?? _settings?.DynamicAchievementsGameLabel ?? string.Empty;

        public string DynamicAchievementsFilterKey
        {
            get => _modernThemeOverride?.DynamicAchievementsFilterKey ?? _settings?.DynamicAchievementsFilterKey ?? DynamicThemeViewKeys.All;
            set
            {
                if (_settings != null) _settings.DynamicAchievementsFilterKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicAchievementsFilterKey = value;
            }
        }

        public string DynamicAchievementsFilterLabel => _modernThemeOverride?.DynamicAchievementsFilterLabel ?? _settings?.DynamicAchievementsFilterLabel ?? DynamicThemeViewKeys.All;

        public string DynamicAchievementsCategoryLabelFilterKey
        {
            get => _modernThemeOverride?.DynamicAchievementsCategoryLabelFilterKey ?? _settings?.DynamicAchievementsCategoryLabelFilterKey ?? DynamicThemeViewKeys.All;
            set
            {
                if (_settings != null) _settings.DynamicAchievementsCategoryLabelFilterKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicAchievementsCategoryLabelFilterKey = value;
            }
        }

        public string DynamicAchievementsCategoryLabelFilterLabel => _modernThemeOverride?.DynamicAchievementsCategoryLabelFilterLabel ?? _settings?.DynamicAchievementsCategoryLabelFilterLabel ?? DynamicThemeViewKeys.All;

        public string DynamicAchievementsStatusFilterKey
        {
            get => GetAchievementGroupFilter(DynamicAchievementsFilterKey, DynamicThemeOptionGroups.AchievementStatusGroup);
            set => SetDynamicAchievementsGroupFilter(new[] { DynamicThemeOptionGroups.AchievementStatusGroup }, value);
        }

        public string DynamicAchievementsProgressFilterKey
        {
            get => GetAchievementGroupFilter(DynamicAchievementsFilterKey, DynamicThemeOptionGroups.AchievementProgressGroup);
            set => SetDynamicAchievementsGroupFilter(new[] { DynamicThemeOptionGroups.AchievementProgressGroup }, value);
        }

        public string DynamicAchievementsRarityFilterKey
        {
            get => GetAchievementGroupFilter(DynamicAchievementsFilterKey, DynamicThemeOptionGroups.AchievementRarityGroup);
            set => SetDynamicAchievementsGroupFilter(new[] { DynamicThemeOptionGroups.AchievementRarityGroup }, value);
        }

        public string DynamicAchievementsTrophyFilterKey
        {
            get => GetAchievementGroupFilter(DynamicAchievementsFilterKey, DynamicThemeOptionGroups.AchievementTrophyGroup);
            set => SetDynamicAchievementsGroupFilter(new[] { DynamicThemeOptionGroups.AchievementTrophyGroup }, value);
        }

        public string DynamicAchievementsCategoryTypeFilterKey
        {
            get => GetAchievementGroupFilter(DynamicAchievementsFilterKey, DynamicThemeOptionGroups.AchievementCategoryTypeGroup);
            set => SetDynamicAchievementsGroupFilter(new[] { DynamicThemeOptionGroups.AchievementCategoryTypeGroup }, value);
        }

        public string DynamicAchievementsCustomizationFilterKey
        {
            get => GetAchievementGroupFilter(DynamicAchievementsFilterKey, DynamicThemeOptionGroups.AchievementCustomizationGroups);
            set => SetDynamicAchievementsGroupFilter(DynamicThemeOptionGroups.AchievementCustomizationGroups, value);
        }

        public string DynamicAchievementsSortKey
        {
            get => _modernThemeOverride?.DynamicAchievementsSortKey ?? _settings?.DynamicAchievementsSortKey ?? DynamicThemeViewKeys.UnlockTime;
            set
            {
                if (_settings != null) _settings.DynamicAchievementsSortKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicAchievementsSortKey = value;
            }
        }

        public string DynamicAchievementsSortLabel => _modernThemeOverride?.DynamicAchievementsSortLabel ?? _settings?.DynamicAchievementsSortLabel ?? DynamicThemeViewKeys.UnlockTime;

        public string DynamicAchievementsSortDirectionKey
        {
            get => _modernThemeOverride?.DynamicAchievementsSortDirectionKey ?? _settings?.DynamicAchievementsSortDirectionKey ?? DynamicThemeViewKeys.Descending;
            set
            {
                if (_settings != null) _settings.DynamicAchievementsSortDirectionKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicAchievementsSortDirectionKey = value;
            }
        }

        public string DynamicAchievementsSortDirectionLabel => _modernThemeOverride?.DynamicAchievementsSortDirectionLabel ?? _settings?.DynamicAchievementsSortDirectionLabel ?? DynamicThemeViewKeys.Descending;

        public string DynamicAchievementsDefaultFilterKey
        {
            get => _modernThemeOverride?.DynamicAchievementsDefaultFilterKey ?? _settings?.DynamicAchievementsDefaultFilterKey ?? DynamicThemeViewKeys.All;
            set
            {
                if (_settings != null) _settings.DynamicAchievementsDefaultFilterKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicAchievementsDefaultFilterKey = value;
            }
        }

        public string DynamicAchievementsDefaultSortKey
        {
            get => _modernThemeOverride?.DynamicAchievementsDefaultSortKey ?? _settings?.DynamicAchievementsDefaultSortKey ?? DynamicThemeViewKeys.UnlockTime;
            set
            {
                if (_settings != null) _settings.DynamicAchievementsDefaultSortKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicAchievementsDefaultSortKey = value;
            }
        }

        public string DynamicAchievementsDefaultSortDirectionKey
        {
            get => _modernThemeOverride?.DynamicAchievementsDefaultSortDirectionKey ?? _settings?.DynamicAchievementsDefaultSortDirectionKey ?? DynamicThemeViewKeys.Descending;
            set
            {
                if (_settings != null) _settings.DynamicAchievementsDefaultSortDirectionKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicAchievementsDefaultSortDirectionKey = value;
            }
        }

        public ObservableCollection<DynamicThemeOption> DynamicAchievementsFilterOptions => _modernThemeOverride?.DynamicAchievementsFilterOptions ?? _settings?.DynamicAchievementsFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicAchievementsSortOptions => _modernThemeOverride?.DynamicAchievementsSortOptions ?? _settings?.DynamicAchievementsSortOptions;

        public ObservableCollection<DynamicThemeOption> DynamicAchievementsSortDirectionOptions => _modernThemeOverride?.DynamicAchievementsSortDirectionOptions ?? _settings?.DynamicAchievementsSortDirectionOptions;

        public ObservableCollection<DynamicThemeOption> DynamicAchievementGameOptions => _modernThemeOverride?.DynamicAchievementGameOptions ?? _settings?.DynamicAchievementGameOptions;

        public ObservableCollection<DynamicThemeOption> DynamicAchievementStatusFilterOptions => _modernThemeOverride?.DynamicAchievementStatusFilterOptions ?? _settings?.DynamicAchievementStatusFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicAchievementProgressFilterOptions => _modernThemeOverride?.DynamicAchievementProgressFilterOptions ?? _settings?.DynamicAchievementProgressFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicAchievementRarityFilterOptions => _modernThemeOverride?.DynamicAchievementRarityFilterOptions ?? _settings?.DynamicAchievementRarityFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicAchievementTrophyFilterOptions => _modernThemeOverride?.DynamicAchievementTrophyFilterOptions ?? _settings?.DynamicAchievementTrophyFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicAchievementCategoryTypeFilterOptions => _modernThemeOverride?.DynamicAchievementCategoryTypeFilterOptions ?? _settings?.DynamicAchievementCategoryTypeFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicAchievementCategoryLabelFilterOptions => _modernThemeOverride?.DynamicAchievementCategoryLabelFilterOptions ?? _settings?.DynamicAchievementCategoryLabelFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicAchievementCustomizationFilterOptions => _modernThemeOverride?.DynamicAchievementCustomizationFilterOptions ?? _settings?.DynamicAchievementCustomizationFilterOptions;

        public ObservableCollection<GameAchievementSummary> DynamicGameSummaries => _modernThemeOverride?.DynamicGameSummaries ?? _settings?.DynamicGameSummaries;

        public string DynamicGameSummariesProviderKey
        {
            get => _modernThemeOverride?.DynamicGameSummariesProviderKey ?? _settings?.DynamicGameSummariesProviderKey ?? DynamicThemeViewKeys.All;
            set
            {
                if (_settings != null) _settings.DynamicGameSummariesProviderKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicGameSummariesProviderKey = value;
            }
        }

        public string DynamicGameSummariesProviderLabel => _modernThemeOverride?.DynamicGameSummariesProviderLabel ?? _settings?.DynamicGameSummariesProviderLabel ?? DynamicThemeViewKeys.All;

        public string DynamicGameSummariesGameKey => _modernThemeOverride?.DynamicGameSummariesGameKey ?? _settings?.DynamicGameSummariesGameKey ?? DynamicThemeViewKeys.All;

        public string DynamicGameSummariesGameLabel => _modernThemeOverride?.DynamicGameSummariesGameLabel ?? _settings?.DynamicGameSummariesGameLabel ?? DynamicThemeViewKeys.All;

        public string DynamicGameSummariesFilterKey
        {
            get => _modernThemeOverride?.DynamicGameSummariesFilterKey ?? _settings?.DynamicGameSummariesFilterKey ?? DynamicThemeViewKeys.All;
            set
            {
                if (_settings != null) _settings.DynamicGameSummariesFilterKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicGameSummariesFilterKey = value;
            }
        }

        public string DynamicGameSummariesFilterLabel => _modernThemeOverride?.DynamicGameSummariesFilterLabel ?? _settings?.DynamicGameSummariesFilterLabel ?? DynamicThemeViewKeys.All;

        public string DynamicGameSummariesProgressFilterKey
        {
            get => GetGameSummaryGroupFilter(DynamicGameSummariesFilterKey, DynamicThemeOptionGroups.GameProgressGroups);
            set => SetDynamicGameSummariesGroupFilter(DynamicThemeOptionGroups.GameProgressGroups, value);
        }

        public string DynamicGameSummariesActivityFilterKey
        {
            get => GetGameSummaryGroupFilter(DynamicGameSummariesFilterKey, DynamicThemeOptionGroups.GameActivityGroups);
            set => SetDynamicGameSummariesGroupFilter(DynamicThemeOptionGroups.GameActivityGroups, value);
        }

        public string DynamicGameSummariesSortKey
        {
            get => _modernThemeOverride?.DynamicGameSummariesSortKey ?? _settings?.DynamicGameSummariesSortKey ?? DynamicThemeViewKeys.LastUnlock;
            set
            {
                if (_settings != null) _settings.DynamicGameSummariesSortKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicGameSummariesSortKey = value;
            }
        }

        public string DynamicGameSummariesSortLabel => _modernThemeOverride?.DynamicGameSummariesSortLabel ?? _settings?.DynamicGameSummariesSortLabel ?? DynamicThemeViewKeys.LastUnlock;

        public string DynamicGameSummariesSortDirectionKey
        {
            get => _modernThemeOverride?.DynamicGameSummariesSortDirectionKey ?? _settings?.DynamicGameSummariesSortDirectionKey ?? DynamicThemeViewKeys.Descending;
            set
            {
                if (_settings != null) _settings.DynamicGameSummariesSortDirectionKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicGameSummariesSortDirectionKey = value;
            }
        }

        public string DynamicGameSummariesSortDirectionLabel => _modernThemeOverride?.DynamicGameSummariesSortDirectionLabel ?? _settings?.DynamicGameSummariesSortDirectionLabel ?? DynamicThemeViewKeys.Descending;

        public string DynamicGameSummariesDefaultProviderKey
        {
            get => _modernThemeOverride?.DynamicGameSummariesDefaultProviderKey ?? _settings?.DynamicGameSummariesDefaultProviderKey ?? DynamicThemeViewKeys.All;
            set
            {
                if (_settings != null) _settings.DynamicGameSummariesDefaultProviderKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicGameSummariesDefaultProviderKey = value;
            }
        }

        public string DynamicGameSummariesDefaultFilterKey
        {
            get => _modernThemeOverride?.DynamicGameSummariesDefaultFilterKey ?? _settings?.DynamicGameSummariesDefaultFilterKey ?? DynamicThemeViewKeys.All;
            set
            {
                if (_settings != null) _settings.DynamicGameSummariesDefaultFilterKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicGameSummariesDefaultFilterKey = value;
            }
        }

        public string DynamicGameSummariesDefaultSortKey
        {
            get => _modernThemeOverride?.DynamicGameSummariesDefaultSortKey ?? _settings?.DynamicGameSummariesDefaultSortKey ?? DynamicThemeViewKeys.LastUnlock;
            set
            {
                if (_settings != null) _settings.DynamicGameSummariesDefaultSortKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicGameSummariesDefaultSortKey = value;
            }
        }

        public string DynamicGameSummariesDefaultSortDirectionKey
        {
            get => _modernThemeOverride?.DynamicGameSummariesDefaultSortDirectionKey ?? _settings?.DynamicGameSummariesDefaultSortDirectionKey ?? DynamicThemeViewKeys.Descending;
            set
            {
                if (_settings != null) _settings.DynamicGameSummariesDefaultSortDirectionKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicGameSummariesDefaultSortDirectionKey = value;
            }
        }

        public ObservableCollection<DynamicThemeOption> DynamicGameSummariesProviderOptions => _modernThemeOverride?.DynamicGameSummariesProviderOptions ?? _settings?.DynamicGameSummariesProviderOptions;

        public ObservableCollection<DynamicThemeOption> DynamicGameSummariesFilterOptions => _modernThemeOverride?.DynamicGameSummariesFilterOptions ?? _settings?.DynamicGameSummariesFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicGameSummariesSortOptions => _modernThemeOverride?.DynamicGameSummariesSortOptions ?? _settings?.DynamicGameSummariesSortOptions;

        public ObservableCollection<DynamicThemeOption> DynamicGameSummariesSortDirectionOptions => _modernThemeOverride?.DynamicGameSummariesSortDirectionOptions ?? _settings?.DynamicGameSummariesSortDirectionOptions;

        public ObservableCollection<GameAchievementSummary> DynamicCategorySummaries => _modernThemeOverride?.DynamicCategorySummaries ?? _settings?.DynamicCategorySummaries;

        public bool HasCategorySummaries => _modernThemeOverride?.HasCategorySummaries ?? _settings?.HasCategorySummaries ?? false;

        public string DynamicCategorySummariesFilterKey
        {
            get => _modernThemeOverride?.DynamicCategorySummariesFilterKey ?? _settings?.DynamicCategorySummariesFilterKey ?? DynamicThemeViewKeys.All;
            set
            {
                if (_settings != null) _settings.DynamicCategorySummariesFilterKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicCategorySummariesFilterKey = value;
            }
        }

        public string DynamicCategorySummariesFilterLabel => _modernThemeOverride?.DynamicCategorySummariesFilterLabel ?? _settings?.DynamicCategorySummariesFilterLabel ?? DynamicThemeViewKeys.All;

        public string DynamicCategorySummariesSortKey
        {
            get => _modernThemeOverride?.DynamicCategorySummariesSortKey ?? _settings?.DynamicCategorySummariesSortKey ?? DynamicThemeViewKeys.Default;
            set
            {
                if (_settings != null) _settings.DynamicCategorySummariesSortKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicCategorySummariesSortKey = value;
            }
        }

        public string DynamicCategorySummariesSortLabel => _modernThemeOverride?.DynamicCategorySummariesSortLabel ?? _settings?.DynamicCategorySummariesSortLabel ?? DynamicThemeViewKeys.Default;

        public string DynamicCategorySummariesSortDirectionKey
        {
            get => _modernThemeOverride?.DynamicCategorySummariesSortDirectionKey ?? _settings?.DynamicCategorySummariesSortDirectionKey ?? DynamicThemeViewKeys.Descending;
            set
            {
                if (_settings != null) _settings.DynamicCategorySummariesSortDirectionKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicCategorySummariesSortDirectionKey = value;
            }
        }

        public string DynamicCategorySummariesSortDirectionLabel => _modernThemeOverride?.DynamicCategorySummariesSortDirectionLabel ?? _settings?.DynamicCategorySummariesSortDirectionLabel ?? DynamicThemeViewKeys.Descending;

        public string DynamicCategorySummariesDefaultFilterKey
        {
            get => _modernThemeOverride?.DynamicCategorySummariesDefaultFilterKey ?? _settings?.DynamicCategorySummariesDefaultFilterKey ?? DynamicThemeViewKeys.All;
            set
            {
                if (_settings != null) _settings.DynamicCategorySummariesDefaultFilterKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicCategorySummariesDefaultFilterKey = value;
            }
        }

        public string DynamicCategorySummariesDefaultSortKey
        {
            get => _modernThemeOverride?.DynamicCategorySummariesDefaultSortKey ?? _settings?.DynamicCategorySummariesDefaultSortKey ?? DynamicThemeViewKeys.Default;
            set
            {
                if (_settings != null) _settings.DynamicCategorySummariesDefaultSortKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicCategorySummariesDefaultSortKey = value;
            }
        }

        public string DynamicCategorySummariesDefaultSortDirectionKey
        {
            get => _modernThemeOverride?.DynamicCategorySummariesDefaultSortDirectionKey ?? _settings?.DynamicCategorySummariesDefaultSortDirectionKey ?? DynamicThemeViewKeys.Descending;
            set
            {
                if (_settings != null) _settings.DynamicCategorySummariesDefaultSortDirectionKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicCategorySummariesDefaultSortDirectionKey = value;
            }
        }

        public ObservableCollection<DynamicThemeOption> DynamicCategorySummariesFilterOptions => _modernThemeOverride?.DynamicCategorySummariesFilterOptions ?? _settings?.DynamicCategorySummariesFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicCategorySummariesSortOptions => _modernThemeOverride?.DynamicCategorySummariesSortOptions ?? _settings?.DynamicCategorySummariesSortOptions;

        public ObservableCollection<DynamicThemeOption> DynamicCategorySummariesSortDirectionOptions => _modernThemeOverride?.DynamicCategorySummariesSortDirectionOptions ?? _settings?.DynamicCategorySummariesSortDirectionOptions;

        public ObservableCollection<DynamicThemeOption> DynamicGameProgressFilterOptions => _modernThemeOverride?.DynamicGameProgressFilterOptions ?? _settings?.DynamicGameProgressFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicGameActivityFilterOptions => _modernThemeOverride?.DynamicGameActivityFilterOptions ?? _settings?.DynamicGameActivityFilterOptions;

        public ObservableCollection<FriendSummaryItem> DynamicFriendSummaries => _modernThemeOverride?.DynamicFriendSummaries ?? _settings?.DynamicFriendSummaries;

        public ObservableCollection<FriendGameAchievementSummary> DynamicFriendGameSummaries => _modernThemeOverride?.DynamicFriendGameSummaries ?? _settings?.DynamicFriendGameSummaries;

        public ObservableCollection<FriendAchievementDisplayItem> DynamicFriendAchievements => _modernThemeOverride?.DynamicFriendAchievements ?? _settings?.DynamicFriendAchievements;

        public string DynamicFriendScopeProviderKey
        {
            get => _modernThemeOverride?.DynamicFriendScopeProviderKey ?? _settings?.DynamicFriendScopeProviderKey ?? DynamicThemeViewKeys.All;
            set
            {
                if (_settings != null) _settings.DynamicFriendScopeProviderKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicFriendScopeProviderKey = value;
            }
        }

        public string DynamicFriendScopeProviderLabel => _modernThemeOverride?.DynamicFriendScopeProviderLabel ?? _settings?.DynamicFriendScopeProviderLabel ?? DynamicThemeViewKeys.All;

        public string DynamicFriendScopeUserKey
        {
            get => _modernThemeOverride?.DynamicFriendScopeUserKey ?? _settings?.DynamicFriendScopeUserKey ?? DynamicThemeViewKeys.All;
            set
            {
                if (_settings != null) _settings.DynamicFriendScopeUserKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicFriendScopeUserKey = value;
            }
        }

        public string DynamicFriendScopeUserLabel => _modernThemeOverride?.DynamicFriendScopeUserLabel ?? _settings?.DynamicFriendScopeUserLabel ?? DynamicThemeViewKeys.All;

        public string DynamicFriendScopeGameKey
        {
            get => _modernThemeOverride?.DynamicFriendScopeGameKey ?? _settings?.DynamicFriendScopeGameKey ?? DynamicThemeViewKeys.All;
            set
            {
                if (_settings != null) _settings.DynamicFriendScopeGameKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicFriendScopeGameKey = value;
            }
        }

        public string DynamicFriendScopeGameLabel => _modernThemeOverride?.DynamicFriendScopeGameLabel ?? _settings?.DynamicFriendScopeGameLabel ?? DynamicThemeViewKeys.All;

        public string DynamicFriendSummariesFilterKey
        {
            get => _modernThemeOverride?.DynamicFriendSummariesFilterKey ?? _settings?.DynamicFriendSummariesFilterKey ?? DynamicThemeViewKeys.All;
            set
            {
                if (_settings != null) _settings.DynamicFriendSummariesFilterKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicFriendSummariesFilterKey = value;
            }
        }

        public string DynamicFriendSummariesFilterLabel => _modernThemeOverride?.DynamicFriendSummariesFilterLabel ?? _settings?.DynamicFriendSummariesFilterLabel ?? DynamicThemeViewKeys.All;

        public string DynamicFriendSummariesLastUnlockFilterKey
        {
            get => GetFriendSummaryGroupFilter(DynamicFriendSummariesFilterKey, DynamicThemeOptionGroups.FriendLastUnlockGroup);
            set => SetDynamicFriendSummariesGroupFilter(DynamicThemeOptionGroups.FriendLastUnlockGroup, value);
        }

        public string DynamicFriendSummariesSortKey
        {
            get => _modernThemeOverride?.DynamicFriendSummariesSortKey ?? _settings?.DynamicFriendSummariesSortKey ?? DynamicThemeViewKeys.LastUnlock;
            set
            {
                if (_settings != null) _settings.DynamicFriendSummariesSortKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicFriendSummariesSortKey = value;
            }
        }

        public string DynamicFriendSummariesSortLabel => _modernThemeOverride?.DynamicFriendSummariesSortLabel ?? _settings?.DynamicFriendSummariesSortLabel ?? DynamicThemeViewKeys.LastUnlock;

        public string DynamicFriendSummariesSortDirectionKey
        {
            get => _modernThemeOverride?.DynamicFriendSummariesSortDirectionKey ?? _settings?.DynamicFriendSummariesSortDirectionKey ?? DynamicThemeViewKeys.Descending;
            set
            {
                if (_settings != null) _settings.DynamicFriendSummariesSortDirectionKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicFriendSummariesSortDirectionKey = value;
            }
        }

        public string DynamicFriendSummariesSortDirectionLabel => _modernThemeOverride?.DynamicFriendSummariesSortDirectionLabel ?? _settings?.DynamicFriendSummariesSortDirectionLabel ?? DynamicThemeViewKeys.Descending;

        public string DynamicFriendGameSummariesFilterKey
        {
            get => _modernThemeOverride?.DynamicFriendGameSummariesFilterKey ?? _settings?.DynamicFriendGameSummariesFilterKey ?? DynamicThemeViewKeys.All;
            set
            {
                if (_settings != null) _settings.DynamicFriendGameSummariesFilterKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicFriendGameSummariesFilterKey = value;
            }
        }

        public string DynamicFriendGameSummariesFilterLabel => _modernThemeOverride?.DynamicFriendGameSummariesFilterLabel ?? _settings?.DynamicFriendGameSummariesFilterLabel ?? DynamicThemeViewKeys.All;

        public string DynamicFriendGameSummariesProgressFilterKey
        {
            get => GetGameSummaryGroupFilter(DynamicFriendGameSummariesFilterKey, DynamicThemeOptionGroups.GameProgressGroups);
            set => SetDynamicFriendGameSummariesGroupFilter(DynamicThemeOptionGroups.GameProgressGroups, value);
        }

        public string DynamicFriendGameSummariesActivityFilterKey
        {
            get => GetGameSummaryGroupFilter(DynamicFriendGameSummariesFilterKey, DynamicThemeOptionGroups.GameActivityGroups);
            set => SetDynamicFriendGameSummariesGroupFilter(DynamicThemeOptionGroups.GameActivityGroups, value);
        }

        public string DynamicFriendGameSummariesSortKey
        {
            get => _modernThemeOverride?.DynamicFriendGameSummariesSortKey ?? _settings?.DynamicFriendGameSummariesSortKey ?? DynamicThemeViewKeys.LastUnlock;
            set
            {
                if (_settings != null) _settings.DynamicFriendGameSummariesSortKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicFriendGameSummariesSortKey = value;
            }
        }

        public string DynamicFriendGameSummariesSortLabel => _modernThemeOverride?.DynamicFriendGameSummariesSortLabel ?? _settings?.DynamicFriendGameSummariesSortLabel ?? DynamicThemeViewKeys.LastUnlock;

        public string DynamicFriendGameSummariesSortDirectionKey
        {
            get => _modernThemeOverride?.DynamicFriendGameSummariesSortDirectionKey ?? _settings?.DynamicFriendGameSummariesSortDirectionKey ?? DynamicThemeViewKeys.Descending;
            set
            {
                if (_settings != null) _settings.DynamicFriendGameSummariesSortDirectionKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicFriendGameSummariesSortDirectionKey = value;
            }
        }

        public string DynamicFriendGameSummariesSortDirectionLabel => _modernThemeOverride?.DynamicFriendGameSummariesSortDirectionLabel ?? _settings?.DynamicFriendGameSummariesSortDirectionLabel ?? DynamicThemeViewKeys.Descending;

        public string DynamicFriendAchievementsFilterKey
        {
            get => _modernThemeOverride?.DynamicFriendAchievementsFilterKey ?? _settings?.DynamicFriendAchievementsFilterKey ?? DynamicThemeViewKeys.All;
            set
            {
                if (_settings != null) _settings.DynamicFriendAchievementsFilterKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicFriendAchievementsFilterKey = value;
            }
        }

        public string DynamicFriendAchievementsFilterLabel => _modernThemeOverride?.DynamicFriendAchievementsFilterLabel ?? _settings?.DynamicFriendAchievementsFilterLabel ?? DynamicThemeViewKeys.All;

        public string DynamicFriendAchievementsStatusFilterKey
        {
            get => GetAchievementGroupFilter(DynamicFriendAchievementsFilterKey, DynamicThemeOptionGroups.AchievementStatusGroup);
            set => SetDynamicFriendAchievementsGroupFilter(new[] { DynamicThemeOptionGroups.AchievementStatusGroup }, value);
        }

        public string DynamicFriendAchievementsProgressFilterKey
        {
            get => GetAchievementGroupFilter(DynamicFriendAchievementsFilterKey, DynamicThemeOptionGroups.AchievementProgressGroup);
            set => SetDynamicFriendAchievementsGroupFilter(new[] { DynamicThemeOptionGroups.AchievementProgressGroup }, value);
        }

        public string DynamicFriendAchievementsRarityFilterKey
        {
            get => GetAchievementGroupFilter(DynamicFriendAchievementsFilterKey, DynamicThemeOptionGroups.AchievementRarityGroup);
            set => SetDynamicFriendAchievementsGroupFilter(new[] { DynamicThemeOptionGroups.AchievementRarityGroup }, value);
        }

        public string DynamicFriendAchievementsTrophyFilterKey
        {
            get => GetAchievementGroupFilter(DynamicFriendAchievementsFilterKey, DynamicThemeOptionGroups.AchievementTrophyGroup);
            set => SetDynamicFriendAchievementsGroupFilter(new[] { DynamicThemeOptionGroups.AchievementTrophyGroup }, value);
        }

        public string DynamicFriendAchievementsCategoryTypeFilterKey
        {
            get => GetAchievementGroupFilter(DynamicFriendAchievementsFilterKey, DynamicThemeOptionGroups.AchievementCategoryTypeGroup);
            set => SetDynamicFriendAchievementsGroupFilter(new[] { DynamicThemeOptionGroups.AchievementCategoryTypeGroup }, value);
        }

        public string DynamicFriendAchievementsCustomizationFilterKey
        {
            get => GetAchievementGroupFilter(DynamicFriendAchievementsFilterKey, DynamicThemeOptionGroups.AchievementCustomizationGroups);
            set => SetDynamicFriendAchievementsGroupFilter(DynamicThemeOptionGroups.AchievementCustomizationGroups, value);
        }

        public string DynamicFriendAchievementsSortKey
        {
            get => _modernThemeOverride?.DynamicFriendAchievementsSortKey ?? _settings?.DynamicFriendAchievementsSortKey ?? DynamicThemeViewKeys.UnlockTime;
            set
            {
                if (_settings != null) _settings.DynamicFriendAchievementsSortKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicFriendAchievementsSortKey = value;
            }
        }

        public string DynamicFriendAchievementsSortLabel => _modernThemeOverride?.DynamicFriendAchievementsSortLabel ?? _settings?.DynamicFriendAchievementsSortLabel ?? DynamicThemeViewKeys.UnlockTime;

        public string DynamicFriendAchievementsSortDirectionKey
        {
            get => _modernThemeOverride?.DynamicFriendAchievementsSortDirectionKey ?? _settings?.DynamicFriendAchievementsSortDirectionKey ?? DynamicThemeViewKeys.Descending;
            set
            {
                if (_settings != null) _settings.DynamicFriendAchievementsSortDirectionKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicFriendAchievementsSortDirectionKey = value;
            }
        }

        public string DynamicFriendAchievementsSortDirectionLabel => _modernThemeOverride?.DynamicFriendAchievementsSortDirectionLabel ?? _settings?.DynamicFriendAchievementsSortDirectionLabel ?? DynamicThemeViewKeys.Descending;

        public ObservableCollection<DynamicThemeOption> DynamicFriendScopeProviderOptions => _modernThemeOverride?.DynamicFriendScopeProviderOptions ?? _settings?.DynamicFriendScopeProviderOptions;

        public ObservableCollection<DynamicThemeOption> DynamicFriendScopeUserOptions => _modernThemeOverride?.DynamicFriendScopeUserOptions ?? _settings?.DynamicFriendScopeUserOptions;

        public ObservableCollection<DynamicThemeOption> DynamicFriendScopeGameOptions => _modernThemeOverride?.DynamicFriendScopeGameOptions ?? _settings?.DynamicFriendScopeGameOptions;

        public ObservableCollection<DynamicThemeOption> DynamicFriendSummariesFilterOptions => _modernThemeOverride?.DynamicFriendSummariesFilterOptions ?? _settings?.DynamicFriendSummariesFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicFriendSummariesSortOptions => _modernThemeOverride?.DynamicFriendSummariesSortOptions ?? _settings?.DynamicFriendSummariesSortOptions;

        public ObservableCollection<DynamicThemeOption> DynamicFriendSummariesSortDirectionOptions => _modernThemeOverride?.DynamicFriendSummariesSortDirectionOptions ?? _settings?.DynamicFriendSummariesSortDirectionOptions;

        public ObservableCollection<DynamicThemeOption> DynamicFriendSummaryLastUnlockFilterOptions => _modernThemeOverride?.DynamicFriendSummaryLastUnlockFilterOptions ?? _settings?.DynamicFriendSummaryLastUnlockFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicFriendGameSummariesFilterOptions => _modernThemeOverride?.DynamicFriendGameSummariesFilterOptions ?? _settings?.DynamicFriendGameSummariesFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicFriendGameSummariesSortOptions => _modernThemeOverride?.DynamicFriendGameSummariesSortOptions ?? _settings?.DynamicFriendGameSummariesSortOptions;

        public ObservableCollection<DynamicThemeOption> DynamicFriendGameSummariesSortDirectionOptions => _modernThemeOverride?.DynamicFriendGameSummariesSortDirectionOptions ?? _settings?.DynamicFriendGameSummariesSortDirectionOptions;

        public ObservableCollection<DynamicThemeOption> DynamicFriendGameProgressFilterOptions => _modernThemeOverride?.DynamicFriendGameProgressFilterOptions ?? _settings?.DynamicFriendGameProgressFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicFriendGameActivityFilterOptions => _modernThemeOverride?.DynamicFriendGameActivityFilterOptions ?? _settings?.DynamicFriendGameActivityFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicFriendAchievementsFilterOptions => _modernThemeOverride?.DynamicFriendAchievementsFilterOptions ?? _settings?.DynamicFriendAchievementsFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicFriendAchievementsSortOptions => _modernThemeOverride?.DynamicFriendAchievementsSortOptions ?? _settings?.DynamicFriendAchievementsSortOptions;

        public ObservableCollection<DynamicThemeOption> DynamicFriendAchievementsSortDirectionOptions => _modernThemeOverride?.DynamicFriendAchievementsSortDirectionOptions ?? _settings?.DynamicFriendAchievementsSortDirectionOptions;

        public ObservableCollection<DynamicThemeOption> DynamicFriendAchievementStatusFilterOptions => _modernThemeOverride?.DynamicFriendAchievementStatusFilterOptions ?? _settings?.DynamicFriendAchievementStatusFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicFriendAchievementProgressFilterOptions => _modernThemeOverride?.DynamicFriendAchievementProgressFilterOptions ?? _settings?.DynamicFriendAchievementProgressFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicFriendAchievementRarityFilterOptions => _modernThemeOverride?.DynamicFriendAchievementRarityFilterOptions ?? _settings?.DynamicFriendAchievementRarityFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicFriendAchievementTrophyFilterOptions => _modernThemeOverride?.DynamicFriendAchievementTrophyFilterOptions ?? _settings?.DynamicFriendAchievementTrophyFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicFriendAchievementCategoryTypeFilterOptions => _modernThemeOverride?.DynamicFriendAchievementCategoryTypeFilterOptions ?? _settings?.DynamicFriendAchievementCategoryTypeFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicFriendAchievementCustomizationFilterOptions => _modernThemeOverride?.DynamicFriendAchievementCustomizationFilterOptions ?? _settings?.DynamicFriendAchievementCustomizationFilterOptions;

        public List<AchievementDetail> DynamicLibraryAchievements => _modernThemeOverride?.DynamicLibraryAchievements ?? _settings?.DynamicLibraryAchievements ?? EmptyAchievementList;

        public string DynamicLibraryAchievementsProviderKey
        {
            get => _modernThemeOverride?.DynamicLibraryAchievementsProviderKey ?? _settings?.DynamicLibraryAchievementsProviderKey ?? DynamicThemeViewKeys.All;
            set
            {
                if (_settings != null) _settings.DynamicLibraryAchievementsProviderKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicLibraryAchievementsProviderKey = value;
            }
        }

        public string DynamicLibraryAchievementsProviderLabel => _modernThemeOverride?.DynamicLibraryAchievementsProviderLabel ?? _settings?.DynamicLibraryAchievementsProviderLabel ?? DynamicThemeViewKeys.All;

        public string DynamicLibraryAchievementsGameKey => _modernThemeOverride?.DynamicLibraryAchievementsGameKey ?? _settings?.DynamicLibraryAchievementsGameKey ?? DynamicThemeViewKeys.All;

        public string DynamicLibraryAchievementsGameLabel => _modernThemeOverride?.DynamicLibraryAchievementsGameLabel ?? _settings?.DynamicLibraryAchievementsGameLabel ?? DynamicThemeViewKeys.All;

        public string DynamicLibraryAchievementsFilterKey
        {
            get => _modernThemeOverride?.DynamicLibraryAchievementsFilterKey ?? _settings?.DynamicLibraryAchievementsFilterKey ?? DynamicThemeViewKeys.All;
            set
            {
                if (_settings != null) _settings.DynamicLibraryAchievementsFilterKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicLibraryAchievementsFilterKey = value;
            }
        }

        public string DynamicLibraryAchievementsFilterLabel => _modernThemeOverride?.DynamicLibraryAchievementsFilterLabel ?? _settings?.DynamicLibraryAchievementsFilterLabel ?? DynamicThemeViewKeys.All;

        public string DynamicLibraryAchievementsStatusFilterKey
        {
            get => GetAchievementGroupFilter(DynamicLibraryAchievementsFilterKey, DynamicThemeOptionGroups.AchievementStatusGroup);
            set => SetDynamicLibraryAchievementsGroupFilter(new[] { DynamicThemeOptionGroups.AchievementStatusGroup }, value);
        }

        public string DynamicLibraryAchievementsProgressFilterKey
        {
            get => GetAchievementGroupFilter(DynamicLibraryAchievementsFilterKey, DynamicThemeOptionGroups.AchievementProgressGroup);
            set => SetDynamicLibraryAchievementsGroupFilter(new[] { DynamicThemeOptionGroups.AchievementProgressGroup }, value);
        }

        public string DynamicLibraryAchievementsRarityFilterKey
        {
            get => GetAchievementGroupFilter(DynamicLibraryAchievementsFilterKey, DynamicThemeOptionGroups.AchievementRarityGroup);
            set => SetDynamicLibraryAchievementsGroupFilter(new[] { DynamicThemeOptionGroups.AchievementRarityGroup }, value);
        }

        public string DynamicLibraryAchievementsTrophyFilterKey
        {
            get => GetAchievementGroupFilter(DynamicLibraryAchievementsFilterKey, DynamicThemeOptionGroups.AchievementTrophyGroup);
            set => SetDynamicLibraryAchievementsGroupFilter(new[] { DynamicThemeOptionGroups.AchievementTrophyGroup }, value);
        }

        public string DynamicLibraryAchievementsCategoryTypeFilterKey
        {
            get => GetAchievementGroupFilter(DynamicLibraryAchievementsFilterKey, DynamicThemeOptionGroups.AchievementCategoryTypeGroup);
            set => SetDynamicLibraryAchievementsGroupFilter(new[] { DynamicThemeOptionGroups.AchievementCategoryTypeGroup }, value);
        }

        public string DynamicLibraryAchievementsCustomizationFilterKey
        {
            get => GetAchievementGroupFilter(DynamicLibraryAchievementsFilterKey, DynamicThemeOptionGroups.AchievementCustomizationGroups);
            set => SetDynamicLibraryAchievementsGroupFilter(DynamicThemeOptionGroups.AchievementCustomizationGroups, value);
        }

        public string DynamicLibraryAchievementsSortKey
        {
            get => _modernThemeOverride?.DynamicLibraryAchievementsSortKey ?? _settings?.DynamicLibraryAchievementsSortKey ?? DynamicThemeViewKeys.UnlockTime;
            set
            {
                if (_settings != null) _settings.DynamicLibraryAchievementsSortKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicLibraryAchievementsSortKey = value;
            }
        }

        public string DynamicLibraryAchievementsSortLabel => _modernThemeOverride?.DynamicLibraryAchievementsSortLabel ?? _settings?.DynamicLibraryAchievementsSortLabel ?? DynamicThemeViewKeys.UnlockTime;

        public string DynamicLibraryAchievementsSortDirectionKey
        {
            get => _modernThemeOverride?.DynamicLibraryAchievementsSortDirectionKey ?? _settings?.DynamicLibraryAchievementsSortDirectionKey ?? DynamicThemeViewKeys.Descending;
            set
            {
                if (_settings != null) _settings.DynamicLibraryAchievementsSortDirectionKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicLibraryAchievementsSortDirectionKey = value;
            }
        }

        public string DynamicLibraryAchievementsSortDirectionLabel => _modernThemeOverride?.DynamicLibraryAchievementsSortDirectionLabel ?? _settings?.DynamicLibraryAchievementsSortDirectionLabel ?? DynamicThemeViewKeys.Descending;

        public string DynamicLibraryAchievementsDefaultProviderKey
        {
            get => _modernThemeOverride?.DynamicLibraryAchievementsDefaultProviderKey ?? _settings?.DynamicLibraryAchievementsDefaultProviderKey ?? DynamicThemeViewKeys.All;
            set
            {
                if (_settings != null) _settings.DynamicLibraryAchievementsDefaultProviderKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicLibraryAchievementsDefaultProviderKey = value;
            }
        }

        public string DynamicLibraryAchievementsDefaultFilterKey
        {
            get => _modernThemeOverride?.DynamicLibraryAchievementsDefaultFilterKey ?? _settings?.DynamicLibraryAchievementsDefaultFilterKey ?? DynamicThemeViewKeys.All;
            set
            {
                if (_settings != null) _settings.DynamicLibraryAchievementsDefaultFilterKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicLibraryAchievementsDefaultFilterKey = value;
            }
        }

        public string DynamicLibraryAchievementsDefaultSortKey
        {
            get => _modernThemeOverride?.DynamicLibraryAchievementsDefaultSortKey ?? _settings?.DynamicLibraryAchievementsDefaultSortKey ?? DynamicThemeViewKeys.UnlockTime;
            set
            {
                if (_settings != null) _settings.DynamicLibraryAchievementsDefaultSortKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicLibraryAchievementsDefaultSortKey = value;
            }
        }

        public string DynamicLibraryAchievementsDefaultSortDirectionKey
        {
            get => _modernThemeOverride?.DynamicLibraryAchievementsDefaultSortDirectionKey ?? _settings?.DynamicLibraryAchievementsDefaultSortDirectionKey ?? DynamicThemeViewKeys.Descending;
            set
            {
                if (_settings != null) _settings.DynamicLibraryAchievementsDefaultSortDirectionKey = value;
                else if (_modernThemeOverride != null) _modernThemeOverride.DynamicLibraryAchievementsDefaultSortDirectionKey = value;
            }
        }

        public ObservableCollection<DynamicThemeOption> DynamicLibraryAchievementsProviderOptions => _modernThemeOverride?.DynamicLibraryAchievementsProviderOptions ?? _settings?.DynamicLibraryAchievementsProviderOptions;

        public ObservableCollection<DynamicThemeOption> DynamicLibraryAchievementsFilterOptions => _modernThemeOverride?.DynamicLibraryAchievementsFilterOptions ?? _settings?.DynamicLibraryAchievementsFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicLibraryAchievementsSortOptions => _modernThemeOverride?.DynamicLibraryAchievementsSortOptions ?? _settings?.DynamicLibraryAchievementsSortOptions;

        public ObservableCollection<DynamicThemeOption> DynamicLibraryAchievementsSortDirectionOptions => _modernThemeOverride?.DynamicLibraryAchievementsSortDirectionOptions ?? _settings?.DynamicLibraryAchievementsSortDirectionOptions;

        public ObservableCollection<DynamicThemeOption> DynamicLibraryAchievementStatusFilterOptions => _modernThemeOverride?.DynamicLibraryAchievementStatusFilterOptions ?? _settings?.DynamicLibraryAchievementStatusFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicLibraryAchievementProgressFilterOptions => _modernThemeOverride?.DynamicLibraryAchievementProgressFilterOptions ?? _settings?.DynamicLibraryAchievementProgressFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicLibraryAchievementRarityFilterOptions => _modernThemeOverride?.DynamicLibraryAchievementRarityFilterOptions ?? _settings?.DynamicLibraryAchievementRarityFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicLibraryAchievementTrophyFilterOptions => _modernThemeOverride?.DynamicLibraryAchievementTrophyFilterOptions ?? _settings?.DynamicLibraryAchievementTrophyFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicLibraryAchievementCategoryTypeFilterOptions => _modernThemeOverride?.DynamicLibraryAchievementCategoryTypeFilterOptions ?? _settings?.DynamicLibraryAchievementCategoryTypeFilterOptions;

        public ObservableCollection<DynamicThemeOption> DynamicLibraryAchievementCustomizationFilterOptions => _modernThemeOverride?.DynamicLibraryAchievementCustomizationFilterOptions ?? _settings?.DynamicLibraryAchievementCustomizationFilterOptions;

        public AchievementRarityStats Common => _modernThemeOverride?.Common ?? _settings?.Common ?? EmptyRarityStats;

        public AchievementRarityStats Uncommon => _modernThemeOverride?.Uncommon ?? _settings?.Uncommon ?? EmptyRarityStats;

        public AchievementRarityStats Rare => _modernThemeOverride?.Rare ?? _settings?.Rare ?? EmptyRarityStats;

        public AchievementRarityStats UltraRare => _modernThemeOverride?.UltraRare ?? _settings?.UltraRare ?? EmptyRarityStats;

        public AchievementRarityStats RareAndUltraRare => _modernThemeOverride?.RareAndUltraRare ?? _settings?.RareAndUltraRare ?? EmptyRarityStats;

        public System.Windows.Input.ICommand SetDynamicAchievementsGameCommand => _settings?.SetDynamicAchievementsGameCommand;

        public System.Windows.Input.ICommand FilterDynamicAchievementsByRunningGameCommand => _settings?.FilterDynamicAchievementsByRunningGameCommand;

        public System.Windows.Input.ICommand SetDynamicAchievementsFilterCommand => _settings?.SetDynamicAchievementsFilterCommand;

        public System.Windows.Input.ICommand SetDynamicAchievementsCategoryLabelFilterCommand => _settings?.SetDynamicAchievementsCategoryLabelFilterCommand;

        public System.Windows.Input.ICommand SetDynamicAchievementsStatusFilterCommand => _settings?.SetDynamicAchievementsStatusFilterCommand;

        public System.Windows.Input.ICommand SetDynamicAchievementsProgressFilterCommand => _settings?.SetDynamicAchievementsProgressFilterCommand;

        public System.Windows.Input.ICommand SetDynamicAchievementsRarityFilterCommand => _settings?.SetDynamicAchievementsRarityFilterCommand;

        public System.Windows.Input.ICommand SetDynamicAchievementsTrophyFilterCommand => _settings?.SetDynamicAchievementsTrophyFilterCommand;

        public System.Windows.Input.ICommand SetDynamicAchievementsCategoryTypeFilterCommand => _settings?.SetDynamicAchievementsCategoryTypeFilterCommand;

        public System.Windows.Input.ICommand SetDynamicAchievementsCustomizationFilterCommand => _settings?.SetDynamicAchievementsCustomizationFilterCommand;

        public System.Windows.Input.ICommand SortDynamicAchievementsCommand => _settings?.SortDynamicAchievementsCommand;

        public System.Windows.Input.ICommand SetDynamicAchievementsSortDirectionCommand => _settings?.SetDynamicAchievementsSortDirectionCommand;

        public System.Windows.Input.ICommand OpenViewAchievementsWindow => _settings?.OpenViewAchievementsWindow;

        public System.Windows.Input.ICommand OpenManageAchievementsWindow => _settings?.OpenManageAchievementsWindow;

        public System.Windows.Input.ICommand FilterDynamicLibraryAchievementsByProviderCommand => _settings?.FilterDynamicLibraryAchievementsByProviderCommand;

        public System.Windows.Input.ICommand FilterDynamicLibraryAchievementsByRunningGameCommand => _settings?.FilterDynamicLibraryAchievementsByRunningGameCommand;

        public System.Windows.Input.ICommand SetDynamicLibraryAchievementsFilterCommand => _settings?.SetDynamicLibraryAchievementsFilterCommand;

        public System.Windows.Input.ICommand SetDynamicLibraryAchievementsStatusFilterCommand => _settings?.SetDynamicLibraryAchievementsStatusFilterCommand;

        public System.Windows.Input.ICommand SetDynamicLibraryAchievementsProgressFilterCommand => _settings?.SetDynamicLibraryAchievementsProgressFilterCommand;

        public System.Windows.Input.ICommand SetDynamicLibraryAchievementsRarityFilterCommand => _settings?.SetDynamicLibraryAchievementsRarityFilterCommand;

        public System.Windows.Input.ICommand SetDynamicLibraryAchievementsTrophyFilterCommand => _settings?.SetDynamicLibraryAchievementsTrophyFilterCommand;

        public System.Windows.Input.ICommand SetDynamicLibraryAchievementsCategoryTypeFilterCommand => _settings?.SetDynamicLibraryAchievementsCategoryTypeFilterCommand;

        public System.Windows.Input.ICommand SetDynamicLibraryAchievementsCustomizationFilterCommand => _settings?.SetDynamicLibraryAchievementsCustomizationFilterCommand;

        public System.Windows.Input.ICommand SortDynamicLibraryAchievementsCommand => _settings?.SortDynamicLibraryAchievementsCommand;

        public System.Windows.Input.ICommand SetDynamicLibraryAchievementsSortDirectionCommand => _settings?.SetDynamicLibraryAchievementsSortDirectionCommand;

        public System.Windows.Input.ICommand FilterDynamicGameSummariesByProviderCommand => _settings?.FilterDynamicGameSummariesByProviderCommand;

        public System.Windows.Input.ICommand FilterDynamicGameSummariesByRunningGameCommand => _settings?.FilterDynamicGameSummariesByRunningGameCommand;

        public System.Windows.Input.ICommand SetDynamicGameSummariesFilterCommand => _settings?.SetDynamicGameSummariesFilterCommand;

        public System.Windows.Input.ICommand SetDynamicGameSummariesProgressFilterCommand => _settings?.SetDynamicGameSummariesProgressFilterCommand;

        public System.Windows.Input.ICommand SetDynamicGameSummariesActivityFilterCommand => _settings?.SetDynamicGameSummariesActivityFilterCommand;

        public System.Windows.Input.ICommand SortDynamicGameSummariesCommand => _settings?.SortDynamicGameSummariesCommand;

        public System.Windows.Input.ICommand SetDynamicGameSummariesSortDirectionCommand => _settings?.SetDynamicGameSummariesSortDirectionCommand;

        public System.Windows.Input.ICommand SetDynamicCategorySummariesFilterCommand => _settings?.SetDynamicCategorySummariesFilterCommand;

        public System.Windows.Input.ICommand SortDynamicCategorySummariesCommand => _settings?.SortDynamicCategorySummariesCommand;

        public System.Windows.Input.ICommand SetDynamicCategorySummariesSortDirectionCommand => _settings?.SetDynamicCategorySummariesSortDirectionCommand;

        public System.Windows.Input.ICommand SetDynamicFriendScopeProviderCommand => _settings?.SetDynamicFriendScopeProviderCommand;

        public System.Windows.Input.ICommand SetDynamicFriendScopeUserCommand => _settings?.SetDynamicFriendScopeUserCommand;

        public System.Windows.Input.ICommand SetDynamicFriendScopeGameCommand => _settings?.SetDynamicFriendScopeGameCommand;

        public System.Windows.Input.ICommand ResetDynamicFriendScopeCommand => _settings?.ResetDynamicFriendScopeCommand;

        public System.Windows.Input.ICommand FilterDynamicFriendSummariesByRunningGameCommand => _settings?.FilterDynamicFriendSummariesByRunningGameCommand;

        public System.Windows.Input.ICommand SetDynamicFriendSummariesFilterCommand => _settings?.SetDynamicFriendSummariesFilterCommand;

        public System.Windows.Input.ICommand SetDynamicFriendSummariesLastUnlockFilterCommand => _settings?.SetDynamicFriendSummariesLastUnlockFilterCommand;

        public System.Windows.Input.ICommand SortDynamicFriendSummariesCommand => _settings?.SortDynamicFriendSummariesCommand;

        public System.Windows.Input.ICommand SetDynamicFriendSummariesSortDirectionCommand => _settings?.SetDynamicFriendSummariesSortDirectionCommand;

        public System.Windows.Input.ICommand FilterDynamicFriendGameSummariesByRunningGameCommand => _settings?.FilterDynamicFriendGameSummariesByRunningGameCommand;

        public System.Windows.Input.ICommand SetDynamicFriendGameSummariesFilterCommand => _settings?.SetDynamicFriendGameSummariesFilterCommand;

        public System.Windows.Input.ICommand SetDynamicFriendGameSummariesProgressFilterCommand => _settings?.SetDynamicFriendGameSummariesProgressFilterCommand;

        public System.Windows.Input.ICommand SetDynamicFriendGameSummariesActivityFilterCommand => _settings?.SetDynamicFriendGameSummariesActivityFilterCommand;

        public System.Windows.Input.ICommand SortDynamicFriendGameSummariesCommand => _settings?.SortDynamicFriendGameSummariesCommand;

        public System.Windows.Input.ICommand SetDynamicFriendGameSummariesSortDirectionCommand => _settings?.SetDynamicFriendGameSummariesSortDirectionCommand;

        public System.Windows.Input.ICommand FilterDynamicFriendAchievementsByRunningGameCommand => _settings?.FilterDynamicFriendAchievementsByRunningGameCommand;

        public System.Windows.Input.ICommand SetDynamicFriendAchievementsFilterCommand => _settings?.SetDynamicFriendAchievementsFilterCommand;

        public System.Windows.Input.ICommand SetDynamicFriendAchievementsStatusFilterCommand => _settings?.SetDynamicFriendAchievementsStatusFilterCommand;

        public System.Windows.Input.ICommand SetDynamicFriendAchievementsProgressFilterCommand => _settings?.SetDynamicFriendAchievementsProgressFilterCommand;

        public System.Windows.Input.ICommand SetDynamicFriendAchievementsRarityFilterCommand => _settings?.SetDynamicFriendAchievementsRarityFilterCommand;

        public System.Windows.Input.ICommand SetDynamicFriendAchievementsTrophyFilterCommand => _settings?.SetDynamicFriendAchievementsTrophyFilterCommand;

        public System.Windows.Input.ICommand SetDynamicFriendAchievementsCategoryTypeFilterCommand => _settings?.SetDynamicFriendAchievementsCategoryTypeFilterCommand;

        public System.Windows.Input.ICommand SetDynamicFriendAchievementsCustomizationFilterCommand => _settings?.SetDynamicFriendAchievementsCustomizationFilterCommand;

        public System.Windows.Input.ICommand SortDynamicFriendAchievementsCommand => _settings?.SortDynamicFriendAchievementsCommand;

        public System.Windows.Input.ICommand SetDynamicFriendAchievementsSortDirectionCommand => _settings?.SetDynamicFriendAchievementsSortDirectionCommand;

        public System.Windows.Input.ICommand ResetDynamicAchievementsCommand => _settings?.ResetDynamicAchievementsCommand;

        public System.Windows.Input.ICommand ResetDynamicLibraryAchievementsCommand => _settings?.ResetDynamicLibraryAchievementsCommand;

        public System.Windows.Input.ICommand ResetDynamicGameSummariesCommand => _settings?.ResetDynamicGameSummariesCommand;

        public System.Windows.Input.ICommand ResetDynamicCategorySummariesCommand => _settings?.ResetDynamicCategorySummariesCommand;

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

            NotifyComputedFilterProperties(propertyName);
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

            NotifyComputedFilterProperties(propertyName);
        }

        private void NotifyForwardedModernProperties()
        {
            foreach (var propertyName in ForwardedModernProperties)
            {
                OnPropertyChanged(propertyName);
            }
        }

        private void NotifyComputedFilterProperties(string propertyName)
        {
            if (propertyName == nameof(PlayniteAchievementsSettings.DynamicAchievementsFilterKey) ||
                propertyName == nameof(ModernThemeBindings.DynamicAchievementsFilterKey))
            {
                OnPropertyChanged(nameof(DynamicAchievementsStatusFilterKey));
                OnPropertyChanged(nameof(DynamicAchievementsProgressFilterKey));
                OnPropertyChanged(nameof(DynamicAchievementsRarityFilterKey));
                OnPropertyChanged(nameof(DynamicAchievementsTrophyFilterKey));
                OnPropertyChanged(nameof(DynamicAchievementsCategoryTypeFilterKey));
                OnPropertyChanged(nameof(DynamicAchievementsCustomizationFilterKey));
            }

            if (propertyName == nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsFilterKey) ||
                propertyName == nameof(ModernThemeBindings.DynamicLibraryAchievementsFilterKey))
            {
                OnPropertyChanged(nameof(DynamicLibraryAchievementsStatusFilterKey));
                OnPropertyChanged(nameof(DynamicLibraryAchievementsProgressFilterKey));
                OnPropertyChanged(nameof(DynamicLibraryAchievementsRarityFilterKey));
                OnPropertyChanged(nameof(DynamicLibraryAchievementsTrophyFilterKey));
                OnPropertyChanged(nameof(DynamicLibraryAchievementsCategoryTypeFilterKey));
                OnPropertyChanged(nameof(DynamicLibraryAchievementsCustomizationFilterKey));
            }

            if (propertyName == nameof(PlayniteAchievementsSettings.DynamicGameSummariesFilterKey) ||
                propertyName == nameof(ModernThemeBindings.DynamicGameSummariesFilterKey))
            {
                OnPropertyChanged(nameof(DynamicGameSummariesProgressFilterKey));
                OnPropertyChanged(nameof(DynamicGameSummariesActivityFilterKey));
            }

            if (propertyName == nameof(PlayniteAchievementsSettings.DynamicFriendSummariesFilterKey) ||
                propertyName == nameof(ModernThemeBindings.DynamicFriendSummariesFilterKey))
            {
                OnPropertyChanged(nameof(DynamicFriendSummariesLastUnlockFilterKey));
            }

            if (propertyName == nameof(PlayniteAchievementsSettings.DynamicFriendGameSummariesFilterKey) ||
                propertyName == nameof(ModernThemeBindings.DynamicFriendGameSummariesFilterKey))
            {
                OnPropertyChanged(nameof(DynamicFriendGameSummariesProgressFilterKey));
                OnPropertyChanged(nameof(DynamicFriendGameSummariesActivityFilterKey));
            }

            if (propertyName == nameof(PlayniteAchievementsSettings.DynamicFriendAchievementsFilterKey) ||
                propertyName == nameof(ModernThemeBindings.DynamicFriendAchievementsFilterKey))
            {
                OnPropertyChanged(nameof(DynamicFriendAchievementsStatusFilterKey));
                OnPropertyChanged(nameof(DynamicFriendAchievementsProgressFilterKey));
                OnPropertyChanged(nameof(DynamicFriendAchievementsRarityFilterKey));
                OnPropertyChanged(nameof(DynamicFriendAchievementsTrophyFilterKey));
                OnPropertyChanged(nameof(DynamicFriendAchievementsCategoryTypeFilterKey));
                OnPropertyChanged(nameof(DynamicFriendAchievementsCustomizationFilterKey));
            }
        }
    }
}

