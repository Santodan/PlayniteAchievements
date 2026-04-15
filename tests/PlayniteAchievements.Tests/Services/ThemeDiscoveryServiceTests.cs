using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Services.ThemeMigration;
using System;
using System.IO;
using System.Linq;

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
