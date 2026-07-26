using System;
using System.Windows.Controls;
using PlayniteAchievements.Models.ThemeIntegration;

namespace PlayniteAchievements.Views.Settings.Display.ThemeControls
{
    /// <summary>
    /// Display settings: Stats theme control page (enable toggle and preview).
    /// </summary>
    public partial class StatsThemePage : UserControl
    {
        private readonly ThemeControlPreviewState _preview;

        public StatsThemePage()
        {
            InitializeComponent();
        }

        internal StatsThemePage(ThemeControlPreviewState preview)
            : this()
        {
            _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        }

        /// <summary>
        /// Mock theme bindings used by the preview control via ThemeDataOverride.
        /// </summary>
        public ModernThemeBindings PreviewThemeData => _preview?.PreviewThemeData;
    }
}
