using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Views.Helpers;
using System;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace PlayniteAchievements.Tests.Views
{
    [TestClass]
    [DoNotParallelize]
    public class RarityGlowPulseTests
    {
        [ThreadStatic] private static StackPanel _root;
        private static void Apply(FrameworkElement element) => typeof(RarityGlowPulse)
            .GetMethod("ApplyAnimation", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { element, new PersistedSettings { RarityGlowPulseSpeed = 0.9 } });

        private static Border Pulse(bool shared = false, bool effect = false)
        {
            var element = new Border();
            RarityGlowPulse.SetPhaseLock(element, shared);
            if (effect)
            {
                element.Effect = new DropShadowEffect();
                RarityGlowPulse.SetTarget(element, RarityGlowPulseTarget.Effect);
            }
            RarityGlowPulse.SetIsActive(element, true);
            _root.Children.Add(element);
            Pump(30);
            Apply(element);
            return element;
        }

        private static double Opacity(Border element) => element.Effect is DropShadowEffect effect
            ? effect.Opacity : element.Opacity;

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void BothPulseTargetsFreezeAndResume(bool effect) => Sta(() =>
        {
            var element = Pulse(effect: effect);
            Pump(130);
            RarityGlowPulse.PauseUnder(element);
            Pump(70); // Clock-controller operations take effect on the next timing tick.
            var frozen = Opacity(element);
            Pump(200);
            Assert.AreEqual(frozen, Opacity(element), 0.0000001);
            RarityGlowPulse.ResumeUnder(element);
            Pump(200);
            Assert.IsTrue(Math.Abs(frozen - Opacity(element)) > 0.001);
            RarityGlowPulse.SetIsActive(element, false);
        });

        [TestMethod]
        public void SharedPulseFreezesOnlyThisConsumerAndRejoinsTheLivePhase() => Sta(() =>
        {
            var toast = Pulse(shared: true);
            var libraryIcon = Pulse(shared: true);
            Apply(toast); // Both consumers now use the same retuned settings/clock.
            Pump(150);
            RarityGlowPulse.PauseUnder(toast);
            var frozen = toast.Opacity;
            var outside = libraryIcon.Opacity;
            Pump(200);
            Assert.AreEqual(frozen, toast.Opacity, 0.0000001);
            Assert.IsTrue(Math.Abs(outside - libraryIcon.Opacity) > 0.001);
            RarityGlowPulse.ResumeUnder(toast);
            Pump(50);
            Assert.AreEqual(libraryIcon.Opacity, toast.Opacity, 0.0000001);
            RarityGlowPulse.SetIsActive(toast, false);
            RarityGlowPulse.SetIsActive(libraryIcon, false);
        });

        [TestMethod]
        public void DeferredEffectClockHonorsAnAlreadyPausedSlide() => Sta(() =>
        {
            var element = Pulse(effect: true);
            RarityGlowPulse.PauseUnder(element);
            Pump(100);
            var frozen = Opacity(element);
            Pump(180);
            Assert.AreEqual(frozen, Opacity(element), 0.0000001);
            RarityGlowPulse.ResumeUnder(element);
            Pump(200);
            Assert.IsTrue(Math.Abs(frozen - Opacity(element)) > 0.001);
            RarityGlowPulse.SetIsActive(element, false);
        });

        [DataTestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void RetuningWhilePausedStaysFrozenAndDeactivationDoesNotResurrectThePulse(bool shared) => Sta(() =>
        {
            var element = Pulse(shared: shared);
            Pump(100);
            RarityGlowPulse.PauseUnder(element);
            Apply(element);
            Pump(70);
            var frozen = element.Opacity;
            Pump(150);
            Assert.AreEqual(frozen, element.Opacity, 0.0000001);
            RarityGlowPulse.SetIsActive(element, false);
            RarityGlowPulse.ResumeUnder(element);
            Pump(100);
            Assert.AreEqual(1.0, element.Opacity);
            Assert.IsFalse(element.HasAnimatedProperties);
        });

        [TestMethod]
        public void EffectTargetSetAfterActivationLeavesTheElementOpaque() => Sta(() =>
        {
            // The toast template's style trigger can turn the pulse on before the element's own
            // Target and PhaseLock attributes apply, so the activation pre-set sees the defaults:
            // an Element target, phase-locked, which pre-sets the pulse's current opacity.
            var element = new Border();
            RarityGlowPulse.SetIsActive(element, true);
            element.Effect = new DropShadowEffect();
            RarityGlowPulse.SetTarget(element, RarityGlowPulseTarget.Effect);
            RarityGlowPulse.SetPhaseLock(element, false);
            _root.Children.Add(element);
            Pump(60);

            Assert.AreEqual(1.0, element.Opacity, "an Effect target's own opacity must not keep the pre-set");
            Assert.AreEqual(DependencyPropertyHelper.GetValueSource(element, UIElement.OpacityProperty).BaseValueSource,
                BaseValueSource.Default);
            RarityGlowPulse.SetIsActive(element, false);
        });

        private static void Pump(int milliseconds)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (s, e) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        private static void Sta(Action action)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                Window window = null;
                try
                {
                    _root = new StackPanel();
                    window = new Window { Width = 16, Height = 16, Left = -10000, Top = -10000,
                        ShowInTaskbar = false, ShowActivated = false, Content = _root };
                    window.Show();
                    typeof(RarityGlowPulse).GetMethod("InvalidateSharedClock", BindingFlags.Static | BindingFlags.NonPublic)
                        .Invoke(null, null);
                    action();
                }
                catch (Exception exception) { failure = exception; }
                finally { window?.Close(); _root = null; Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "The animation dispatcher did not finish.");
            if (failure != null) throw new AssertFailedException(failure.ToString());
        }
    }
}
