using System;
using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.ThemeIntegration;

namespace PlayniteAchievements.Views.Settings.Display.ThemeControls
{
    /// <summary>
    /// Display settings: Compact List theme control page (enable toggle, preview, rarity bar and
    /// glow options, and sort options).
    /// </summary>
    public partial class CompactListThemePage : UserControl
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ThemeControlPreviewState _preview;

        public CompactListThemePage()
        {
            InitializeComponent();
        }

        internal CompactListThemePage(PlayniteAchievementsSettings settings, ThemeControlPreviewState preview)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        }

        /// <summary>
        /// Mock theme bindings used by the preview control via ThemeDataOverride.
        /// </summary>
        public ModernThemeBindings PreviewThemeData => _preview?.PreviewThemeData;

        private void ToggleCompactListSortDescending(object sender, RoutedEventArgs e)
        {
            var persisted = _settings?.Persisted;
            if (persisted != null)
            {
                persisted.CompactListSortDescending = !persisted.CompactListSortDescending;
            }
        }
    }
}
