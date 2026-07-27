using System;
using System.Windows.Controls;
using PlayniteAchievements.Models.ThemeIntegration;

namespace PlayniteAchievements.Views.Settings.Display.ThemeControls
{
    /// <summary>
    /// Display settings: Progress Bar theme control page (enable toggle and preview).
    /// </summary>
    public partial class ProgressBarThemePage : UserControl
    {
        private readonly ThemeControlPreviewState _preview;

        public ProgressBarThemePage()
        {
            InitializeComponent();
        }

        internal ProgressBarThemePage(ThemeControlPreviewState preview)
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
