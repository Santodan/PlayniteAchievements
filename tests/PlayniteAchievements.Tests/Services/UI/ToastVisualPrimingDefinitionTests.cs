using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Tests.Services.UI
{
    // Guards the two invariants that make the toast slide smooth and that nothing else can catch:
    // the card must have painted before the slide starts timing itself, and the background image's
    // prime must request the size the template actually asks for. ToastNotificationService is not
    // linked into this project, so both are asserted against its source.
    [TestClass]
    public class ToastVisualPrimingDefinitionTests
    {
        private const string ToastServiceRelativePath = "ToastNotificationService.cs";

        [TestMethod]
        public void BackgroundPrime_RequestsTheDecodeSizeTheTemplateAsksFor()
        {
            var service = ReadToastService();
            var template = File.ReadAllText(FindRepoFile(
                "source", "Resources", "DefaultTemplates", "AchievementToast.xaml"));

            var primeMatch = Regex.Match(service, @"PrimeBackgroundDecodePixel\s*=\s*(\d+)\s*;");
            Assert.IsTrue(primeMatch.Success, "PrimeBackgroundDecodePixel is no longer declared.");

            // The background's authored decode size is the cache key however the template carries it:
            // on an ImageBrush, a Freezable with no size to infer from, or on a zero-size host Image
            // feeding one, where the inferred size is 0 and the explicit value wins. Every other image
            // on the card is a laid-out Image element, whose key folds in its own size and the monitor
            // scale, so those sizes are hints and are deliberately not asserted here.
            //
            // Matched on the ToastBackground* prefix rather than one property name. Which source the
            // template binds is free to change -- ToastBackgroundImagePath, a render-source variant for
            // animated backgrounds -- without touching the invariant under test, which is only that the
            // authored decode size equals the size the prime asks for. Pinning the property name here
            // would turn an unrelated rename into a failing test.
            var authored = Regex
                .Matches(
                    template,
                    @"Binding\s+ToastBackground\w*\}""\s+helpers:AsyncImage\.DecodePixel=""(\d+)""")
                .Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .ToList();

            Assert.AreNotEqual(
                0,
                authored.Count,
                "The toast template no longer binds a background image with an authored decode size.");
            foreach (var value in authored)
            {
                Assert.AreEqual(
                    primeMatch.Groups[1].Value,
                    value,
                    "The background prime must request the template's decode size or it warms a " +
                    "different cache key and the image still lands mid-slide.");
            }
        }

        [TestMethod]
        public void SlideStarts_OnlyAfterTheCardHasPainted()
        {
            var service = ReadToastService();

            var shown = service.IndexOf("PlaceWindow(window, \"shown\")", StringComparison.Ordinal);
            var warm = service.IndexOf("WaitForComposedFramesAsync(WarmFrameCount", StringComparison.Ordinal);
            var slide = service.IndexOf("SlideInPhysical(window, reveal: visible)", StringComparison.Ordinal);

            Assert.IsTrue(shown >= 0, "The settled placement call was renamed.");
            Assert.IsTrue(warm >= 0, "The warm-frame wait before the slide is gone.");
            Assert.IsTrue(slide >= 0, "The slide-in call was renamed.");

            // The slide's clock is wall time, so starting it on the card's first frame does not slow the
            // slide, it skips most of it. The wait has to stay between these two.
            Assert.IsTrue(
                shown < warm && warm < slide,
                "The toast must wait for composed frames after being placed and before sliding in.");
        }

        [TestMethod]
        public void SlideTravel_IsReservedBeforeTheSettledPlacement()
        {
            var service = ReadToastService();

            var reserve = service.IndexOf("ReserveSlideTravel(window, items)", StringComparison.Ordinal);
            var shown = service.IndexOf("PlaceWindow(window, \"shown\")", StringComparison.Ordinal);

            Assert.IsTrue(reserve >= 0, "The slide-travel reservation is gone; the card is clipped mid-slide.");
            Assert.IsTrue(shown >= 0, "The settled placement call was renamed.");

            // Reserving the travel changes the window's size, and the settled placement is what puts the
            // now-larger window where the card lands on the corner. Reversed, the toast is placed for a
            // size it no longer has.
            Assert.IsTrue(
                reserve < shown,
                "Slide travel must be reserved before the settled placement, not after.");
        }

        [TestMethod]
        public void Placement_MeasuresTheCardRatherThanTheWindow()
        {
            var service = ReadToastService();
            var placer = File.ReadAllText(FindRepoFile("source", "Services", "UI", "ToastWindowPlacer.cs"));

            // The window is larger than the card by the reserved slide travel, and that room is meant to
            // hang past the anchor edge. Sizing or clamping the placement on the window would put the
            // padding at the corner and push the card inward by the whole travel distance.
            Assert.IsTrue(
                placer.Contains("TryMeasureCardPhysical"),
                "The placer no longer measures the card; placement would size on the padded window.");
            Assert.IsTrue(
                service.Contains("SlideOffsetDipX(), SlideOffsetDipY()"),
                "Placement must subtract the live slide offset, or a mid-slide pass chases the animation.");
        }

        [TestMethod]
        public void SlideTiming_IsResolvedOncePerWaveRatherThanPerSlide()
        {
            var service = ReadToastService();

            // Resolving the themeable storyboards reaches the filesystem and the resource
            // dictionaries; doing it inside a slide put that on the frame the slide subscribed on.
            Assert.IsTrue(
                service.Contains("ResolveWaveSlideTiming();"),
                "The per-wave slide timing resolve is gone.");
            Assert.AreEqual(
                1,
                CountOccurrences(service, "ResolveWaveSlideTiming();"),
                "Slide timing should be resolved once per wave.");
            foreach (var perSlideCall in new[]
            {
                "_activeSlideInStoryboard, from, 0d, DefaultSlideInEase, _activeSlideInMs",
                "_activeSlideOutStoryboard, 0d, to, DefaultSlideOutEase, _activeSlideOutMs"
            })
            {
                Assert.IsTrue(
                    service.Contains(perSlideCall),
                    "The slides must consume the pre-resolved storyboard: " + perSlideCall);
            }
        }

        [TestMethod]
        public void BundledSlideStoryboards_TargetThePathTheServiceAnimates()
        {
            var service = ReadToastService();
            var resources = File.ReadAllText(FindRepoFile("source", "Resources", "NotificationResources.xaml"));

            var pathMatch = Regex.Match(
                service, @"private const string SlideTargetPath\s*=\s*""([^""]+)""");
            Assert.IsTrue(pathMatch.Success, "SlideTargetPath is no longer declared.");
            var slidePath = pathMatch.Groups[1].Value;

            // The bundled storyboards now run as authored, so a target property that does not match what
            // the service animates is not a mismatch it can detect: the child is not recognised as the
            // slide, so no travel is filled in, and the path fails to resolve against the host's
            // transform group. Both failures are silent — the notification simply appears without
            // animating. That shipped once; this is the guard.
            var slideStoryboards = Regex.Matches(
                resources,
                @"<Storyboard x:Key=""PlayAch\.Storyboard\.ToastSlide(?:In|Out)"">(.*?)</Storyboard>",
                RegexOptions.Singleline);

            Assert.AreEqual(2, slideStoryboards.Count, "The bundled slide storyboards were renamed.");
            foreach (Match storyboard in slideStoryboards)
            {
                StringAssert.Contains(
                    storyboard.Groups[1].Value,
                    "Storyboard.TargetProperty=\"" + slidePath + "\"",
                    "A bundled slide storyboard does not target the path the service animates, so it " +
                    "would run without moving the card.");
            }
        }

        [TestMethod]
        public void ThemeStoryboard_ThatDoesNotMoveTheCard_LeavesItAtRest()
        {
            var service = ReadToastService();

            // A theme may replace the slide with a fade or a scale, which animates nothing positional.
            // The card must then stay at its resting corner: parking it at the slide's start would
            // strand it in the reserved travel room with nothing to move it back.
            Assert.IsTrue(
                service.Contains("var restDip = travels ? toDip : 0d;"),
                "The non-travelling case no longer rests the card at its corner.");
            Assert.IsTrue(
                service.Contains("transform.Y = travels ? fromDip : restDip;"),
                "A non-travelling animation must not park the card at the slide's start position.");
        }

        private static string ReadToastService()
        {
            return File.ReadAllText(FindRepoFile("source", "Services", "UI", ToastServiceRelativePath));
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
