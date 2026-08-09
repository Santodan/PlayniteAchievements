using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK.Events;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Dialogs
{
    /// <summary>
    /// Popout gallery for a game's or achievement's saved unlock captures. Screenshots render via
    /// the shared <see cref="AsyncImage"/> pipeline; video clips play in an in-window MediaElement
    /// driven by the shared transport bar. The MediaElement is always stopped on navigation away and
    /// on close so the .mp4 file handle is released.
    /// </summary>
    public partial class CaptureGalleryViewer : UserControl, IFullscreenControllerNavigable
    {
        private static readonly TimeSpan SeekStep = TimeSpan.FromSeconds(5);

        private readonly CaptureGalleryViewModel _vm;

        public CaptureGalleryViewer(CaptureGalleryViewModel viewModel)
        {
            _vm = viewModel;
            InitializeComponent();
            DataContext = _vm;

            Transport.Attach(VideoPlayer);

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
            if (isVideo && Transport.IsPlaying)
            {
                Transport.Pause();
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
                    Transport.Play();
                }
            }
            catch
            {
                ShowVideoError();
            }
        }

        private void StopVideo()
        {
            Transport.Reset();
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
        }

        private void ShowVideoError()
        {
            Transport.Reset();
            if (VideoPlayer != null)
            {
                VideoPlayer.Visibility = Visibility.Collapsed;
            }

            if (VideoErrorText != null)
            {
                VideoErrorText.Visibility = Visibility.Visible;
            }
        }

        private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            Transport.NotifyMediaOpened();
        }

        private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            Transport.NotifyMediaEnded();
        }

        private void VideoPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            ShowVideoError();
        }

        private void TogglePlayPause()
        {
            if (_vm.ShowVideo)
            {
                Transport.TogglePlayPause();
            }
        }

        private void CaptureGalleryViewer_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Plain arrows page between captures, so seeking takes the Shift-modified pair.
            var isSeek = _vm.ShowVideo && Keyboard.Modifiers == ModifierKeys.Shift;

            switch (e.Key)
            {
                case Key.Left:
                    if (isSeek)
                    {
                        Transport.SeekBy(-SeekStep);
                        e.Handled = true;
                        break;
                    }

                    e.Handled = _vm.TryMovePrevious();
                    break;
                case Key.Right:
                    if (isSeek)
                    {
                        Transport.SeekBy(SeekStep);
                        e.Handled = true;
                        break;
                    }

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
