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

            // A prefix, not the full call: the scope's arguments are free to change without
            // touching the invariant under test, which is only where the engage happens.
            var engage = service.IndexOf(
                "_activeSlideQuiet = new SlideQuietScope(host", StringComparison.Ordinal);
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

        [TestMethod]
        public void CardPixels_ArePrimedBetweenTheShadowCaptureAndTheSlide()
        {
            var service = ReadToastService();

            var shadows = service.IndexOf(
                "CaptureWaveShadowLayers(trackRecorder, window, cardItems);", StringComparison.Ordinal);
            var prime = service.IndexOf(
                "PrimeWaveCardPixels(trackRecorder, window, cardItems);", StringComparison.Ordinal);
            var slide = service.IndexOf(
                "SlideInPhysical(window, reveal: visible)", StringComparison.Ordinal);

            Assert.IsTrue(shadows >= 0, "The pre-slide shadow capture call was renamed.");
            Assert.IsTrue(prime >= 0, "The pre-slide pixel prime is gone; the first card render lands mid-slide.");
            Assert.IsTrue(slide >= 0, "The slide-in call was renamed.");

            // The prime must land after the warm frames (alongside the shadow capture) and before
            // the slide clock starts, or its cost moves back into the slide-in span.
            Assert.IsTrue(
                shadows < prime && prime < slide,
                "The pixel prime must run after the shadow capture and before the slide-in.");
        }

        [TestMethod]
        public void PrimedPixels_AreSubmittedAheadOfTheFrozenSlidePath_WithTheFadeDiscard()
        {
            var service = ReadToastService();

            var primedSubmit = service.IndexOf(
                "if (scratch.PrimedPixels != null)", StringComparison.Ordinal);
            var frozen = service.IndexOf(
                "var slideFrozen = _runningSlideStoryboard != null && scratch.HasPixelFrame;",
                StringComparison.Ordinal);

            Assert.IsTrue(primedSubmit >= 0, "The sampler no longer consumes the primed pixel frame.");
            Assert.IsTrue(frozen >= 0, "The frozen-slide sample path was renamed.");

            // Behind the frozen branch the primed frame would never be submitted: a frozen tick
            // records position only, and the worker drops pixel-less ticks with no frame under
            // them, which erases the whole slide-in from the track.
            Assert.IsTrue(
                primedSubmit < frozen,
                "The primed submit must precede the frozen-slide branch or the slide-in vanishes from clips.");

            // A fade theme's first tick must rasterize live rather than submit opacity-1 pixels
            // primed before the fade began.
            Assert.IsTrue(
                service.Contains("recorder.ReturnRentedBuffer(vm, primed);"),
                "The mid-fade discard path is gone; a fade theme's clip would pop in fully opaque.");
        }

        [TestMethod]
        public void ProgressApplication_DefersWhileASlideRuns_WithABound()
        {
            var monitor = File.ReadAllText(FindRepoFile("source", "Services", "InGameAchievementMonitor.cs"));

            // Unbounded, a leaked gate would stall unlock detection; unguarded, the ~550 ms apply
            // cadence lands its UI fan-out inside slides as the maxGap spikes the log shows.
            Assert.IsTrue(
                Regex.IsMatch(monitor, @"RenderQuietGate\.WhenClearAsync\(maxDeferMs:\s*\d+\)"),
                "Progress application no longer defers (with a numeric bound) while a slide runs.");
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
