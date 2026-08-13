using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Tests.TestInfrastructure;
using System.Windows;
using System.Windows.Markup;

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

        [TestMethod]
        public void ToastBackground_UsesOneImageHostAndOneRenderedBrush()
        {
            var path = FindRepoFile("source", "Resources", "DefaultTemplates", "AchievementToast.xaml");
            var document = XDocument.Parse(File.ReadAllText(path));
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            XNamespace helpers = "clr-namespace:PlayniteAchievements.Views.Helpers;assembly=PlayniteAchievements";

            var sourceHost = document
                .Descendants(presentation + "Image")
                .Single(element => (string)element.Attribute(x + "Name") == "ToastBackgroundSourceHost");
            Assert.AreEqual(
                "{Binding ToastBackgroundRenderSource}",
                (string)sourceHost.Attribute(helpers + "AsyncImage.Uri"));

            var sharedBrushes = document
                .Descendants(presentation + "ImageBrush")
                .Where(element =>
                    string.Equals(
                        (string)element.Attribute("ImageSource"),
                        "{Binding Source, ElementName=ToastBackgroundSourceHost}",
                        StringComparison.Ordinal))
                .ToList();

            // One brush per layer, both fed by the single host above. The backing copy inside the effect
            // layer is what lets the shadow / glow wrap the image's own alpha instead of a rectangle; the
            // copy in the effect-free content layer is what the user actually sees, because anything
            // inside an effect is rasterized to an intermediate first and the card carries a fractional
            // DPI LayoutTransform that would resample it. Sharing the host is what makes this one decode
            // and one animation clock regardless of how many brushes paint it -- which is the invariant
            // the no-independent-decoder assertion below pins.
            Assert.AreEqual(
                2, sharedBrushes.Count, "The background must be painted once per layer, from one decoder.");
            Assert.IsFalse(
                document.Descendants(presentation + "ImageBrush")
                    .Any(element => element.Attribute(helpers + "AsyncImage.Uri") != null),
                "The bundled toast must not run an independent decoder on either ImageBrush.");
        }

        [TestMethod]
        public void NotificationPreview_RefreshesOnlyWhenAsyncImagePublishesANewSource()
        {
            // DependencyPropertyDescriptor sees WriteableBitmap dirty notifications as Source
            // changes. Using it here would rebuild the entire preview on every GIF frame.
            var source = File.ReadAllText(FindRepoFile(
                "source",
                "Views",
                "Settings",
                "General",
                "NotificationAppearanceSection.xaml.cs"));

            StringAssert.Contains(source, "AsyncImage.AddSourceReadyHandler(");
            StringAssert.Contains(source, "AsyncImage.RemoveSourceReadyHandler(");
            Assert.IsFalse(
                source.Contains("DependencyPropertyDescriptor.FromProperty(\r\n                Image.SourceProperty") ||
                source.Contains("DependencyPropertyDescriptor.FromProperty(\n                Image.SourceProperty"),
                "The preview must not subscribe to mutable Image.Source sub-property changes.");

            var asyncImageSource = File.ReadAllText(FindRepoFile(
                "source",
                "Views",
                "Helpers",
                "AsyncImage.cs"));
            var normalizedAsyncImageSource = asyncImageSource.Replace("\r\n", "\n");
            StringAssert.Contains(
                normalizedAsyncImageSource,
                "if (!sourceIdentityChanged && GetUri(d) is ImageSource)",
                "A shared mutable source must not be reapplied on every GIF frame.");
            StringAssert.Contains(
                normalizedAsyncImageSource,
                "await TryStartNativeGifAsync(\n                        image,\n                        uriString,\n                        bmp,",
                "The native decoder must receive the static bitmap as fallback without first displaying it.");
        }

        [TestMethod]
        public void BundledToastTemplate_LoadsThroughTheRuntimeXamlParser()
        {
            LocalizationAssemblyInitializer.RunOnSta(() =>
            {
                var path = FindRepoFile("source", "Resources", "DefaultTemplates", "AchievementToast.xaml");
                using (var stream = File.OpenRead(path))
                {
                    var context = new ParserContext
                    {
                        BaseUri = new Uri("pack://application:,,,/", UriKind.Absolute)
                    };
                    var dictionary = (ResourceDictionary)XamlReader.Load(stream, context);
                    Assert.IsNotNull(dictionary["PlayAch.Template.AchievementToast"]);
                }
            });
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
