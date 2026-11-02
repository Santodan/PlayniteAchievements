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
    /// Display settings tab: a master-detail navigation over the Display sections. Sections are
    /// created lazily when first selected. The per-control theme preview pages live on the Themes
    /// tab; theme migration appears on both, driven by one shared controller.
    /// </summary>
    public partial class DisplaySettingsTab : UserControl, IDisposable
    {
        private ObservableCollection<SettingsNavigationItem> _navigationItems;

        private DisplayGeneralSection _generalSection;
        private SpoilersSection _spoilersSection;
        private ColorsSection _colorsSection;
        private MigrationThemePage _migrationPage;
        private readonly Action _onDisplaySettingsReset;

        public DisplaySettingsTab()
        {
            InitializeComponent();
        }

        internal DisplaySettingsTab(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ILogger logger,
            Func<Window, string, string> pickColor,
            ThemeMigrationController themeMigrationController,
            Action onDisplaySettingsReset)
            : this()
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (plugin == null) throw new ArgumentNullException(nameof(plugin));
            if (pickColor == null) throw new ArgumentNullException(nameof(pickColor));
            if (themeMigrationController == null) throw new ArgumentNullException(nameof(themeMigrationController));

            _onDisplaySettingsReset = onDisplaySettingsReset;

            // Per-grid display options are edited from each grid's own "Display settings" menu,
            // so this tab carries only the settings that are not tied to a single grid.
            _navigationItems = new ObservableCollection<SettingsNavigationItem>
            {
                new SettingsNavigationItem(
                    "General",
                    ResourceProvider.GetString("LOCPlayAch_Common_General"),
                    iconGlyph: "\uEF3A",
                    viewFactory: () => _generalSection =
                        new DisplayGeneralSection(settings, plugin, logger, OnDisplaySettingsReset)),
                new SettingsNavigationItem(
                    "Colors",
                    ResourceProvider.GetString("LOCPlayAch_Settings_Display_Colors"),
                    iconGlyph: "\uEFB3",
                    viewFactory: () => _colorsSection =
                        new ColorsSection(settings, plugin, logger, pickColor)),
                new SettingsNavigationItem(
                    "Spoilers",
                    ResourceProvider.GetString("LOCPlayAch_Settings_Spoilers"),
                    iconGlyph: "\uEF22",
                    viewFactory: () => _spoilersSection =
                        new SpoilersSection(settings, plugin, logger)),
                // Deliberately also listed on the Themes tab. Both entries bind the same
                // controller, so acting in either place is reflected in the other.
                new SettingsNavigationItem(
                    "Migration",
                    ResourceProvider.GetString("LOCPlayAch_ThemeMigration_Title"),
                    iconGlyph: "\uEF18",
                    viewFactory: () => _migrationPage = new MigrationThemePage(themeMigrationController))
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
            _colorsSection?.RefreshAppearanceEditorFromPersisted();
            _spoilersSection?.RefreshVisibilityPreview();
            _onDisplaySettingsReset?.Invoke();
        }

        public void Dispose()
        {
            _generalSection?.Dispose();
            _spoilersSection?.Dispose();
            _colorsSection?.Dispose();
            _migrationPage?.Detach();
        }
    }
}
