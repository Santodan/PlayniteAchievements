using System;
using System.Collections.Generic;
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
    ///
    /// Phase-locked element pulses (every library surface) are not WPF animations: the shared glow
    /// tick (<see cref="RayAnimationDriver"/>, 30 per second) writes the epoch's current value to
    /// each of them, so the pulse neither composes at the display's refresh rate nor runs while
    /// <see cref="GlowAnimationGate"/> is suspended. Phase-lock opt-outs (the unlock notification
    /// card) keep their own clocks.
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

        private static readonly DependencyProperty ElementPulseProperty =
            DependencyProperty.RegisterAttached(
                "ElementPulse", typeof(ElementPulse), typeof(RarityGlowPulse), new PropertyMetadata(null));

        private static readonly DependencyProperty IsPausedProperty =
            DependencyProperty.RegisterAttached(
                "IsPaused", typeof(bool), typeof(RarityGlowPulse), new PropertyMetadata(false));

        // Marks the local Opacity OnIsActiveChanged pre-sets, so it can be taken back. A template
        // can apply the trigger that sets IsActive before its own Target attribute: the pre-set
        // then lands on an element that turns out to be an Effect target, whose pulse never drives
        // the element's Opacity, and the stray value (the pulse floor, for a phase-locked preview)
        // would leave the whole glow layer, card surface included, translucent.
        private static readonly DependencyProperty HasPresetOpacityProperty =
            DependencyProperty.RegisterAttached(
                "HasPresetOpacity", typeof(bool), typeof(RarityGlowPulse), new PropertyMetadata(false));

        // An opt-out element pulse's own clock, so a slide can pause and resume it.
        private sealed class ElementPulse
        {
            internal AnimationClock Clock;

            internal void Pause() => Clock.Controller?.Pause();

            internal void Resume() => Clock.Controller?.Resume();
        }

        private static void ApplyElementClock(FrameworkElement element, AnimationClock clock)
        {
            var pulse = new ElementPulse { Clock = clock };
            element.SetValue(ElementPulseProperty, pulse);
            element.ApplyAnimationClock(UIElement.OpacityProperty, clock);
            if ((bool)element.GetValue(IsPausedProperty)) pulse.Pause();
        }

        /// <summary>
        /// Writes the shared epoch's pulse value to every phase-locked element pulse once per glow
        /// tick. Elements are held weakly: recycled item containers do not reliably raise Unloaded
        /// (see <see cref="RayAnimationDriver"/>), and a strong list would root every dead tile.
        /// </summary>
        private sealed class PhaseLockedTicker : IRayAnimationTarget
        {
            internal static readonly PhaseLockedTicker Instance = new PhaseLockedTicker();

            private readonly List<WeakReference<FrameworkElement>> _elements =
                new List<WeakReference<FrameworkElement>>();

            // The settings the newest activation resolved. Every caller passes the plugin's live
            // instance, so this follows retuning without a subscription of its own.
            internal PersistedSettings Settings;

            internal void Add(FrameworkElement element)
            {
                if (IndexOf(element) < 0)
                {
                    _elements.Add(new WeakReference<FrameworkElement>(element));
                }

                RayAnimationDriver.Subscribe(this);
            }

            internal void Remove(FrameworkElement element)
            {
                var index = IndexOf(element);
                if (index >= 0)
                {
                    _elements.RemoveAt(index);
                }

                if (_elements.Count == 0)
                {
                    RayAnimationDriver.Unsubscribe(this);
                }
            }

            private int IndexOf(FrameworkElement element)
            {
                for (var i = 0; i < _elements.Count; i++)
                {
                    if (_elements[i].TryGetTarget(out var existing) && ReferenceEquals(existing, element))
                    {
                        return i;
                    }
                }

                return -1;
            }

            public bool WantsRayFrames
            {
                get
                {
                    _elements.RemoveAll(reference => !reference.TryGetTarget(out _));
                    return _elements.Count > 0;
                }
            }

            public bool IgnoresGlowGate => false;

            public bool TakesEveryHeldFrame => false;

            public void OnRayFrame()
            {
                var opacity = CurrentPulseOpacity(Settings);
                for (var i = 0; i < _elements.Count; i++)
                {
                    if (_elements[i].TryGetTarget(out var element) &&
                        !(bool)element.GetValue(IsPausedProperty))
                    {
                        element.Opacity = opacity;
                    }
                }
            }
        }

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
                    element.SetValue(HasPresetOpacityProperty, true);
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

        private static void ApplyAnimation(FrameworkElement element, PersistedSettings persisted)
        {
            if (GetTarget(element) == RarityGlowPulseTarget.Element && GetPhaseLock(element))
            {
                // Every phase-locked pulse wants the identical value at the identical moment, so
                // one tick computes it once and writes it to all of them. Settings are read per
                // tick, so retuning needs no rebuild.
                var ticker = PhaseLockedTicker.Instance;
                ticker.Settings = persisted;
                if (!(bool)element.GetValue(IsPausedProperty))
                {
                    element.Opacity = CurrentPulseOpacity(persisted);
                }

                ticker.Add(element);
                return;
            }

            var (min, max, seconds) = ResolvePulseParams(persisted);

            // Phase-locked to the shared epoch (full cycle = fade in + auto-reversed fade
            // out) so a recreated element resumes the pulse mid-cycle instead of restarting
            // it. The BeginTime is stamped immediately before each BeginAnimation call —
            // computing it earlier would bake the deferral delay in as a phase error.
            var cycleMilliseconds = seconds * 2000.0;
            var animation = AnimationFrameRate.Apply(new DoubleAnimation
            {
                From = min,
                To = max,
                Duration = new Duration(TimeSpan.FromSeconds(seconds)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });

            if (GetTarget(element) == RarityGlowPulseTarget.Effect)
            {
                // The target is settled by activation; an Element pre-set made before the
                // template's Target attribute applied is not this pulse's to keep.
                ClearPresetOpacity(element);

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
                            if ((bool)element.GetValue(IsPausedProperty)) clock.Controller?.Pause();
                        }
                    }),
                    DispatcherPriority.Loaded);
            }
            else
            {
                animation.BeginTime = GetPhaseLock(element)
                    ? GlowAnimationClock.PhaseLockBeginTime(cycleMilliseconds)
                    : GlowAnimationClock.PeakStartBeginTime(cycleMilliseconds);
                ApplyElementClock(element, animation.CreateClock());
            }
        }

        private static void StopAnimation(FrameworkElement element)
        {
            PhaseLockedTicker.Instance.Remove(element);
            element.ClearValue(IsPausedProperty);
            element.ClearValue(ElementPulseProperty);
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
                // Detaches an opt-out's own clock; a no-op for a ticker-driven element.
                element.ApplyAnimationClock(UIElement.OpacityProperty, null);

                // Drop the phase pre-set local value (see OnIsActiveChanged) so the element
                // returns to its style/default opacity when the pulse is off.
                element.ClearValue(UIElement.OpacityProperty);
                element.ClearValue(HasPresetOpacityProperty);
            }
        }

        private static void ClearPresetOpacity(FrameworkElement element)
        {
            if ((bool)element.GetValue(HasPresetOpacityProperty))
            {
                element.ClearValue(UIElement.OpacityProperty);
                element.ClearValue(HasPresetOpacityProperty);
            }
        }

        /// <summary>
        /// Freezes both card-effect and icon-opacity pulses during a slide. Either kind would
        /// invalidate the whole-card motion cache every frame. The shared tick skips paused
        /// elements, so consumers outside this subtree keep pulsing. UI thread only.
        /// </summary>
        public static void PauseUnder(DependencyObject root)
        {
            ForEachElement(root, element =>
            {
                if (!GetIsActive(element) || (bool)element.GetValue(IsPausedProperty)) return;
                element.SetValue(IsPausedProperty, true);
                (element.GetValue(EffectClockProperty) as AnimationClock)?.Controller?.Pause();
                (element.GetValue(ElementPulseProperty) as ElementPulse)?.Pause();
            });
        }

        /// <summary>
        /// Stops <paramref name="element"/>'s Effect-target pulse with the effect at the pulse's
        /// peak, and reports the opacity it showed at that moment and the sweep it was running
        /// (floor, peak, seconds per half cycle). For a caller that shows the element through
        /// something it can dim itself: the element then stands still at the peak and the caller
        /// reproduces the sweep. False, changing nothing, when the element has no running Effect
        /// pulse. Undone by <see cref="ReleaseEffectPulse"/>. UI thread only.
        /// </summary>
        internal static bool TryHoldEffectPulseAtPeak(
            FrameworkElement element, out double shown, out double floor, out double peak, out double halfCycleSeconds)
        {
            shown = floor = peak = halfCycleSeconds = 0d;
            if (element == null || !GetIsActive(element) || (bool)element.GetValue(IsPausedProperty) ||
                !(element.GetValue(EffectClockProperty) is AnimationClock clock) ||
                !(clock.Timeline is DoubleAnimation sweep) || clock.Controller == null ||
                !(element.Effect is DropShadowEffect effect) ||
                !sweep.From.HasValue || !sweep.To.HasValue || !sweep.Duration.HasTimeSpan)
            {
                return false;
            }

            shown = effect.Opacity;
            floor = sweep.From.Value;
            peak = sweep.To.Value;
            halfCycleSeconds = sweep.Duration.TimeSpan.TotalSeconds;
            if (halfCycleSeconds <= 0)
            {
                return false;
            }

            element.SetValue(IsPausedProperty, true);
            clock.Controller.Pause();
            clock.Controller.SeekAlignedToLastTick(sweep.Duration.TimeSpan, TimeSeekOrigin.BeginTime);
            return true;
        }

        /// <summary>
        /// Resumes a pulse held by <see cref="TryHoldEffectPulseAtPeak"/> from
        /// <paramref name="cycleSeconds"/> into its cycle (the rise, then the fall).
        /// </summary>
        internal static void ReleaseEffectPulse(FrameworkElement element, double cycleSeconds)
        {
            if (element == null || !(bool)element.GetValue(IsPausedProperty))
            {
                return;
            }

            element.ClearValue(IsPausedProperty);
            var controller = (element.GetValue(EffectClockProperty) as AnimationClock)?.Controller;
            if (controller == null)
            {
                return;
            }

            controller.SeekAlignedToLastTick(
                TimeSpan.FromSeconds(Math.Max(0d, cycleSeconds)), TimeSeekOrigin.BeginTime);
            controller.Resume();
        }

        /// <summary>Resumes the clocks <see cref="PauseUnder"/> paused. UI thread only.</summary>
        public static void ResumeUnder(DependencyObject root)
        {
            ForEachElement(root, element =>
            {
                if (!(bool)element.GetValue(IsPausedProperty)) return;
                element.ClearValue(IsPausedProperty);
                (element.GetValue(EffectClockProperty) as AnimationClock)?.Controller?.Resume();
                (element.GetValue(ElementPulseProperty) as ElementPulse)?.Resume();
            });
        }

        private static void ForEachElement(DependencyObject root, Action<FrameworkElement> action)
        {
            if (root == null)
            {
                return;
            }

            if (root is FrameworkElement element)
            {
                action(element);
            }

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                ForEachElement(VisualTreeHelper.GetChild(root, i), action);
            }
        }

        private static double Clamp(double value, double min, double max) =>
            value < min ? min : (value > max ? max : value);
    }
}
