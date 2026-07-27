using System;
using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.ThemeIntegration;

namespace PlayniteAchievements.Views.Settings.Display.ThemeControls
{
    /// <summary>
    /// Display settings: Compact Unlocked List theme control page (enable toggle, preview, glow
    /// option, and sort options).
    /// </summary>
    public partial class CompactUnlockedListThemePage : UserControl
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ThemeControlPreviewState _preview;

        public CompactUnlockedListThemePage()
        {
            InitializeComponent();
        }

        internal CompactUnlockedListThemePage(PlayniteAchievementsSettings settings, ThemeControlPreviewState preview)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        }

        /// <summary>
        /// Mock theme bindings used by the preview control via ThemeDataOverride.
        /// </summary>
        public ModernThemeBindings PreviewThemeData => _preview?.PreviewThemeData;

        private void ToggleCompactUnlockedListSortDescending(object sender, RoutedEventArgs e)
        {
            var persisted = _settings?.Persisted;
            if (persisted != null)
            {
                persisted.CompactUnlockedListSortDescending = !persisted.CompactUnlockedListSortDescending;
            }
        }
    }
}
