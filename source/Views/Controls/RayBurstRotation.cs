using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// The single rotation every <see cref="RarityRayBurst"/> turns on.
    ///
    /// One transform with one animation, shared by every burst in the application. A burst used to own
    /// its own timeline, which meant a grid of thirty visible rows ran thirty independent animations,
    /// each ticking and dirtying its own layer every frame — the cause of the lag in ray mode. Sharing
    /// makes that a single timeline regardless of how many bursts exist, and every burst turns in step,
    /// which reads as deliberate rather than as thirty things spinning out of phase.
    ///
    /// The transform is mutable and shared, which is fine: WPF allows one unfrozen Freezable to be
    /// referenced from many visuals, and only this class ever writes to it.
    /// </summary>
    internal static class RayBurstRotation
    {
        // Slow and fast ends of the rotation period, mapped from the shared 0-1 RarityGlowPulseSpeed
        // setting. Deliberately not the pulse's own 10s-to-0.1s half-cycle range, which reads as a blur
        // rather than a rotation.
        private const double SlowRotationSeconds = 72.0;
        private const double FastRotationSeconds = 5.0;

        private static readonly RotateTransform SharedRotation = new RotateTransform();
        private static readonly object SyncRoot = new object();
        private static bool _subscribed;

        /// <summary>The shared transform to place in a burst's own transform group.</summary>
        public static Transform Transform => SharedRotation;

        /// <summary>
        /// Starts or refreshes the shared rotation, and begins tracking the settings that govern it.
        /// Safe to call from every burst as it loads; the work happens once.
        /// </summary>
        public static void Ensure()
        {
            lock (SyncRoot)
            {
                if (!_subscribed)
                {
                    var persisted = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted;
                    if (persisted != null)
                    {
                        // One handler for the process, on the app-lifetime settings instance, so there
                        // is nothing to detach: the rotation outlives every individual burst.
                        persisted.PropertyChanged += OnSettingsChanged;
                        _subscribed = true;
                    }
                }
            }

            Apply();
        }

        private static void OnSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PersistedSettings.RarityGlowPulseSpeed) ||
                e.PropertyName == nameof(PersistedSettings.AnimateRarityGlows) ||
                e.PropertyName == nameof(PersistedSettings.RarityGlowRayTiers))
            {
                Apply();
            }
        }

        private static void Apply()
        {
            var persisted = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted;

            // Nothing to drive when no tier has rays, which is the default. Leaving a timeline running
            // then would invalidate every element referencing this transform for no visible result.
            var raysOff = persisted != null && persisted.RarityGlowRayTiers == RaritySelection.None;
            if (raysOff || (persisted != null && !persisted.AnimateRarityGlows))
            {
                // Held still rather than turning, matching what the toggle does to the glow pulse.
                SharedRotation.BeginAnimation(RotateTransform.AngleProperty, null);
                SharedRotation.Angle = 0.0;
                return;
            }

            var seconds = ResolveRotationSeconds(persisted);
            var animation = new DoubleAnimation
            {
                From = 0.0,
                To = -360.0,
                Duration = new Duration(TimeSpan.FromSeconds(seconds)),
                RepeatBehavior = RepeatBehavior.Forever
            };

            // Phase-locked to the shared epoch so restarting the animation — after a speed change, say
            // — does not visibly snap every burst back to zero at once.
            animation.BeginTime = GlowAnimationClock.PhaseLockBeginTime(seconds * 1000.0);
            SharedRotation.BeginAnimation(RotateTransform.AngleProperty, animation);
        }

        private static double ResolveRotationSeconds(PersistedSettings persisted)
        {
            var speed = persisted?.RarityGlowPulseSpeed ?? 0.5;
            speed = speed < 0.0 ? 0.0 : (speed > 1.0 ? 1.0 : speed);
            return SlowRotationSeconds - (speed * (SlowRotationSeconds - FastRotationSeconds));
        }
    }
}
