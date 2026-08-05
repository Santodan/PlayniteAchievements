using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Dialogs
{
    /// <summary>
    /// Black-background lightbox that shows a single capture (image or video) filling the screen,
    /// hosted in a borderless maximized window by the gallery viewer. Clicking closes an image;
    /// clicking toggles play/pause on a video. Esc (handled by the host window) closes either.
    /// </summary>
    public partial class FullscreenMediaViewer : UserControl
    {
        private readonly bool _isVideo;
        private bool _isPlaying;

        public FullscreenMediaViewer(string path, bool isVideo)
        {
            _isVideo = isVideo;
            InitializeComponent();

            if (isVideo)
            {
                Img.Visibility = Visibility.Collapsed;
                Player.Visibility = Visibility.Visible;
                Loaded += (_, __) =>
                {
                    Focus();
                    try
                    {
                        Player.Source = new Uri(path);
                        Player.Play();
                        _isPlaying = true;
                    }
                    catch
                    {
                        ShowError();
                    }
                };
                Unloaded += (_, __) => StopVideo();
            }
            else
            {
                AsyncImage.SetUri(Img, path);
                Loaded += (_, __) => Focus();
            }
        }

        public event EventHandler RequestClose;

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            RequestClose?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }

        private void ClickLayer_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isVideo)
            {
                TogglePlayPause();
            }
            else
            {
                RequestClose?.Invoke(this, EventArgs.Empty);
            }

            e.Handled = true;
        }

        private void TogglePlayPause()
        {
            if (_isPlaying)
            {
                Player.Pause();
                _isPlaying = false;
            }
            else
            {
                Player.Play();
                _isPlaying = true;
            }
        }

        private void StopVideo()
        {
            try
            {
                Player.Stop();
                Player.Source = null;
            }
            catch
            {
                // Ignore teardown races.
            }
        }

        private void Player_MediaEnded(object sender, RoutedEventArgs e)
        {
            Player.Pause();
            _isPlaying = false;
        }

        private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            ShowError();
        }

        private void ShowError()
        {
            Player.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
