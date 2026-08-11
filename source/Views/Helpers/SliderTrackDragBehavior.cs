using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Lets a press that lands on a slider's track keep scrubbing while the button stays down.
    ///
    /// <see cref="Slider.IsMoveToPointEnabled"/> jumps the value to the click point but hands the
    /// mouse to nobody and marks the press handled, so WPF starts no drag: the most natural gesture,
    /// press on the track and drag, otherwise goes nowhere. This captures that press and maps every
    /// subsequent move through the template's PART_Track. A press on the thumb is left alone, since
    /// that already starts a real Thumb drag.
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
            if (track?.Thumb == null || track.Thumb.IsMouseOver)
            {
                return;
            }

            _isDragging = true;
            _draggingChanged?.Invoke(true);
            _slider.CaptureMouse();
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

            var track = GetTrack();
            if (track == null)
            {
                return;
            }

            var value = track.ValueFromPoint(e.GetPosition(track));
            if (!double.IsNaN(value) && !double.IsInfinity(value))
            {
                _slider.Value = value;
            }
        }

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
