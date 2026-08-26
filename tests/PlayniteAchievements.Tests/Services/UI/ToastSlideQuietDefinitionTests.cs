using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Tests.Services.UI
{
    // Guards the quiet-slide invariants: while a notification slide storyboard runs, everything
    // else that animates or invalidates must stand down so the slide composes at monitor refresh.
    // ToastNotificationService is not linked into this project, so the wiring is asserted against
    // its source, matching ToastVisualPrimingDefinitionTests.
    [TestClass]
    public class ToastSlideQuietDefinitionTests
    {
        [TestMethod]
        public void Slide_EngagesTheQuietScope_BeforeTheStoryboardBegins()
        {
            var service = ReadToastService();

            var engage = service.IndexOf(
                "_activeSlideQuiet = new SlideQuietScope(host);", StringComparison.Ordinal);
            var completed = service.IndexOf(
                "storyboard.Completed += (s, e) => DisposeSlideQuiet();", StringComparison.Ordinal);
            var begin = service.IndexOf(
                "storyboard.Begin(host, isControllable: true);", StringComparison.Ordinal);

            Assert.IsTrue(engage >= 0, "The slide no longer engages the quiet scope.");
            Assert.IsTrue(completed >= 0, "The slide's natural end no longer releases the quiet scope.");
            Assert.IsTrue(begin >= 0, "The storyboard begin call was renamed.");

            // Completed subscribers attached after Begin never reach the running clock, and a
            // scope engaged after Begin leaves the slide's first frames contended.
            Assert.IsTrue(
                engage < begin && completed < begin,
                "The quiet scope and its Completed release must be wired before the storyboard begins.");
        }

        [TestMethod]
        public void StopActiveSlide_IsTheQuietScopesBackstop()
        {
            var service = ReadToastService();

            // StopActiveSlide runs at the settled snap, the wave's finally, and the top of every
            // slide, so releasing there first is what makes a leak impossible.
            Assert.IsTrue(
                Regex.IsMatch(service, @"private void StopActiveSlide\(\)\s*\{\s*DisposeSlideQuiet\(\);"),
                "StopActiveSlide must release the quiet scope as its first statement.");
        }

        [TestMethod]
        public void RayDriver_StandsDownWhileTheGateIsEngaged()
        {
            var driver = File.ReadAllText(FindRepoFile("source", "Views", "Helpers", "RayAnimationDriver.cs"));

            var gate = driver.IndexOf("RenderQuietGate.IsEngaged", StringComparison.Ordinal);
            var catchUp = driver.IndexOf("_nextDueMs +=", StringComparison.Ordinal);

            Assert.IsTrue(gate >= 0, "The ray driver no longer reads the quiet gate.");
            Assert.IsTrue(catchUp >= 0, "The ray driver's due-time catch-up loop was renamed.");

            // Skipping before the due-time math makes the quiet span read as a stall, which the
            // catch-up loop already resumes on cadence; skipping after it would bunch frames.
            Assert.IsTrue(
                gate < catchUp,
                "The gate check must precede the due-time catch-up so resumption stays on cadence.");
        }

        [TestMethod]
        public void GlowPulse_EffectTarget_RunsOnAControllableClock()
        {
            var pulse = File.ReadAllText(FindRepoFile("source", "Views", "Helpers", "RarityGlowPulse.cs"));

            Assert.IsTrue(
                pulse.Contains("ApplyAnimationClock(DropShadowEffect.OpacityProperty"),
                "The effect-target pulse no longer runs on a clock; it cannot be paused for the slide.");
            Assert.IsFalse(
                pulse.Contains("effect.BeginAnimation(DropShadowEffect.OpacityProperty, animation)"),
                "BeginAnimation creates a clock with no reachable controller; the slide cannot pause it.");
        }

        [TestMethod]
        public void CountdownBar_IsDetachedBetweenTheHoldAndTheSlideOut()
        {
            var service = ReadToastService();

            var hold = service.IndexOf(
                "await HoldWaveAsync(remainingMs).ConfigureAwait(true);", StringComparison.Ordinal);
            var stop = service.IndexOf("StopCountdownBars(window);", StringComparison.Ordinal);
            var slideOut = service.IndexOf("SlideOutPhysical(window);", StringComparison.Ordinal);

            Assert.IsTrue(hold >= 0, "The wave hold call was renamed.");
            Assert.IsTrue(stop >= 0, "The countdown bar is no longer detached before slide-out.");
            Assert.IsTrue(slideOut >= 0, "The slide-out call was renamed.");

            // The bar's clock nominally completes as the hold ends, but that is timing skew, not
            // a guarantee; a clock still producing values during the slide-out costs it frames.
            Assert.IsTrue(
                hold < stop && stop < slideOut,
                "The countdown bar must be detached after the hold and before the slide-out.");
        }

        private static string ReadToastService()
        {
            return File.ReadAllText(FindRepoFile("source", "Services", "UI", "ToastNotificationService.cs"));
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
