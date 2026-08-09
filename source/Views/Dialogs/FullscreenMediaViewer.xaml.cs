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
    /// clicking toggles play/pause on a video, which also gets the shared transport bar for seeking.
    /// Esc (handled by the host window) closes either.
    /// </summary>
    public partial class FullscreenMediaViewer : UserControl
    {
        private static readonly TimeSpan SeekStep = TimeSpan.FromSeconds(5);

        private readonly bool _isVideo;

        public FullscreenMediaViewer(string path, bool isVideo)
        {
            _isVideo = isVideo;
            InitializeComponent();

            if (isVideo)
            {
                Img.Visibility = Visibility.Collapsed;
                Player.Visibility = Visibility.Visible;
                Transport.Visibility = Visibility.Visible;
                Transport.Attach(Player);
                PreviewKeyDown += FullscreenMediaViewer_PreviewKeyDown;
                Loaded += (_, __) =>
                {
                    Focus();
                    try
                    {
                        Player.Source = new Uri(path);
                        Transport.Play();
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
                Transport.TogglePlayPause();
            }
            else
            {
                RequestClose?.Invoke(this, EventArgs.Empty);
            }

            e.Handled = true;
        }

        private void StopVideo()
        {
            Transport.Reset();
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

        private void Player_MediaOpened(object sender, RoutedEventArgs e)
        {
            Transport.NotifyMediaOpened();
        }

        private void Player_MediaEnded(object sender, RoutedEventArgs e)
        {
            Transport.NotifyMediaEnded();
        }

        private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            ShowError();
        }

        private void ShowError()
        {
            Transport.Reset();
            Transport.Visibility = Visibility.Collapsed;
            Player.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Unlike the gallery, the lightbox has no previous/next, so the plain arrows seek here.
        /// </summary>
        private void FullscreenMediaViewer_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Left:
                    Transport.SeekBy(-SeekStep);
                    e.Handled = true;
                    break;
                case Key.Right:
                    Transport.SeekBy(SeekStep);
                    e.Handled = true;
                    break;
                case Key.Space:
                    Transport.TogglePlayPause();
                    e.Handled = true;
                    break;
            }
        }
    }
}
