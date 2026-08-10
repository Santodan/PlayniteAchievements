using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.Views
{
    /// <summary>
    /// Guards the findings the rays glow already cost this codebase once, and the wiring that cannot
    /// fail loudly: the two notification templates are excluded from the XAML compile and parsed at
    /// runtime, so a typo in them survives a green build.
    /// </summary>
    [TestClass]
    public class RayGlowRenderDefinitionTests
    {
        [TestMethod]
        public void RarityRayBurst_IsNeitherCachedNorEffected()
        {
            // A layer that moves must not be bitmap-cached: WPF re-rasterizes a cache whenever the
            // element changes, so caching this cost a full re-rasterization per row per frame and was
            // what made a populated grid lag.
            var source = File.ReadAllText(FindRepoFile("source", "Views", "Controls", "RarityRayBurst.cs"));

            Assert.IsFalse(
                source.Contains("CacheMode ="),
                "the ray burst must not set CacheMode; a moving layer that is cached re-rasterizes every frame");
            Assert.IsFalse(
                source.Contains("Effect ="),
                "the ray burst must not carry a bitmap effect");
            Assert.IsFalse(
                source.Contains("DesiredFrameRate"),
                "Timeline.DesiredFrameRate does not throttle this and can cost the whole composition tick");
        }

        [TestMethod]
        public void RarityRayBurst_StillReportsNoDesiredSize()
        {
            // The subject has to be what establishes the cell. An Image measuring to its source's
            // natural size was once enough to inflate a 28px icon cell and its whole row.
            var source = File.ReadAllText(FindRepoFile("source", "Views", "Controls", "RarityRayBurst.cs"));

            StringAssert.Contains(source, "protected override Size MeasureOverride");
            StringAssert.Contains(source, "var empty = new Size(0, 0);");
            StringAssert.Contains(source, "return empty;");
        }

        [TestMethod]
        public void RayAnimationDriver_LeavesTheCompositionTickWhenNothingWantsIt()
        {
            // An animation attached to rows that draw nothing was the other half of the lag.
            var source = File.ReadAllText(FindRepoFile("source", "Views", "Helpers", "RayAnimationDriver.cs"));

            StringAssert.Contains(source, "CompositionTarget.Rendering -=");
            StringAssert.Contains(source, "WantsRayFrames");
        }

        [TestMethod]
        public void DefaultTemplates_RemainWellFormedAndPointAtTheirArt()
        {
            // These two are excluded from the XAML compile and loaded through XamlReader at runtime, so
            // nothing else catches a malformed edit until a notification fires.
            foreach (var name in new[] { "AchievementToast.xaml", "ScreenshotFrame.xaml" })
            {
                var path = FindRepoFile("source", "Resources", "DefaultTemplates", name);
                var xaml = File.ReadAllText(path);

                try
                {
                    XDocument.Parse(xaml);
                }
                catch (Exception ex)
                {
                    Assert.Fail($"{name} is not well-formed XML: {ex.Message}");
                }

                StringAssert.Contains(
                    xaml,
                    "SubjectUri=",
                    $"{name} must tell the ray burst which art to trace, or it falls back to a plain rectangle");
            }
        }

        [TestMethod]
        public void EveryRayBurstCallSiteNamesItsSubject()
        {
            var callSites = new[]
            {
                Path.Combine("source", "Resources", "AchievementCellTemplates.xaml"),
                Path.Combine("source", "Views", "Controls", "GameSummariesGridControl.xaml"),
                Path.Combine("source", "Views", "Controls", "AchievementCompactItemControl.xaml"),
                Path.Combine("source", "Views", "ThemeIntegration", "Legacy", "Controls", "AchievementImage.xaml"),
                Path.Combine("source", "Resources", "DefaultTemplates", "AchievementToast.xaml"),
                Path.Combine("source", "Resources", "DefaultTemplates", "ScreenshotFrame.xaml")
            };

            foreach (var relative in callSites)
            {
                var xaml = File.ReadAllText(FindRepoFile(relative.Split(Path.DirectorySeparatorChar)));

                StringAssert.Contains(xaml, "RarityRayBurst", relative);

                // Three legitimate spellings: a plain attribute, a property element for a MultiBinding,
                // and a style setter where the art depends on a trigger.
                Assert.IsTrue(
                    xaml.Contains("SubjectUri=")
                        || xaml.Contains("RarityRayBurst.SubjectUri")
                        || xaml.Contains("Property=\"SubjectUri\""),
                    $"{relative} places a ray burst without telling it what to trace");
            }
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

            Assert.Fail("Could not find " + Path.Combine(parts));
            return null;
        }
    }
}
