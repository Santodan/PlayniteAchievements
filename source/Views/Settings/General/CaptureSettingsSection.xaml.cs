using System;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;

namespace PlayniteAchievements.Views.Settings.General
{
    /// <summary>
    /// General settings: Screenshots and recordings section. Hosts the unlock screenshot
    /// options (variants, suffixes, folder) and the ffmpeg-based unlock recording options.
    /// DataContext (the settings object) is inherited from the settings view.
    /// </summary>
    public partial class CaptureSettingsSection : UserControl
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly Services.Recording.FfmpegValidationService _ffmpegValidation;

        public CaptureSettingsSection()
        {
            InitializeComponent();
        }

        internal CaptureSettingsSection(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ILogger logger)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _ffmpegValidation = new Services.Recording.FfmpegValidationService(logger);
        }

        private void ScreenshotDirectory_Browse_Click(object sender, RoutedEventArgs e)
        {
            var settings = _settings?.Persisted;
            if (settings == null)
            {
                return;
            }

            var selected = _plugin?.PlayniteApi?.Dialogs?.SelectFolder();
            if (!string.IsNullOrWhiteSpace(selected))
            {
                settings.UnlockScreenshotDirectory = selected;
            }
        }

        private void RecordingDirectory_Browse_Click(object sender, RoutedEventArgs e)
        {
            var settings = _settings?.Persisted;
            if (settings == null)
            {
                return;
            }

            var selected = _plugin?.PlayniteApi?.Dialogs?.SelectFolder();
            if (!string.IsNullOrWhiteSpace(selected))
            {
                settings.UnlockRecordingDirectory = selected;
            }
        }

        private void FfmpegPath_Browse_Click(object sender, RoutedEventArgs e)
        {
            var settings = _settings?.Persisted;
            if (settings == null)
            {
                return;
            }

            var selected = _plugin?.PlayniteApi?.Dialogs?.SelectFile("ffmpeg|ffmpeg.exe|Executable|*.exe");
            if (!string.IsNullOrWhiteSpace(selected))
            {
                settings.FfmpegPath = selected;
            }
        }

        /// <summary>
        /// Runs the ffmpeg validation (version + encoder probes + a 1s screen-capture smoke
        /// test) and reports the outcome in the status line. The button is disabled while the
        /// probes run; results are cached per path for the session.
        /// </summary>
        private async void FfmpegTest_Click(object sender, RoutedEventArgs e)
        {
            var path = _settings?.Persisted?.FfmpegPath;
            if (_ffmpegValidation == null || FfmpegTestButton == null || FfmpegStatusText == null)
            {
                return;
            }

            FfmpegTestButton.IsEnabled = false;
            try
            {
                var result = await _ffmpegValidation.ValidateAsync(path, runSmokeTest: true);
                if (result?.IsValid == true)
                {
                    FfmpegStatusText.Text = string.Format(
                        ResourceProvider.GetString("LOCPlayAch_Settings_RecordingFfmpegValid"),
                        result.Version,
                        string.Join(", ", result.AvailableEncoders));
                    // Back to the muted style's own foreground for the success case.
                    FfmpegStatusText.ClearValue(TextBlock.ForegroundProperty);
                }
                else
                {
                    FfmpegStatusText.Text = string.Format(
                        ResourceProvider.GetString("LOCPlayAch_Settings_RecordingFfmpegInvalid"),
                        result?.Error ?? string.Empty);
                    if (TryFindResource("PlayAch.Brush.ErrorText") is System.Windows.Media.Brush errorBrush)
                    {
                        FfmpegStatusText.Foreground = errorBrush;
                    }
                }

                FfmpegStatusText.Visibility = Visibility.Visible;
            }
            catch (Exception)
            {
                // Validation never throws by design; guard the async-void boundary anyway.
            }
            finally
            {
                FfmpegTestButton.IsEnabled = true;
            }
        }
    }
}
