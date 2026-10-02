using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Which opacity the glow pulse drives: the element's own Opacity (for a cached glow layer
    /// behind a sharp icon) or its DropShadowEffect's Opacity (for a glow applied directly as an
    /// Effect, e.g. the toast card border, where the glow can't be isolated onto its own layer).
    /// </summary>
    public enum RarityGlowPulseTarget
    {
        Element,
        Effect
    }

    /// <summary>
    /// Attached behavior that fades a rarity/completion glow in and out using the user-configured
    /// floor, ceiling, and speed (<see cref="PersistedSettings.RarityGlowPulseMinOpacity"/>,
    /// <see cref="PersistedSettings.RarityGlowPulseMaxOpacity"/>,
    /// <see cref="PersistedSettings.RarityGlowPulseDurationSeconds"/>). A style trigger sets
    /// <c>IsActive=True</c> while the glow should pulse; the behavior builds the looping animation
    /// from the live settings and restarts it when they change, so tuning updates on-screen glows
    /// immediately. It stops (reverting to full opacity) when IsActive returns false or the
    /// element unloads, and resumes on reload, so virtualized grid rows keep pulsing after
    /// scrolling off and back. Replaces static XAML storyboards, whose From/To/Duration cannot be
    /// bound.
    /// </summary>
    public static class RarityGlowPulse
    {
        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.RegisterAttached(
                "IsActive", typeof(bool), typeof(RarityGlowPulse),
                new PropertyMetadata(false, OnIsActiveChanged));

        public static void SetIsActive(DependencyObject element, bool value) =>
            element.SetValue(IsActiveProperty, value);

        public static bool GetIsActive(DependencyObject element) =>
            (bool)element.GetValue(IsActiveProperty);

        public static readonly DependencyProperty TargetProperty =
            DependencyProperty.RegisterAttached(
                "Target", typeof(RarityGlowPulseTarget), typeof(RarityGlowPulse),
                new PropertyMetadata(RarityGlowPulseTarget.Element));

        public static void SetTarget(DependencyObject element, RarityGlowPulseTarget value) =>
            element.SetValue(TargetProperty, value);

        public static RarityGlowPulseTarget GetTarget(DependencyObject element) =>
            (RarityGlowPulseTarget)element.GetValue(TargetProperty);

        // When true (default), the pulse phase-locks to the process-wide epoch so recreated
        // elements (grid recycling, settings mockup rebuilds) resume mid-cycle. Set false on
        // surfaces that should pulse from the cycle start each time they are built — the toast
        // templates opt out so every wave's glow (and its screenshots/clips) starts at the same
        // deterministic point.
        public static readonly DependencyProperty PhaseLockProperty =
            DependencyProperty.RegisterAttached(
                "PhaseLock", typeof(bool), typeof(RarityGlowPulse),
                new PropertyMetadata(true));

        public static void SetPhaseLock(DependencyObject element, bool value) =>
            element.SetValue(PhaseLockProperty, value);

        public static bool GetPhaseLock(DependencyObject element) =>
            (bool)element.GetValue(PhaseLockProperty);

        // Stores the per-element settings-changed handler so it can be detached. The subscription
        // goes through PropertyChangedEventManager (weak): recycled item containers do not
        // reliably raise Unloaded (see RayAnimationDriver), so a strong PropertyChanged handler
        // would let the app-lifetime PersistedSettings instance root every dead tile. The element
        // keeps the delegate alive via this slot; the settings object holds it weakly.
        private static readonly DependencyProperty SettingsHandlerProperty =
            DependencyProperty.RegisterAttached(
                "SettingsHandler", typeof(EventHandler<PropertyChangedEventArgs>), typeof(RarityGlowPulse),
                new PropertyMetadata(null));

        // The PersistedSettings instance SettingsHandler was added on. Settings edits can replace
        // Settings.Persisted, so removal must target the instance that was subscribed, not a
        // re-resolved current one.
        private static readonly DependencyProperty SettingsSourceProperty =
            DependencyProperty.RegisterAttached(
                "SettingsSource", typeof(PersistedSettings), typeof(RarityGlowPulse),
                new PropertyMetadata(null));

        // Stores the Effect-target pulse's root clock so PauseUnder/ResumeUnder can find it on a
        // visual-tree walk. BeginAnimation would create a clock with no reachable controller;
        // pausing the notification card's glow for the slide's span requires the clock handle.
        private static readonly DependencyProperty EffectClockProperty =
            DependencyProperty.RegisterAttached(
                "EffectClock", typeof(AnimationClock), typeof(RarityGlowPulse),
                new PropertyMetadata(null));

        private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is FrameworkElement element))
            {
                return;
            }

            if ((bool)e.NewValue)
            {
                element.Loaded += OnElementLoaded;
                element.Unloaded += OnElementUnloaded;

                // The first render outranks Loaded (Render beats Loaded in dispatcher
                // priority), so a recreated element would paint one frame at its static
                // opacity before the animation attaches. Pre-set the pulse value now so that
                // frame already matches: the epoch phase for phase-locked elements, the cycle
                // peak for opt-outs (whose animation starts at the peak and fades down, so a
                // freshly revealed toast shows its glow at full strength).
                if (GetTarget(element) == RarityGlowPulseTarget.Element)
                {
                    var persisted = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted;
                    element.Opacity = GetPhaseLock(element)
                        ? CurrentPulseOpacity(persisted)
                        : ResolvePulseParams(persisted).Max;
                }

                if (element.IsLoaded)
                {
                    Activate(element);
                }
            }
            else
            {
                element.Loaded -= OnElementLoaded;
                element.Unloaded -= OnElementUnloaded;
                Deactivate(element);
            }
        }

        private static void OnElementLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                Activate(element);
            }
        }

        private static void OnElementUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                Deactivate(element);
            }
        }

        private static void Activate(FrameworkElement element)
        {
            var persisted = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted;
            ApplyAnimation(element, persisted);

            if (persisted == null)
            {
                return;
            }

            if (element.GetValue(SettingsSourceProperty) is PersistedSettings previous)
            {
                if (ReferenceEquals(previous, persisted) &&
                    element.GetValue(SettingsHandlerProperty) != null)
                {
                    // Loaded re-fired without an Unloaded (recycled/re-parented container);
                    // the existing subscription already targets the current instance.
                    return;
                }

                // The Persisted instance was swapped since this element subscribed.
                DetachSettingsHandler(element);
            }

            EventHandler<PropertyChangedEventArgs> handler = (s, args) =>
            {
                if (args.PropertyName == nameof(PersistedSettings.RarityGlowPulseMinOpacity) ||
                    args.PropertyName == nameof(PersistedSettings.RarityGlowPulseMaxOpacity) ||
                    args.PropertyName == nameof(PersistedSettings.RarityGlowPulseSpeed))
                {
                    // Retune the shared clock once; every element then re-attaches to it.
                    InvalidateSharedClock();
                    ApplyAnimation(element, persisted);
                }
            };

            PropertyChangedEventManager.AddHandler(persisted, handler, string.Empty);
            element.SetValue(SettingsHandlerProperty, handler);
            element.SetValue(SettingsSourceProperty, persisted);
        }

        private static void Deactivate(FrameworkElement element)
        {
            DetachSettingsHandler(element);
            StopAnimation(element);
        }

        private static void DetachSettingsHandler(FrameworkElement element)
        {
            if (element.GetValue(SettingsHandlerProperty) is EventHandler<PropertyChangedEventArgs> handler &&
                element.GetValue(SettingsSourceProperty) is PersistedSettings source)
            {
                PropertyChangedEventManager.RemoveHandler(source, handler, string.Empty);
            }

            element.SetValue(SettingsHandlerProperty, null);
            element.SetValue(SettingsSourceProperty, null);
        }

        private static (double Min, double Max, double Seconds) ResolvePulseParams(PersistedSettings persisted)
        {
            var min = Clamp(persisted?.RarityGlowPulseMinOpacity ?? 0.6, 0.0, 1.0);
            var max = Clamp(persisted?.RarityGlowPulseMaxOpacity ?? 1.0, 0.0, 1.0);
            if (max < min)
            {
                var swap = max;
                max = min;
                min = swap;
            }

            // Speed is a normalized 0-1 value; map it to a half-cycle duration where 0 is slow
            // and 1 is fast (SlowSeconds down to FastSeconds).
            const double slowSeconds = 10.0;
            const double fastSeconds = 0.1;
            var speed = Clamp(persisted?.RarityGlowPulseSpeed ?? 0.5, 0.0, 1.0);
            var seconds = slowSeconds - speed * (slowSeconds - fastSeconds);

            return (min, max, seconds);
        }

        /// <summary>
        /// The pulse opacity at the current point of the shared epoch's cycle, replicating the
        /// animation's sine-eased auto-reversed sweep.
        /// </summary>
        private static double CurrentPulseOpacity(PersistedSettings persisted)
        {
            var (min, max, seconds) = ResolvePulseParams(persisted);
            var halfMilliseconds = seconds * 1000.0;
            var t = GlowAnimationClock.ElapsedMilliseconds % (halfMilliseconds * 2.0);
            var progress = t < halfMilliseconds
                ? t / halfMilliseconds
                : 2.0 - (t / halfMilliseconds);
            var eased = (1.0 - Math.Cos(Math.PI * progress)) / 2.0;
            return min + ((max - min) * eased);
        }

        // One clock drives every phase-locked pulse. Each BeginAnimation would otherwise create an
        // independent clock that the timing manager ticks separately every frame, so a dense
        // surface (the showcase icon mosaic shows up to 64 glowing icons at once) paid for dozens
        // of redundant clocks. Phase-locked pulses all want the identical curve at the identical
        // phase, so they can share one. Opt-outs (toasts, which must start at the cycle peak) keep
        // their own clock.
        private static AnimationClock _sharedElementClock;
        private static string _sharedClockKey;

        private static AnimationClock GetSharedClock(DoubleAnimation animation, double cycleMilliseconds)
        {
            var key = animation.From.ToString() + "|" + animation.To.ToString() + "|" +
                animation.Duration.ToString();
            if (_sharedElementClock == null || !string.Equals(_sharedClockKey, key, StringComparison.Ordinal))
            {
                animation.BeginTime = GlowAnimationClock.PhaseLockBeginTime(cycleMilliseconds);
                _sharedElementClock = animation.CreateClock();
                _sharedClockKey = key;
            }

            return _sharedElementClock;
        }

        /// <summary>
        /// Drops the shared clock so the next activation rebuilds it from current settings.
        /// </summary>
        private static void InvalidateSharedClock()
        {
            _sharedElementClock = null;
            _sharedClockKey = null;
        }

        private static void ApplyAnimation(FrameworkElement element, PersistedSettings persisted)
        {
            var (min, max, seconds) = ResolvePulseParams(persisted);

            // Phase-locked to the shared epoch (full cycle = fade in + auto-reversed fade
            // out) so a recreated element resumes the pulse mid-cycle instead of restarting
            // it. The BeginTime is stamped immediately before each BeginAnimation call —
            // computing it earlier would bake the deferral delay in as a phase error.
            var cycleMilliseconds = seconds * 2000.0;
            var animation = new DoubleAnimation
            {
                From = min,
                To = max,
                Duration = new Duration(TimeSpan.FromSeconds(seconds)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };

            if (GetTarget(element) == RarityGlowPulseTarget.Element && GetPhaseLock(element))
            {
                element.ApplyAnimationClock(
                    UIElement.OpacityProperty,
                    GetSharedClock(animation, cycleMilliseconds));
                return;
            }

            if (GetTarget(element) == RarityGlowPulseTarget.Effect)
            {
                // Defer to after the current trigger pass: the element's default Effect is itself
                // a DropShadowEffect (the neutral card shadow), so applying inline could animate
                // the effect that the border-glow trigger is about to replace. By Loaded priority
                // element.Effect is the settled glow.
                element.Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        if (GetIsActive(element) && element.Effect is DropShadowEffect effect)
                        {
                            animation.BeginTime = GetPhaseLock(element)
                                ? GlowAnimationClock.PhaseLockBeginTime(cycleMilliseconds)
                                : GlowAnimationClock.PeakStartBeginTime(cycleMilliseconds);

                            // A root clock rather than BeginAnimation: the controller is what
                            // lets PauseUnder/ResumeUnder freeze the pulse for the slide's span.
                            var clock = animation.CreateClock();
                            effect.ApplyAnimationClock(DropShadowEffect.OpacityProperty, clock);
                            element.SetValue(EffectClockProperty, clock);
                        }
                    }),
                    DispatcherPriority.Loaded);
            }
            else
            {
                animation.BeginTime = GetPhaseLock(element)
                    ? GlowAnimationClock.PhaseLockBeginTime(cycleMilliseconds)
                    : GlowAnimationClock.PeakStartBeginTime(cycleMilliseconds);
                element.BeginAnimation(UIElement.OpacityProperty, animation);
            }
        }

        private static void StopAnimation(FrameworkElement element)
        {
            if (GetTarget(element) == RarityGlowPulseTarget.Effect)
            {
                if (element.Effect is DropShadowEffect effect)
                {
                    effect.ApplyAnimationClock(DropShadowEffect.OpacityProperty, null);
                }

                element.SetValue(EffectClockProperty, null);
            }
            else
            {
                // Detaches whether the element was driven by the shared clock or its own.
                element.ApplyAnimationClock(UIElement.OpacityProperty, null);

                // Drop the phase pre-set local value (see OnIsActiveChanged) so the element
                // returns to its style/default opacity when the pulse is off.
                element.ClearValue(UIElement.OpacityProperty);
            }
        }

        /// <summary>
        /// Pauses every Effect-target pulse clock in <paramref name="root"/>'s visual tree, for
        /// the notification slide's span: the pulse invalidates a software-blurred
        /// DropShadowEffect subtree every frame, which competes with the slide for the frame
        /// budget. Scoped to a subtree rather than global on purpose — pausing a phase-locked
        /// clock would desynchronize it from the shared epoch, so only the notification card
        /// (whose templates opt out of phase lock) should ever be paused. UI thread only.
        /// </summary>
        public static void PauseUnder(DependencyObject root)
        {
            ForEachEffectClock(root, clock =>
            {
                if (!clock.IsPaused)
                {
                    clock.Controller?.Pause();
                }
            });
        }

        /// <summary>Resumes the clocks <see cref="PauseUnder"/> paused. UI thread only.</summary>
        public static void ResumeUnder(DependencyObject root)
        {
            ForEachEffectClock(root, clock =>
            {
                if (clock.IsPaused)
                {
                    clock.Controller?.Resume();
                }
            });
        }

        private static void ForEachEffectClock(DependencyObject root, Action<AnimationClock> action)
        {
            if (root == null)
            {
                return;
            }

            if (root.GetValue(EffectClockProperty) is AnimationClock clock)
            {
                action(clock);
            }

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                ForEachEffectClock(VisualTreeHelper.GetChild(root, i), action);
            }
        }

        private static double Clamp(double value, double min, double max) =>
            value < min ? min : (value > max ? max : value);
    }
}
