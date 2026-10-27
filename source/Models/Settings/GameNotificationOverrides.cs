using System;
using PlayniteAchievements.Models.Achievements;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// The six capture rarity filters, shared by the global settings and a game's own capture
    /// settings so one editor serves both.
    /// </summary>
    public interface IUnlockCaptureRarities
    {
        RaritySelection UnlockScreenshotCleanRarities { get; set; }

        RaritySelection UnlockScreenshotWithToastRarities { get; set; }

        RaritySelection UnlockScreenshotFramedRarities { get; set; }

        RaritySelection UnlockRecordingCleanRarities { get; set; }

        RaritySelection UnlockRecordingRarities { get; set; }

        RaritySelection UnlockRecordingFramedRarities { get; set; }
    }

    /// <summary>
    /// A game's complete notification behavior snapshot. A null instance on
    /// <see cref="GameCustomDataFile"/> means the game follows its provider and the globals live;
    /// a set instance replaces them outright, master switch included. Property names match
    /// <see cref="PersistedSettings"/> so the settings section binds to either unchanged.
    /// </summary>
    /// <remarks>
    /// Fields carry no initializers: the store writes with DefaultValueHandling.Ignore, so an
    /// omitted value must read back as the CLR default it was written from.
    /// </remarks>
    public sealed class GameNotificationBehaviorOverride : ObservableObject
    {
        private bool _enableNotifications;
        private bool _enableUnlockToasts;
        private bool _enableFriendUnlockToasts;
        private bool _enableProgressToasts;
        private int _progressToastIntervalPercent;
        private double _notificationDelaySeconds;

        public bool EnableNotifications
        {
            get => _enableNotifications;
            set => SetValue(ref _enableNotifications, value);
        }

        public bool EnableUnlockToasts
        {
            get => _enableUnlockToasts;
            set => SetValue(ref _enableUnlockToasts, value);
        }

        public bool EnableFriendUnlockToasts
        {
            get => _enableFriendUnlockToasts;
            set => SetValue(ref _enableFriendUnlockToasts, value);
        }

        public bool EnableProgressToasts
        {
            get => _enableProgressToasts;
            set => SetValue(ref _enableProgressToasts, value);
        }

        public int ProgressToastIntervalPercent
        {
            get => _progressToastIntervalPercent;
            set => SetValue(ref _progressToastIntervalPercent, Math.Max(0, Math.Min(99, value)));
        }

        public double NotificationDelaySeconds
        {
            get => _notificationDelaySeconds;
            set => SetValue(ref _notificationDelaySeconds, Math.Max(0, value));
        }

        /// <summary>
        /// The values a game without its own snapshot resolves to: the globals, with the provider's
        /// stored toast switches in place of the global ones.
        /// </summary>
        public static GameNotificationBehaviorOverride FromEffective(
            PersistedSettings settings,
            ProviderNotificationOverride provider)
        {
            if (settings == null)
            {
                return new GameNotificationBehaviorOverride();
            }

            return new GameNotificationBehaviorOverride
            {
                EnableNotifications = settings.EnableNotifications,
                EnableUnlockToasts = provider?.UnlockToasts ?? settings.EnableUnlockToasts,
                EnableFriendUnlockToasts = provider?.FriendUnlockToasts ?? settings.EnableFriendUnlockToasts,
                EnableProgressToasts = provider?.ProgressToasts ?? settings.EnableProgressToasts,
                ProgressToastIntervalPercent = settings.ProgressToastIntervalPercent,
                NotificationDelaySeconds = settings.NotificationDelaySeconds
            };
        }

        public GameNotificationBehaviorOverride Clone()
        {
            return new GameNotificationBehaviorOverride
            {
                EnableNotifications = EnableNotifications,
                EnableUnlockToasts = EnableUnlockToasts,
                EnableFriendUnlockToasts = EnableFriendUnlockToasts,
                EnableProgressToasts = EnableProgressToasts,
                ProgressToastIntervalPercent = ProgressToastIntervalPercent,
                NotificationDelaySeconds = NotificationDelaySeconds
            };
        }
    }

    /// <summary>
    /// A game's complete unlock capture snapshot: the screenshot and clip variants with their
    /// rarity filters, and the capture timing. A null instance on <see cref="GameCustomDataFile"/>
    /// means the game follows its provider and the globals live; a set instance replaces them
    /// outright, master switches included. Directories, file naming and recording quality stay
    /// global. Property names match <see cref="PersistedSettings"/>.
    /// </summary>
    public sealed class GameCaptureOverride : ObservableObject, IUnlockCaptureRarities
    {
        private double _captureDelaySeconds;
        private bool _enableUnlockScreenshots;
        private bool _unlockScreenshotClean;
        private bool _unlockScreenshotWithToast;
        private bool _unlockScreenshotFramed;
        private RaritySelection _unlockScreenshotCleanRarities;
        private RaritySelection _unlockScreenshotWithToastRarities;
        private RaritySelection _unlockScreenshotFramedRarities;
        private bool _unlockScreenshotCleanAlwaysCaptureCompletion;
        private bool _unlockScreenshotWithToastAlwaysCaptureCompletion;
        private bool _unlockScreenshotFramedAlwaysCaptureCompletion;
        private bool _enableUnlockRecordings;
        private bool _unlockRecordingClean;
        private bool _unlockRecordingWithToast;
        private bool _unlockRecordingFramed;
        private RaritySelection _unlockRecordingCleanRarities;
        private RaritySelection _unlockRecordingRarities;
        private RaritySelection _unlockRecordingFramedRarities;
        private bool _unlockRecordingCleanAlwaysCaptureCompletion;
        private bool _unlockRecordingAlwaysCaptureCompletion;
        private bool _unlockRecordingFramedAlwaysCaptureCompletion;
        private int? _unlockRecordingFramedSeconds;
        private bool _recordingIncludeAudio;
        private string _captureDirectory;

        public double CaptureDelaySeconds
        {
            get => _captureDelaySeconds;
            set => SetValue(ref _captureDelaySeconds, Math.Max(0, value));
        }

        public bool EnableUnlockScreenshots
        {
            get => _enableUnlockScreenshots;
            set => SetValue(ref _enableUnlockScreenshots, value);
        }

        public bool UnlockScreenshotClean
        {
            get => _unlockScreenshotClean;
            set => SetValue(ref _unlockScreenshotClean, value);
        }

        public bool UnlockScreenshotWithToast
        {
            get => _unlockScreenshotWithToast;
            set => SetValue(ref _unlockScreenshotWithToast, value);
        }

        public bool UnlockScreenshotFramed
        {
            get => _unlockScreenshotFramed;
            set => SetValue(ref _unlockScreenshotFramed, value);
        }

        public RaritySelection UnlockScreenshotCleanRarities
        {
            get => _unlockScreenshotCleanRarities;
            set => SetValue(ref _unlockScreenshotCleanRarities, value);
        }

        public RaritySelection UnlockScreenshotWithToastRarities
        {
            get => _unlockScreenshotWithToastRarities;
            set => SetValue(ref _unlockScreenshotWithToastRarities, value);
        }

        public RaritySelection UnlockScreenshotFramedRarities
        {
            get => _unlockScreenshotFramedRarities;
            set => SetValue(ref _unlockScreenshotFramedRarities, value);
        }

        public bool UnlockScreenshotCleanAlwaysCaptureCompletion
        {
            get => _unlockScreenshotCleanAlwaysCaptureCompletion;
            set => SetValue(ref _unlockScreenshotCleanAlwaysCaptureCompletion, value);
        }

        public bool UnlockScreenshotWithToastAlwaysCaptureCompletion
        {
            get => _unlockScreenshotWithToastAlwaysCaptureCompletion;
            set => SetValue(ref _unlockScreenshotWithToastAlwaysCaptureCompletion, value);
        }

        public bool UnlockScreenshotFramedAlwaysCaptureCompletion
        {
            get => _unlockScreenshotFramedAlwaysCaptureCompletion;
            set => SetValue(ref _unlockScreenshotFramedAlwaysCaptureCompletion, value);
        }

        public bool EnableUnlockRecordings
        {
            get => _enableUnlockRecordings;
            set => SetValue(ref _enableUnlockRecordings, value);
        }

        public bool UnlockRecordingClean
        {
            get => _unlockRecordingClean;
            set => SetValue(ref _unlockRecordingClean, value);
        }

        public bool UnlockRecordingWithToast
        {
            get => _unlockRecordingWithToast;
            set => SetValue(ref _unlockRecordingWithToast, value);
        }

        public bool UnlockRecordingFramed
        {
            get => _unlockRecordingFramed;
            set => SetValue(ref _unlockRecordingFramed, value);
        }

        public RaritySelection UnlockRecordingCleanRarities
        {
            get => _unlockRecordingCleanRarities;
            set => SetValue(ref _unlockRecordingCleanRarities, value);
        }

        /// <summary>The with-notification clip's rarity filter, named as in <see cref="PersistedSettings"/>.</summary>
        public RaritySelection UnlockRecordingRarities
        {
            get => _unlockRecordingRarities;
            set => SetValue(ref _unlockRecordingRarities, value);
        }

        public RaritySelection UnlockRecordingFramedRarities
        {
            get => _unlockRecordingFramedRarities;
            set => SetValue(ref _unlockRecordingFramedRarities, value);
        }

        public bool UnlockRecordingCleanAlwaysCaptureCompletion
        {
            get => _unlockRecordingCleanAlwaysCaptureCompletion;
            set => SetValue(ref _unlockRecordingCleanAlwaysCaptureCompletion, value);
        }

        public bool UnlockRecordingAlwaysCaptureCompletion
        {
            get => _unlockRecordingAlwaysCaptureCompletion;
            set => SetValue(ref _unlockRecordingAlwaysCaptureCompletion, value);
        }

        public bool UnlockRecordingFramedAlwaysCaptureCompletion
        {
            get => _unlockRecordingFramedAlwaysCaptureCompletion;
            set => SetValue(ref _unlockRecordingFramedAlwaysCaptureCompletion, value);
        }

        public int? UnlockRecordingFramedSeconds
        {
            get => _unlockRecordingFramedSeconds;
            set => SetValue(ref _unlockRecordingFramedSeconds, value.HasValue && value.Value > 0 ? value : null);
        }

        /// <summary>
        /// The folder this game's screenshots and clips are saved straight into, with no game
        /// subfolder. Null keeps the global layout (&lt;capture folder&gt;\&lt;Game&gt;).
        /// </summary>
        public string CaptureDirectory
        {
            get => _captureDirectory;
            set => SetValue(ref _captureDirectory, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
        }

        /// <summary>
        /// Record audio into this game's clips. Read when its recording session starts; the audio
        /// source and microphone stay global.
        /// </summary>
        public bool RecordingIncludeAudio
        {
            get => _recordingIncludeAudio;
            set => SetValue(ref _recordingIncludeAudio, value);
        }

        /// <summary>
        /// The values a game without its own snapshot resolves to: the globals, with the provider's
        /// stored screenshot and clip switches in place of the global ones. A clip variant without
        /// its own provider value is off when the provider's legacy all-clips switch is off.
        /// </summary>
        public static GameCaptureOverride FromEffective(
            PersistedSettings settings,
            ProviderNotificationOverride provider)
        {
            if (settings == null)
            {
                return new GameCaptureOverride();
            }

            return new GameCaptureOverride
            {
                CaptureDelaySeconds = settings.CaptureDelaySeconds,
                EnableUnlockScreenshots = settings.EnableUnlockScreenshots,
                UnlockScreenshotClean = provider?.ScreenshotClean ?? settings.UnlockScreenshotClean,
                UnlockScreenshotWithToast = provider?.ScreenshotWithToast ?? settings.UnlockScreenshotWithToast,
                UnlockScreenshotFramed = provider?.ScreenshotFramed ?? settings.UnlockScreenshotFramed,
                UnlockScreenshotCleanRarities = settings.UnlockScreenshotCleanRarities,
                UnlockScreenshotWithToastRarities = settings.UnlockScreenshotWithToastRarities,
                UnlockScreenshotFramedRarities = settings.UnlockScreenshotFramedRarities,
                UnlockScreenshotCleanAlwaysCaptureCompletion = settings.UnlockScreenshotCleanAlwaysCaptureCompletion,
                UnlockScreenshotWithToastAlwaysCaptureCompletion = settings.UnlockScreenshotWithToastAlwaysCaptureCompletion,
                UnlockScreenshotFramedAlwaysCaptureCompletion = settings.UnlockScreenshotFramedAlwaysCaptureCompletion,
                EnableUnlockRecordings = settings.EnableUnlockRecordings,
                UnlockRecordingClean = ResolveClipVariant(provider, provider?.RecordingClean, settings.UnlockRecordingClean),
                UnlockRecordingWithToast = ResolveClipVariant(provider, provider?.RecordingWithToast, settings.UnlockRecordingWithToast),
                UnlockRecordingFramed = ResolveClipVariant(provider, provider?.RecordingFramed, settings.UnlockRecordingFramed),
                UnlockRecordingCleanRarities = settings.UnlockRecordingCleanRarities,
                UnlockRecordingRarities = settings.UnlockRecordingRarities,
                UnlockRecordingFramedRarities = settings.UnlockRecordingFramedRarities,
                UnlockRecordingCleanAlwaysCaptureCompletion = settings.UnlockRecordingCleanAlwaysCaptureCompletion,
                UnlockRecordingAlwaysCaptureCompletion = settings.UnlockRecordingAlwaysCaptureCompletion,
                UnlockRecordingFramedAlwaysCaptureCompletion = settings.UnlockRecordingFramedAlwaysCaptureCompletion,
                UnlockRecordingFramedSeconds = settings.UnlockRecordingFramedSeconds,
                RecordingIncludeAudio = settings.RecordingIncludeAudio
            };
        }

        /// <summary>
        /// A clip variant's own provider value, else off when the provider's stored all-clips
        /// switch is off, else the global variant setting.
        /// </summary>
        private static bool ResolveClipVariant(ProviderNotificationOverride provider, bool? own, bool global)
        {
            if (own.HasValue)
            {
                return own.Value;
            }

            return provider?.Recordings != false && global;
        }

        public GameCaptureOverride Clone()
        {
            return new GameCaptureOverride
            {
                CaptureDelaySeconds = CaptureDelaySeconds,
                EnableUnlockScreenshots = EnableUnlockScreenshots,
                UnlockScreenshotClean = UnlockScreenshotClean,
                UnlockScreenshotWithToast = UnlockScreenshotWithToast,
                UnlockScreenshotFramed = UnlockScreenshotFramed,
                UnlockScreenshotCleanRarities = UnlockScreenshotCleanRarities,
                UnlockScreenshotWithToastRarities = UnlockScreenshotWithToastRarities,
                UnlockScreenshotFramedRarities = UnlockScreenshotFramedRarities,
                UnlockScreenshotCleanAlwaysCaptureCompletion = UnlockScreenshotCleanAlwaysCaptureCompletion,
                UnlockScreenshotWithToastAlwaysCaptureCompletion = UnlockScreenshotWithToastAlwaysCaptureCompletion,
                UnlockScreenshotFramedAlwaysCaptureCompletion = UnlockScreenshotFramedAlwaysCaptureCompletion,
                EnableUnlockRecordings = EnableUnlockRecordings,
                UnlockRecordingClean = UnlockRecordingClean,
                UnlockRecordingWithToast = UnlockRecordingWithToast,
                UnlockRecordingFramed = UnlockRecordingFramed,
                UnlockRecordingCleanRarities = UnlockRecordingCleanRarities,
                UnlockRecordingRarities = UnlockRecordingRarities,
                UnlockRecordingFramedRarities = UnlockRecordingFramedRarities,
                UnlockRecordingCleanAlwaysCaptureCompletion = UnlockRecordingCleanAlwaysCaptureCompletion,
                UnlockRecordingAlwaysCaptureCompletion = UnlockRecordingAlwaysCaptureCompletion,
                UnlockRecordingFramedAlwaysCaptureCompletion = UnlockRecordingFramedAlwaysCaptureCompletion,
                UnlockRecordingFramedSeconds = UnlockRecordingFramedSeconds,
                RecordingIncludeAudio = RecordingIncludeAudio,
                CaptureDirectory = CaptureDirectory
            };
        }
    }
}
