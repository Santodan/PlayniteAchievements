using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Transport bar for a <see cref="MediaElement"/> in manual mode: play/pause, stop, a seekable
    /// position slider, an elapsed/total readout, and a mute button with a volume slider. Shared by
    /// the capture gallery popout and the fullscreen lightbox so both surfaces behave the same way.
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
        private static readonly string MuteGlyph = char.ConvertFromUtf32(0xE74F);
        private static readonly string VolumeLowGlyph = char.ConvertFromUtf32(0xE993);
        private static readonly string VolumeMediumGlyph = char.ConvertFromUtf32(0xE994);
        private static readonly string VolumeHighGlyph = char.ConvertFromUtf32(0xE995);

        // A clip paused this close to its end counts as finished, so pressing play restarts it
        // instead of resuming at a position with nothing left to render.
        private static readonly TimeSpan EndEpsilon = TimeSpan.FromMilliseconds(250);

        private const double DurationTolerance = 0.001;
        private const double DefaultVolume = 0.5;

        // A seek lands on the nearest keyframe at or before the target, so it can legitimately stop
        // short of it; anything this close counts as arrived. Sized for the one-per-second cadence
        // of clips recorded before KeyframeCadence tightened it, which are still on disk.
        private const double SeekLandedTolerance = 1.25;
        private const int MaxAwaitedSeekTicks = 4;

        // Setting MediaElement.Position flushes the pipeline, so driving it from every mouse move
        // queues far more seeks than the media engine can retire: the picture trails the thumb and
        // keeps catching up after the button comes up. Scrub seeks are coalesced to this interval,
        // with the position the user released on applied immediately.
        private static readonly TimeSpan ScrubSeekInterval = TimeSpan.FromMilliseconds(100);

        // Volume and mute carry across clips, and between the gallery and the lightbox, for as long
        // as Playnite runs. They are deliberately not written to settings.
        private static double _sessionVolume = DefaultVolume;
        private static bool _sessionMuted;

        private readonly DispatcherTimer _positionTimer;
        private readonly DispatcherTimer _scrubTimer;
        private MediaElement _player;
        private bool _isPlaying;

        // The user is holding the position thumb or dragging its track: the timer must not
        // overwrite the value under them.
        private bool _isScrubbing;
        private double? _pendingScrubSeconds;
        private bool _resumeAfterScrub;

        // Where the last seek asked the player to go. A seek does not complete synchronously, so
        // until the player reports arriving there its Position still reads the pre-seek spot;
        // copying that onto the slider would pull the thumb back off the point the user picked.
        private double? _awaitingSeekSeconds;
        private int _awaitingSeekTicks;

        // A slider's value is being written programmatically: ignore the resulting ValueChanged
        // instead of acting on a change the user did not make.
        private bool _suppressSeek;
        private bool _suppressVolumeApply;

        public MediaTransportBar()
        {
            InitializeComponent();

            _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _positionTimer.Tick += PositionTimer_Tick;

            _scrubTimer = new DispatcherTimer { Interval = ScrubSeekInterval };
            _scrubTimer.Tick += (_, __) => FlushPendingScrub();

            Unloaded += (_, __) =>
            {
                _positionTimer.Stop();
                _scrubTimer.Stop();
            };

            SliderTrackDragBehavior.Attach(
                PositionSlider,
                dragging =>
                {
                    if (dragging)
                    {
                        BeginScrub();
                    }
                    else
                    {
                        EndScrub();
                    }
                });
            SliderTrackDragBehavior.Attach(VolumeSlider);

            SetVolumeSliderWithoutApply(_sessionVolume);
            UpdateVolumeGlyph();
        }

        /// <summary>True while the attached player is playing.</summary>
        public bool IsPlaying => _isPlaying;

        /// <summary>Binds the bar to the player it drives. Call once, before loading any media.</summary>
        public void Attach(MediaElement player)
        {
            _player = player;
            ApplyVolumeToPlayer();
            UpdatePlayPauseGlyph();
            UpdateVolumeGlyph();
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
            _scrubTimer.Stop();
            _isPlaying = false;
            _isScrubbing = false;
            _pendingScrubSeconds = null;
            _resumeAfterScrub = false;
            _awaitingSeekSeconds = null;
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
            StartPlayback();
        }

        private void StartPlayback()
        {
            if (_player == null)
            {
                return;
            }

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
            _awaitingSeekSeconds = null;
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

        /// <summary>Moves the player and the slider together. Not for use mid-scrub.</summary>
        private void SeekTo(double seconds)
        {
            var position = MovePlayerTo(seconds);
            if (position == null)
            {
                return;
            }

            SetSliderValueWithoutSeek(position.Value.TotalSeconds);
            UpdateTimeText(position.Value);
        }

        /// <summary>
        /// Moves the player alone and returns where it landed, or null if it could not move. The
        /// slider is left untouched: writing it back mid-scrub would yank the thumb out from under
        /// the user, back to a position they had already dragged past.
        /// </summary>
        private TimeSpan? MovePlayerTo(double seconds)
        {
            var duration = Duration;
            if (_player == null || _player.Source == null || duration <= TimeSpan.Zero)
            {
                return null;
            }

            var position = TimeSpan.FromSeconds(Math.Max(0, Math.Min(seconds, duration.TotalSeconds)));

            try
            {
                _player.Position = position;
            }
            catch
            {
                // Ignore teardown races.
                return null;
            }

            _awaitingSeekSeconds = position.TotalSeconds;
            _awaitingSeekTicks = 0;
            return position;
        }

        private void BeginScrub()
        {
            if (_isScrubbing)
            {
                return;
            }

            _isScrubbing = true;

            // Seeking a playing element fights the scrub: it keeps decoding forward between the
            // seeks and the picture settles late. Pausing makes each scrub seek land on a frame.
            _resumeAfterScrub = _isPlaying;
            if (_isPlaying)
            {
                Pause();
            }

            _scrubTimer.Start();
        }

        private void EndScrub()
        {
            if (!_isScrubbing)
            {
                return;
            }

            _isScrubbing = false;
            _scrubTimer.Stop();
            FlushPendingScrub();

            if (_resumeAfterScrub)
            {
                // Deliberately not Play(): the user may have scrubbed to the very end on purpose,
                // and restarting from zero there would throw away the position they picked.
                StartPlayback();
            }

            _resumeAfterScrub = false;
        }

        private void FlushPendingScrub()
        {
            if (_pendingScrubSeconds == null)
            {
                return;
            }

            var seconds = _pendingScrubSeconds.Value;
            _pendingScrubSeconds = null;

            var position = MovePlayerTo(seconds);
            if (position != null)
            {
                UpdateTimeText(position.Value);
            }
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

            if (!SeekHasLanded())
            {
                return;
            }

            SetSliderValueWithoutSeek(_player.Position.TotalSeconds);
            UpdateTimeText(_player.Position);
        }

        /// <summary>
        /// True once the player has caught up with the last requested seek, or once enough ticks
        /// have passed that it clearly is not going to (a keyframe-snapped landing, say). Until
        /// then the position readings are stale and must not be copied onto the slider.
        /// </summary>
        private bool SeekHasLanded()
        {
            if (_awaitingSeekSeconds == null)
            {
                return true;
            }

            var landed = Math.Abs(_player.Position.TotalSeconds - _awaitingSeekSeconds.Value) <= SeekLandedTolerance;
            if (!landed && ++_awaitingSeekTicks < MaxAwaitedSeekTicks)
            {
                return false;
            }

            _awaitingSeekSeconds = null;
            return true;
        }

        private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressSeek)
            {
                return;
            }

            if (_isScrubbing)
            {
                // The readout follows the thumb every move; the player itself is moved on the
                // scrub timer so a fast drag cannot outrun the media engine.
                _pendingScrubSeconds = e.NewValue;
                UpdateTimeText(TimeSpan.FromSeconds(e.NewValue));
                return;
            }

            SeekTo(e.NewValue);
        }

        private void PositionSlider_DragStarted(object sender, DragStartedEventArgs e)
        {
            BeginScrub();
        }

        private void PositionSlider_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            EndScrub();
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suppressVolumeApply)
            {
                return;
            }

            _sessionVolume = e.NewValue;

            // Raising the volume off zero reads as an unmute, which saves a trip to the button.
            if (_sessionMuted && _sessionVolume > 0)
            {
                _sessionMuted = false;
            }

            ApplyVolumeToPlayer();
            UpdateVolumeGlyph();
        }

        private void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            _sessionMuted = !_sessionMuted;

            // Unmuting a slider sitting at zero would stay silent, so give it something to play at.
            if (!_sessionMuted && _sessionVolume <= 0)
            {
                _sessionVolume = DefaultVolume;
                SetVolumeSliderWithoutApply(_sessionVolume);
            }

            ApplyVolumeToPlayer();
            UpdateVolumeGlyph();
            e.Handled = true;
        }

        private void SetVolumeSliderWithoutApply(double volume)
        {
            _suppressVolumeApply = true;
            try
            {
                VolumeSlider.Value = volume;
            }
            finally
            {
                _suppressVolumeApply = false;
            }
        }

        private void ApplyVolumeToPlayer()
        {
            if (_player == null)
            {
                return;
            }

            _player.Volume = _sessionVolume;
            _player.IsMuted = _sessionMuted;
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

        private void UpdatePlayPauseGlyph()
        {
            PlayPauseButton.Content = _isPlaying ? PauseGlyph : PlayGlyph;
        }

        private void UpdateVolumeGlyph()
        {
            if (_sessionMuted || _sessionVolume <= 0)
            {
                MuteButton.Content = MuteGlyph;
                return;
            }

            MuteButton.Content =
                _sessionVolume < 0.34 ? VolumeLowGlyph :
                _sessionVolume < 0.67 ? VolumeMediumGlyph :
                VolumeHighGlyph;
        }

        private void UpdateTimeText(TimeSpan position)
        {
            TimeText.Text = $"{position:mm\\:ss} / {Duration:mm\\:ss}";
        }
    }
}
