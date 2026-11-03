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
        public void ForkCacheIsOptInAndChangingTheOptionDoesNotAffectUpstreamDiscovery()
        {
            var root = CreateThemesRoot();
            try
            {
                var theme = Path.Combine(root, "Desktop", "ForkScopeTest");
                Directory.CreateDirectory(theme);
                File.WriteAllText(Path.Combine(theme, "theme.yaml"), "Name: Scope Test\nVersion: 1.0\n");
                File.WriteAllText(Path.Combine(theme, "View.xaml"),
                    "<TextBlock Text=\"SuccessStory\" /> <!-- " + Guid.NewGuid() + " -->");
                var enabled = false;
                var fork = new ThemeDiscoveryService(new FakeLogger(), new FakePlayniteApi(), () => enabled);
                var upstream = new ThemeDiscoveryService(new FakeLogger(), new FakePlayniteApi());
                var misses = ThemeDiscoveryService.ContentScanMisses;
                Assert.IsTrue(upstream.DiscoverThemes(root).Single().NeedsMigration);
                Assert.IsTrue(fork.DiscoverThemes(root).Single().NeedsMigration);
                Assert.AreEqual(misses, ThemeDiscoveryService.ContentScanMisses);
                enabled = true;
                Assert.IsTrue(fork.DiscoverThemes(root).Single().NeedsMigration);
                Assert.IsTrue(ThemeDiscoveryService.ContentScanMisses > misses);
                misses = ThemeDiscoveryService.ContentScanMisses;
                Assert.IsTrue(fork.DiscoverThemes(root).Single().NeedsMigration);
                Assert.AreEqual(misses, ThemeDiscoveryService.ContentScanMisses);
                enabled = false;
                File.AppendAllText(Path.Combine(theme, "View.xaml"), "<!-- changed -->");
                Assert.IsTrue(fork.DiscoverThemes(root).Single().NeedsMigration);
                Assert.IsTrue(upstream.DiscoverThemes(root).Single().NeedsMigration);
                Assert.AreEqual(misses, ThemeDiscoveryService.ContentScanMisses,
                    "Disabled fork scans and upstream scans must bypass the cache entirely.");
            }
            finally { DeleteDirectory(root); }
        }

        [TestMethod]
        public void DiscoverThemes_CachedScanDetectsSameLengthEditAndRevertAcrossInstances()
        {
            var themesRoot = CreateThemesRoot();
            try
            {
                var themePath = Path.Combine(themesRoot, "Desktop", "ContentCacheTest");
                Directory.CreateDirectory(themePath);
                File.WriteAllText(Path.Combine(themePath, "theme.yaml"), "Name: Cache Test\nVersion: 1.0\n");
                var viewPath = Path.Combine(themePath, "View.xaml");
                var original = "<TextBlock Text=\"SuccessStory\" />";
                var edited = original.Replace("SuccessStory", "PlainContent");
                File.WriteAllText(viewPath, original);
                var timestamp = File.GetLastWriteTimeUtc(viewPath);
                Assert.IsTrue(new ThemeDiscoveryService(new FakeLogger(), new FakePlayniteApi(), () => true)
                    .DiscoverThemes(themesRoot).Single().NeedsMigration);

                File.WriteAllText(viewPath, edited);
                File.SetLastWriteTimeUtc(viewPath, timestamp);
                Assert.AreEqual(original.Length, edited.Length);
                Assert.IsFalse(new ThemeDiscoveryService(new FakeLogger(), new FakePlayniteApi(), () => true)
                    .DiscoverThemes(themesRoot).Single().NeedsMigration);

                File.WriteAllText(viewPath, original);
                File.SetLastWriteTimeUtc(viewPath, timestamp);
                Assert.IsTrue(new ThemeDiscoveryService(new FakeLogger(), new FakePlayniteApi(), () => true)
                    .DiscoverThemes(themesRoot).Single().NeedsMigration);
            }
            finally { DeleteDirectory(themesRoot); }
        }

        [TestMethod]
        public void AnalyzeThemeContent_RepeatedLargeScanReusesResults()
        {
            var prefix = Guid.NewGuid().ToString();
            var contents = Enumerable.Range(0, 700)
                .Select(i => "<Grid /> <!-- " + prefix + i + " -->").ToList();
            foreach (var content in contents) ThemeDiscoveryService.AnalyzeThemeContent(content);
            var misses = ThemeDiscoveryService.ContentScanMisses;
            foreach (var content in contents) ThemeDiscoveryService.AnalyzeThemeContent(content);
            Assert.AreEqual(misses, ThemeDiscoveryService.ContentScanMisses,
                "Revisiting a theme library larger than the old 512-file limit must reuse its scan results.");
        }

        [TestMethod]
        public void AnalyzeThemeContent_EvictionPreservesMixedThemeDetection()
        {
            var mixed = "PlayniteAchievementsSantodan SuccessStory";
            Assert.AreEqual((true, true), ThemeDiscoveryService.AnalyzeThemeContent(mixed));
            for (var i = 0; i < 4100; i++)
            {
                Assert.AreEqual((false, false), ThemeDiscoveryService.AnalyzeThemeContent("<Grid /> <!-- " + i + " -->"));
            }
            Assert.AreEqual((true, true), ThemeDiscoveryService.AnalyzeThemeContent(mixed));
        }

        [TestMethod]
        public async Task MigrateThemeAsync_RepairsAlreadyMigratedPs5RanksAndRestoresBackup()
        {
            var themesRoot=CreateThemesRoot();
            try
            {
                var themePath=Path.Combine(themesRoot,"Fullscreen","PS5RankTest");
                Directory.CreateDirectory(themePath);
                File.WriteAllText(Path.Combine(themePath,"theme.yaml"),"Name: PS5 Rank Test\nVersion: 1.0\n");
                var path=Path.Combine(themePath,"Main.xaml");
                var original=PlayniteAchievements.Tests.Services.Ps5RankMigrationTests.Badges();
                File.WriteAllText(path,original);
                var discovery=new ThemeDiscoveryService(new FakeLogger(),new FakePlayniteApi());
                Assert.IsTrue(discovery.DiscoverThemes(themesRoot).Single().NeedsMigration);
                var service=new ThemeMigrationService(new FakeLogger());
                var result=await service.MigrateThemeAsync(themePath,MigrationMode.Limited);
                Assert.IsTrue(result.Success,result.Message);
                var migrated=File.ReadAllText(path);
                StringAssert.Contains(migrated,"Value=\"Silver5\"");StringAssert.Contains(migrated,"Value=\"Master1\"");
                Assert.IsTrue((await service.MigrateThemeAsync(themePath,MigrationMode.Limited)).Success);
                Assert.AreEqual(migrated,File.ReadAllText(path));
                Assert.IsTrue((await service.RevertThemeAsync(themePath)).Success);
                Assert.AreEqual(original,File.ReadAllText(path));
            }
            finally { DeleteDirectory(themesRoot); }
        }

        [DataTestMethod]
        [DataRow(MigrationMode.Limited, true, false)]
        [DataRow(MigrationMode.Full, false, false)]
        [DataRow(MigrationMode.Full, true, true)]
        [DataRow(MigrationMode.Full, true, false)]
        [DataRow(MigrationMode.Custom, true, false)]
        public async Task MigrateThemeAsync_AppliesPersistedOptions(
            MigrationMode mode, bool scrollable, bool highlight)
        {
            var themesRoot = CreateThemesRoot();
            try
            {
                var themePath = Path.Combine(themesRoot, "Desktop", "OptionsTest");
                Directory.CreateDirectory(themePath);
                var viewPath = Path.Combine(themePath, "View.xaml");
                File.WriteAllText(viewPath,
                    "<StackPanel><ContentControl x:Name='SuccessStory_PluginButton'/>" +
                    "<ContentControl x:Name='SuccessStory_PluginCompactUnlocked'/></StackPanel>");
                var settings = new PlayniteAchievements.Models.Settings.PersistedSettings
                {
                    ThemeMigrationMode = mode,
                    ThemeMigrationUseScrollableAchievements = scrollable,
                    ThemeMigrationHighlightLatestAchievement = highlight,
                    ThemeMigrationControlOptions = new System.Collections.Generic.Dictionary<string, bool>
                    {
                        ["PluginButton"] = false,
                        ["PluginCompactUnlocked"] = true
                    }
                };
                var result = await new ThemeMigrationService(new FakeLogger()).MigrateThemeAsync(
                    themePath, settings.ThemeMigrationMode, CustomMigrationSelection.FromSettings(settings));
                Assert.IsTrue(result.Success, result.Message);
                var migrated = File.ReadAllText(viewPath);
                StringAssert.Contains(migrated, mode == MigrationMode.Full
                    ? "PlayniteAchievements_AchievementButton" : "PlayniteAchievements_PluginButton");
                var expectedUnlocked = mode == MigrationMode.Limited || !scrollable
                    ? "PluginCompactUnlocked"
                    : highlight ? "AchievementCompactUnlockedList" : "AchievementCompactUnlockedScrollableList";
                StringAssert.Contains(migrated, "PlayniteAchievements_" + expectedUnlocked);
            }
            finally
            {
                DeleteDirectory(themesRoot);
            }
        }

        [TestMethod]
        public async Task MigrateThemeAsync_PreservesDistinctSolarisNamesAndReferences()
        {
            var themesRoot = CreateThemesRoot();
            try
            {
                var themePath = Path.Combine(themesRoot, "Fullscreen", "Solaris");
                Directory.CreateDirectory(themePath);
                var viewPath = Path.Combine(themePath, "View.xaml");
                const string original = @"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
<StackPanel>
<Border x:Name='SuccessStory'/><Border x:Name='PlayniteAchievements'/>
<CheckBox Name='ToggleSuccessStory'/><CheckBox Name='TogglePlayniteAchievements'/>
<TextBlock Text='{Binding Tag, ElementName=SuccessStory}'/>
<ContentControl x:Name='SuccessStory_PluginList'/>
</StackPanel>
<ControlTemplate.Triggers><Trigger Property='Tag' Value='Test'>
<Setter TargetName='SuccessStory' Property='Visibility' Value='Collapsed'/>
</Trigger></ControlTemplate.Triggers></ControlTemplate>";
                File.WriteAllText(viewPath, original);
                var service = new ThemeMigrationService(new FakeLogger());
                Assert.IsTrue((await service.MigrateThemeAsync(themePath)).Success);
                var migrated = File.ReadAllText(viewPath);
                Assert.AreEqual(original.Replace("SuccessStory_PluginList", "PlayniteAchievements_PluginList"), migrated);
                Assert.IsTrue((await service.MigrateThemeAsync(themePath)).Success);
                Assert.AreEqual(migrated, File.ReadAllText(viewPath));
            }
            finally
            {
                DeleteDirectory(themesRoot);
            }
        }

        [TestMethod]
        public void DiscoverThemes_DoesNotFlagThemeForMigration_WhenThemeContainsNativeSantodanSupport()
        {
            var themesRoot = CreateThemesRoot();

            try
            {
                var themePath = Path.Combine(themesRoot, "Fullscreen", "Aniki-ReMake_ab123456");
                Directory.CreateDirectory(themePath);
                File.WriteAllText(Path.Combine(themePath, "theme.yaml"), "Name: Aniki ReMake\nVersion: 2.5.5\n");
                File.WriteAllText(
                    Path.Combine(themePath, "View.xaml"),
                    "<TextBlock Text=\"PlayniteAchievementsSantodan\" />");

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

        [TestMethod]
        public async Task MigrateThemeAsync_AddsLocalToSolarisDynamicAndPresetLists()
        {
            var themesRoot = CreateThemesRoot();

            try
            {
                var themePath = Path.Combine(themesRoot, "Fullscreen", "Solaris_ab123456");
                Directory.CreateDirectory(themePath);
                var viewPath = Path.Combine(themePath, "Main.xaml");
                File.WriteAllText(Path.Combine(themePath, "theme.yaml"), "Name: Solaris\nVersion: 1.0.0\n");
                File.WriteAllText(
                    viewPath,
                    string.Join(
                        "\n",
                        "<ButtonEx Content=\"Hoyoverse\" CommandParameter=\"Hoyoverse\"",
                        "    Command=\"{PluginSettings Plugin=PlayniteAchievements, Path=FilterDynamicGameSummariesByProviderCommand}\">",
                        "    <ButtonEx.Triggers />",
                        "</ButtonEx>",
                        "<ComboBoxItem Content=\"Hoyoverse\" Tag=\"HoyoverseGames\" />",
                        "<!-- List//HoyoverseGames -->",
                        "<ListView x:Name=\"HoyoverseGames\" ItemsSource=\"{PluginSettings Plugin=PlayniteAchievements, Path=HoyoverseGames}\"",
                        "    Visibility=\"Collapsed\" />",
                        "<Setter Property=\"Visibility\" Value=\"Collapsed\" TargetName=\"HoyoverseGames\" />",
                        "<MultiDataTrigger>",
                        "    <MultiDataTrigger.Conditions>",
                        "        <Condition Value=\"HoyoverseGames\" />",
                        "    </MultiDataTrigger.Conditions>",
                        "    <Setter Property=\"Text\" Value=\"Hoyoverse\" />",
                        "    <Setter Property=\"Visibility\" Value=\"Visible\" TargetName=\"HoyoverseGames\" />",
                        "</MultiDataTrigger>"));

                var service = new ThemeMigrationService(new FakeLogger());
                var result = await service.MigrateThemeAsync(themePath, MigrationMode.Limited);
                var migrated = File.ReadAllText(viewPath);

                Assert.IsTrue(result.Success);
                StringAssert.Contains(migrated, "CommandParameter=\"Local\"");
                StringAssert.Contains(migrated, "Content=\"Local\" Tag=\"LocalGames\"");
                StringAssert.Contains(migrated, "x:Name=\"LocalGames\"");
                StringAssert.Contains(migrated, "Path=LocalGames");
                StringAssert.Contains(migrated, "Value=\"LocalGames\"");
                StringAssert.Contains(migrated, "TargetName=\"LocalGames\"");

                var secondResult = await service.MigrateThemeAsync(themePath, MigrationMode.Limited);
                var migratedAgain = File.ReadAllText(viewPath);

                Assert.IsTrue(secondResult.Success);
                Assert.AreEqual(migrated, migratedAgain);
                Assert.AreEqual(1, CountOccurrences(migratedAgain, "CommandParameter=\"Local\""));
                Assert.AreEqual(1, CountOccurrences(migratedAgain, "Tag=\"LocalGames\""));
                Assert.AreEqual(1, CountOccurrences(migratedAgain, "x:Name=\"LocalGames\""));
                Assert.AreEqual(1, CountOccurrences(migratedAgain, "Value=\"LocalGames\""));
            }
            finally
            {
                DeleteDirectory(themesRoot);
            }
        }

        [TestMethod]
        public async Task MigrateThemeAsync_PreservesDistinctResourceKeysAndCrossFileReferences()
        {
            var themesRoot = CreateThemesRoot();
            try
            {
                var themePath = Path.Combine(themesRoot, "Fullscreen", "Solaris_resources");
                Directory.CreateDirectory(themePath);
                var mediaPath = Path.Combine(themePath, "Media.xaml");
                File.WriteAllText(mediaPath,
                    "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
                    "<SolidColorBrush x:Key=\"SuccessStoryIcon\" Color=\"Red\"/>" +
                    "<SolidColorBrush x:Key=\"PlayniteAchievementsIcon\" Color=\"Blue\"/>" +
                    "<SolidColorBrush x:Key='SSHelper_Icon' Color='Green'/></ResourceDictionary>");
                var viewPath = Path.Combine(themePath, "Main.xaml");
                var view = "<Border Background=\"{StaticResource SuccessStoryIcon}\" " +
                    "BorderBrush=\"{DynamicResource ResourceKey=SSHelper_Icon}\" " +
                    "Tag=\"{StaticResource 'SuccessStoryIcon'}\"/>";
                File.WriteAllText(viewPath, view);
                var service = new ThemeMigrationService(new FakeLogger());
                Assert.IsTrue((await service.MigrateThemeAsync(themePath)).Success);
                var media = File.ReadAllText(mediaPath);
                var document = System.Xml.Linq.XDocument.Parse(media);
                var keys = document.Root.Elements().Select(element =>
                    (string)element.Attribute(System.Xml.Linq.XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))).ToArray();
                CollectionAssert.AreEqual(new[] { "SuccessStoryIcon", "PlayniteAchievementsIcon", "SSHelper_Icon" }, keys);
                Assert.AreEqual(view, File.ReadAllText(viewPath));
                Assert.IsTrue((await service.MigrateThemeAsync(themePath)).Success);
                Assert.AreEqual(media, File.ReadAllText(mediaPath));
            }
            finally
            {
                DeleteDirectory(themesRoot);
            }
        }

        private static int CountOccurrences(string content, string value)
        {
            var count = 0;
            var index = 0;
            while ((index = content.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
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
