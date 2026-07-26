using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Tests.Services.UI
{
    [TestClass]
    public class AchievementToastTemplateResolverTests
    {
        [TestMethod]
        public void ResolveTemplate_LoadedThemeResourceWinsOverThemeFile()
        {
            RunOnSta(() =>
            {
                var root = CreateConfigurationRoot();
                try
                {
                    WriteThemeOverride(root, "Desktop", "ThemeA", "file");
                    var appResources = new ResourceDictionary
                    {
                        [AchievementToastTemplateResolver.TemplateKey] = CreateTemplate("app")
                    };

                    var resolver = CreateResolver(root, ApplicationMode.Desktop, desktopTheme: "ThemeA");
                    var template = resolver.ResolveTemplate(appResources);

                    Assert.AreEqual("app", GetMarker(template));
                }
                finally
                {
                    DeleteDirectory(root);
                }
            });
        }

        [TestMethod]
        public void ResolveTemplate_ThemeFileWinsOverDefault()
        {
            RunOnSta(() =>
            {
                var root = CreateConfigurationRoot();
                try
                {
                    WriteThemeOverride(root, "Desktop", "ThemeA", "file");

                    var resolver = CreateResolver(root, ApplicationMode.Desktop, desktopTheme: "ThemeA");
                    var template = resolver.ResolveTemplate(new ResourceDictionary());

                    Assert.AreEqual("file", GetMarker(template));
                }
                finally
                {
                    DeleteDirectory(root);
                }
            });
        }

        [TestMethod]
        public void ResolveTemplate_InvalidThemeFileFallsBackToDefault()
        {
            RunOnSta(() =>
            {
                var root = CreateConfigurationRoot();
                try
                {
                    var overridePath = GetOverridePath(root, "Desktop", "ThemeA");
                    Directory.CreateDirectory(Path.GetDirectoryName(overridePath));
                    File.WriteAllText(overridePath, "<ResourceDictionary>");

                    var resolver = CreateResolver(root, ApplicationMode.Desktop, desktopTheme: "ThemeA");
                    var template = resolver.ResolveTemplate(new ResourceDictionary());

                    Assert.AreEqual("default", GetMarker(template));
                }
                finally
                {
                    DeleteDirectory(root);
                }
            });
        }

        [TestMethod]
        public void ResolveTemplate_Windows1252EncodedThemeFileLoads()
        {
            RunOnSta(() =>
            {
                var root = CreateConfigurationRoot();
                try
                {
                    var overridePath = GetOverridePath(root, "Desktop", "ThemeA");
                    Directory.CreateDirectory(Path.GetDirectoryName(overridePath));
                    var xaml = CreateTemplateXaml("ansi").Replace(
                        "<DataTemplate x:Key",
                        "<!-- Dur\u00e9e d'affichage en secondes -->" +
                        "<DataTemplate x:Key");
                    File.WriteAllBytes(overridePath, Encoding.GetEncoding(1252).GetBytes(xaml));

                    var logger = new CapturingLogger();
                    var resolver = CreateResolver(root, ApplicationMode.Desktop, desktopTheme: "ThemeA", logger: logger);
                    CollectionAssert.Contains(
                        new List<string>(resolver.ResolveActiveThemeOverridePaths(new ResourceDictionary())),
                        overridePath);
                    var template = resolver.ResolveTemplate(new ResourceDictionary());

                    Assert.AreEqual("ansi", GetMarker(template), string.Join(Environment.NewLine, logger.Messages));
                }
                finally
                {
                    DeleteDirectory(root);
                }
            });
        }

        [TestMethod]
        public void ResolveTemplate_UsesFullscreenThemeWhenFullscreen()
        {
            RunOnSta(() =>
            {
                var root = CreateConfigurationRoot();
                try
                {
                    WriteThemeOverride(root, "Desktop", "DesktopTheme", "desktop");
                    WriteThemeOverride(root, "Fullscreen", "FullscreenTheme", "fullscreen");

                    var resolver = CreateResolver(
                        root,
                        ApplicationMode.Fullscreen,
                        desktopTheme: "DesktopTheme",
                        fullscreenTheme: "FullscreenTheme");
                    var template = resolver.ResolveTemplate(new ResourceDictionary());

                    Assert.AreEqual("fullscreen", GetMarker(template));
                }
                finally
                {
                    DeleteDirectory(root);
                }
            });
        }

        [TestMethod]
        public void ResolveTemplate_UsesApplicationPathThemeRootWhenConfigurationPathMissing()
        {
            RunOnSta(() =>
            {
                var root = CreateConfigurationRoot();
                try
                {
                    WriteThemeOverride(root, "Desktop", "ThemeA", "portable");

                    var resolver = CreateResolver(
                        configurationRoot: null,
                        mode: ApplicationMode.Desktop,
                        desktopTheme: "ThemeA",
                        applicationPath: root);
                    var template = resolver.ResolveTemplate(new ResourceDictionary());

                    Assert.AreEqual("portable", GetMarker(template));
                }
                finally
                {
                    DeleteDirectory(root);
                }
            });
        }

        [TestMethod]
        public void ResolveTemplate_MatchesThemeFolderSuffixWhenThemeIdIsBareGuid()
        {
            RunOnSta(() =>
            {
                var root = CreateConfigurationRoot();
                try
                {
                    var themeId = "bb8728bd-ac83-4324-88b1-ee5c586527d1";
                    var folderName = "Aniki_ReMake_" + themeId;
                    WriteThemeOverride(root, "Fullscreen", folderName, "suffix");

                    var resolver = CreateResolver(
                        root,
                        ApplicationMode.Fullscreen,
                        fullscreenTheme: themeId);
                    var template = resolver.ResolveTemplate(new ResourceDictionary());

                    Assert.AreEqual("suffix", GetMarker(template));
                }
                finally
                {
                    DeleteDirectory(root);
                }
            });
        }

        [TestMethod]
        public void ResolveTemplate_UsesLoadedThemeDirectoryWhenSettingsPathHasNoOverride()
        {
            RunOnSta(() =>
            {
                var root = CreateConfigurationRoot();
                try
                {
                    Directory.CreateDirectory(Path.Combine(root, "Themes", "Desktop", "ThemeA"));
                    WriteThemeOverride(root, "Desktop", "FriendlyFolder", "loaded");
                    var loadedResourcePath = Path.Combine(root, "Themes", "Desktop", "FriendlyFolder", "ThemeResources.xaml");
                    File.WriteAllText(loadedResourcePath, CreateEmptyResourceDictionaryXaml());

                    var appResources = new ResourceDictionary();
                    appResources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri(loadedResourcePath, UriKind.Absolute)
                    });

                    var resolver = CreateResolver(root, ApplicationMode.Desktop, desktopTheme: "ThemeA");
                    var template = resolver.ResolveTemplate(appResources);

                    Assert.AreEqual("loaded", GetMarker(template));
                }
                finally
                {
                    DeleteDirectory(root);
                }
            });
        }

        [TestMethod]
        public void ResolveStoryboard_LoadsAnimationFromThemeFile()
        {
            RunOnSta(() =>
            {
                var root = CreateConfigurationRoot();
                try
                {
                    var overridePath = GetOverridePath(root, "Desktop", "ThemeA");
                    Directory.CreateDirectory(Path.GetDirectoryName(overridePath));
                    File.WriteAllText(overridePath, CreateSlideInStoryboardXaml("0:0:0.5"));

                    var resolver = CreateResolver(root, ApplicationMode.Desktop, desktopTheme: "ThemeA");
                    var storyboard = resolver.ResolveStoryboard(
                        AchievementToastTemplateResolver.SlideInStoryboardKey);

                    Assert.IsNotNull(storyboard);
                    Assert.AreEqual(1, storyboard.Children.Count);
                    var animation = storyboard.Children[0] as DoubleAnimation;
                    Assert.IsNotNull(animation);
                    Assert.AreEqual(TimeSpan.FromSeconds(0.5), animation.Duration.TimeSpan);
                }
                finally
                {
                    DeleteDirectory(root);
                }
            });
        }

        [TestMethod]
        public void ResolveActiveThemeOverridePath_MatchesThemeYamlId()
        {
            RunOnSta(() =>
            {
                var root = CreateConfigurationRoot();
                try
                {
                    var themeDirectory = Path.Combine(root, "Themes", "Desktop", "FriendlyFolder");
                    Directory.CreateDirectory(Path.Combine(themeDirectory, "PlayniteAchievements"));
                    File.WriteAllText(Path.Combine(themeDirectory, "theme.yaml"), "Id: Theme-Id\nName: Friendly\n");

                    var resolver = CreateResolver(root, ApplicationMode.Desktop, desktopTheme: "Theme-Id");
                    var path = resolver.ResolveActiveThemeOverridePath();

                    Assert.AreEqual(
                        Path.Combine(themeDirectory, AchievementToastTemplateResolver.ThemeOverrideRelativePath),
                        path);
                }
                finally
                {
                    DeleteDirectory(root);
                }
            });
        }

        [TestMethod]
        public void ResolveFrameTemplate_LoadedThemeResourceWinsOverThemeFile()
        {
            RunOnSta(() =>
            {
                var root = CreateConfigurationRoot();
                try
                {
                    WriteFrameThemeOverride(root, "Desktop", "ThemeA", "frame-file");
                    var appResources = new ResourceDictionary
                    {
                        [AchievementToastTemplateResolver.FrameTemplateKey] = CreateTemplate("frame-app")
                    };

                    var resolver = CreateResolver(root, ApplicationMode.Desktop, desktopTheme: "ThemeA");
                    var template = resolver.ResolveFrameTemplate(appResources);

                    Assert.AreEqual("frame-app", GetMarker(template));
                }
                finally
                {
                    DeleteDirectory(root);
                }
            });
        }

        [TestMethod]
        public void ResolveFrameTemplate_ThemeFileWinsOverDefault()
        {
            RunOnSta(() =>
            {
                var root = CreateConfigurationRoot();
                try
                {
                    WriteFrameThemeOverride(root, "Desktop", "ThemeA", "frame-file");

                    var resolver = CreateResolver(root, ApplicationMode.Desktop, desktopTheme: "ThemeA");
                    var template = resolver.ResolveFrameTemplate(new ResourceDictionary());

                    Assert.AreEqual("frame-file", GetMarker(template));
                }
                finally
                {
                    DeleteDirectory(root);
                }
            });
        }

        [TestMethod]
        public void ResolveFrameTemplate_FallsBackToDefaultWhenNoOverride()
        {
            RunOnSta(() =>
            {
                var root = CreateConfigurationRoot();
                try
                {
                    var resolver = CreateResolver(root, ApplicationMode.Desktop, desktopTheme: "ThemeA");
                    var template = resolver.ResolveFrameTemplate(new ResourceDictionary());

                    Assert.AreEqual("frame-default", GetMarker(template));
                }
                finally
                {
                    DeleteDirectory(root);
                }
            });
        }

        [TestMethod]
        public void ResolveFrameTemplate_IgnoresFrameKeyInToastOverrideFile()
        {
            RunOnSta(() =>
            {
                var root = CreateConfigurationRoot();
                try
                {
                    // The frame key placed inside AchievementToast.xaml must NOT satisfy the frame
                    // lookup: the file-based tier for frames reads ScreenshotFrame.xaml only.
                    var toastOverridePath = GetOverridePath(root, "Desktop", "ThemeA");
                    Directory.CreateDirectory(Path.GetDirectoryName(toastOverridePath));
                    File.WriteAllText(
                        toastOverridePath,
                        CreateTemplateXaml("misplaced").Replace(
                            AchievementToastTemplateResolver.TemplateKey,
                            AchievementToastTemplateResolver.FrameTemplateKey));

                    var resolver = CreateResolver(root, ApplicationMode.Desktop, desktopTheme: "ThemeA");
                    var template = resolver.ResolveFrameTemplate(new ResourceDictionary());

                    Assert.AreEqual("frame-default", GetMarker(template));
                }
                finally
                {
                    DeleteDirectory(root);
                }
            });
        }

        [TestMethod]
        public void ResolveTemplate_ToastAndFrameOverrideFilesResolveIndependently()
        {
            RunOnSta(() =>
            {
                var root = CreateConfigurationRoot();
                try
                {
                    WriteThemeOverride(root, "Desktop", "ThemeA", "toast-file");
                    WriteFrameThemeOverride(root, "Desktop", "ThemeA", "frame-file");

                    var resolver = CreateResolver(root, ApplicationMode.Desktop, desktopTheme: "ThemeA");

                    Assert.AreEqual("toast-file", GetMarker(resolver.ResolveTemplate(new ResourceDictionary())));
                    Assert.AreEqual("frame-file", GetMarker(resolver.ResolveFrameTemplate(new ResourceDictionary())));
                }
                finally
                {
                    DeleteDirectory(root);
                }
            });
        }

        private static AchievementToastTemplateResolver CreateResolver(
            string configurationRoot,
            ApplicationMode mode,
            string desktopTheme = null,
            string fullscreenTheme = null,
            string applicationPath = null,
            ILogger logger = null)
        {
            return new AchievementToastTemplateResolver(
                new FakePlayniteApi(
                    new FakePlayniteInfo(mode),
                    new FakePlayniteSettings(desktopTheme, fullscreenTheme),
                    new FakePlaynitePaths(configurationRoot, applicationPath)),
                logger ?? new FakeLogger(),
                () => CreateTemplate("default"),
                () => CreateTemplate("frame-default"));
        }

        private static DataTemplate CreateTemplate(string marker)
        {
            var template = new DataTemplate();
            template.Resources["Marker"] = marker;
            return template;
        }

        private static string GetMarker(DataTemplate template)
        {
            Assert.IsNotNull(template);
            return template.Resources["Marker"] as string;
        }

        private static void WriteThemeOverride(string configurationRoot, string mode, string themeName, string marker)
        {
            var overridePath = GetOverridePath(configurationRoot, mode, themeName);
            Directory.CreateDirectory(Path.GetDirectoryName(overridePath));
            File.WriteAllText(overridePath, CreateTemplateXaml(marker));
        }

        private static string GetOverridePath(string configurationRoot, string mode, string themeName)
        {
            return Path.Combine(
                configurationRoot,
                "Themes",
                mode,
                themeName,
                AchievementToastTemplateResolver.ThemeOverrideRelativePath);
        }

        private static void WriteFrameThemeOverride(string configurationRoot, string mode, string themeName, string marker)
        {
            var overridePath = Path.Combine(
                configurationRoot,
                "Themes",
                mode,
                themeName,
                AchievementToastTemplateResolver.FrameThemeOverrideRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(overridePath));
            File.WriteAllText(
                overridePath,
                CreateTemplateXaml(marker).Replace(
                    AchievementToastTemplateResolver.TemplateKey,
                    AchievementToastTemplateResolver.FrameTemplateKey));
        }

        private static string CreateTemplateXaml(string marker)
        {
            return
                "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" " +
                "xmlns:sys=\"clr-namespace:System;assembly=mscorlib\">" +
                "<DataTemplate x:Key=\"PlayAch.Template.AchievementToast\">" +
                "<DataTemplate.Resources>" +
                $"<sys:String x:Key=\"Marker\">{marker}</sys:String>" +
                "</DataTemplate.Resources>" +
                "<TextBlock Text=\"Toast\"/>" +
                "</DataTemplate>" +
                "</ResourceDictionary>";
        }

        private static string CreateSlideInStoryboardXaml(string duration)
        {
            return
                "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
                "<Storyboard x:Key=\"PlayAch.Storyboard.ToastSlideIn\">" +
                $"<DoubleAnimation Storyboard.TargetProperty=\"(Window.Top)\" Duration=\"{duration}\"/>" +
                "</Storyboard>" +
                "</ResourceDictionary>";
        }

        private static string CreateEmptyResourceDictionaryXaml()
        {
            return
                "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
                "</ResourceDictionary>";
        }

        private static string CreateConfigurationRoot()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievementsTests",
                nameof(AchievementToastTemplateResolverTests),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static void DeleteDirectory(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }

        private static void RunOnSta(Action action)
        {
            Exception exception = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    exception = ex;
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (exception != null)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }
        }

        private sealed class FakeLogger : ILogger
        {
            public void Debug(string message) { }
            public void Debug(Exception exception, string message) { }
            public void Error(string message) { }
            public void Error(Exception exception, string message) { }
            public void Info(string message) { }
            public void Info(Exception exception, string message) { }
            public void Trace(string message) { }
            public void Trace(Exception exception, string message) { }
            public void Warn(string message) { }
            public void Warn(Exception exception, string message) { }
        }

        private sealed class CapturingLogger : ILogger
        {
            public readonly List<string> Messages = new List<string>();

            public void Debug(string message) => Messages.Add(message);
            public void Debug(Exception exception, string message) => Messages.Add(message + Environment.NewLine + exception);
            public void Error(string message) => Messages.Add(message);
            public void Error(Exception exception, string message) => Messages.Add(message + Environment.NewLine + exception);
            public void Info(string message) => Messages.Add(message);
            public void Info(Exception exception, string message) => Messages.Add(message + Environment.NewLine + exception);
            public void Trace(string message) => Messages.Add(message);
            public void Trace(Exception exception, string message) => Messages.Add(message + Environment.NewLine + exception);
            public void Warn(string message) => Messages.Add(message);
            public void Warn(Exception exception, string message) => Messages.Add(message + Environment.NewLine + exception);
        }

        private sealed class FakePlayniteApi : IPlayniteAPI
        {
            public FakePlayniteApi(
                IPlayniteInfoAPI applicationInfo,
                IPlayniteSettingsAPI applicationSettings,
                IPlaynitePathsAPI paths)
            {
                ApplicationInfo = applicationInfo;
                ApplicationSettings = applicationSettings;
                Paths = paths;
            }

            public IMainViewAPI MainView => null;
            public IGameDatabaseAPI Database => null;
            public IDialogsFactory Dialogs => null;
            public IPlaynitePathsAPI Paths { get; }
            public INotificationsAPI Notifications => null;
            public IPlayniteInfoAPI ApplicationInfo { get; }
            public IWebViewFactory WebViews => null;
            public IResourceProvider Resources => null;
            public IUriHandlerAPI UriHandler => null;
            public IPlayniteSettingsAPI ApplicationSettings { get; }
            public IAddons Addons => null;
            public IEmulationAPI Emulation => null;

            public string ExpandGameVariables(Game game, string source) => source;
            public string ExpandGameVariables(Game game, string source, string fallbackValue) => source ?? fallbackValue;
            public GameAction ExpandGameVariables(Game game, GameAction source) => source;
            public void StartGame(Guid id) { }
            public void InstallGame(Guid id) { }
            public void UninstallGame(Guid id) { }
            public void AddCustomElementSupport(Plugin plugin, AddCustomElementSupportArgs args) { }
            public void AddSettingsSupport(Plugin plugin, AddSettingsSupportArgs args) { }
            public void AddConvertersSupport(Plugin plugin, AddConvertersSupportArgs args) { }
        }

        private sealed class FakePlayniteInfo : IPlayniteInfoAPI
        {
            public FakePlayniteInfo(ApplicationMode mode)
            {
                Mode = mode;
            }

            public Version ApplicationVersion => new Version(10, 0);
            public ApplicationMode Mode { get; }
            public bool IsPortable => false;
            public bool InOfflineMode => false;
            public bool IsDebugBuild => false;
            public bool ThrowAllErrors => false;
        }

        private sealed class FakePlaynitePaths : IPlaynitePathsAPI
        {
            public FakePlaynitePaths(string configurationPath, string applicationPath = null)
            {
                ConfigurationPath = configurationPath;
                ApplicationPath = applicationPath;
            }

            public bool IsPortable => false;
            public string ApplicationPath { get; }
            public string ConfigurationPath { get; }
            public string ExtensionsDataPath => null;
        }

        private sealed class FakePlayniteSettings : IPlayniteSettingsAPI
        {
            public FakePlayniteSettings(string desktopTheme, string fullscreenTheme)
            {
                DesktopTheme = desktopTheme;
                FullscreenTheme = fullscreenTheme;
            }

            public int Version => 0;
            public int GridItemWidthRatio => 0;
            public int GridItemHeightRatio => 0;
            public bool FirstTimeWizardComplete => false;
            public bool DisableHwAcceleration => false;
            public bool AsyncImageLoading => false;
            public bool DownloadMetadataOnImport => false;
            public bool StartInFullscreen => false;
            public string DatabasePath => null;
            public bool MinimizeToTray => false;
            public bool CloseToTray => false;
            public bool EnableTray => false;
            public string Language => "en_US";
            public bool UpdateLibStartup => false;
            public string DesktopTheme { get; }
            public string FullscreenTheme { get; }
            public bool StartMinimized => false;
            public bool StartOnBoot => false;
            public bool ForcePlayTimeSync => false;
            public PlaytimeImportMode PlaytimeImportMode => PlaytimeImportMode.Never;
            public string FontFamilyName => null;
            public bool DiscordPresenceEnabled => false;
            public AgeRatingOrg AgeRatingOrgPriority => AgeRatingOrg.ESRB;
            public bool SidebarVisible => false;
            public Dock SidebarPosition => Dock.Left;
            public IFullscreenSettingsAPI Fullscreen => null;
            public ICompletionStatusSettignsApi CompletionStatus => null;

            public bool GetGameExcludedFromImport(string gameId, Guid libraryId) => false;
        }
    }
}
