using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Playnite.SDK.Events;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Dialogs
{
    /// <summary>
    /// Popout gallery for a game's or achievement's saved unlock captures. Screenshots render via
    /// the shared <see cref="AsyncImage"/> pipeline; video clips play in an in-window MediaElement
    /// with a basic transport. The MediaElement is always stopped on navigation away and on close so
    /// the .mp4 file handle is released.
    /// </summary>
    public partial class CaptureGalleryViewer : UserControl, IFullscreenControllerNavigable
    {
        // Segoe MDL2 Assets glyphs (built from code points to keep the source pure ASCII).
        private static readonly string PlayGlyph = char.ConvertFromUtf32(0xE768);
        private static readonly string PauseGlyph = char.ConvertFromUtf32(0xE769);
        private static readonly string VolumeGlyph = char.ConvertFromUtf32(0xE767);
        private static readonly string MuteGlyph = char.ConvertFromUtf32(0xE74F);

        private readonly CaptureGalleryViewModel _vm;
        private readonly DispatcherTimer _positionTimer;
        private bool _isPlaying;
        private bool _isDraggingSlider;

        public CaptureGalleryViewer(CaptureGalleryViewModel viewModel)
        {
            _vm = viewModel;
            InitializeComponent();
            DataContext = _vm;

            _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _positionTimer.Tick += PositionTimer_Tick;

            _vm.PropertyChanged += ViewModel_PropertyChanged;
            PreviewKeyDown += CaptureGalleryViewer_PreviewKeyDown;
            Loaded += (_, __) => { Focus(); LoadCurrentMedia(); };
            Unloaded += (_, __) => StopVideo();
        }

        /// <summary>Stops playback and releases the media file handle. Call from the window's Closed.</summary>
        public void StopMedia() => StopVideo();

        private void FullscreenButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;

            var isVideo = _vm.IsVideo;
            var path = isVideo ? _vm.CurrentVideoPath : _vm.CurrentImagePath;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            // Pause the inline player so audio doesn't double up while the lightbox plays.
            if (isVideo && _isPlaying)
            {
                VideoPlayer?.Pause();
                _isPlaying = false;
                UpdatePlayPauseGlyph();
            }

            var content = new FullscreenMediaViewer(path, isVideo);
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = System.Windows.Media.Brushes.Black,
                ShowInTaskbar = false,
                Owner = Window.GetWindow(this),
                Content = content
            };
            content.RequestClose += (_, __) => window.Close();
            window.Loaded += (_, __) => window.WindowState = WindowState.Maximized;
            window.PreviewKeyDown += (s, args) =>
            {
                if (args.Key == Key.Escape)
                {
                    window.Close();
                    args.Handled = true;
                }
            };
            window.ShowDialog();
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CaptureGalleryViewModel.Current) ||
                e.PropertyName == nameof(CaptureGalleryViewModel.ShowVideo))
            {
                LoadCurrentMedia();
            }
        }

        private void LoadCurrentMedia()
        {
            if (_vm.ShowVideo && !string.IsNullOrEmpty(_vm.CurrentVideoPath))
            {
                LoadVideo(_vm.CurrentVideoPath);
            }
            else
            {
                StopVideo();
            }
        }

        private void LoadVideo(string path)
        {
            try
            {
                if (VideoErrorText != null)
                {
                    VideoErrorText.Visibility = Visibility.Collapsed;
                }

                if (VideoPlayer != null)
                {
                    VideoPlayer.Visibility = Visibility.Visible;
                    VideoPlayer.Stop();
                    VideoPlayer.Source = new Uri(path);
                    VideoPlayer.Play();
                    _isPlaying = true;
                    UpdatePlayPauseGlyph();
                    _positionTimer.Start();
                }
            }
            catch
            {
                ShowVideoError();
            }
        }

        private void StopVideo()
        {
            _positionTimer.Stop();
            _isPlaying = false;
            if (VideoPlayer != null)
            {
                try
                {
                    VideoPlayer.Stop();
                    VideoPlayer.Source = null;
                }
                catch
                {
                    // Ignore teardown races.
                }
            }

            UpdatePlayPauseGlyph();
            if (PositionSlider != null)
            {
                PositionSlider.Value = 0;
            }
        }

        private void ShowVideoError()
        {
            _positionTimer.Stop();
            if (VideoPlayer != null)
            {
                VideoPlayer.Visibility = Visibility.Collapsed;
            }

            if (VideoErrorText != null)
            {
                VideoErrorText.Visibility = Visibility.Visible;
            }
        }

        private void PositionTimer_Tick(object sender, EventArgs e)
        {
            if (_isDraggingSlider || VideoPlayer == null)
            {
                return;
            }

            if (VideoPlayer.NaturalDuration.HasTimeSpan)
            {
                var total = VideoPlayer.NaturalDuration.TimeSpan;
                if (PositionSlider != null && total.TotalSeconds > 0)
                {
                    PositionSlider.Value = VideoPlayer.Position.TotalSeconds;
                }

                UpdateTimeText(VideoPlayer.Position, total);
            }
        }

        private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (VideoPlayer.NaturalDuration.HasTimeSpan && PositionSlider != null)
            {
                PositionSlider.Maximum = VideoPlayer.NaturalDuration.TimeSpan.TotalSeconds;
                UpdateTimeText(TimeSpan.Zero, VideoPlayer.NaturalDuration.TimeSpan);
            }
        }

        private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            // Pause on the last frame rather than looping.
            VideoPlayer.Pause();
            _isPlaying = false;
            UpdatePlayPauseGlyph();
        }

        private void VideoPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            ShowVideoError();
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            TogglePlayPause();
            e.Handled = true;
        }

        private void TogglePlayPause()
        {
            if (VideoPlayer == null || !_vm.ShowVideo)
            {
                return;
            }

            if (_isPlaying)
            {
                VideoPlayer.Pause();
                _isPlaying = false;
            }
            else
            {
                VideoPlayer.Play();
                _isPlaying = true;
                _positionTimer.Start();
            }

            UpdatePlayPauseGlyph();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            if (VideoPlayer != null)
            {
                VideoPlayer.Stop();
                _isPlaying = false;
                if (PositionSlider != null)
                {
                    PositionSlider.Value = 0;
                }

                UpdatePlayPauseGlyph();
            }

            e.Handled = true;
        }

        private void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (VideoPlayer != null && MuteButton != null)
            {
                VideoPlayer.IsMuted = !VideoPlayer.IsMuted;
                MuteButton.Content = VideoPlayer.IsMuted ? MuteGlyph : VolumeGlyph;
            }

            e.Handled = true;
        }

        private void PositionSlider_DragStarted(object sender, DragStartedEventArgs e)
        {
            _isDraggingSlider = true;
        }

        private void PositionSlider_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (VideoPlayer != null && PositionSlider != null)
            {
                VideoPlayer.Position = TimeSpan.FromSeconds(PositionSlider.Value);
            }

            _isDraggingSlider = false;
        }

        private void UpdatePlayPauseGlyph()
        {
            if (PlayPauseButton != null)
            {
                PlayPauseButton.Content = _isPlaying ? PauseGlyph : PlayGlyph;
            }
        }

        private void UpdateTimeText(TimeSpan position, TimeSpan total)
        {
            if (TimeText != null)
            {
                TimeText.Text = $"{position:mm\\:ss} / {total:mm\\:ss}";
            }
        }

        private void CaptureGalleryViewer_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Left:
                    e.Handled = _vm.TryMovePrevious();
                    break;
                case Key.Right:
                    e.Handled = _vm.TryMoveNext();
                    break;
                case Key.Space:
                    if (_vm.ShowVideo)
                    {
                        TogglePlayPause();
                        e.Handled = true;
                    }

                    break;
            }
        }

        public bool HandleFullscreenControllerInput(ControllerInput input)
        {
            switch (input)
            {
                case ControllerInput.DPadLeft:
                case ControllerInput.LeftStickLeft:
                    return _vm.TryMovePrevious();
                case ControllerInput.DPadRight:
                case ControllerInput.LeftStickRight:
                    return _vm.TryMoveNext();
                case ControllerInput.LeftShoulder:
                    return _vm.CycleVariant(-1);
                case ControllerInput.RightShoulder:
                    return _vm.CycleVariant(1);
                case ControllerInput.A:
                    if (_vm.ShowVideo)
                    {
                        TogglePlayPause();
                        return true;
                    }

                    return false;
                default:
                    return false;
            }
        }
    }
}
