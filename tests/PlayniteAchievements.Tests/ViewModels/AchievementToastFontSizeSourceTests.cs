using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels;
using System;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Tests.ViewModels
{
    /// <summary>
    /// The notification card is a fixed-geometry overlay, so its default text sizes must not be
    /// derived from the running Playnite theme: fullscreen themes declare much larger font
    /// constants than desktop ones (FontSizeSmall 18 vs 12), which sized the same card differently
    /// per mode and left the body text larger than the achievement title. The test host nulls
    /// Application.Current, so a value assertion alone cannot catch a reintroduced resource
    /// lookup -- the source contract below is what pins it.
    /// </summary>
    [TestClass]
    public class AchievementToastFontSizeSourceTests
    {
        [TestMethod]
        public void ToastFontSizeDefaults_NeverResolveThroughApplicationResources()
        {
            var code = File.ReadAllText(FindRepoFile("source", "ViewModels", "AchievementToastViewModel.cs"));

            StringAssert.Contains(code, "public double ToastHeaderFontSize => _style.Toast.HeaderFontSize ?? DefaultToastCaptionFontSize;");
            StringAssert.Contains(code, "public double ToastRarityFontSize => _style.Toast.RarityFontSize ?? DefaultToastCaptionFontSize;");
            StringAssert.Contains(code, "public double ToastTitleFontSize => _style.Toast.TitleFontSize ?? DefaultToastTitleFontSize;");
            StringAssert.Contains(code, "(isFrame ? FrameBodyFontFallback : DefaultToastCaptionFontSize);");
            StringAssert.Contains(code, "(isFrame ? FrameGameCategoryFontFallback : DefaultToastCaptionFontSize);");

            // The helper that performed the theme lookup is gone; nothing may reintroduce it.
            Assert.IsFalse(
                code.Contains("ResolveFontSizeResource"),
                "Toast font sizes must not be resolved from Application resources.");
            Assert.IsFalse(
                code.Contains("PlayAch.FontSize."),
                "Toast font sizes must not reference the theme-derived PlayAch.FontSize tokens.");
        }

        [TestMethod]
        public void UnsetToastSizes_RestOnTheConstantsTheSettingsEditorShows()
        {
            var viewModel = new AchievementToastViewModel(
                new AchievementUnlockedEventArgs(),
                new PersistedSettings());

            Assert.AreEqual(11d, viewModel.ToastHeaderFontSize);
            Assert.AreEqual(11d, viewModel.ToastRarityFontSize);
            Assert.AreEqual(16d, viewModel.ToastTitleFontSize);

            // The title must stay the largest line on the card; the fullscreen theme's missing
            // FontSizeLarge used to invert that.
            Assert.IsTrue(
                viewModel.ToastLines.Where(line => !(line is ToastTitleLine)).All(
                    line => line.FontSize < viewModel.ToastTitleFontSize),
                "Every non-title toast line must be smaller than the title.");
        }

        [TestMethod]
        public void ExplicitToastSizes_StillWin()
        {
            var viewModel = new AchievementToastViewModel(
                new AchievementUnlockedEventArgs(),
                new PersistedSettings
                {
                    NotificationStyle = new NotificationStyleSettings
                    {
                        Toast = new NotificationSurfaceStyle
                        {
                            HeaderFontSize = 21,
                            TitleFontSize = 27,
                            RarityFontSize = 23
                        }
                    }
                });

            Assert.AreEqual(21d, viewModel.ToastHeaderFontSize);
            Assert.AreEqual(27d, viewModel.ToastTitleFontSize);
            Assert.AreEqual(23d, viewModel.ToastRarityFontSize);
        }

        private static string FindRepoFile(params string[] parts)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                if (File.Exists(path))
                {
                    return path;
                }

                directory = directory.Parent;
            }

            throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar.ToString(), parts));
        }
    }
}
