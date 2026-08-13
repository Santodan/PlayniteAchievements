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

            // The background is painted by an ImageBrush, which is not a FrameworkElement, so
            // AsyncImage uses the authored value verbatim and it is the cache key. Every other image
            // on the card is an Image element, whose key also folds in its laid-out size and the
            // monitor scale, so those sizes are hints and are deliberately not asserted here.
            var authored = Regex
                .Matches(
                    template,
                    @"ToastBackgroundImagePath\}""\s+helpers:AsyncImage\.DecodePixel=""(\d+)""")
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

            // The slide reads progress from frame timestamps, so starting it on the card's first frame
            // does not slow the slide, it skips most of it. The wait has to stay between these two.
            Assert.IsTrue(
                shown < warm && warm < slide,
                "The toast must wait for composed frames after being placed and before sliding in.");
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
                "RunPhysicalSlide(window, rx, startY, ry, _activeSlideInEase, _activeSlideInMs",
                "RunPhysicalSlide(window, rx, ry, endY, _activeSlideOutEase, _activeSlideOutMs"
            })
            {
                Assert.IsTrue(
                    service.Contains(perSlideCall),
                    "The slides must consume the pre-resolved timing: " + perSlideCall);
            }
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
