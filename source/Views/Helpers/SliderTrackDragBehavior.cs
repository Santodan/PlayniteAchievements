using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Gives a slider press-to-jump and press-and-drag on its track.
    ///
    /// This owns the gesture rather than leaning on <see cref="Slider.IsMoveToPointEnabled"/>, whose
    /// behavior depends on the theme's template: it jumps the value but starts no drag, and where it
    /// does not take effect the track's RepeatButtons page by LargeChange instead, so the click
    /// lands a fixed step away rather than where it was aimed. Here the press is mapped through the
    /// template's PART_Track (falling back to a linear map over the slider's width when the template
    /// has no such part), applied, captured, and marked handled so nothing else acts on it. A press
    /// on the thumb is left alone, since that already starts a real Thumb drag.
    ///
    /// Sliders using this should set IsMoveToPointEnabled="False" so WPF does not also act.
    /// </summary>
    internal sealed class SliderTrackDragBehavior
    {
        private readonly Slider _slider;
        private readonly Action<bool> _draggingChanged;
        private Track _track;
        private bool _isDragging;

        private SliderTrackDragBehavior(Slider slider, Action<bool> draggingChanged)
        {
            _slider = slider;
            _draggingChanged = draggingChanged;
        }

        /// <summary>
        /// Attaches the behavior to <paramref name="slider"/>. <paramref name="draggingChanged"/>
        /// reports when a track drag starts and ends, for callers that must not write the value
        /// from elsewhere while the user holds it.
        /// </summary>
        public static void Attach(Slider slider, Action<bool> draggingChanged = null)
        {
            if (slider == null)
            {
                return;
            }

            new SliderTrackDragBehavior(slider, draggingChanged).Hook();
        }

        private void Hook()
        {
            // handledEventsToo: Slider's move-to-point class handler marks the press handled before
            // an ordinary instance handler would see it.
            _slider.AddHandler(
                UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler(OnPreviewMouseLeftButtonDown),
                true);
            _slider.AddHandler(
                UIElement.PreviewMouseMoveEvent,
                new MouseEventHandler(OnPreviewMouseMove),
                true);
            _slider.AddHandler(
                UIElement.PreviewMouseLeftButtonUpEvent,
                new MouseButtonEventHandler(OnPreviewMouseLeftButtonUp),
                true);
            _slider.LostMouseCapture += (_, __) => EndDrag();
        }

        private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var track = GetTrack();
            if (track?.Thumb != null && track.Thumb.IsMouseOver)
            {
                // The Thumb takes its own drag from here, reported through DragStarted.
                return;
            }

            var value = ValueFromPoint(e);
            if (value == null)
            {
                return;
            }

            // Announced before the value moves, so a caller that quiesces its target for the drag
            // (a media player pausing, say) has done so before it sees the new value. Applying the
            // value first would let the caller act on it while still running, and then quiesce at
            // whatever point it had reached -- which is not the same place twice.
            _isDragging = true;
            _draggingChanged?.Invoke(true);

            _slider.Value = value.Value;
            _slider.CaptureMouse();

            // Keeps the track's RepeatButtons from also paging by LargeChange.
            e.Handled = true;
        }

        private void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging)
            {
                return;
            }

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndDrag();
                return;
            }

            var value = ValueFromPoint(e);
            if (value != null)
            {
                _slider.Value = value.Value;
            }
        }

        /// <summary>
        /// Maps a mouse position to the slider value at that position, as pure geometry over the
        /// track's thumb-adjusted travel.
        ///
        /// Deliberately not <see cref="Track.ValueFromPoint"/>: that returns the current value plus
        /// the distance from the thumb's last arranged centre, so it is only correct when value and
        /// layout agree and it is read once. Used for the press and again for every mouse move of
        /// the same gesture, the second read measures against a thumb that has not moved yet and
        /// lands somewhere else entirely — in practice pinned to Maximum — which made one pixel map
        /// to a different value depending on where the slider already sat.
        /// </summary>
        private double? ValueFromPoint(MouseEventArgs e)
        {
            var track = GetTrack();
            var reference = track ?? (FrameworkElement)_slider;
            var width = reference.ActualWidth;
            if (width <= 0)
            {
                return null;
            }

            var x = e.GetPosition(reference).X;

            // The thumb can only travel between its own half-widths, so the value at a pixel is
            // measured over that reduced span, matching where the thumb actually renders.
            var thumbWidth = track?.Thumb?.ActualWidth ?? 0;
            var travel = width - thumbWidth;
            var fraction = travel > 0
                ? (x - (thumbWidth / 2)) / travel
                : x / width;

            return Clamp(_slider.Minimum + (fraction * (_slider.Maximum - _slider.Minimum)));
        }

        private double Clamp(double value) =>
            Math.Max(_slider.Minimum, Math.Min(_slider.Maximum, value));

        private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            EndDrag();
        }

        private void EndDrag()
        {
            if (!_isDragging)
            {
                return;
            }

            _isDragging = false;
            _draggingChanged?.Invoke(false);

            if (_slider.IsMouseCaptured)
            {
                _slider.ReleaseMouseCapture();
            }
        }

        private Track GetTrack()
        {
            if (_track == null)
            {
                _slider.ApplyTemplate();
                _track = _slider.Template?.FindName("PART_Track", _slider) as Track;
            }

            return _track;
        }
    }
}
