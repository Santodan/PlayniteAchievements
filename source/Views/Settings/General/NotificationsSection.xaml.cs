using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Views.Settings.General
{
    /// <summary>
    /// General settings: Notifications section. Hosts notification toggles, the live toast
    /// mockup and example toast buttons, toast behavior/appearance options, and screenshots.
    /// </summary>
    public partial class NotificationsSection : UserControl, IDisposable
    {
        /// <summary>
        /// Persisted setting names that affect the toast mockup rendering.
        /// </summary>
        private static readonly string[] ToastRefreshProperties =
        {
            nameof(PersistedSettings.ShowCompactListRarityBar),
            nameof(PersistedSettings.ShowHiddenIcon),
            nameof(PersistedSettings.ShowHiddenTitle),
            nameof(PersistedSettings.ShowHiddenDescription),
            nameof(PersistedSettings.ShowHiddenSuffix),
            nameof(PersistedSettings.ShowLockedIcon),
            nameof(PersistedSettings.UseSeparateLockedIconsWhenAvailable),
            nameof(PersistedSettings.UseUniformRarityBadges),
            nameof(PersistedSettings.RarityColors),
            nameof(PersistedSettings.ToastShowHeader),
            nameof(PersistedSettings.ToastShowName),
            nameof(PersistedSettings.ToastShowRarityBadge),
            nameof(PersistedSettings.ToastShowRarityGlow),
            nameof(PersistedSettings.ToastRarityColoredName),
            nameof(PersistedSettings.ToastShowRarityPercent),
            nameof(PersistedSettings.ToastShowDescription),
            nameof(PersistedSettings.ToastShowCategory),
            nameof(PersistedSettings.ToastShowGameName),
            nameof(PersistedSettings.ToastShowUnlockTime),
            nameof(PersistedSettings.FrameShowHeader),
            nameof(PersistedSettings.FrameShowName),
            nameof(PersistedSettings.FrameShowDescription),
            nameof(PersistedSettings.FrameShowCategory),
            nameof(PersistedSettings.FrameShowGameName),
            nameof(PersistedSettings.FrameShowRarityBadge),
            nameof(PersistedSettings.FrameShowRarityPercent),
            nameof(PersistedSettings.FrameShowRarityGlow),
            nameof(PersistedSettings.FrameRarityColoredName),
            nameof(PersistedSettings.FrameShowUnlockTime)
        };

        private readonly PlayniteAchievementsSettings _settings;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly AchievementToastTemplateResolver _toastTemplateResolver;
        private readonly PersistedSettingsSubscription _persistedSubscription;
        private readonly ProviderNotificationSettingsViewModel _providerOverridesViewModel;
        private readonly Services.Recording.FfmpegValidationService _ffmpegValidation;
        private Window _framePreviewWindow;

        public NotificationsSection()
        {
            InitializeComponent();
        }

        internal NotificationsSection(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ILogger logger)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));

            _toastTemplateResolver = new AchievementToastTemplateResolver(plugin.PlayniteApi, logger);
            _ffmpegValidation = new Services.Recording.FfmpegValidationService(logger);

            _persistedSubscription = new PersistedSettingsSubscription(
                _settings,
                OnPersistedPropertyChanged,
                UpdateToastMockup);

            // The overrides grid is a DataContext island: its view model is independent of this
            // section's settings DataContext, and its ItemsSource is never reset in code-behind.
            _providerOverridesViewModel = new ProviderNotificationSettingsViewModel(
                settings,
                plugin,
                plugin.ProviderRegistry,
                logger);
            ProviderOverridesGrid.DataContext = _providerOverridesViewModel;

            Loaded += (s, e) => UpdateToastMockup();
        }

        private void OnPersistedPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (ToastRefreshProperties.Contains(e.PropertyName))
            {
                UpdateToastMockup();
            }
        }

        /// <summary>
        /// Rebuilds the inline toast mockup from the current persisted settings so the preview
        /// reflects appearance toggles (glow, rarity color, shown fields, badge colors) live.
        /// </summary>
        private void UpdateToastMockup()
        {
            if (ToastMockupHost == null)
            {
                return;
            }

            var persisted = _settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            ToastMockupHost.ContentTemplate = _toastTemplateResolver.ResolveTemplate();
            ToastMockupHost.Content = new AchievementToastViewModel(BuildToastPreviewArgs("mockup"), persisted);
            UpdateFrameMockup(persisted);
        }

        /// <summary>
        /// Rebuilds the inline screenshot-frame mockup (shown when the framed variant is enabled)
        /// from the resolved frame template and the same sample view model as the toast mockup.
        /// </summary>
        private void UpdateFrameMockup(PersistedSettings persisted)
        {
            if (FrameMockupHost == null || persisted == null)
            {
                return;
            }

            FrameMockupHost.ContentTemplate = _toastTemplateResolver.ResolveFrameTemplate();
            FrameMockupHost.Content = new AchievementToastViewModel(BuildToastPreviewArgs("mockup"), persisted);
        }

        /// <summary>
        /// Shows the screenshot frame full-monitor over Playnite so themes can be checked at real
        /// scale. Reproduces the compositor's 1080-DIP virtual canvas exactly (Viewbox Fill onto
        /// the monitor), so what is shown matches what gets stamped onto saved images. Dismissed
        /// by click, Escape, or a 10s auto-close timer.
        /// </summary>
        private void FramePreviewButton_Click(object sender, RoutedEventArgs e)
        {
            var kind = (sender as Button)?.CommandParameter as string ?? "mockup";
            var persisted = _settings?.Persisted;
            if (persisted == null)
            {
                return;
            }

            CloseFramePreview();

            var template = _toastTemplateResolver.ResolveFrameTemplate();
            if (template == null)
            {
                return;
            }

            var window = Views.Helpers.PlayniteUiProvider.CreateBorderlessTopmostWindow(
                _plugin.PlayniteApi,
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"));
            window.SizeToContent = SizeToContent.Manual;
            window.ShowActivated = true;
            window.Focusable = true;

            var reference = _plugin.PlayniteApi?.Dialogs?.GetCurrentAppWindow() ?? Window.GetWindow(this);
            var monitorPixels = Views.Helpers.PlayniteUiProvider.PlaceOnWindowMonitor(window, reference);
            if (monitorPixels == null)
            {
                return;
            }

            var (canvasWidth, canvasHeight, _) = ScreenshotFrameCompositor.ComputeCanvas(
                monitorPixels.Value.Width,
                monitorPixels.Value.Height);
            var canvas = new Grid
            {
                Width = canvasWidth,
                Height = canvasHeight,
                // Almost-transparent so the live screen shows through while the window still
                // receives the dismissing click (fully transparent pixels are not hit-testable).
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromArgb(1, 0, 0, 0)),
            };
            canvas.Children.Add(new ContentControl
            {
                Content = new AchievementToastViewModel(BuildToastPreviewArgs(kind), persisted),
                ContentTemplate = template,
            });
            window.Content = new System.Windows.Controls.Viewbox
            {
                Stretch = System.Windows.Media.Stretch.Fill,
                Child = canvas,
            };

            var autoClose = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(10),
            };
            autoClose.Tick += (s, args) => window.Close();
            window.PreviewMouseDown += (s, args) => window.Close();
            window.PreviewKeyDown += (s, args) =>
            {
                if (args.Key == System.Windows.Input.Key.Escape)
                {
                    args.Handled = true;
                    window.Close();
                }
            };
            window.Closed += (s, args) =>
            {
                autoClose.Stop();
                if (ReferenceEquals(_framePreviewWindow, window))
                {
                    _framePreviewWindow = null;
                }
            };

            _framePreviewWindow = window;
            window.Show();
            window.Focus();
            autoClose.Start();
        }

        private void CloseFramePreview()
        {
            try
            {
                _framePreviewWindow?.Close();
            }
            catch
            {
            }

            _framePreviewWindow = null;
        }

        private void ShowToastPreview(AchievementUnlockedEventArgs args)
        {
            if (args == null)
            {
                return;
            }

            PlayniteAchievementsPlugin.NotifyAchievementUnlocked(args);
        }

        private AchievementUnlockedEventArgs BuildToastPreviewArgs(string kind)
        {
            var sampleGame = L("LOCPlayAch_Settings_ToastPreviewSampleGame");
            var sampleCategory = L("LOCPlayAch_Settings_ToastPreviewSampleCategory");
            var sampleTitle = L("LOCPlayAch_Settings_ToastPreviewSampleTitle");
            var sampleDescription = L("LOCPlayAch_Settings_ToastPreviewSampleDescription");

            switch (kind)
            {
                case "common":
                    return SampleUnlock("Common", 61.4, false);
                case "uncommon":
                    return SampleUnlock("Uncommon", 28.7, false);
                case "rare":
                    return SampleUnlock("Rare", 9.3, false);
                case "ultrarare":
                    return SampleUnlock("UltraRare", 1.8, false);
                case "capstone":
                    var capstone = SampleUnlock("UltraRare", 1.2, true);
                    capstone.IsCompletionAchievement = true;
                    return capstone;
                case "complete":
                    // The standalone completion notification (own wave after unlock toasts).
                    return new AchievementUnlockedEventArgs
                    {
                        IsPreview = true,
                        GameName = sampleGame,
                        UnlockedCount = 40,
                        TotalCount = 40,
                        UnlockTimeUtc = DateTime.UtcNow,
                        IsGameCompleted = true
                    };
                case "friend":
                    var friend = SampleUnlock("Rare", 7.5, false);
                    friend.IsFriendUnlock = true;
                    friend.FriendDisplayName = L("LOCPlayAch_Settings_ToastPreviewSampleFriend");
                    friend.FriendAvatarUrl =
                        "pack://application:,,,/PlayniteAchievements;component/Resources/UnlockedAchIcon.png";
                    return friend;
                case "mockup":
                default:
                    return SampleUnlock("Rare", 9.3, false);
            }

            AchievementUnlockedEventArgs SampleUnlock(string rarity, double percent, bool capstone)
            {
                return new AchievementUnlockedEventArgs
                {
                    IsPreview = true,
                    GameName = sampleGame,
                    Category = sampleCategory,
                    DisplayName = sampleTitle,
                    Description = sampleDescription,
                    RarityTier = rarity,
                    GlobalPercent = percent,
                    IsCapstone = capstone,
                    UnlockedCount = 27,
                    TotalCount = 40,
                    UnlockTimeUtc = DateTime.UtcNow.AddMinutes(-3)
                };
            }
        }

        private void ToastPreviewButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { CommandParameter: string rarity })
            {
                ShowToastPreview(BuildToastPreviewArgs(rarity));
            }
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

        public void Dispose()
        {
            _persistedSubscription?.Dispose();
            _providerOverridesViewModel?.Dispose();
            CloseFramePreview();
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }
}
