using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>The axis a claim scrolls first.</summary>
    internal enum WheelScrollAxis
    {
        /// <summary>Scroll vertically; ignore the wheel when there is no vertical room.</summary>
        Vertical,

        /// <summary>Scroll horizontally where there is horizontal room, vertically otherwise.</summary>
        PreferHorizontal,
    }

    /// <summary>
    /// Scrolls a hosted control's own viewport on the wheel by claiming the event at the window,
    /// ahead of anything sitting between the window and the control.
    /// </summary>
    /// <remarks>
    /// A Playnite theme may handle the tunnelling PreviewMouseWheel at its page level to scroll the
    /// details view. That leaves a hosted control's scroll viewer with nothing to react to: the page
    /// moves and the control stays where it is, however much it has left to scroll. Tunnelling runs
    /// root to leaf, so no handler inside the control can get there first; a handler on the window
    /// can. The claim is scoped to events raised inside the owner, so everything else keeps the
    /// wheel it would otherwise have had, and it is released at the ends of the viewport so a
    /// surface with nothing left in that direction hands the wheel back rather than trapping it.
    /// </remarks>
    internal sealed class WindowWheelScrollClaim
    {
        /// <summary>Wheel notches are 120; a third of that is close to WPF's own three-line step.</summary>
        private const double WheelDeltaDivisor = 3.0;

        private readonly FrameworkElement _owner;
        private readonly WheelScrollAxis _axis;
        private Window _window;

        public WindowWheelScrollClaim(FrameworkElement owner, WheelScrollAxis axis)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _axis = axis;
        }

        /// <summary>
        /// Registers the hook. Safe to call again without an intervening <see cref="Detach"/>:
        /// Loaded can fire more than once, and RemoveHandler drops one registration per call, so
        /// an unguarded attach would strand a copy on the window that nothing can take off again.
        /// </summary>
        public void Attach()
        {
            if (_window != null)
            {
                return;
            }

            _window = Window.GetWindow(_owner);
            _window?.AddHandler(
                UIElement.PreviewMouseWheelEvent,
                new MouseWheelEventHandler(OnWindowPreviewMouseWheel));
        }

        public void Detach()
        {
            _window?.RemoveHandler(
                UIElement.PreviewMouseWheelEvent,
                new MouseWheelEventHandler(OnWindowPreviewMouseWheel));
            _window = null;
        }

        private void OnWindowPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta == 0 || !_owner.IsLoaded)
            {
                return;
            }

            var scrollViewer = ResolveScrollViewer(e.OriginalSource as DependencyObject);
            if (scrollViewer == null)
            {
                return;
            }

            if (_axis == WheelScrollAxis.PreferHorizontal && scrollViewer.ScrollableWidth > 0)
            {
                if (IsAtEnd(e.Delta, scrollViewer.HorizontalOffset, scrollViewer.ScrollableWidth))
                {
                    return;
                }

                e.Handled = true;
                scrollViewer.ScrollToHorizontalOffset(
                    scrollViewer.HorizontalOffset - (e.Delta / WheelDeltaDivisor));
                return;
            }

            if (scrollViewer.ScrollableHeight > 0)
            {
                if (IsAtEnd(e.Delta, scrollViewer.VerticalOffset, scrollViewer.ScrollableHeight))
                {
                    return;
                }

                e.Handled = true;
                scrollViewer.ScrollToVerticalOffset(
                    scrollViewer.VerticalOffset - (e.Delta / WheelDeltaDivisor));
            }
        }

        private static bool IsAtEnd(int delta, double offset, double scrollable)
        {
            return (delta > 0 && offset <= 0) || (delta < 0 && offset >= scrollable);
        }

        /// <summary>
        /// The scroll viewer nearest the event's source, searched from the source upward and
        /// stopping at the owner. Taken from the source rather than from the owner downward
        /// because the owner may host several scrolling surfaces, and the one under the pointer
        /// is the one the wheel belongs to. Returns null for an event raised outside the owner,
        /// which is what keeps this hook off the rest of the window.
        /// </summary>
        private ScrollViewer ResolveScrollViewer(DependencyObject source)
        {
            ScrollViewer found = null;
            while (source != null)
            {
                if (found == null && source is ScrollViewer scrollViewer)
                {
                    found = scrollViewer;
                }

                if (ReferenceEquals(source, _owner))
                {
                    return found;
                }

                source = source is Visual
                    ? VisualTreeHelper.GetParent(source)
                    : (source as FrameworkContentElement)?.Parent;
            }

            return null;
        }
    }
}
