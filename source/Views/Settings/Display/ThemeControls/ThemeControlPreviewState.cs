using System;
using System.ComponentModel;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Settings.Display.ThemeControls
{
    /// <summary>
    /// Shared mock preview state for the per-control theme pages. Owns the single
    /// <see cref="ModernThemeBindings"/> instance bound by every preview control via
    /// ThemeDataOverride and refreshes it when persisted settings that affect the previews change.
    /// </summary>
    internal sealed class ThemeControlPreviewState : IDisposable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly PersistedSettingsSubscription _persistedSubscription;
        private ModernThemeBindings _previewThemeData;

        public ThemeControlPreviewState(PlayniteAchievementsSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            _persistedSubscription = new PersistedSettingsSubscription(
                _settings,
                OnPersistedPropertyChanged,
                RefreshMockPreviews);
        }

        /// <summary>
        /// Gets modern theme bindings populated with mock achievements for modern control previews.
        /// </summary>
        public ModernThemeBindings PreviewThemeData
        {
            get
            {
                if (_previewThemeData == null)
                {
                    _previewThemeData = MockDataHelper.GetPreviewThemeData();
                }
                return _previewThemeData;
            }
        }

        /// <summary>
        /// Refreshes the mock preview theme bindings to reflect current settings.
        /// </summary>
        public void RefreshMockPreviews()
        {
            var settings = _settings?.Persisted;
            if (settings == null) return;

            _previewThemeData?.RefreshDisplayItems(
                settings.ShowHiddenIcon, settings.ShowHiddenTitle, settings.ShowHiddenDescription,
                settings.ShowHiddenSuffix, settings.ShowLockedIcon, settings.UseSeparateLockedIconsWhenAvailable, settings.ShowCompactListRarityBar);
        }

        private void OnPersistedPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (DisplayPreviewProperties.AffectsMockPreviews(e.PropertyName))
            {
                RefreshMockPreviews();
            }
        }

        public void Dispose()
        {
            _persistedSubscription?.Dispose();
        }
    }
}
