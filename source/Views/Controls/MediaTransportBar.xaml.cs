using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Transport bar for a <see cref="MediaElement"/> in manual mode: play/pause, stop, a seekable
    /// position slider, an elapsed/total readout, and mute. Shared by the capture gallery popout and
    /// the fullscreen lightbox so both surfaces seek the same way.
    ///
    /// The host owns the MediaElement and its MediaOpened/MediaEnded/MediaFailed handlers (it owns
    /// the error UI); it calls <see cref="Attach"/> once and then forwards those events in through
    /// <see cref="NotifyMediaOpened"/> and <see cref="NotifyMediaEnded"/>.
    /// </summary>
    public partial class MediaTransportBar : UserControl
    {
        // Segoe MDL2 Assets glyphs (built from code points to keep the source pure ASCII).
        private static readonly string PlayGlyph = char.ConvertFromUtf32(0xE768);
        private static readonly string PauseGlyph = char.ConvertFromUtf32(0xE769);
        private static readonly string VolumeGlyph = char.ConvertFromUtf32(0xE767);
        private static readonly string MuteGlyph = char.ConvertFromUtf32(0xE74F);

        // A clip paused this close to its end counts as finished, so pressing play restarts it
        // instead of resuming at a position with nothing left to render.
        private static readonly TimeSpan EndEpsilon = TimeSpan.FromMilliseconds(250);

        private const double DurationTolerance = 0.001;

        private readonly DispatcherTimer _positionTimer;
        private MediaElement _player;
        private Track _track;
        private bool _isPlaying;

        // The user is holding the thumb: the timer must not overwrite the value under them.
        private bool _isScrubbing;

        // The press landed on the track rather than the thumb, so this control owns the drag.
        private bool _isTrackScrubbing;

        // The slider's value is being written programmatically: ignore the resulting ValueChanged
        // instead of seeking back to where the player already is.
        private bool _suppressSeek;

        public MediaTransportBar()
        {
            InitializeComponent();

            _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _positionTimer.Tick += PositionTimer_Tick;
            Unloaded += (_, __) => _positionTimer.Stop();

            // Registered with handledEventsToo because Slider's move-to-point class handler marks
            // the press handled before any instance handler would normally see it.
            PositionSlider.AddHandler(
                PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler(PositionSlider_PreviewMouseLeftButtonDown),
                true);
            PositionSlider.AddHandler(
                PreviewMouseMoveEvent,
                new MouseEventHandler(PositionSlider_PreviewMouseMove),
                true);
            PositionSlider.AddHandler(
                PreviewMouseLeftButtonUpEvent,
                new MouseButtonEventHandler(PositionSlider_PreviewMouseLeftButtonUp),
                true);
            PositionSlider.LostMouseCapture += (_, __) => EndTrackScrub();
        }

        /// <summary>True while the attached player is playing.</summary>
        public bool IsPlaying => _isPlaying;

        /// <summary>Binds the bar to the player it drives. Call once, before loading any media.</summary>
        public void Attach(MediaElement player)
        {
            _player = player;
            UpdatePlayPauseGlyph();
            UpdateMuteGlyph();
            UpdateTimeText(TimeSpan.Zero);
        }

        /// <summary>Forward the player's MediaOpened: adopts the clip's duration.</summary>
        public void NotifyMediaOpened()
        {
            ApplyDuration();
            UpdateTimeText(_player?.Position ?? TimeSpan.Zero);
        }

        /// <summary>Forward the player's MediaEnded: pauses on the last frame rather than looping.</summary>
        public void NotifyMediaEnded()
        {
            _player?.Pause();
            _isPlaying = false;
            UpdatePlayPauseGlyph();
        }

        /// <summary>
        /// Returns the bar to its idle state. Call before releasing the player's source so the
        /// position timer stops ticking against a torn-down element.
        /// </summary>
        public void Reset()
        {
            _positionTimer.Stop();
            _isPlaying = false;
            _isScrubbing = false;
            UpdatePlayPauseGlyph();
            SetSliderValueWithoutSeek(0);
            UpdateTimeText(TimeSpan.Zero);
        }

        public void Play()
        {
            if (_player == null)
            {
                return;
            }

            RestartIfFinished();
            _player.Play();
            _isPlaying = true;
            _positionTimer.Start();
            UpdatePlayPauseGlyph();
        }

        public void Pause()
        {
            if (_player == null)
            {
                return;
            }

            _player.Pause();
            _isPlaying = false;
            UpdatePlayPauseGlyph();
        }

        public void TogglePlayPause()
        {
            if (_isPlaying)
            {
                Pause();
            }
            else
            {
                Play();
            }
        }

        public void Stop()
        {
            if (_player == null)
            {
                return;
            }

            _player.Stop();
            _isPlaying = false;
            UpdatePlayPauseGlyph();
            SetSliderValueWithoutSeek(0);
            UpdateTimeText(TimeSpan.Zero);
        }

        /// <summary>Seeks relative to the current position, clamped to the clip.</summary>
        public void SeekBy(TimeSpan delta)
        {
            if (_player == null)
            {
                return;
            }

            SeekTo((_player.Position + delta).TotalSeconds);
        }

        private TimeSpan Duration =>
            _player != null && _player.NaturalDuration.HasTimeSpan
                ? _player.NaturalDuration.TimeSpan
                : TimeSpan.Zero;

        private void SeekTo(double seconds)
        {
            var duration = Duration;
            if (_player == null || _player.Source == null || duration <= TimeSpan.Zero)
            {
                return;
            }

            var clamped = Math.Max(0, Math.Min(seconds, duration.TotalSeconds));
            var position = TimeSpan.FromSeconds(clamped);

            try
            {
                _player.Position = position;
            }
            catch
            {
                // Ignore teardown races.
                return;
            }

            SetSliderValueWithoutSeek(clamped);
            UpdateTimeText(position);
        }

        private void RestartIfFinished()
        {
            var duration = Duration;
            if (duration <= TimeSpan.Zero)
            {
                return;
            }

            if (_player.Position >= duration - EndEpsilon)
            {
                SeekTo(0);
            }
        }

        private void ApplyDuration()
        {
            var duration = Duration;
            if (duration <= TimeSpan.Zero)
            {
                return;
            }

            // Shrinking the maximum can coerce the value, which would otherwise read as a user seek.
            _suppressSeek = true;
            try
            {
                PositionSlider.Maximum = duration.TotalSeconds;
            }
            finally
            {
                _suppressSeek = false;
            }
        }

        private void SetSliderValueWithoutSeek(double seconds)
        {
            _suppressSeek = true;
            try
            {
                PositionSlider.Value = seconds;
            }
            finally
            {
                _suppressSeek = false;
            }
        }

        private void PositionTimer_Tick(object sender, EventArgs e)
        {
            if (_isScrubbing || _player == null)
            {
                return;
            }

            var duration = Duration;
            if (duration <= TimeSpan.Zero)
            {
                return;
            }

            // MediaOpened does not always carry the duration; adopt it once it appears.
            if (Math.Abs(PositionSlider.Maximum - duration.TotalSeconds) > DurationTolerance)
            {
                ApplyDuration();
            }

            SetSliderValueWithoutSeek(_player.Position.TotalSeconds);
            UpdateTimeText(_player.Position);
        }

        private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSeek)
            {
                return;
            }

            SeekTo(e.NewValue);
        }

        private void PositionSlider_DragStarted(object sender, DragStartedEventArgs e)
        {
            _isScrubbing = true;
        }

        private void PositionSlider_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            // The seek itself already landed through ValueChanged as the thumb moved.
            _isScrubbing = false;
        }

        /// <summary>
        /// Starts a scrub for a press that landed on the track. Slider's move-to-point handling
        /// jumps the value to the click point but hands the mouse to nobody, so without this the
        /// most natural gesture -- press on the track and drag -- would go nowhere.
        /// </summary>
        private void PositionSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var track = GetTrack();
            if (track?.Thumb == null || track.Thumb.IsMouseOver)
            {
                // A press on the thumb starts a real Thumb drag, reported by DragStarted.
                return;
            }

            _isTrackScrubbing = true;
            _isScrubbing = true;
            PositionSlider.CaptureMouse();
        }

        private void PositionSlider_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isTrackScrubbing)
            {
                return;
            }

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndTrackScrub();
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
                // Routed through the slider so the seek runs on the one ValueChanged path.
                PositionSlider.Value = value;
            }
        }

        private void PositionSlider_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            EndTrackScrub();
        }

        private void EndTrackScrub()
        {
            if (!_isTrackScrubbing)
            {
                return;
            }

            _isTrackScrubbing = false;
            _isScrubbing = false;

            if (PositionSlider.IsMouseCaptured)
            {
                PositionSlider.ReleaseMouseCapture();
            }
        }

        private Track GetTrack()
        {
            if (_track == null)
            {
                PositionSlider.ApplyTemplate();
                _track = PositionSlider.Template?.FindName("PART_Track", PositionSlider) as Track;
            }

            return _track;
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            TogglePlayPause();
            e.Handled = true;
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            Stop();
            e.Handled = true;
        }

        private void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_player != null)
            {
                _player.IsMuted = !_player.IsMuted;
                UpdateMuteGlyph();
            }

            e.Handled = true;
        }

        private void UpdatePlayPauseGlyph()
        {
            PlayPauseButton.Content = _isPlaying ? PauseGlyph : PlayGlyph;
        }

        private void UpdateMuteGlyph()
        {
            MuteButton.Content = _player != null && _player.IsMuted ? MuteGlyph : VolumeGlyph;
        }

        private void UpdateTimeText(TimeSpan position)
        {
            TimeText.Text = $"{position:mm\\:ss} / {Duration:mm\\:ss}";
        }
    }
}
