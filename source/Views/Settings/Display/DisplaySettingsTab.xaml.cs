using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
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
        private SettingsNavigationItem _friendsOverviewNavigationItem;
        private SettingsNavigationItem _friendsAchievementsNavigationItem;
        private PlayniteAchievementsSettings _settings;

        private DisplayGeneralSection _generalSection;
        private AppearanceSection _appearanceSection;
        private StartPageDisplaySection _startPageSection;
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

            _settings = settings;
            _previewState = new ThemeControlPreviewState(settings);
            _themeMigrationController = new ThemeMigrationController(settings, plugin, logger);

            var themeControlsGroup = ResourceProvider.GetString("LOCPlayAch_Settings_Display_ThemeIntegration");
            var themeMigrationGroup = ResourceProvider.GetString("LOCPlayAch_ThemeMigration_Title");

            _friendsOverviewNavigationItem = new SettingsNavigationItem(
                "FriendsOverview",
                ResourceProvider.GetString("LOCPlayAch_FriendsOverview_Title"),
                iconGlyph: "",
                viewFactory: () => new FriendsOverviewDisplaySection());

            _friendsAchievementsNavigationItem = new SettingsNavigationItem(
                "FriendsAchievementsWindow",
                ResourceProvider.GetString("LOCPlayAch_ViewFriendsAchievements_TitleFallback"),
                iconGlyph: "",
                viewFactory: () => new FriendsAchievementsWindowDisplaySection());

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
                        new AppearanceSection(settings, pickColor)),
                new SettingsNavigationItem(
                    "Overview",
                    ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Tab_Overview"),
                    iconGlyph: "",
                    viewFactory: () => new OverviewDisplaySection()),
                new SettingsNavigationItem(
                    "AchievementsWindow",
                    ResourceProvider.GetString("LOCPlayAch_Settings_ViewAchievementsWindow"),
                    iconGlyph: "",
                    viewFactory: () => new AchievementsWindowDisplaySection()),
                new SettingsNavigationItem(
                    "StartPage",
                    ResourceProvider.GetString("LOCPlayAch_Settings_Display_StartPageSection"),
                    iconGlyph: "",
                    viewFactory: () => _startPageSection =
                        new StartPageDisplaySection(settings)),
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
                    viewFactory: () => new MigrationThemePage(_themeMigrationController)),
                new SettingsNavigationItem(
                    "Revert",
                    ResourceProvider.GetString("LOCPlayAch_ThemeMigration_Revert"),
                    groupName: themeMigrationGroup,
                    iconGlyph: "",
                    viewFactory: () => new RevertThemePage(_themeMigrationController))
            };

            if (settings.Persisted.EnableFriendsFeatures)
            {
                InsertFriendsNavigationItems();
            }

            MasterDetail.ItemsSource = _navigationItems;
            MasterDetail.SelectedItem = _navigationItems[0];

            settings.Persisted.PropertyChanged += Persisted_PropertyChanged;
        }

        private void Persisted_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e?.PropertyName != nameof(PersistedSettings.EnableFriendsFeatures))
            {
                return;
            }

            if (_settings.Persisted.EnableFriendsFeatures)
            {
                InsertFriendsNavigationItems();
            }
            else
            {
                var wasSelected = MasterDetail.SelectedItem == _friendsOverviewNavigationItem ||
                                  MasterDetail.SelectedItem == _friendsAchievementsNavigationItem;
                _navigationItems.Remove(_friendsOverviewNavigationItem);
                _navigationItems.Remove(_friendsAchievementsNavigationItem);
                if (wasSelected)
                {
                    MasterDetail.SelectedItem = _navigationItems[0];
                }
            }
        }

        private void InsertFriendsNavigationItems()
        {
            InsertNavigationItemAfter(_friendsOverviewNavigationItem, "Overview");
            InsertNavigationItemAfter(_friendsAchievementsNavigationItem, "AchievementsWindow");
        }

        private void InsertNavigationItemAfter(SettingsNavigationItem item, string precedingKey)
        {
            if (_navigationItems.Contains(item))
            {
                return;
            }

            var precedingItem = _navigationItems.FirstOrDefault(x =>
                string.Equals(x.Key, precedingKey, StringComparison.OrdinalIgnoreCase));
            var insertIndex = precedingItem == null
                ? _navigationItems.Count
                : _navigationItems.IndexOf(precedingItem) + 1;
            _navigationItems.Insert(Math.Min(insertIndex, _navigationItems.Count), item);
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
            _previewState?.RefreshMockPreviews();
        }

        public void Dispose()
        {
            if (_settings != null)
            {
                _settings.Persisted.PropertyChanged -= Persisted_PropertyChanged;
            }
            _generalSection?.Dispose();
            _appearanceSection?.Dispose();
            _startPageSection?.Dispose();
            _previewState?.Dispose();
        }
    }
}
