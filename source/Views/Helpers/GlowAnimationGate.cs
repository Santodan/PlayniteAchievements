using System;
using System.Windows;
using System.Windows.Threading;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Suspends the looping rarity glow animations while nobody is looking at Playnite: its main
    /// window is minimized or hidden (tray), or a game is running. Rays and phase-locked pulses
    /// share one frame tick (<see cref="RayAnimationDriver"/>), which leaves the composition tick
    /// entirely while suspended, so Playnite stops redrawing at the display's refresh rate behind
    /// the game. Phase comes from <see cref="GlowAnimationClock"/>, so resuming lands mid-cycle.
    ///
    /// Losing focus alone does not suspend: a Playnite window left visible on another monitor keeps
    /// animating. Content that must keep animating over a running game (the unlock notification
    /// card) marks its window with <see cref="IsExemptProperty"/>.
    ///
    /// Inputs and <see cref="Changed"/> are UI-thread only.
    /// </summary>
    internal static class GlowAnimationGate
    {
        /// <summary>
        /// Inherited marker: glow animations under an element with this set keep running while the
        /// gate is suspended.
        /// </summary>
        public static readonly DependencyProperty IsExemptProperty =
            DependencyProperty.RegisterAttached(
                "IsExempt", typeof(bool), typeof(GlowAnimationGate),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

        public static void SetIsExempt(DependencyObject element, bool value) =>
            element.SetValue(IsExemptProperty, value);

        public static bool GetIsExempt(DependencyObject element) =>
            element != null && (bool)element.GetValue(IsExemptProperty);

        private static bool _mainWindowAway;
        private static bool _gameRunning;
        private static Window _mainWindow;

        /// <summary>Whether non-exempt glow animations should stand still.</summary>
        public static bool IsSuspended => _mainWindowAway || _gameRunning;

        /// <summary>Raised on the UI thread when <see cref="IsSuspended"/> flips.</summary>
        public static event EventHandler Changed;

        /// <summary>Records whether a game is running. UI thread only.</summary>
        public static void SetGameRunning(bool running)
        {
            Update(() => _gameRunning = running);
        }

        /// <summary>
        /// Follows <paramref name="window"/>'s minimized and visible state. Replaces any window
        /// tracked before. UI thread only.
        /// </summary>
        public static void TrackMainWindow(Window window)
        {
            if (ReferenceEquals(_mainWindow, window))
            {
                return;
            }

            if (_mainWindow != null)
            {
                _mainWindow.StateChanged -= OnMainWindowStateChanged;
                _mainWindow.IsVisibleChanged -= OnMainWindowIsVisibleChanged;
                _mainWindow.Closed -= OnMainWindowClosed;
            }

            _mainWindow = window;
            if (window != null)
            {
                window.StateChanged += OnMainWindowStateChanged;
                window.IsVisibleChanged += OnMainWindowIsVisibleChanged;
                window.Closed += OnMainWindowClosed;
            }

            RefreshMainWindowAway();
        }

        /// <summary>
        /// Marshals <paramref name="action"/> to <paramref name="dispatcher"/> unless already on it.
        /// For callers on Playnite's game lifecycle callbacks, which are not guaranteed to run on
        /// the UI thread.
        /// </summary>
        public static void Post(Dispatcher dispatcher, Action action)
        {
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
        }

        private static void OnMainWindowStateChanged(object sender, EventArgs e) => RefreshMainWindowAway();

        private static void OnMainWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) =>
            RefreshMainWindowAway();

        private static void OnMainWindowClosed(object sender, EventArgs e) => TrackMainWindow(null);

        private static void RefreshMainWindowAway()
        {
            var window = _mainWindow;
            Update(() => _mainWindowAway = window != null &&
                (!window.IsVisible || window.WindowState == WindowState.Minimized));
        }

        private static void Update(Action change)
        {
            var before = IsSuspended;
            change();
            if (before != IsSuspended)
            {
                Changed?.Invoke(null, EventArgs.Empty);
            }
        }
    }
}
