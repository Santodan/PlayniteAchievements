using System;
using System.Windows.Controls;
using PlayniteAchievements.Models.ThemeIntegration;

namespace PlayniteAchievements.Views.Settings.Display.ThemeControls
{
    /// <summary>
    /// Display settings: Achievement DataGrid theme control page (enable toggle, preview, and
    /// grid options).
    /// </summary>
    public partial class DataGridThemePage : UserControl
    {
        private readonly ThemeControlPreviewState _preview;

        public DataGridThemePage()
        {
            InitializeComponent();
        }

        internal DataGridThemePage(ThemeControlPreviewState preview)
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
