using System;
using System.Windows.Controls;
using PlayniteAchievements.Models.ThemeIntegration;

namespace PlayniteAchievements.Views.Settings.Display.ThemeControls
{
    /// <summary>
    /// Display settings: View Item theme control page (enable toggle and preview).
    /// </summary>
    public partial class ViewItemThemePage : UserControl
    {
        private readonly ThemeControlPreviewState _preview;

        public ViewItemThemePage()
        {
            InitializeComponent();
        }

        internal ViewItemThemePage(ThemeControlPreviewState preview)
            : this()
        {
            _preview = preview ?? throw new ArgumentNullException(nameof(preview));
        }

        /// <summary>
        /// Mock theme bindings used by the preview controls via ThemeDataOverride.
        /// </summary>
        public ModernThemeBindings PreviewThemeData => _preview?.PreviewThemeData;
    }
}
