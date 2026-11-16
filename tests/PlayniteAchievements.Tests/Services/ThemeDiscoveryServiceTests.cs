using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Services.ThemeMigration;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace PlayniteAchievements.ThemeMigration.Tests
{
    [TestClass]
    public class ThemeDiscoveryServiceTests
    {
        [TestMethod]
        public void DiscoverThemes_DoesNotFlagThemeForMigration_WhenThemeFilesContainPlayniteAchievements()
        {
            var themesRoot = CreateThemesRoot();

            try
            {
                var themePath = Path.Combine(themesRoot, "Fullscreen", "Aniki-ReMake_ab123456");
                Directory.CreateDirectory(themePath);
                File.WriteAllText(Path.Combine(themePath, "theme.yaml"), "Name: Aniki ReMake\nVersion: 2.5.5\n");
                File.WriteAllText(
                    Path.Combine(themePath, "View.xaml"),
                    "<TextBlock Text=\"SuccessStoryFullscreenHelper\" />\n<TextBlock Text=\"PlayniteAchievements\" />");

                var service = new ThemeDiscoveryService(new FakeLogger(), new FakePlayniteApi());
                var themes = service.DiscoverThemes(themesRoot);
                var theme = themes.Single();

                Assert.AreEqual("Fullscreen/Aniki ReMake", theme.BestDisplayName);
                Assert.IsFalse(theme.NeedsMigration);
                Assert.IsFalse(theme.CouldNotScan);
            }
            finally
            {
                DeleteDirectory(themesRoot);
            }
        }

        [TestMethod]
        public void DiscoverThemes_FlagsLegacyThemeWithoutNativeSupportForMigration()
        {
            var themesRoot = CreateThemesRoot();

            try
            {
                var themePath = Path.Combine(themesRoot, "Desktop", "LegacyTheme_ab123456");
                Directory.CreateDirectory(themePath);
                File.WriteAllText(Path.Combine(themePath, "theme.yaml"), "Name: Legacy Theme\nVersion: 1.0.0\n");
                File.WriteAllText(Path.Combine(themePath, "View.xaml"), "<TextBlock Text=\"SuccessStory\" />");

                var service = new ThemeDiscoveryService(new FakeLogger(), new FakePlayniteApi());
                var themes = service.DiscoverThemes(themesRoot);
                var theme = themes.Single();

                Assert.IsTrue(theme.NeedsMigration);
                Assert.IsFalse(theme.CouldNotScan);
            }
            finally
            {
                DeleteDirectory(themesRoot);
            }
        }

        [DataTestMethod]
        [DataRow(MigrationMode.Full)]
        [DataRow(MigrationMode.Custom)]
        public async Task MigrateThemeAsync_BlocksAdvancedModesForFullscreenThemes(MigrationMode mode)
        {
            var themesRoot = CreateThemesRoot();

            try
            {
                var themePath = Path.Combine(themesRoot, "Fullscreen", "Aniki-ReMake_ab123456");
                Directory.CreateDirectory(themePath);
                var viewPath = Path.Combine(themePath, "View.xaml");
                File.WriteAllText(Path.Combine(themePath, "theme.yaml"), "Name: Aniki ReMake\nVersion: 2.5.5\n");
                File.WriteAllText(viewPath, "<TextBlock Text=\"SuccessStory_PluginButton\" />");

                var service = new ThemeMigrationService(new FakeLogger());
                var result = await service.MigrateThemeAsync(themePath, mode, new CustomMigrationSelection());

                Assert.IsFalse(result.Success);
                Assert.AreEqual(ThemeMigrationService.FullscreenThemesLimitedOnlyMessage, result.Message);
                Assert.IsFalse(Directory.Exists(Path.Combine(themePath, "PlayniteAchievements_backup")));
                Assert.AreEqual("<TextBlock Text=\"SuccessStory_PluginButton\" />", File.ReadAllText(viewPath));
            }
            finally
            {
                DeleteDirectory(themesRoot);
            }
        }

        [TestMethod]
        public async Task MigrateThemeAsync_AllowsLimitedModeForFullscreenThemes()
        {
            var themesRoot = CreateThemesRoot();

            try
            {
                var themePath = Path.Combine(themesRoot, "Fullscreen", "Aniki-ReMake_ab123456");
                Directory.CreateDirectory(themePath);
                var viewPath = Path.Combine(themePath, "View.xaml");
                File.WriteAllText(Path.Combine(themePath, "theme.yaml"), "Name: Aniki ReMake\nVersion: 2.5.5\n");
                File.WriteAllText(viewPath, "<TextBlock Text=\"SuccessStory\" />");

                var service = new ThemeMigrationService(new FakeLogger());
                var result = await service.MigrateThemeAsync(themePath, MigrationMode.Limited);

                Assert.IsTrue(result.Success);
                Assert.IsTrue(Directory.Exists(Path.Combine(themePath, "PlayniteAchievements_backup")));
                Assert.AreEqual("<TextBlock Text=\"PlayniteAchievements\" />", File.ReadAllText(viewPath));
            }
            finally
            {
                DeleteDirectory(themesRoot);
            }
        }

        private const string NativeLegacyView =
            "<ContentControl x:Name=\"PlayniteAchievements_PluginButton\" " +
            "Visibility=\"{Binding LegacyData.HasData}\" />\n" +
            "<ContentControl x:Name=\"SuccessStory_PluginButton\" />\n" +
            "<TextBlock Text=\"\" />";

        [DataTestMethod]
        [DataRow("Desktop", true)]
        [DataRow("Fullscreen", false)]
        public void DiscoverThemes_FlagsNativeLegacyThemeForMigration_OnDesktopOnly(string themeType, bool expectedNeedsMigration)
        {
            var themesRoot = CreateThemesRoot();

            try
            {
                var themePath = Path.Combine(themesRoot, themeType, "NativeLegacy_ab123456");
                Directory.CreateDirectory(themePath);
                File.WriteAllText(Path.Combine(themePath, "theme.yaml"), "Name: Native Legacy\nVersion: 1.0.0\n");
                File.WriteAllText(Path.Combine(themePath, "View.xaml"), NativeLegacyView);

                var service = new ThemeDiscoveryService(new FakeLogger(), new FakePlayniteApi());
                var theme = service.DiscoverThemes(themesRoot).Single();

                Assert.IsTrue(theme.ModernizeOnly);
                Assert.AreEqual(expectedNeedsMigration, theme.NeedsMigration);
                Assert.IsFalse(theme.CouldNotScan);
            }
            finally
            {
                DeleteDirectory(themesRoot);
            }
        }

        [TestMethod]
        public async Task MigrateThemeAsync_FullModernizesNativeLegacyThemeWithoutSuccessStoryRenames()
        {
            var themesRoot = CreateThemesRoot();

            try
            {
                var themePath = Path.Combine(themesRoot, "Desktop", "NativeLegacy_ab123456");
                Directory.CreateDirectory(themePath);
                var viewPath = Path.Combine(themePath, "View.xaml");
                File.WriteAllText(Path.Combine(themePath, "theme.yaml"), "Name: Native Legacy\nVersion: 1.0.0\n");
                File.WriteAllText(viewPath, NativeLegacyView);

                var service = new ThemeMigrationService(new FakeLogger());
                var result = await service.MigrateThemeAsync(themePath, MigrationMode.Full);

                Assert.IsTrue(result.Success);
                Assert.IsTrue(Directory.Exists(Path.Combine(themePath, "PlayniteAchievements_backup")));
                Assert.AreEqual(
                    "<ContentControl x:Name=\"PlayniteAchievements_AchievementButton\" " +
                    "Visibility=\"{Binding ModernTheme.HasAchievements}\" />\n" +
                    "<ContentControl x:Name=\"SuccessStory_PluginButton\" />\n" +
                    "<TextBlock Text=\"\" />",
                    File.ReadAllText(viewPath));

                var revert = await service.RevertThemeAsync(themePath);

                Assert.IsTrue(revert.Success);
                Assert.AreEqual(NativeLegacyView, File.ReadAllText(viewPath));
            }
            finally
            {
                DeleteDirectory(themesRoot);
            }
        }

        [TestMethod]
        public async Task MigrateThemeAsync_LimitedLeavesNativeLegacyThemeUnchanged()
        {
            var themesRoot = CreateThemesRoot();

            try
            {
                var themePath = Path.Combine(themesRoot, "Desktop", "NativeLegacy_ab123456");
                Directory.CreateDirectory(themePath);
                var viewPath = Path.Combine(themePath, "View.xaml");
                File.WriteAllText(Path.Combine(themePath, "theme.yaml"), "Name: Native Legacy\nVersion: 1.0.0\n");
                File.WriteAllText(viewPath, NativeLegacyView);

                var service = new ThemeMigrationService(new FakeLogger());
                var result = await service.MigrateThemeAsync(themePath, MigrationMode.Limited);

                Assert.IsTrue(result.Success);
                Assert.AreEqual(0, result.FilesBackedUp);
                Assert.IsFalse(Directory.Exists(Path.Combine(themePath, "PlayniteAchievements_backup")));
                Assert.AreEqual(NativeLegacyView, File.ReadAllText(viewPath));
            }
            finally
            {
                DeleteDirectory(themesRoot);
            }
        }

        private static string CreateThemesRoot()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievementsTests",
                nameof(ThemeDiscoveryServiceTests),
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(Path.Combine(root, "Desktop"));
            Directory.CreateDirectory(Path.Combine(root, "Fullscreen"));
            return root;
        }

        private static void DeleteDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return;
            }

            Directory.Delete(path, recursive: true);
        }

        private sealed class FakeLogger : ILogger
        {
            public void Debug(string message)
            {
            }

            public void Debug(Exception exception, string message)
            {
            }

            public void Error(string message)
            {
            }

            public void Error(Exception exception, string message)
            {
            }

            public void Info(string message)
            {
            }

            public void Info(Exception exception, string message)
            {
            }

            public void Trace(string message)
            {
            }

            public void Trace(Exception exception, string message)
            {
            }

            public void Warn(string message)
            {
            }

            public void Warn(Exception exception, string message)
            {
            }
        }

        private sealed class FakePlayniteApi : IPlayniteAPI
        {
            public IMainViewAPI MainView => null;

            public IGameDatabaseAPI Database => null;

            public IDialogsFactory Dialogs => null;

            public IPlaynitePathsAPI Paths => null;

            public INotificationsAPI Notifications => null;

            public IPlayniteInfoAPI ApplicationInfo => null;

            public IWebViewFactory WebViews => null;

            public IResourceProvider Resources => null;

            public IUriHandlerAPI UriHandler => null;

            public IPlayniteSettingsAPI ApplicationSettings => null;

            public IAddons Addons => null;

            public IEmulationAPI Emulation => null;

            public string ExpandGameVariables(Game game, string source)
            {
                return source;
            }

            public string ExpandGameVariables(Game game, string source, string fallbackValue)
            {
                return source ?? fallbackValue;
            }

            public GameAction ExpandGameVariables(Game game, GameAction source)
            {
                return source;
            }

            public void StartGame(Guid id)
            {
            }

            public void InstallGame(Guid id)
            {
            }

            public void UninstallGame(Guid id)
            {
            }

            public void AddCustomElementSupport(Plugin plugin, AddCustomElementSupportArgs args)
            {
            }

            public void AddSettingsSupport(Plugin plugin, AddSettingsSupportArgs args)
            {
            }

            public void AddConvertersSupport(Plugin plugin, AddConvertersSupportArgs args)
            {
            }
        }
    }
}
