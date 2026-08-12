using System;
using System.ComponentModel;
using System.Windows;
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

        // Stores the per-element settings-changed handler so it can be detached (kept only while
        // the element is loaded and active, so it never outlives the element on the app-lifetime
        // PersistedSettings instance).
        private static readonly DependencyProperty SettingsHandlerProperty =
            DependencyProperty.RegisterAttached(
                "SettingsHandler", typeof(PropertyChangedEventHandler), typeof(RarityGlowPulse),
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

            if (persisted != null && element.GetValue(SettingsHandlerProperty) == null)
            {
                PropertyChangedEventHandler handler = (s, args) =>
                {
                    if (args.PropertyName == nameof(PersistedSettings.RarityGlowPulseMinOpacity) ||
                        args.PropertyName == nameof(PersistedSettings.RarityGlowPulseMaxOpacity) ||
                        args.PropertyName == nameof(PersistedSettings.RarityGlowPulseSpeed))
                    {
                        ApplyAnimation(element, persisted);
                    }
                };

                persisted.PropertyChanged += handler;
                element.SetValue(SettingsHandlerProperty, handler);
            }
        }

        private static void Deactivate(FrameworkElement element)
        {
            if (element.GetValue(SettingsHandlerProperty) is PropertyChangedEventHandler handler)
            {
                var persisted = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted;
                if (persisted != null)
                {
                    persisted.PropertyChanged -= handler;
                }

                element.SetValue(SettingsHandlerProperty, null);
            }

            StopAnimation(element);
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
                            effect.BeginAnimation(DropShadowEffect.OpacityProperty, animation);
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
                    effect.BeginAnimation(DropShadowEffect.OpacityProperty, null);
                }
            }
            else
            {
                element.BeginAnimation(UIElement.OpacityProperty, null);

                // Drop the phase pre-set local value (see OnIsActiveChanged) so the element
                // returns to its style/default opacity when the pulse is off.
                element.ClearValue(UIElement.OpacityProperty);
            }
        }

        private static double Clamp(double value, double min, double max) =>
            value < min ? min : (value > max ? max : value);
    }
}
