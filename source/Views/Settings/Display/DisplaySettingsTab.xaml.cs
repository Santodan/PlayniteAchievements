using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Views.Settings.Display.ThemeControls;
using PlayniteAchievements.Views.Settings.Navigation;

namespace PlayniteAchievements.Views.Settings.Display
{
    /// <summary>
    /// Display settings tab: a grouped master-detail navigation over the Display sections, the
    /// per-control theme pages, and the theme migration pages. Sections are created lazily when
    /// first selected.
    /// </summary>
    public partial class DisplaySettingsTab : UserControl, IDisposable
    {
        private ObservableCollection<SettingsNavigationItem> _navigationItems;

        private DisplayGeneralSection _generalSection;
        private SpoilersSection _spoilersSection;
        private AppearanceSection _appearanceSection;
        private ThemeControlPreviewState _previewState;
        private ThemeMigrationController _themeMigrationController;

        public DisplaySettingsTab()
        {
            InitializeComponent();
        }

        internal DisplaySettingsTab(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ILogger logger,
            Func<Window, string, string> pickColor)
            : this()
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));
            if (pickColor == null) throw new ArgumentNullException(nameof(pickColor));

            _previewState = new ThemeControlPreviewState(settings);
            _themeMigrationController = new ThemeMigrationController(settings, plugin, logger);

            var themeControlsGroup = ResourceProvider.GetString("LOCPlayAch_Settings_Display_ThemeIntegration");
            var themeMigrationGroup = ResourceProvider.GetString("LOCPlayAch_ThemeMigration_Title");

            // Per-grid display options are edited from each grid's own "Display settings" menu,
            // so this tab carries only the settings that are not tied to a single grid.
            _navigationItems = new ObservableCollection<SettingsNavigationItem>
            {
                new SettingsNavigationItem(
                    "General",
                    ResourceProvider.GetString("LOCPlayAch_Common_General"),
                    iconGlyph: "",
                    viewFactory: () => _generalSection =
                        new DisplayGeneralSection(settings, plugin, logger, OnDisplaySettingsReset)),
                new SettingsNavigationItem(
                    "Appearance",
                    ResourceProvider.GetString("LOCPlayAch_Settings_Appearance"),
                    iconGlyph: "",
                    viewFactory: () => _appearanceSection =
                        new AppearanceSection(settings, plugin.ProviderRegistry, pickColor)),
                new SettingsNavigationItem(
                    "Overview",
                    ResourceProvider.GetString("LOCPlayAch_Settings_Display_OverviewLayout"),
                    iconGlyph: "",
                    viewFactory: () => new OverviewDisplaySection()),
                new SettingsNavigationItem(
                    "Spoilers",
                    ResourceProvider.GetString("LOCPlayAch_Settings_Spoilers"),
                    iconGlyph: "",
                    viewFactory: () => _spoilersSection =
                        new SpoilersSection(settings, plugin, logger)),
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
                    viewFactory: () => new BarChartThemePage(_previewState)),
                new SettingsNavigationItem(
                    "Migration",
                    ResourceProvider.GetString("LOCPlayAch_ThemeMigration_Title"),
                    groupName: themeMigrationGroup,
                    iconGlyph: "",
                    viewFactory: () => new MigrationThemePage(_themeMigrationController))
            };

            MasterDetail.ItemsSource = _navigationItems;
            MasterDetail.SelectedItem = _navigationItems[0];
        }

        /// <summary>
        /// Selects the navigation item with the given key (e.g. "Migration"). Used by the
        /// General tab's quick links to jump directly to a Display page.
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
        /// Refreshes already-created sections after the General section reset display settings
        /// to defaults. Sections that do not exist yet pick up the new values on creation.
        /// </summary>
        private void OnDisplaySettingsReset()
        {
            _appearanceSection?.RefreshAppearanceEditorFromPersisted();
            _spoilersSection?.RefreshVisibilityPreview();
            _previewState?.RefreshMockPreviews();
        }

        public void Dispose()
        {
            _generalSection?.Dispose();
            _spoilersSection?.Dispose();
            _appearanceSection?.Dispose();
            _previewState?.Dispose();
        }
    }
}
