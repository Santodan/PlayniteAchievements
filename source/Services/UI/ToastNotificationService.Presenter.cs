using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Services.UI
{
    // The thumbnail presenter of a wave: whether its motion can be presented, the presenter's
    // start and stop, the frame pump that steps it, and the motion it shows.
    internal sealed partial class ToastNotificationService
    {
        /// <summary>
        /// Whether a presenter can reproduce this wave's entrance and exit. It shows the motion
        /// host's transform and opacity and nothing else, which covers the style motions and the
        /// built-in slide; a theme storyboard qualifies when every child the plugin aims at the
        /// host animates one of those.
        /// </summary>
        private bool WaveMotionIsPresentable()
        {
            if (_activeMotionPlan != null)
            {
                return true;
            }

            return StoryboardIsPresentable(_activeSlideInStoryboard) &&
                StoryboardIsPresentable(_activeSlideOutStoryboard);
        }

        private static bool StoryboardIsPresentable(Storyboard storyboard)
        {
            if (storyboard == null)
            {
                return true;
            }

            foreach (var child in storyboard.Children)
            {
                if (!IsUntargeted(child) || AnimatesSlide(child))
                {
                    continue;
                }

                var path = Storyboard.GetTargetProperty(child)?.Path;
                if (path == null ||
                    (path.IndexOf("RenderTransform", StringComparison.Ordinal) < 0 &&
                     path.IndexOf("Opacity", StringComparison.Ordinal) < 0))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Creates the wave's thumbnail presenter and starts mirroring the motion host onto it every
        /// composed frame. When the presenter cannot be created the card moves back under the motion
        /// host, which then carries the motion inside the window as it does without a presenter.
        /// </summary>
        private void StartPresenter(Window window, bool perMonitorWindow)
        {
            ToastThumbnailPresenter presenter;
            using (perMonitorWindow ? Common.DpiAwarenessScope.PerMonitorV2() : null)
            {
                presenter = ToastThumbnailPresenter.TryCreate(window);
            }

            if (presenter == null)
            {
                _logger?.Info("[Toast] Presenter: unavailable, the window shows itself.");
                if (_activeSlideHost is Panel cardHost && _activeMotionHost is Panel motionHost &&
                    _activeCardSurface != null)
                {
                    cardHost.Children.Remove(_activeCardSurface);
                    motionHost.Children.Add(_activeCardSurface);
                    motionHost.Visibility = cardHost.Visibility;
                    _activeSlideHost = motionHost;
                }

                _activeMotionHost = null;
                return;
            }

            _activePresenter = presenter;
            _activeFramePump = new DwmFramePump(window.Dispatcher) { Flushed = presenter.StepTrack };
            _activePresenterTick = (s, e) => UpdatePresenter();
            HookFrame(_activePresenterTick);
        }

        /// <summary>
        /// Subscribes a per-frame handler for the active wave: to the wave's frame pump when it has
        /// one, otherwise to WPF's render loop.
        /// </summary>
        private void HookFrame(EventHandler handler)
        {
            if (handler == null)
            {
                return;
            }

            if (_activeFramePump != null)
            {
                _activeFramePump.Frame += handler;
            }
            else
            {
                CompositionTarget.Rendering += handler;
            }
        }

        private void UnhookFrame(EventHandler handler)
        {
            if (handler == null)
            {
                return;
            }

            // From both: the pump may have started or stopped since the handler was hooked.
            if (_activeFramePump != null)
            {
                _activeFramePump.Frame -= handler;
            }

            CompositionTarget.Rendering -= handler;
        }

        /// <summary>
        /// Raises <see cref="Frame"/> on the UI thread once per DWM composition.
        ///
        /// A presented wave changes nothing in its window while it moves, and WPF then raised
        /// CompositionTarget.Rendering about three times a second under a GPU-bound game
        /// (TemplateBench --presenter), which is what the slide's clock drive, the thumbnail and
        /// the track sampler would have stepped at. DwmFlush returns at each composition whatever
        /// WPF has to draw. A frame the UI thread has not handled yet is not queued again.
        /// <see cref="Flushed"/> runs on the pump's own thread at each composition, whether or not
        /// the UI thread is free.
        /// </summary>
        private sealed class DwmFramePump : IDisposable
        {
            [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
            private static extern int DwmFlush();

            private readonly System.Windows.Threading.Dispatcher _dispatcher;
            private readonly Action _raise;
            private volatile bool _running = true;
            private int _pending;

            public DwmFramePump(System.Windows.Threading.Dispatcher dispatcher)
            {
                _dispatcher = dispatcher;
                _raise = Raise;
                new System.Threading.Thread(Run)
                {
                    IsBackground = true,
                    Name = "PlayAch toast frame pump",
                    Priority = System.Threading.ThreadPriority.AboveNormal,
                }.Start();
            }

            public event EventHandler Frame;

            /// <summary>Called on the pump's thread after each composition.</summary>
            public Action Flushed { get; set; }

            public void Dispose()
            {
                _running = false;
                Frame = null;
                Flushed = null;
            }

            private void Run()
            {
                while (_running)
                {
                    try
                    {
                        if (DwmFlush() != 0)
                        {
                            System.Threading.Thread.Sleep(8);
                        }

                        if (_running)
                        {
                            Flushed?.Invoke();
                        }

                        if (_running && System.Threading.Interlocked.CompareExchange(ref _pending, 1, 0) == 0)
                        {
                            _dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send, _raise);
                        }
                    }
                    catch
                    {
                        System.Threading.Thread.Sleep(8);
                    }
                }
            }

            private void Raise()
            {
                System.Threading.Volatile.Write(ref _pending, 0);
                var handlers = Frame;
                if (!_running || handlers == null)
                {
                    return;
                }

                foreach (EventHandler handler in handlers.GetInvocationList())
                {
                    try
                    {
                        handler(this, EventArgs.Empty);
                    }
                    catch
                    {
                        // One handler's failure must not cost the others their frame.
                    }
                }
            }
        }

        private void StopPresenter()
        {
            if (_activePresenterTick != null)
            {
                UnhookFrame(_activePresenterTick);
                _activePresenterTick = null;
            }

            var presenter = _activePresenter;
            _activePresenter = null;
            presenter?.Dispose();

            var pump = _activeFramePump;
            _activeFramePump = null;
            pump?.Dispose();
        }

        /// <summary>
        /// Shows the thumbnail where the motion host's render transform and opacity put the card.
        /// The transform is taken as WPF applies it, about the host's RenderTransformOrigin.
        /// </summary>
        private void UpdatePresenter()
        {
            var presenter = _activePresenter;
            var host = _activeMotionHost;
            if (presenter == null || host == null)
            {
                return;
            }

            try
            {
                // The pulse is a function of its own clock, so the presenter reads it on the
                // frame pump's thread at every composition, whatever the UI thread is doing.
                var pulse = _activeHoldPulse;
                if (!ReferenceEquals(presenter.MainOpacitySource?.Target, pulse))
                {
                    presenter.MainOpacitySource = pulse != null ? pulse.Scale : (Func<double>)null;
                }

                presenter.Update(PresentedMotion(), host.Opacity);
            }
            catch
            {
                // A frame without an update keeps the thumbnail where it was.
            }
        }

        /// <summary>
        /// The motion host's render transform in window DIPs, as WPF applies it about the host's
        /// RenderTransformOrigin: where a presented wave's thumbnail is drawn relative to the card
        /// at rest. Identity when the wave has no presenter.
        /// </summary>
        private Matrix PresentedMotion()
        {
            var host = _activeMotionHost;
            if (host == null)
            {
                return Matrix.Identity;
            }

            var origin = new Point(
                host.RenderTransformOrigin.X * host.ActualWidth,
                host.RenderTransformOrigin.Y * host.ActualHeight);
            var motion = Matrix.Identity;
            motion.Translate(-origin.X, -origin.Y);
            motion.Append(host.RenderTransform?.Value ?? Matrix.Identity);
            motion.Translate(origin.X, origin.Y);
            return motion;
        }
    }
}
