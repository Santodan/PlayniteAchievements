using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Views.Settings.Display.ThemeControls;
using PlayniteAchievements.Views.Settings.Navigation;

namespace PlayniteAchievements.Views.Settings.Themes
{
    /// <summary>
    /// Themes settings tab: theme migration plus the per-control theme preview pages, which
    /// previously crowded the Display tab's navigation. Pages are created lazily when first
    /// selected. Theme migration is listed here and on the Display tab; both entries bind the one
    /// shared controller this tab is handed.
    /// </summary>
    public partial class ThemesSettingsTab : UserControl, IDisposable
    {
        private ObservableCollection<SettingsNavigationItem> _navigationItems;

        private ThemeControlPreviewState _previewState;
        private MigrationThemePage _migrationPage;

        public ThemesSettingsTab()
        {
            InitializeComponent();
        }

        internal ThemesSettingsTab(
            PlayniteAchievementsSettings settings,
            ThemeMigrationController themeMigrationController)
            : this()
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (themeMigrationController == null) throw new ArgumentNullException(nameof(themeMigrationController));

            _previewState = new ThemeControlPreviewState(settings);

            var themeMigrationGroup = ResourceProvider.GetString("LOCPlayAch_ThemeMigration_Title");
            var themeControlsGroup = ResourceProvider.GetString("LOCPlayAch_Settings_Display_ThemeIntegration");

            // Registration order sets group order: the nav rail groups with a
            // PropertyGroupDescription and no sort, so groups render as first encountered.
            _navigationItems = new ObservableCollection<SettingsNavigationItem>
            {
                new SettingsNavigationItem(
                    "Migration",
                    ResourceProvider.GetString("LOCPlayAch_ThemeMigration_Title"),
                    groupName: themeMigrationGroup,
                    iconGlyph: "",
                    viewFactory: () => _migrationPage = new MigrationThemePage(themeMigrationController)),
                new SettingsNavigationItem(
                    "DataGrid",
                    ResourceProvider.GetString("LOCPlayAch_Settings_AchievementDataGridPreview"),
                    groupName: themeControlsGroup,
                    iconGlyph: "",
                    viewFactory: () => new DataGridThemePage(_previewState)),
                new SettingsNavigationItem(
                    "CompactList",
                    ResourceProvider.GetString("LOCPlayAch_Settings_CompactListPreview"),
                    groupName: themeControlsGroup,
                    iconGlyph: "",
                    viewFactory: () => new CompactListThemePage(settings, _previewState)),
                new SettingsNavigationItem(
                    "CompactUnlockedList",
                    ResourceProvider.GetString("LOCPlayAch_Settings_CompactUnlockedListPreview"),
                    groupName: themeControlsGroup,
                    iconGlyph: "",
                    viewFactory: () => new CompactUnlockedListThemePage(settings, _previewState)),
                new SettingsNavigationItem(
                    "CompactLockedList",
                    ResourceProvider.GetString("LOCPlayAch_Settings_CompactLockedListPreview"),
                    groupName: themeControlsGroup,
                    iconGlyph: "",
                    viewFactory: () => new CompactLockedListThemePage(settings, _previewState)),
                new SettingsNavigationItem(
                    "ProgressBar",
                    ResourceProvider.GetString("LOCPlayAch_Settings_ProgressBarPreview"),
                    groupName: themeControlsGroup,
                    iconGlyph: "",
                    viewFactory: () => new ProgressBarThemePage(_previewState)),
                new SettingsNavigationItem(
                    "Stats",
                    ResourceProvider.GetString("LOCPlayAch_Settings_StatsPreview"),
                    groupName: themeControlsGroup,
                    iconGlyph: "",
                    viewFactory: () => new StatsThemePage(_previewState)),
                new SettingsNavigationItem(
                    "Button",
                    ResourceProvider.GetString("LOCPlayAch_Settings_ButtonPreview"),
                    groupName: themeControlsGroup,
                    iconGlyph: "",
                    viewFactory: () => new ButtonThemePage(_previewState)),
                new SettingsNavigationItem(
                    "ViewItem",
                    ResourceProvider.GetString("LOCPlayAch_Settings_ViewItemPreview"),
                    groupName: themeControlsGroup,
                    iconGlyph: "",
                    viewFactory: () => new ViewItemThemePage(_previewState)),
                new SettingsNavigationItem(
                    "PieChart",
                    ResourceProvider.GetString("LOCPlayAch_Settings_PieChartPreview"),
                    groupName: themeControlsGroup,
                    iconGlyph: "",
                    viewFactory: () => new PieChartThemePage(_previewState)),
                new SettingsNavigationItem(
                    "BarChart",
                    ResourceProvider.GetString("LOCPlayAch_Settings_BarChartPreview"),
                    groupName: themeControlsGroup,
                    iconGlyph: "",
                    viewFactory: () => new BarChartThemePage(_previewState))
            };

            MasterDetail.ItemsSource = _navigationItems;
            MasterDetail.SelectedItem = _navigationItems[0];
        }

        /// <summary>
        /// Selects the navigation item with the given key (e.g. "Migration").
        /// </summary>
        public void NavigateToPage(string key)
        {
            var item = _navigationItems?.FirstOrDefault(x =>
                string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                MasterDetail.SelectedItem = item;
            }
        }

        /// <summary>
        /// Repaints the already-created preview pages after Display settings were reset to
        /// defaults from the Display tab. Pages not created yet pick up the new values on creation.
        /// </summary>
        public void RefreshAfterDisplaySettingsReset()
        {
            _previewState?.RefreshMockPreviews();
        }

        public void Dispose()
        {
            _migrationPage?.Detach();
            _previewState?.Dispose();
        }
    }
}
