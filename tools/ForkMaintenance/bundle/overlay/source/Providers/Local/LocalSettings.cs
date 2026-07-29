using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Providers.Local
{
    public enum LocalSteamSchemaPreference
    {
        PreferSteam = 0,
        PreferSteamHunters = 1,
        PreferSteamCommunity = 2,
        PreferCompletionist = 3
    }

    public enum LocalImportedGameLibraryTarget
    {
        None = 0,
        Steam = 1,
        CustomSource = 2
    }

    public enum LocalExistingGameImportBehavior
    {
        OverwriteExisting = 0,
        SkipExisting = 1
    }

    public enum LocalIconRateLimitRetryMode
    {
        None = 0,
        FixedRounds = 1,
        Infinite = 2
    }

    public enum LocalUnlockScreenshotCaptureMode
    {
        FullDesktop = 0,
        ActiveWindow = 1
    }

    public enum LocalUnlockScreenshotImageFormat
    {
        Png = 0,
        Jpeg = 1
    }

    public enum LocalUnlockNotificationDeliveryMode
    {
        WindowsToast = 0,
        Overlay = 1,
        Hybrid = 2
    }

    public sealed class ScoreProgressNotificationSettings
    {
        public bool NotifyLevelUp { get; set; }
        public bool NotifyTierUp { get; set; }
        public int CustomStyleSlotIndex { get; set; }
        public string SoundPath { get; set; } = string.Empty;
        public int SoundLeadMilliseconds { get; set; }
    }

    public enum LocalUnlockOverlayPosition
    {
        TopRight = 0,
        TopLeft = 1,
        BottomRight = 2,
        BottomLeft = 3,
        TopCenter = 4,
        BottomCenter = 5
    }

    public enum LocalOverlayCoverPosition
    {
        None = 0,
        Left = 1,
        Right = 2
    }

    public enum LocalUnlockOverlayTransitionStyle
    {
        Fade = 0,
        SlideFromRight = 1,
        SlideFromLeft = 2,
        SlideFromTop = 3,
        SlideFromBottom = 4,
        Circle = 5,
        SanDefault = 6,
        SanXqjan = 7,
        SanDeck = 8,
        SanEpic = 9,
        SanXboxModern = 10,
        SanXboxClassic = 11,
        SanPsModern = 12,
        SanPsClassic = 13,
        SanPsRetro = 14,
        SanSquare = 15,
        SanAero = 16
    }

    public enum LocalOverlayIconSource
    {
        None = -1,
        AchievementIcon = 0,
        TrophyIcon = 1,
        PentagonIcon = 2,
        ProviderIcon = 3,
        CustomIcon = 4
    }

    public enum LocalCustomOverlayLayoutStyle
    {
        Standard = 0,
        XboxModern = 1,
        XboxClassic = 2
    }

    public enum LocalCustomOverlayAnimationStyle
    {
        Standard = 0,
        SanXqjan = 1,
        SanExpandCard = 2,
        SanSlideCard = 3,
        SanEpic = 4,
        SanAero = 5,
        SanDefault = 6
    }

    public enum LocalSanProviderLogoSource
    {
        ExtensionIcon = 0,
        SanLogo = 1
    }

    public enum LocalSanElementPosition
    {
        Left = 0,
        Right = 1
    }

    public enum LocalSanTextViewTarget
    {
        View1 = 0,
        View2 = 1,
        Both = 2
    }

    public sealed class LocalMetadataSourceOption
    {
        public LocalMetadataSourceOption(string id, string displayName)
        {
            Id = id ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
        }

        public string Id { get; }

        public string DisplayName { get; }
    }

    public sealed class LocalSteamAppCacheUserOption
    {
        public LocalSteamAppCacheUserOption(string userId, string displayName)
        {
            UserId = userId ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
        }

        public string UserId { get; }

        public string DisplayName { get; }
    }

    public sealed class LocalCustomOverlayStyleSlot
    {
        public string Name { get; set; } = string.Empty;
        public bool AutoResizeToContent { get; set; }
        public bool WrapAllText { get; set; }
        public bool ShowLine1 { get; set; } = true;
        public bool ShowBorder { get; set; } = true;
        public bool ShowGameName { get; set; }
        public bool ShowMeta { get; set; }
        public double IconSize { get; set; } = 58;
        public double SecondaryIconSize { get; set; } = 58;
        public double IconCornerRadius { get; set; } = 10;
        public double SecondaryIconCornerRadius { get; set; } = 10;
        public double Width { get; set; } = 460;
        public double Height { get; set; } = 128;
        public double CornerRadius { get; set; } = 18;
        public double TitleFontSize { get; set; } = 17;
        public double DetailFontSize { get; set; } = 13;
        public double MetaFontSize { get; set; } = 11;
        public double Line4FontSize { get; set; } = 11;
        public double Line5FontSize { get; set; } = 11;
        public double Line6FontSize { get; set; } = 11;
        public string BackgroundColor { get; set; } = "#1E2430";
        public string BorderColor { get; set; } = "#6FA3D8";
        public string AccentColor { get; set; } = "#A7E0FF";
        public string TitleColor { get; set; } = "#FFFFFF";
        public string DetailColor { get; set; } = "#E7EEF7";
        public string MetaColor { get; set; } = "#BCD0E5";
        public string BackgroundImagePath { get; set; } = string.Empty;
        public string TitleTemplate { get; set; } = "Achievement unlocked";
        public string GameNameTemplate { get; set; } = "<gameName>";
        public string AchievementTemplate { get; set; } = "Unlocked: <achievementName>";
        public string MetaTemplate { get; set; } = "<provider> / Custom";
        public string Line4Template { get; set; } = string.Empty;
        public string Line5Template { get; set; } = string.Empty;
        public string Line6Template { get; set; } = string.Empty;
        public bool ShowLine4 { get; set; }
        public bool ShowLine5 { get; set; }
        public bool ShowLine6 { get; set; }
        public string Line4Color { get; set; } = "#BCD0E5";
        public string Line5Color { get; set; } = "#BCD0E5";
        public string Line6Color { get; set; } = "#BCD0E5";
        public string Line1OutlineColor { get; set; } = "transparent";
        public string Line2OutlineColor { get; set; } = "transparent";
        public string Line3OutlineColor { get; set; } = "transparent";
        public string Line4OutlineColor { get; set; } = "transparent";
        public string Line5OutlineColor { get; set; } = "transparent";
        public string Line6OutlineColor { get; set; } = "transparent";
        public string Line1ShadowColor { get; set; } = "transparent";
        public string Line2ShadowColor { get; set; } = "transparent";
        public string Line3ShadowColor { get; set; } = "transparent";
        public string Line4ShadowColor { get; set; } = "transparent";
        public string Line5ShadowColor { get; set; } = "transparent";
        public string Line6ShadowColor { get; set; } = "transparent";
        public double OutlineSize { get; set; } = 0.6;
        public double ShadowSize { get; set; } = 2;
        public double Line1OutlineSize { get; set; } = 0.6;
        public double Line2OutlineSize { get; set; } = 0.6;
        public double Line3OutlineSize { get; set; } = 0.6;
        public double Line4OutlineSize { get; set; } = 0.6;
        public double Line5OutlineSize { get; set; } = 0.6;
        public double Line6OutlineSize { get; set; } = 0.6;
        public double Line1ShadowSize { get; set; } = 2;
        public double Line2ShadowSize { get; set; } = 2;
        public double Line3ShadowSize { get; set; } = 2;
        public double Line4ShadowSize { get; set; } = 2;
        public double Line5ShadowSize { get; set; } = 2;
        public double Line6ShadowSize { get; set; } = 2;
        public bool Line1OutlineEnabled { get; set; }
        public bool Line2OutlineEnabled { get; set; }
        public bool Line3OutlineEnabled { get; set; }
        public bool Line4OutlineEnabled { get; set; }
        public bool Line5OutlineEnabled { get; set; }
        public bool Line6OutlineEnabled { get; set; }
        public bool Line1ShadowEnabled { get; set; }
        public bool Line2ShadowEnabled { get; set; }
        public bool Line3ShadowEnabled { get; set; }
        public bool Line4ShadowEnabled { get; set; }
        public bool Line5ShadowEnabled { get; set; }
        public bool Line6ShadowEnabled { get; set; }
        public double LineSpacing { get; set; } = 3;
        public double Line1Spacing { get; set; } = 3;
        public double Line2Spacing { get; set; } = 3;
        public double Line3Spacing { get; set; } = 3;
        public double Line4Spacing { get; set; } = 3;
        public double Line5Spacing { get; set; } = 3;
        public double Line6Spacing { get; set; } = 3;
        public LocalSanTextViewTarget Line1View { get; set; } = LocalSanTextViewTarget.View1;
        public LocalSanTextViewTarget Line2View { get; set; } = LocalSanTextViewTarget.View2;
        public LocalSanTextViewTarget Line3View { get; set; } = LocalSanTextViewTarget.View2;
        public LocalSanTextViewTarget Line4View { get; set; } = LocalSanTextViewTarget.View2;
        public LocalSanTextViewTarget Line5View { get; set; } = LocalSanTextViewTarget.View2;
        public LocalSanTextViewTarget Line6View { get; set; } = LocalSanTextViewTarget.View2;
        public int Line1Order { get; set; } = 1;
        public int Line2Order { get; set; } = 1;
        public int Line3Order { get; set; } = 2;
        public int Line4Order { get; set; } = 3;
        public int Line5Order { get; set; } = 4;
        public int Line6Order { get; set; } = 5;
        public LocalCustomOverlayLayoutStyle LayoutStyle { get; set; } = LocalCustomOverlayLayoutStyle.Standard;
        public LocalCustomOverlayAnimationStyle AnimationStyle { get; set; } = LocalCustomOverlayAnimationStyle.Standard;
        public LocalOverlayIconSource SecondaryIconSource { get; set; } = LocalOverlayIconSource.None;
        public bool ShowSecondaryIcon { get; set; }
        public string SanElementPresetId { get; set; } = string.Empty;
        public LocalSanElementPosition SanElementPosition { get; set; } = LocalSanElementPosition.Left;
        public LocalSanProviderLogoSource SanProviderLogoSource { get; set; } = LocalSanProviderLogoSource.ExtensionIcon;
        public LocalUnlockOverlayTransitionStyle TransitionStyle { get; set; } = LocalUnlockOverlayTransitionStyle.Fade;
        public string SanPresetId { get; set; } = string.Empty;
        public string SanPresetHtml { get; set; } = string.Empty;
        public string SanPresetCss { get; set; } = string.Empty;
        public string SanBaseCss { get; set; } = string.Empty;
        public string SanBaseAnimCss { get; set; } = string.Empty;
        public string SanAssetRootPath { get; set; } = string.Empty;
        public string SanThemeJson { get; set; } = string.Empty;
        public string SanThemeDirectory { get; set; } = string.Empty;
        public string ManualElementCss { get; set; } = string.Empty;
        public int SanView1DurationMilliseconds { get; set; } = 5000;
        public int SanView2DurationMilliseconds { get; set; } = 5000;
        public bool TitleBold { get; set; } = true;
        public bool TitleItalic { get; set; }
        public bool TitleUnderline { get; set; }
        public bool TitleStrikethrough { get; set; }
        public bool DetailBold { get; set; }
        public bool DetailItalic { get; set; }
        public bool DetailUnderline { get; set; }
        public bool DetailStrikethrough { get; set; }
        public bool MetaBold { get; set; } = true;
        public bool MetaItalic { get; set; }
        public bool MetaUnderline { get; set; }
        public bool MetaStrikethrough { get; set; }
        public bool Line4Bold { get; set; }
        public bool Line4Italic { get; set; }
        public bool Line4Underline { get; set; }
        public bool Line4Strikethrough { get; set; }
        public bool Line5Bold { get; set; }
        public bool Line5Italic { get; set; }
        public bool Line5Underline { get; set; }
        public bool Line5Strikethrough { get; set; }
        public bool Line6Bold { get; set; }
        public bool Line6Italic { get; set; }
        public bool Line6Underline { get; set; }
        public bool Line6Strikethrough { get; set; }
        public LocalOverlayIconSource IconSource { get; set; } = LocalOverlayIconSource.AchievementIcon;
        public string CustomIconPath { get; set; } = string.Empty;
        public string SecondaryCustomIconPath { get; set; } = string.Empty;
        public bool ShowIconRarityGlow { get; set; }
        public bool ShowSecondaryIconRarityGlow { get; set; }
        public bool EnableGameCoverInOverlay { get; set; }
        public LocalOverlayCoverPosition GameCoverPosition { get; set; } = LocalOverlayCoverPosition.Right;
        public double GameCoverWidth { get; set; } = 80;
        public bool EnableGameBannerAsBackground { get; set; }
        public double GameBannerOpacity { get; set; } = 0.3;
        public int GameBannerBlurRadius { get; set; } = 8;
        public string CustomCoverImagePath { get; set; } = string.Empty;
        public string CustomBannerImagePath { get; set; } = string.Empty;
    }

    public class LocalSettings : ProviderSettingsBase
    {
        public const int MinActiveGameMonitoringIntervalSeconds = 1;
        public const int MaxActiveGameMonitoringIntervalSeconds = 60;
        public const int MinScreenshotDelayMilliseconds = 0;
        public const int MaxScreenshotDelayMilliseconds = 10000;
        public const double MinCustomOverlayHeight = 45;
        public const int ProviderIconMaxPixelSize = 64;
        public const string DefaultBundledUnlockSoundPath = @"Resources\Sounds\Steam.wav";
        public const string DefaultScreenshotFilenameTemplate = "<dateTime>_<gameName>_<achievementName>";
        public const string DefaultRecordingFilenameTemplate = "<dateTime>_<gameName>_<achievementName>";
        public const string SteamAppCacheUserNone = "<none>";

        private Dictionary<Guid, int> _steamAppIdOverrides = new Dictionary<Guid, int>();
        private Dictionary<Guid, int> _lumaPlayAppIdOverrides = new Dictionary<Guid, int>();
        private Dictionary<Guid, string> _lumaPlayIniPathOverrides = new Dictionary<Guid, string>();
        private Dictionary<Guid, string> _localFolderOverrides = new Dictionary<Guid, string>();
        private Dictionary<Guid, string> _customSchemaPathOverrides = new Dictionary<Guid, string>();
        private Dictionary<Guid, bool> _customSchemaEnabledOverrides = new Dictionary<Guid, bool>();
        private Dictionary<Guid, string> _steamAppCacheUserOverrides = new Dictionary<Guid, string>();
        private Dictionary<Guid, bool> _refreshOnGameCloseOverrides = new Dictionary<Guid, bool>();
        private string _steamUserdataPath = string.Empty;
        private bool _enableActiveGameMonitoring;
        private bool _refreshAchievementsOnRealtimeUnlock;
        private bool _refreshAchievementsOnGameClose;
        private int _activeGameMonitoringIntervalSeconds = 5;
        private bool _enableUnlockScreenshots;
        private string _screenshotSaveFolder = string.Empty;
        private string _screenshotFilenameTemplate = DefaultScreenshotFilenameTemplate;
        private int _screenshotDelayMilliseconds = 750;
        private LocalUnlockScreenshotCaptureMode _screenshotCaptureMode = LocalUnlockScreenshotCaptureMode.FullDesktop;
        private LocalUnlockScreenshotImageFormat _screenshotImageFormat = LocalUnlockScreenshotImageFormat.Png;
        private bool _enableUnlockRecordings;
        private string _ffmpegPath = string.Empty;
        private string _recordingSaveFolder = string.Empty;
        private string _recordingFilenameTemplate = DefaultRecordingFilenameTemplate;
        private int _recordingClipSeconds = 15;
        private int _recordingFps = 30;
        private RecordingResolution _recordingResolution = RecordingResolution.Native;
        private RecordingEncoder _recordingEncoder = RecordingEncoder.Auto;
        private RecordingCaptureBackend _recordingCaptureBackend = RecordingCaptureBackend.Auto;
        private bool _recordingIncludeAudio;
        private LocalUnlockNotificationDeliveryMode _unlockNotificationDeliveryMode = LocalUnlockNotificationDeliveryMode.Hybrid;
        private LocalUnlockOverlayPosition _unlockOverlayPosition = LocalUnlockOverlayPosition.TopRight;
        private bool _showOverlayOnActiveGameMonitor = false;
        private bool _enableOverlayDebugLogging;
        private int _unlockOverlayDurationMilliseconds = 3400;
        private int _unlockOverlayFadeInMilliseconds = 180;
        private int _unlockOverlayFadeOutMilliseconds = 280;
        private LocalUnlockOverlayTransitionStyle _unlockOverlayTransitionStyle = LocalUnlockOverlayTransitionStyle.Fade;
        private int _unlockOverlaySlideDistance = 72;
        private int _unlockSoundLeadMilliseconds;
        private double _overlaySteamOpacity = 0.96;
        private double _overlayPlayStationOpacity = 0.96;
        private double _overlayXboxOpacity = 0.96;
        private double _overlayMinimalOpacity = 0.96;
        private double _overlaySteamScale = 1.00;
        private double _overlayPlayStationScale = 1.05;
        private double _overlayXboxScale = 1.00;
        private double _overlayMinimalScale = 0.92;
        private double _overlayCustomOpacity = 0.98;
        private double _overlayCustomScale = 1.00;
        private double _overlayCustomIconSize = 58;
        private double _overlayCustomSecondaryIconSize = 58;
        private double _overlayCustomIconCornerRadius = 10;
        private double _overlayCustomSecondaryIconCornerRadius = 10;
        private double _overlayCustomWidth = 460;
        private double _overlayCustomHeight = 128;
        private double _overlayCustomCornerRadius = 18;
        private double _overlayCustomTitleFontSize = 17;
        private double _overlayCustomDetailFontSize = 13;
        private double _overlayCustomMetaFontSize = 11;
        private double _overlayCustomLine4FontSize = 11;
        private double _overlayCustomLine5FontSize = 11;
        private double _overlayCustomLine6FontSize = 11;
        private bool _overlayCustomAutoResizeToContent;
        private bool _overlayCustomWrapAllText;
        private bool _overlayCustomShowLine1 = true;
        private bool _overlayCustomShowBorder = true;
        private bool _overlayCustomShowGameName;
        private bool _overlayCustomShowMeta;
        private string _overlayCustomBackgroundColor = "#1E2430";
        private string _overlayCustomBorderColor = "#6FA3D8";
        private string _overlayCustomAccentColor = "#A7E0FF";
        private string _overlayCustomTitleColor = "#FFFFFF";
        private string _overlayCustomDetailColor = "#E7EEF7";
        private string _overlayCustomMetaColor = "#BCD0E5";
        private string _overlayCustomBackgroundImagePath = string.Empty;
        private string _overlayCustomTitleTemplate = "Achievement unlocked";
        private string _overlayCustomGameNameTemplate = "<gameName>";
        private string _overlayCustomAchievementTemplate = "Unlocked: <achievementName>";
        private string _overlayCustomMetaTemplate = "<provider> / Custom";
        private string _overlayCustomLine4Template = string.Empty;
        private string _overlayCustomLine5Template = string.Empty;
        private string _overlayCustomLine6Template = string.Empty;
        private bool _overlayCustomShowLine4;
        private bool _overlayCustomShowLine5;
        private bool _overlayCustomShowLine6;
        private string _overlayCustomLine4Color = "#BCD0E5";
        private string _overlayCustomLine5Color = "#BCD0E5";
        private string _overlayCustomLine6Color = "#BCD0E5";
        private string _overlayCustomLine1OutlineColor = "transparent";
        private string _overlayCustomLine2OutlineColor = "transparent";
        private string _overlayCustomLine3OutlineColor = "transparent";
        private string _overlayCustomLine4OutlineColor = "transparent";
        private string _overlayCustomLine5OutlineColor = "transparent";
        private string _overlayCustomLine6OutlineColor = "transparent";
        private string _overlayCustomLine1ShadowColor = "transparent";
        private string _overlayCustomLine2ShadowColor = "transparent";
        private string _overlayCustomLine3ShadowColor = "transparent";
        private string _overlayCustomLine4ShadowColor = "transparent";
        private string _overlayCustomLine5ShadowColor = "transparent";
        private string _overlayCustomLine6ShadowColor = "transparent";
        private double _overlayCustomOutlineSize = 0.6;
        private double _overlayCustomShadowSize = 2;
        private double _overlayCustomLine1OutlineSize = 0.6;
        private double _overlayCustomLine2OutlineSize = 0.6;
        private double _overlayCustomLine3OutlineSize = 0.6;
        private double _overlayCustomLine4OutlineSize = 0.6;
        private double _overlayCustomLine5OutlineSize = 0.6;
        private double _overlayCustomLine6OutlineSize = 0.6;
        private double _overlayCustomLine1ShadowSize = 2;
        private double _overlayCustomLine2ShadowSize = 2;
        private double _overlayCustomLine3ShadowSize = 2;
        private double _overlayCustomLine4ShadowSize = 2;
        private double _overlayCustomLine5ShadowSize = 2;
        private double _overlayCustomLine6ShadowSize = 2;
        private bool _overlayCustomLine1OutlineEnabled;
        private bool _overlayCustomLine2OutlineEnabled;
        private bool _overlayCustomLine3OutlineEnabled;
        private bool _overlayCustomLine4OutlineEnabled;
        private bool _overlayCustomLine5OutlineEnabled;
        private bool _overlayCustomLine6OutlineEnabled;
        private bool _overlayCustomLine1ShadowEnabled;
        private bool _overlayCustomLine2ShadowEnabled;
        private bool _overlayCustomLine3ShadowEnabled;
        private bool _overlayCustomLine4ShadowEnabled;
        private bool _overlayCustomLine5ShadowEnabled;
        private bool _overlayCustomLine6ShadowEnabled;
        private double _overlayCustomLineSpacing = 3;
        private double _overlayCustomLine1Spacing = 3;
        private double _overlayCustomLine2Spacing = 3;
        private double _overlayCustomLine3Spacing = 3;
        private double _overlayCustomLine4Spacing = 3;
        private double _overlayCustomLine5Spacing = 3;
        private double _overlayCustomLine6Spacing = 3;
        private LocalSanTextViewTarget _overlayCustomLine1View = LocalSanTextViewTarget.View1;
        private LocalSanTextViewTarget _overlayCustomLine2View = LocalSanTextViewTarget.View2;
        private LocalSanTextViewTarget _overlayCustomLine3View = LocalSanTextViewTarget.View2;
        private LocalSanTextViewTarget _overlayCustomLine4View = LocalSanTextViewTarget.View2;
        private LocalSanTextViewTarget _overlayCustomLine5View = LocalSanTextViewTarget.View2;
        private LocalSanTextViewTarget _overlayCustomLine6View = LocalSanTextViewTarget.View2;
        private int _overlayCustomLine1Order = 1;
        private int _overlayCustomLine2Order = 2;
        private int _overlayCustomLine3Order = 3;
        private int _overlayCustomLine4Order = 3;
        private int _overlayCustomLine5Order = 4;
        private int _overlayCustomLine6Order = 5;
        private LocalCustomOverlayLayoutStyle _overlayCustomLayoutStyle = LocalCustomOverlayLayoutStyle.Standard;
        private LocalCustomOverlayAnimationStyle _overlayCustomAnimationStyle = LocalCustomOverlayAnimationStyle.Standard;
        private LocalOverlayIconSource _overlayCustomSecondaryIconSource = LocalOverlayIconSource.None;
        private bool _overlayCustomShowSecondaryIcon;
                private bool _overlayCustomSecondaryIconNextToPrimary;
        private string _overlayCustomSanElementPresetId = string.Empty;
        private LocalSanElementPosition _overlayCustomSanElementPosition = LocalSanElementPosition.Left;
        private LocalSanProviderLogoSource _overlayCustomSanProviderLogoSource = LocalSanProviderLogoSource.ExtensionIcon;
        private string _overlayCustomSanPresetId = string.Empty;
        private string _overlayCustomSanPresetHtml = string.Empty;
        private string _overlayCustomSanPresetCss = string.Empty;
        private string _overlayCustomSanBaseCss = string.Empty;
        private string _overlayCustomSanBaseAnimCss = string.Empty;
        private string _overlayCustomSanAssetRootPath = string.Empty;
        private string _overlayCustomSanThemeJson = string.Empty;
        private string _overlayCustomSanThemeDirectory = string.Empty;
        private string _overlayCustomManualElementCss = string.Empty;
        private int _overlayCustomSanView1DurationMilliseconds = 5000;
        private int _overlayCustomSanView2DurationMilliseconds = 5000;
        private bool _overlayCustomTitleBold = true;
        private bool _overlayCustomTitleItalic;
        private bool _overlayCustomTitleUnderline;
        private bool _overlayCustomTitleStrikethrough;
        private bool _overlayCustomDetailBold;
        private bool _overlayCustomDetailItalic;
        private bool _overlayCustomDetailUnderline;
        private bool _overlayCustomDetailStrikethrough;
        private bool _overlayCustomMetaBold = true;
        private bool _overlayCustomMetaItalic;
        private bool _overlayCustomMetaUnderline;
        private bool _overlayCustomMetaStrikethrough;
        private bool _overlayCustomLine4Bold;
        private bool _overlayCustomLine4Italic;
        private bool _overlayCustomLine4Underline;
        private bool _overlayCustomLine4Strikethrough;
        private bool _overlayCustomLine5Bold;
        private bool _overlayCustomLine5Italic;
        private bool _overlayCustomLine5Underline;
        private bool _overlayCustomLine5Strikethrough;
        private bool _overlayCustomLine6Bold;
        private bool _overlayCustomLine6Italic;
        private bool _overlayCustomLine6Underline;
        private bool _overlayCustomLine6Strikethrough;
        private LocalOverlayIconSource _overlayCustomIconSource = LocalOverlayIconSource.AchievementIcon;
        private string _overlayCustomIconPath = string.Empty;
        private string _overlayCustomSecondaryIconPath = string.Empty;
        private string _overlayCustomIconLibraryPaths = string.Empty;
        private bool _overlayCustomShowIconRarityGlow;
        private bool _overlayCustomShowSecondaryIconRarityGlow;
        private string _overlayCustomCoverImagePath = string.Empty;
        private string _overlayCustomBannerImagePath = string.Empty;
        private int _selectedCustomStyleSlot = 1;
        private List<LocalCustomOverlayStyleSlot> _customOverlayStyleSlots = CreateDefaultCustomOverlayStyleSlots();
        private bool _enableInAppUnlockNotifications = true;
        private bool _enableWindowsToastNotifications = true;
        private string _bundledUnlockSoundPath = string.Empty;
        private string _customUnlockSoundPath = string.Empty;
        private string _extraUnlockSoundPaths = string.Empty;
        private ScoreProgressNotificationSettings _collectionProgressNotifications = new ScoreProgressNotificationSettings();
        private ScoreProgressNotificationSettings _prestigeProgressNotifications = new ScoreProgressNotificationSettings();
        private LocalSteamSchemaPreference _steamSchemaPreference = LocalSteamSchemaPreference.PreferSteam;
        private LocalImportedGameLibraryTarget _importedGameLibraryTarget = LocalImportedGameLibraryTarget.None;
        private string _importedGameCustomSourceName = string.Empty;
        private string _importedGameMetadataSourceId = string.Empty;
        private string _successStoryImportTargetSourceName = string.Empty;
        private string _successStoryImportPath = string.Empty;
        private string _successStoryImportCategoryKeys = string.Empty;
        private string _successStoryImportExcludedCategoryKeys = string.Empty;
        private string _successStoryCategoryScanSnapshotJson = string.Empty;
        private bool _successStoryOverwriteExistingAchievements;
        private LocalExistingGameImportBehavior _existingGameImportBehavior = LocalExistingGameImportBehavior.OverwriteExisting;
        private bool _includeFoldersWithoutAchievementFilesOnImport;
        private LocalIconRateLimitRetryMode _iconRateLimitRetryMode = LocalIconRateLimitRetryMode.FixedRounds;
        private int _iconRateLimitRetryRounds = 2;
        private string _steamAppCacheUserId = string.Empty;
        private string _customProviderIconPath = string.Empty;
        private string _borrowedProviderIconKey = string.Empty;
        private bool _warnOnAmbiguousLocalFolder = true;
        private bool _enableGameCoverInOverlay = false;
        private LocalOverlayCoverPosition _gameCoverPosition = LocalOverlayCoverPosition.Right;
        private double _gameCoverWidth = 80;
        private bool _enableGameBannerAsBackground = false;
        private double _gameBannerOpacity = 0.3;
        private int _gameBannerBlurRadius = 8;

        public override string ProviderKey => "Local";

        public string ExtraLocalPaths { get; set; } = string.Empty;

        public string ExcludedLocalPaths { get; set; } = string.Empty;

        public bool EnableActiveGameMonitoring
        {
            get => _enableActiveGameMonitoring;
            set => SetValue(ref _enableActiveGameMonitoring, value);
        }

        public bool RefreshAchievementsOnRealtimeUnlock
        {
            get => _refreshAchievementsOnRealtimeUnlock;
            set => SetValue(ref _refreshAchievementsOnRealtimeUnlock, value);
        }

        public int ActiveGameMonitoringIntervalSeconds
        {
            get => _activeGameMonitoringIntervalSeconds;
            set => SetValue(
                ref _activeGameMonitoringIntervalSeconds,
                Math.Max(MinActiveGameMonitoringIntervalSeconds, Math.Min(MaxActiveGameMonitoringIntervalSeconds, value)));
        }

        public bool EnableUnlockScreenshots
        {
            get => _enableUnlockScreenshots;
            set => SetValue(ref _enableUnlockScreenshots, value);
        }

        public string ScreenshotSaveFolder
        {
            get => _screenshotSaveFolder;
            set
            {
                if (SetValue(ref _screenshotSaveFolder, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(EffectiveScreenshotSaveFolder));
                }
            }
        }

        [JsonIgnore]
        public string EffectiveScreenshotSaveFolder => GetEffectiveScreenshotSaveFolder();

        public string ScreenshotFilenameTemplate
        {
            get => _screenshotFilenameTemplate;
            set => SetValue(
                ref _screenshotFilenameTemplate,
                string.IsNullOrWhiteSpace(value) ? DefaultScreenshotFilenameTemplate : value);
        }

        public int ScreenshotDelayMilliseconds
        {
            get => _screenshotDelayMilliseconds;
            set => SetValue(
                ref _screenshotDelayMilliseconds,
                Math.Max(MinScreenshotDelayMilliseconds, Math.Min(MaxScreenshotDelayMilliseconds, value)));
        }

        public LocalUnlockScreenshotCaptureMode ScreenshotCaptureMode
        {
            get => _screenshotCaptureMode;
            set => SetValue(ref _screenshotCaptureMode, value);
        }

        public LocalUnlockScreenshotImageFormat ScreenshotImageFormat
        {
            get => _screenshotImageFormat;
            set => SetValue(ref _screenshotImageFormat, value);
        }

        public LocalUnlockNotificationDeliveryMode UnlockNotificationDeliveryMode
        {
            get => _unlockNotificationDeliveryMode;
            set => SetValue(ref _unlockNotificationDeliveryMode, value);
        }

        public LocalUnlockOverlayPosition UnlockOverlayPosition
        {
            get => _unlockOverlayPosition;
            set => SetValue(ref _unlockOverlayPosition, value);
        }

        public bool EnableUnlockRecordings
        {
            get => _enableUnlockRecordings;
            set => SetValue(ref _enableUnlockRecordings, value);
        }

        public string FfmpegPath
        {
            get => _ffmpegPath;
            set => SetValue(ref _ffmpegPath, value ?? string.Empty);
        }

        public string RecordingSaveFolder
        {
            get => _recordingSaveFolder;
            set => SetValue(ref _recordingSaveFolder, value ?? string.Empty);
        }

        public string RecordingFilenameTemplate
        {
            get => _recordingFilenameTemplate;
            set => SetValue(
                ref _recordingFilenameTemplate,
                string.IsNullOrWhiteSpace(value) ? DefaultRecordingFilenameTemplate : value);
        }

        public int RecordingClipSeconds
        {
            get => _recordingClipSeconds;
            set => SetValue(ref _recordingClipSeconds, Math.Min(60, Math.Max(5, value)));
        }

        public int RecordingFps
        {
            get => _recordingFps;
            set => SetValue(ref _recordingFps, Math.Min(60, Math.Max(10, value)));
        }

        public RecordingResolution RecordingResolution
        {
            get => _recordingResolution;
            set => SetValue(ref _recordingResolution, value);
        }

        public RecordingEncoder RecordingEncoder
        {
            get => _recordingEncoder;
            set => SetValue(ref _recordingEncoder, value);
        }

        public RecordingCaptureBackend RecordingCaptureBackend
        {
            get => _recordingCaptureBackend;
            set => SetValue(ref _recordingCaptureBackend, value);
        }

        public bool RecordingIncludeAudio
        {
            get => _recordingIncludeAudio;
            set => SetValue(ref _recordingIncludeAudio, value);
        }

        public bool ShowOverlayOnActiveGameMonitor
        {
            get => _showOverlayOnActiveGameMonitor;
            set => SetValue(ref _showOverlayOnActiveGameMonitor, value);
        }

        public bool EnableOverlayDebugLogging
        {
            get => _enableOverlayDebugLogging;
            set => SetValue(ref _enableOverlayDebugLogging, value);
        }

        public int UnlockOverlayDurationMilliseconds
        {
            get => _unlockOverlayDurationMilliseconds;
            set => SetValue(ref _unlockOverlayDurationMilliseconds, Math.Max(1000, Math.Min(15000, value)));
        }

        public int UnlockOverlayFadeInMilliseconds
        {
            get => _unlockOverlayFadeInMilliseconds;
            set => SetValue(ref _unlockOverlayFadeInMilliseconds, Math.Max(0, Math.Min(4000, value)));
        }

        public int UnlockOverlayFadeOutMilliseconds
        {
            get => _unlockOverlayFadeOutMilliseconds;
            set => SetValue(ref _unlockOverlayFadeOutMilliseconds, Math.Max(0, Math.Min(4000, value)));
        }

        public LocalUnlockOverlayTransitionStyle UnlockOverlayTransitionStyle
        {
            get => _unlockOverlayTransitionStyle;
            set => SetValue(ref _unlockOverlayTransitionStyle, value);
        }

        public int UnlockOverlaySlideDistance
        {
            get => _unlockOverlaySlideDistance;
            set => SetValue(ref _unlockOverlaySlideDistance, Math.Max(0, Math.Min(450, value)));
        }

        public int UnlockSoundLeadMilliseconds
        {
            get => _unlockSoundLeadMilliseconds;
            set => SetValue(ref _unlockSoundLeadMilliseconds, value);
        }

        public double OverlaySteamOpacity
        {
            get => _overlaySteamOpacity;
            set => SetValue(ref _overlaySteamOpacity, Math.Max(0.35, Math.Min(1.0, value)));
        }

        public double OverlayPlayStationOpacity
        {
            get => _overlayPlayStationOpacity;
            set => SetValue(ref _overlayPlayStationOpacity, Math.Max(0.35, Math.Min(1.0, value)));
        }

        public double OverlayXboxOpacity
        {
            get => _overlayXboxOpacity;
            set => SetValue(ref _overlayXboxOpacity, Math.Max(0.35, Math.Min(1.0, value)));
        }

        public double OverlayMinimalOpacity
        {
            get => _overlayMinimalOpacity;
            set => SetValue(ref _overlayMinimalOpacity, Math.Max(0.35, Math.Min(1.0, value)));
        }

        public double OverlaySteamScale
        {
            get => _overlaySteamScale;
            set => SetValue(ref _overlaySteamScale, Math.Max(0.70, Math.Min(1.60, value)));
        }

        public double OverlayPlayStationScale
        {
            get => _overlayPlayStationScale;
            set => SetValue(ref _overlayPlayStationScale, Math.Max(0.70, Math.Min(1.60, value)));
        }

        public double OverlayXboxScale
        {
            get => _overlayXboxScale;
            set => SetValue(ref _overlayXboxScale, Math.Max(0.70, Math.Min(1.60, value)));
        }

        public double OverlayMinimalScale
        {
            get => _overlayMinimalScale;
            set => SetValue(ref _overlayMinimalScale, Math.Max(0.70, Math.Min(1.60, value)));
        }

        public double OverlayCustomOpacity
        {
            get => _overlayCustomOpacity;
            set => SetValue(ref _overlayCustomOpacity, Math.Max(0.35, Math.Min(1.0, value)));
        }

        public double OverlayCustomScale
        {
            get => _overlayCustomScale;
            set => SetValue(ref _overlayCustomScale, Math.Max(0.70, Math.Min(1.60, value)));
        }

        public double OverlayCustomIconSize
        {
            get => _overlayCustomIconSize;
            set => SetValue(ref _overlayCustomIconSize, Math.Max(24, Math.Min(220, value)));
        }

        public double OverlayCustomSecondaryIconSize
        {
            get => _overlayCustomSecondaryIconSize;
            set => SetValue(ref _overlayCustomSecondaryIconSize, Math.Max(24, Math.Min(220, value)));
        }

        public double OverlayCustomWidth
        {
            get => _overlayCustomWidth;
            set => SetValue(ref _overlayCustomWidth, Math.Max(280, Math.Min(900, value)));
        }

        public double OverlayCustomHeight
        {
            get => _overlayCustomHeight;
            set => SetValue(ref _overlayCustomHeight, Math.Max(MinCustomOverlayHeight, Math.Min(320, value)));
        }

        public double OverlayCustomCornerRadius
        {
            get => _overlayCustomCornerRadius;
            set => SetValue(ref _overlayCustomCornerRadius, Math.Max(0, Math.Min(60, value)));
        }

        public double OverlayCustomTitleFontSize
        {
            get => _overlayCustomTitleFontSize;
            set => SetValue(ref _overlayCustomTitleFontSize, Math.Max(10, Math.Min(34, value)));
        }

        public double OverlayCustomDetailFontSize
        {
            get => _overlayCustomDetailFontSize;
            set => SetValue(ref _overlayCustomDetailFontSize, Math.Max(9, Math.Min(28, value)));
        }

        public double OverlayCustomMetaFontSize
        {
            get => _overlayCustomMetaFontSize;
            set => SetValue(ref _overlayCustomMetaFontSize, Math.Max(8, Math.Min(24, value)));
        }

        public double OverlayCustomLine4FontSize
        {
            get => _overlayCustomLine4FontSize;
            set => SetValue(ref _overlayCustomLine4FontSize, Math.Max(8, Math.Min(24, value)));
        }

        public double OverlayCustomLine5FontSize
        {
            get => _overlayCustomLine5FontSize;
            set => SetValue(ref _overlayCustomLine5FontSize, Math.Max(8, Math.Min(24, value)));
        }

        public double OverlayCustomLine6FontSize
        {
            get => _overlayCustomLine6FontSize;
            set => SetValue(ref _overlayCustomLine6FontSize, Math.Max(8, Math.Min(24, value)));
        }

        public bool OverlayCustomAutoResizeToContent
        {
            get => _overlayCustomAutoResizeToContent;
            set => SetValue(ref _overlayCustomAutoResizeToContent, value);
        }

        public bool OverlayCustomWrapAllText
        {
            get => _overlayCustomWrapAllText;
            set => SetValue(ref _overlayCustomWrapAllText, value);
        }

        public bool OverlayCustomShowLine1
        {
            get => _overlayCustomShowLine1;
            set => SetValue(ref _overlayCustomShowLine1, value);
        }

        public bool OverlayCustomShowBorder
        {
            get => _overlayCustomShowBorder;
            set => SetValue(ref _overlayCustomShowBorder, value);
        }

        public bool OverlayCustomShowGameName
        {
            get => _overlayCustomShowGameName;
            set => SetValue(ref _overlayCustomShowGameName, value);
        }

        public bool OverlayCustomShowMeta
        {
            get => _overlayCustomShowMeta;
            set => SetValue(ref _overlayCustomShowMeta, value);
        }

        public string OverlayCustomBackgroundColor
        {
            get => _overlayCustomBackgroundColor;
            set => SetValue(ref _overlayCustomBackgroundColor, NormalizeColorSetting(value, "#1E2430"));
        }

        public string OverlayCustomBorderColor
        {
            get => _overlayCustomBorderColor;
            set => SetValue(ref _overlayCustomBorderColor, NormalizeColorSetting(value, "#6FA3D8"));
        }

        public string OverlayCustomAccentColor
        {
            get => _overlayCustomAccentColor;
            set => SetValue(ref _overlayCustomAccentColor, NormalizeColorSetting(value, "#A7E0FF"));
        }

        public string OverlayCustomTitleColor
        {
            get => _overlayCustomTitleColor;
            set => SetValue(ref _overlayCustomTitleColor, NormalizeColorSetting(value, "#FFFFFF"));
        }

        public string OverlayCustomDetailColor
        {
            get => _overlayCustomDetailColor;
            set => SetValue(ref _overlayCustomDetailColor, NormalizeColorSetting(value, "#E7EEF7"));
        }

        public string OverlayCustomMetaColor
        {
            get => _overlayCustomMetaColor;
            set => SetValue(ref _overlayCustomMetaColor, NormalizeColorSetting(value, "#BCD0E5"));
        }

        public string OverlayCustomBackgroundImagePath
        {
            get => _overlayCustomBackgroundImagePath;
            set => SetValue(ref _overlayCustomBackgroundImagePath, value ?? string.Empty);
        }

        public string OverlayCustomTitleTemplate
        {
            get => _overlayCustomTitleTemplate;
            set => SetValue(ref _overlayCustomTitleTemplate, value ?? string.Empty);
        }

        public string OverlayCustomGameNameTemplate
        {
            get => _overlayCustomGameNameTemplate;
            set => SetValue(ref _overlayCustomGameNameTemplate, value ?? string.Empty);
        }

        public string OverlayCustomAchievementTemplate
        {
            get => _overlayCustomAchievementTemplate;
            set => SetValue(ref _overlayCustomAchievementTemplate, value ?? string.Empty);
        }

        public string OverlayCustomMetaTemplate
        {
            get => _overlayCustomMetaTemplate;
            set => SetValue(ref _overlayCustomMetaTemplate, value ?? string.Empty);
        }

        public string OverlayCustomLine4Template
        {
            get => _overlayCustomLine4Template;
            set => SetValue(ref _overlayCustomLine4Template, value ?? string.Empty);
        }

        public string OverlayCustomLine5Template
        {
            get => _overlayCustomLine5Template;
            set => SetValue(ref _overlayCustomLine5Template, value ?? string.Empty);
        }

        public string OverlayCustomLine6Template
        {
            get => _overlayCustomLine6Template;
            set => SetValue(ref _overlayCustomLine6Template, value ?? string.Empty);
        }

        public bool OverlayCustomShowLine4
        {
            get => _overlayCustomShowLine4;
            set => SetValue(ref _overlayCustomShowLine4, value);
        }

        public bool OverlayCustomShowLine5
        {
            get => _overlayCustomShowLine5;
            set => SetValue(ref _overlayCustomShowLine5, value);
        }

        public bool OverlayCustomShowLine6
        {
            get => _overlayCustomShowLine6;
            set => SetValue(ref _overlayCustomShowLine6, value);
        }

        public string OverlayCustomLine4Color
        {
            get => _overlayCustomLine4Color;
            set => SetValue(ref _overlayCustomLine4Color, NormalizeColorSetting(value, "#BCD0E5"));
        }

        public string OverlayCustomLine5Color
        {
            get => _overlayCustomLine5Color;
            set => SetValue(ref _overlayCustomLine5Color, NormalizeColorSetting(value, "#BCD0E5"));
        }

        public string OverlayCustomLine6Color
        {
            get => _overlayCustomLine6Color;
            set => SetValue(ref _overlayCustomLine6Color, NormalizeColorSetting(value, "#BCD0E5"));
        }

        public string OverlayCustomLine1OutlineColor
        {
            get => _overlayCustomLine1OutlineColor;
            set => SetValue(ref _overlayCustomLine1OutlineColor, NormalizeColorSetting(value, "transparent"));
        }

        public string OverlayCustomLine2OutlineColor
        {
            get => _overlayCustomLine2OutlineColor;
            set => SetValue(ref _overlayCustomLine2OutlineColor, NormalizeColorSetting(value, "transparent"));
        }

        public string OverlayCustomLine3OutlineColor
        {
            get => _overlayCustomLine3OutlineColor;
            set => SetValue(ref _overlayCustomLine3OutlineColor, NormalizeColorSetting(value, "transparent"));
        }

        public string OverlayCustomLine4OutlineColor
        {
            get => _overlayCustomLine4OutlineColor;
            set => SetValue(ref _overlayCustomLine4OutlineColor, NormalizeColorSetting(value, "transparent"));
        }

        public string OverlayCustomLine5OutlineColor
        {
            get => _overlayCustomLine5OutlineColor;
            set => SetValue(ref _overlayCustomLine5OutlineColor, NormalizeColorSetting(value, "transparent"));
        }

        public string OverlayCustomLine6OutlineColor
        {
            get => _overlayCustomLine6OutlineColor;
            set => SetValue(ref _overlayCustomLine6OutlineColor, NormalizeColorSetting(value, "transparent"));
        }

        public string OverlayCustomLine1ShadowColor
        {
            get => _overlayCustomLine1ShadowColor;
            set => SetValue(ref _overlayCustomLine1ShadowColor, NormalizeColorSetting(value, "transparent"));
        }

        public string OverlayCustomLine2ShadowColor
        {
            get => _overlayCustomLine2ShadowColor;
            set => SetValue(ref _overlayCustomLine2ShadowColor, NormalizeColorSetting(value, "transparent"));
        }

        public string OverlayCustomLine3ShadowColor
        {
            get => _overlayCustomLine3ShadowColor;
            set => SetValue(ref _overlayCustomLine3ShadowColor, NormalizeColorSetting(value, "transparent"));
        }

        public string OverlayCustomLine4ShadowColor
        {
            get => _overlayCustomLine4ShadowColor;
            set => SetValue(ref _overlayCustomLine4ShadowColor, NormalizeColorSetting(value, "transparent"));
        }

        public string OverlayCustomLine5ShadowColor
        {
            get => _overlayCustomLine5ShadowColor;
            set => SetValue(ref _overlayCustomLine5ShadowColor, NormalizeColorSetting(value, "transparent"));
        }

        public string OverlayCustomLine6ShadowColor
        {
            get => _overlayCustomLine6ShadowColor;
            set => SetValue(ref _overlayCustomLine6ShadowColor, NormalizeColorSetting(value, "transparent"));
        }

        public bool OverlayCustomLine1OutlineEnabled
        {
            get => _overlayCustomLine1OutlineEnabled;
            set => SetValue(ref _overlayCustomLine1OutlineEnabled, value);
        }

        public double OverlayCustomOutlineSize
        {
            get => _overlayCustomOutlineSize;
            set => SetValue(ref _overlayCustomOutlineSize, Math.Max(0, Math.Min(8, value)));
        }

        public double OverlayCustomShadowSize
        {
            get => _overlayCustomShadowSize;
            set => SetValue(ref _overlayCustomShadowSize, Math.Max(0, Math.Min(24, value)));
        }

        public double OverlayCustomLine1OutlineSize { get => _overlayCustomLine1OutlineSize; set => SetValue(ref _overlayCustomLine1OutlineSize, Math.Max(0, Math.Min(8, value))); }
        public double OverlayCustomLine2OutlineSize { get => _overlayCustomLine2OutlineSize; set => SetValue(ref _overlayCustomLine2OutlineSize, Math.Max(0, Math.Min(8, value))); }
        public double OverlayCustomLine3OutlineSize { get => _overlayCustomLine3OutlineSize; set => SetValue(ref _overlayCustomLine3OutlineSize, Math.Max(0, Math.Min(8, value))); }
        public double OverlayCustomLine4OutlineSize { get => _overlayCustomLine4OutlineSize; set => SetValue(ref _overlayCustomLine4OutlineSize, Math.Max(0, Math.Min(8, value))); }
        public double OverlayCustomLine5OutlineSize { get => _overlayCustomLine5OutlineSize; set => SetValue(ref _overlayCustomLine5OutlineSize, Math.Max(0, Math.Min(8, value))); }
        public double OverlayCustomLine6OutlineSize { get => _overlayCustomLine6OutlineSize; set => SetValue(ref _overlayCustomLine6OutlineSize, Math.Max(0, Math.Min(8, value))); }
        public double OverlayCustomLine1ShadowSize { get => _overlayCustomLine1ShadowSize; set => SetValue(ref _overlayCustomLine1ShadowSize, Math.Max(0, Math.Min(24, value))); }
        public double OverlayCustomLine2ShadowSize { get => _overlayCustomLine2ShadowSize; set => SetValue(ref _overlayCustomLine2ShadowSize, Math.Max(0, Math.Min(24, value))); }
        public double OverlayCustomLine3ShadowSize { get => _overlayCustomLine3ShadowSize; set => SetValue(ref _overlayCustomLine3ShadowSize, Math.Max(0, Math.Min(24, value))); }
        public double OverlayCustomLine4ShadowSize { get => _overlayCustomLine4ShadowSize; set => SetValue(ref _overlayCustomLine4ShadowSize, Math.Max(0, Math.Min(24, value))); }
        public double OverlayCustomLine5ShadowSize { get => _overlayCustomLine5ShadowSize; set => SetValue(ref _overlayCustomLine5ShadowSize, Math.Max(0, Math.Min(24, value))); }
        public double OverlayCustomLine6ShadowSize { get => _overlayCustomLine6ShadowSize; set => SetValue(ref _overlayCustomLine6ShadowSize, Math.Max(0, Math.Min(24, value))); }

        public bool OverlayCustomLine2OutlineEnabled
        {
            get => _overlayCustomLine2OutlineEnabled;
            set => SetValue(ref _overlayCustomLine2OutlineEnabled, value);
        }

        public bool OverlayCustomLine3OutlineEnabled
        {
            get => _overlayCustomLine3OutlineEnabled;
            set => SetValue(ref _overlayCustomLine3OutlineEnabled, value);
        }

        public bool OverlayCustomLine4OutlineEnabled
        {
            get => _overlayCustomLine4OutlineEnabled;
            set => SetValue(ref _overlayCustomLine4OutlineEnabled, value);
        }

        public bool OverlayCustomLine5OutlineEnabled
        {
            get => _overlayCustomLine5OutlineEnabled;
            set => SetValue(ref _overlayCustomLine5OutlineEnabled, value);
        }

        public bool OverlayCustomLine6OutlineEnabled
        {
            get => _overlayCustomLine6OutlineEnabled;
            set => SetValue(ref _overlayCustomLine6OutlineEnabled, value);
        }

        public bool OverlayCustomLine1ShadowEnabled
        {
            get => _overlayCustomLine1ShadowEnabled;
            set => SetValue(ref _overlayCustomLine1ShadowEnabled, value);
        }

        public bool OverlayCustomLine2ShadowEnabled
        {
            get => _overlayCustomLine2ShadowEnabled;
            set => SetValue(ref _overlayCustomLine2ShadowEnabled, value);
        }

        public bool OverlayCustomLine3ShadowEnabled
        {
            get => _overlayCustomLine3ShadowEnabled;
            set => SetValue(ref _overlayCustomLine3ShadowEnabled, value);
        }

        public bool OverlayCustomLine4ShadowEnabled
        {
            get => _overlayCustomLine4ShadowEnabled;
            set => SetValue(ref _overlayCustomLine4ShadowEnabled, value);
        }

        public bool OverlayCustomLine5ShadowEnabled
        {
            get => _overlayCustomLine5ShadowEnabled;
            set => SetValue(ref _overlayCustomLine5ShadowEnabled, value);
        }

        public bool OverlayCustomLine6ShadowEnabled
        {
            get => _overlayCustomLine6ShadowEnabled;
            set => SetValue(ref _overlayCustomLine6ShadowEnabled, value);
        }

        public double OverlayCustomLineSpacing
        {
            get => _overlayCustomLineSpacing;
            set => SetValue(ref _overlayCustomLineSpacing, Math.Max(0, Math.Min(24, value)));
        }

        public double OverlayCustomLine1Spacing
        {
            get => _overlayCustomLine1Spacing;
            set => SetValue(ref _overlayCustomLine1Spacing, Math.Max(0, Math.Min(48, value)));
        }

        public double OverlayCustomLine2Spacing
        {
            get => _overlayCustomLine2Spacing;
            set => SetValue(ref _overlayCustomLine2Spacing, Math.Max(0, Math.Min(48, value)));
        }

        public double OverlayCustomLine3Spacing
        {
            get => _overlayCustomLine3Spacing;
            set => SetValue(ref _overlayCustomLine3Spacing, Math.Max(0, Math.Min(48, value)));
        }

        public double OverlayCustomLine4Spacing
        {
            get => _overlayCustomLine4Spacing;
            set => SetValue(ref _overlayCustomLine4Spacing, Math.Max(0, Math.Min(48, value)));
        }

        public double OverlayCustomLine5Spacing
        {
            get => _overlayCustomLine5Spacing;
            set => SetValue(ref _overlayCustomLine5Spacing, Math.Max(0, Math.Min(48, value)));
        }

        public double OverlayCustomLine6Spacing
        {
            get => _overlayCustomLine6Spacing;
            set => SetValue(ref _overlayCustomLine6Spacing, Math.Max(0, Math.Min(48, value)));
        }

        public LocalSanTextViewTarget OverlayCustomLine1View
        {
            get => _overlayCustomLine1View;
            set => SetValue(ref _overlayCustomLine1View, value);
        }

        public LocalSanTextViewTarget OverlayCustomLine2View
        {
            get => _overlayCustomLine2View;
            set => SetValue(ref _overlayCustomLine2View, value);
        }

        public LocalSanTextViewTarget OverlayCustomLine3View
        {
            get => _overlayCustomLine3View;
            set => SetValue(ref _overlayCustomLine3View, value);
        }

        public LocalSanTextViewTarget OverlayCustomLine4View
        {
            get => _overlayCustomLine4View;
            set => SetValue(ref _overlayCustomLine4View, value);
        }

        public LocalSanTextViewTarget OverlayCustomLine5View
        {
            get => _overlayCustomLine5View;
            set => SetValue(ref _overlayCustomLine5View, value);
        }

        public LocalSanTextViewTarget OverlayCustomLine6View
        {
            get => _overlayCustomLine6View;
            set => SetValue(ref _overlayCustomLine6View, value);
        }

        public int OverlayCustomLine1Order
        {
            get => _overlayCustomLine1Order;
            set => SetValue(ref _overlayCustomLine1Order, Math.Max(1, Math.Min(6, value)));
        }

        public int OverlayCustomLine2Order
        {
            get => _overlayCustomLine2Order;
            set => SetValue(ref _overlayCustomLine2Order, Math.Max(1, Math.Min(6, value)));
        }

        public int OverlayCustomLine3Order
        {
            get => _overlayCustomLine3Order;
            set => SetValue(ref _overlayCustomLine3Order, Math.Max(1, Math.Min(6, value)));
        }

        public int OverlayCustomLine4Order
        {
            get => _overlayCustomLine4Order;
            set => SetValue(ref _overlayCustomLine4Order, Math.Max(1, Math.Min(6, value)));
        }

        public int OverlayCustomLine5Order
        {
            get => _overlayCustomLine5Order;
            set => SetValue(ref _overlayCustomLine5Order, Math.Max(1, Math.Min(6, value)));
        }

        public int OverlayCustomLine6Order
        {
            get => _overlayCustomLine6Order;
            set => SetValue(ref _overlayCustomLine6Order, Math.Max(1, Math.Min(6, value)));
        }

        public LocalCustomOverlayLayoutStyle OverlayCustomLayoutStyle
        {
            get => _overlayCustomLayoutStyle;
            set => SetValue(ref _overlayCustomLayoutStyle, value);
        }

        public LocalCustomOverlayAnimationStyle OverlayCustomAnimationStyle
        {
            get => _overlayCustomAnimationStyle;
            set => SetValue(ref _overlayCustomAnimationStyle, value);
        }

        public LocalOverlayIconSource OverlayCustomSecondaryIconSource
        {
            get => _overlayCustomShowSecondaryIcon ? _overlayCustomSecondaryIconSource : LocalOverlayIconSource.None;
            set
            {
                var source = value;
                if (source == LocalOverlayIconSource.None)
                {
                    if (SetValue(ref _overlayCustomSecondaryIconSource, LocalOverlayIconSource.None))
                    {
                        OnPropertyChanged(nameof(OverlayCustomShowSecondaryIcon));
                    }

                    SetValue(ref _overlayCustomShowSecondaryIcon, false);
                    return;
                }

                if (SetValue(ref _overlayCustomSecondaryIconSource, source))
                {
                    OnPropertyChanged(nameof(OverlayCustomShowSecondaryIcon));
                }

                SetValue(ref _overlayCustomShowSecondaryIcon, true);
            }
        }

        public bool OverlayCustomShowSecondaryIcon
        {
            get => _overlayCustomShowSecondaryIcon && _overlayCustomSecondaryIconSource != LocalOverlayIconSource.None;
            set
            {
                if (!value)
                {
                    OverlayCustomSecondaryIconSource = LocalOverlayIconSource.None;
                    return;
                }

                if (_overlayCustomSecondaryIconSource == LocalOverlayIconSource.None)
                {
                    SetValue(ref _overlayCustomSecondaryIconSource, LocalOverlayIconSource.AchievementIcon);
                }

                if (SetValue(ref _overlayCustomShowSecondaryIcon, true))
                {
                    OnPropertyChanged(nameof(OverlayCustomSecondaryIconSource));
                }
            }
        }

        public bool OverlayCustomSecondaryIconNextToPrimary
        {
            get => _overlayCustomSecondaryIconNextToPrimary;
            set => SetValue(ref _overlayCustomSecondaryIconNextToPrimary, value);
        }

        public string OverlayCustomSanElementPresetId
        {
            get => _overlayCustomSanElementPresetId;
            set => SetValue(ref _overlayCustomSanElementPresetId, value ?? string.Empty);
        }

        public LocalSanElementPosition OverlayCustomSanElementPosition
        {
            get => _overlayCustomSanElementPosition;
            set => SetValue(ref _overlayCustomSanElementPosition, value);
        }

        public LocalSanProviderLogoSource OverlayCustomSanProviderLogoSource
        {
            get => _overlayCustomSanProviderLogoSource;
            set => SetValue(ref _overlayCustomSanProviderLogoSource, value);
        }

        public string OverlayCustomSanPresetId
        {
            get => _overlayCustomSanPresetId;
            set => SetValue(ref _overlayCustomSanPresetId, value ?? string.Empty);
        }

        public string OverlayCustomSanPresetHtml
        {
            get => _overlayCustomSanPresetHtml;
            set => SetValue(ref _overlayCustomSanPresetHtml, value ?? string.Empty);
        }

        public string OverlayCustomSanPresetCss
        {
            get => _overlayCustomSanPresetCss;
            set => SetValue(ref _overlayCustomSanPresetCss, value ?? string.Empty);
        }

        public string OverlayCustomSanBaseCss
        {
            get => _overlayCustomSanBaseCss;
            set => SetValue(ref _overlayCustomSanBaseCss, value ?? string.Empty);
        }

        public string OverlayCustomSanBaseAnimCss
        {
            get => _overlayCustomSanBaseAnimCss;
            set => SetValue(ref _overlayCustomSanBaseAnimCss, value ?? string.Empty);
        }

        public string OverlayCustomSanAssetRootPath
        {
            get => _overlayCustomSanAssetRootPath;
            set => SetValue(ref _overlayCustomSanAssetRootPath, value ?? string.Empty);
        }

        public string OverlayCustomSanThemeJson
        {
            get => _overlayCustomSanThemeJson;
            set => SetValue(ref _overlayCustomSanThemeJson, value ?? string.Empty);
        }

        public string OverlayCustomSanThemeDirectory
        {
            get => _overlayCustomSanThemeDirectory;
            set => SetValue(ref _overlayCustomSanThemeDirectory, value ?? string.Empty);
        }

        public string OverlayCustomManualElementCss
        {
            get => _overlayCustomManualElementCss;
            set => SetValue(ref _overlayCustomManualElementCss, value ?? string.Empty);
        }

        public int OverlayCustomSanView1DurationMilliseconds
        {
            get => _overlayCustomSanView1DurationMilliseconds;
            set => SetValue(ref _overlayCustomSanView1DurationMilliseconds, Math.Max(500, Math.Min(30000, value)));
        }

        public int OverlayCustomSanView2DurationMilliseconds
        {
            get => _overlayCustomSanView2DurationMilliseconds;
            set => SetValue(ref _overlayCustomSanView2DurationMilliseconds, Math.Max(500, Math.Min(30000, value)));
        }

        public bool OverlayCustomTitleBold
        {
            get => _overlayCustomTitleBold;
            set => SetValue(ref _overlayCustomTitleBold, value);
        }

        public bool OverlayCustomTitleItalic
        {
            get => _overlayCustomTitleItalic;
            set => SetValue(ref _overlayCustomTitleItalic, value);
        }

        public bool OverlayCustomTitleUnderline
        {
            get => _overlayCustomTitleUnderline;
            set => SetValue(ref _overlayCustomTitleUnderline, value);
        }

        public bool OverlayCustomTitleStrikethrough
        {
            get => _overlayCustomTitleStrikethrough;
            set => SetValue(ref _overlayCustomTitleStrikethrough, value);
        }

        public bool OverlayCustomDetailBold
        {
            get => _overlayCustomDetailBold;
            set => SetValue(ref _overlayCustomDetailBold, value);
        }

        public bool OverlayCustomDetailItalic
        {
            get => _overlayCustomDetailItalic;
            set => SetValue(ref _overlayCustomDetailItalic, value);
        }

        public bool OverlayCustomDetailUnderline
        {
            get => _overlayCustomDetailUnderline;
            set => SetValue(ref _overlayCustomDetailUnderline, value);
        }

        public bool OverlayCustomDetailStrikethrough
        {
            get => _overlayCustomDetailStrikethrough;
            set => SetValue(ref _overlayCustomDetailStrikethrough, value);
        }

        public bool OverlayCustomMetaBold
        {
            get => _overlayCustomMetaBold;
            set => SetValue(ref _overlayCustomMetaBold, value);
        }

        public bool OverlayCustomMetaItalic
        {
            get => _overlayCustomMetaItalic;
            set => SetValue(ref _overlayCustomMetaItalic, value);
        }

        public bool OverlayCustomMetaUnderline
        {
            get => _overlayCustomMetaUnderline;
            set => SetValue(ref _overlayCustomMetaUnderline, value);
        }

        public bool OverlayCustomMetaStrikethrough
        {
            get => _overlayCustomMetaStrikethrough;
            set => SetValue(ref _overlayCustomMetaStrikethrough, value);
        }

        public bool OverlayCustomLine4Bold
        {
            get => _overlayCustomLine4Bold;
            set => SetValue(ref _overlayCustomLine4Bold, value);
        }

        public bool OverlayCustomLine4Italic
        {
            get => _overlayCustomLine4Italic;
            set => SetValue(ref _overlayCustomLine4Italic, value);
        }

        public bool OverlayCustomLine4Underline
        {
            get => _overlayCustomLine4Underline;
            set => SetValue(ref _overlayCustomLine4Underline, value);
        }

        public bool OverlayCustomLine4Strikethrough
        {
            get => _overlayCustomLine4Strikethrough;
            set => SetValue(ref _overlayCustomLine4Strikethrough, value);
        }

        public bool OverlayCustomLine5Bold
        {
            get => _overlayCustomLine5Bold;
            set => SetValue(ref _overlayCustomLine5Bold, value);
        }

        public bool OverlayCustomLine5Italic
        {
            get => _overlayCustomLine5Italic;
            set => SetValue(ref _overlayCustomLine5Italic, value);
        }

        public bool OverlayCustomLine5Underline
        {
            get => _overlayCustomLine5Underline;
            set => SetValue(ref _overlayCustomLine5Underline, value);
        }

        public bool OverlayCustomLine5Strikethrough
        {
            get => _overlayCustomLine5Strikethrough;
            set => SetValue(ref _overlayCustomLine5Strikethrough, value);
        }

        public bool OverlayCustomLine6Bold
        {
            get => _overlayCustomLine6Bold;
            set => SetValue(ref _overlayCustomLine6Bold, value);
        }

        public bool OverlayCustomLine6Italic
        {
            get => _overlayCustomLine6Italic;
            set => SetValue(ref _overlayCustomLine6Italic, value);
        }

        public bool OverlayCustomLine6Underline
        {
            get => _overlayCustomLine6Underline;
            set => SetValue(ref _overlayCustomLine6Underline, value);
        }

        public bool OverlayCustomLine6Strikethrough
        {
            get => _overlayCustomLine6Strikethrough;
            set => SetValue(ref _overlayCustomLine6Strikethrough, value);
        }

        public LocalOverlayIconSource OverlayCustomIconSource
        {
            get => _overlayCustomIconSource;
            set => SetValue(ref _overlayCustomIconSource, value);
        }

        public string OverlayCustomIconPath
        {
            get => _overlayCustomIconPath;
            set => SetValue(ref _overlayCustomIconPath, value ?? string.Empty);
        }

        public string OverlayCustomSecondaryIconPath
        {
            get => _overlayCustomSecondaryIconPath;
            set => SetValue(ref _overlayCustomSecondaryIconPath, value ?? string.Empty);
        }

        public string OverlayCustomIconLibraryPaths
        {
            get => _overlayCustomIconLibraryPaths;
            set => SetValue(ref _overlayCustomIconLibraryPaths, value ?? string.Empty);
        }

        public IEnumerable<string> GetOverlayCustomIconLibraryPathEntries()
        {
            return SplitCustomIconLibraryPaths(OverlayCustomIconLibraryPaths);
        }

        public void SetOverlayCustomIconLibraryPathEntries(IEnumerable<string> paths)
        {
            OverlayCustomIconLibraryPaths = JoinCustomIconLibraryPaths(paths);
        }

        public bool OverlayCustomShowIconRarityGlow
        {
            get => _overlayCustomShowIconRarityGlow;
            set => SetValue(ref _overlayCustomShowIconRarityGlow, value);
        }

        public bool OverlayCustomShowSecondaryIconRarityGlow
        {
            get => _overlayCustomShowSecondaryIconRarityGlow;
            set => SetValue(ref _overlayCustomShowSecondaryIconRarityGlow, value);
        }

        public string OverlayCustomCoverImagePath
        {
            get => _overlayCustomCoverImagePath;
            set => SetValue(ref _overlayCustomCoverImagePath, value ?? string.Empty);
        }

        public string OverlayCustomBannerImagePath
        {
            get => _overlayCustomBannerImagePath;
            set => SetValue(ref _overlayCustomBannerImagePath, value ?? string.Empty);
        }

        public int SelectedCustomStyleSlot
        {
            get => _selectedCustomStyleSlot;
            set => SetValue(ref _selectedCustomStyleSlot, Math.Max(1, value));
        }

        public List<LocalCustomOverlayStyleSlot> CustomOverlayStyleSlots
        {
            get => _customOverlayStyleSlots;
            set => SetValue(ref _customOverlayStyleSlots, NormalizeCustomOverlayStyleSlots(value));
        }

        public bool EnableWindowsToastNotifications
        {
            get => _enableWindowsToastNotifications;
            set => SetValue(ref _enableWindowsToastNotifications, value);
        }

        public bool EnableInAppUnlockNotifications
        {
            get => _enableInAppUnlockNotifications;
            set => SetValue(ref _enableInAppUnlockNotifications, value);
        }

        public string ExtraUnlockSoundPaths
        {
            get => _extraUnlockSoundPaths;
            set => SetValue(ref _extraUnlockSoundPaths, value ?? string.Empty);
        }

        public double OverlayCustomIconCornerRadius
        {
            get => _overlayCustomIconCornerRadius;
            set => SetValue(ref _overlayCustomIconCornerRadius, Math.Max(0, Math.Min(110, value)));
        }

        public double OverlayCustomSecondaryIconCornerRadius
        {
            get => _overlayCustomSecondaryIconCornerRadius;
            set => SetValue(ref _overlayCustomSecondaryIconCornerRadius, Math.Max(0, Math.Min(110, value)));
        }

        public ScoreProgressNotificationSettings CollectionProgressNotifications
        {
            get => _collectionProgressNotifications ?? (_collectionProgressNotifications = new ScoreProgressNotificationSettings());
            set => SetValue(ref _collectionProgressNotifications, value ?? new ScoreProgressNotificationSettings());
        }

        public ScoreProgressNotificationSettings PrestigeProgressNotifications
        {
            get => _prestigeProgressNotifications ?? (_prestigeProgressNotifications = new ScoreProgressNotificationSettings());
            set => SetValue(ref _prestigeProgressNotifications, value ?? new ScoreProgressNotificationSettings());
        }

        public LocalSettings CreateProgressNotificationCopy(ScoreProgressNotificationSettings notification)
        {
            var copy = (LocalSettings)Clone();
            var slots = copy.CustomOverlayStyleSlots;
            if (slots == null || slots.Count == 0)
            {
                return copy;
            }

            var index = Math.Max(0, Math.Min(slots.Count - 1, notification?.CustomStyleSlotIndex ?? 0));
            ApplyCustomStyleSlot(copy, slots[index]);
            return copy;
        }

        public static void ApplyCustomStyleSlot(LocalSettings target, LocalCustomOverlayStyleSlot slot)
        {
            if (target == null || slot == null) return;
            foreach (var sourceProperty in typeof(LocalCustomOverlayStyleSlot).GetProperties())
            {
                if (!sourceProperty.CanRead || sourceProperty.Name == nameof(LocalCustomOverlayStyleSlot.Name)) continue;
                var targetName = "OverlayCustom" + sourceProperty.Name;
                if (sourceProperty.Name == nameof(LocalCustomOverlayStyleSlot.TransitionStyle)) targetName = nameof(UnlockOverlayTransitionStyle);
                else if (sourceProperty.Name == nameof(LocalCustomOverlayStyleSlot.CustomIconPath)) targetName = nameof(OverlayCustomIconPath);
                else if (sourceProperty.Name == nameof(LocalCustomOverlayStyleSlot.SecondaryCustomIconPath)) targetName = nameof(OverlayCustomSecondaryIconPath);
                else if (sourceProperty.Name == nameof(LocalCustomOverlayStyleSlot.CustomCoverImagePath)) targetName = nameof(OverlayCustomCoverImagePath);
                else if (sourceProperty.Name == nameof(LocalCustomOverlayStyleSlot.CustomBannerImagePath)) targetName = nameof(OverlayCustomBannerImagePath);

                var targetProperty = typeof(LocalSettings).GetProperty(targetName) ?? typeof(LocalSettings).GetProperty(sourceProperty.Name);
                if (targetProperty?.CanWrite == true && targetProperty.PropertyType == sourceProperty.PropertyType)
                {
                    targetProperty.SetValue(target, sourceProperty.GetValue(slot));
                }
            }
        }

        public string BundledUnlockSoundPath
        {
            get => _bundledUnlockSoundPath;
            set
            {
                if (SetValue(ref _bundledUnlockSoundPath, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(UnlockSoundPath));
                }
            }
        }

        public string CustomUnlockSoundPath
        {
            get => _customUnlockSoundPath;
            set
            {
                if (SetValue(ref _customUnlockSoundPath, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(UnlockSoundPath));
                }
            }
        }

        [JsonIgnore]
        public string UnlockSoundPath
        {
            get => !string.IsNullOrWhiteSpace(CustomUnlockSoundPath)
                ? CustomUnlockSoundPath
                : GetEffectiveBundledUnlockSoundPath();
        }

        [JsonIgnore]
        public string EffectiveBundledUnlockSoundPath => GetEffectiveBundledUnlockSoundPath();

        [JsonProperty("UnlockSoundPath", NullValueHandling = NullValueHandling.Ignore)]
        public string LegacyUnlockSoundPath
        {
            get => null;
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return;
                }

                if (Path.IsPathRooted(value))
                {
                    CustomUnlockSoundPath = value;
                }
                else
                {
                    BundledUnlockSoundPath = value;
                }
            }
        }

        public string SteamUserdataPath
        {
            get => _steamUserdataPath;
            set => SetValue(ref _steamUserdataPath, value ?? string.Empty);
        }

        public Dictionary<Guid, string> SteamAppCacheUserOverrides
        {
            get => _steamAppCacheUserOverrides;
            set => SetValue(ref _steamAppCacheUserOverrides, value ?? new Dictionary<Guid, string>());
        }

        public bool RefreshAchievementsOnGameClose
        {
            get => _refreshAchievementsOnGameClose;
            set => SetValue(ref _refreshAchievementsOnGameClose, value);
        }

        public Dictionary<Guid, bool> RefreshOnGameCloseOverrides
        {
            get => _refreshOnGameCloseOverrides;
            set => SetValue(ref _refreshOnGameCloseOverrides, value ?? new Dictionary<Guid, bool>());
        }

        public LocalSteamSchemaPreference SteamSchemaPreference
        {
            get => _steamSchemaPreference;
            set => SetValue(ref _steamSchemaPreference, value);
        }

        public LocalImportedGameLibraryTarget ImportedGameLibraryTarget
        {
            get => _importedGameLibraryTarget;
            set => SetValue(ref _importedGameLibraryTarget, value);
        }

        public string ImportedGameCustomSourceName
        {
            get => _importedGameCustomSourceName;
            set => SetValue(ref _importedGameCustomSourceName, value ?? string.Empty);
        }

        public string ImportedGameMetadataSourceId
        {
            get => _importedGameMetadataSourceId;
            set => SetValue(ref _importedGameMetadataSourceId, value ?? string.Empty);
        }

        public string SuccessStoryImportCategoryKeys
        {
            get => _successStoryImportCategoryKeys;
            set => SetValue(ref _successStoryImportCategoryKeys, value ?? string.Empty);
        }

        public string SuccessStoryImportExcludedCategoryKeys
        {
            get => _successStoryImportExcludedCategoryKeys;
            set => SetValue(ref _successStoryImportExcludedCategoryKeys, value ?? string.Empty);
        }

        public string SuccessStoryImportTargetSourceName
        {
            get => _successStoryImportTargetSourceName;
            set => SetValue(ref _successStoryImportTargetSourceName, value ?? string.Empty);
        }

        public string SuccessStoryImportPath
        {
            get => _successStoryImportPath;
            set => SetValue(ref _successStoryImportPath, value ?? string.Empty);
        }

        public string SuccessStoryCategoryScanSnapshotJson
        {
            get => _successStoryCategoryScanSnapshotJson;
            set => SetValue(ref _successStoryCategoryScanSnapshotJson, value ?? string.Empty);
        }

        public bool SuccessStoryOverwriteExistingAchievements
        {
            get => _successStoryOverwriteExistingAchievements;
            set => SetValue(ref _successStoryOverwriteExistingAchievements, value);
        }

        public LocalExistingGameImportBehavior ExistingGameImportBehavior
        {
            get => _existingGameImportBehavior;
            set => SetValue(ref _existingGameImportBehavior, value);
        }

        public bool IncludeFoldersWithoutAchievementFilesOnImport
        {
            get => _includeFoldersWithoutAchievementFilesOnImport;
            set => SetValue(ref _includeFoldersWithoutAchievementFilesOnImport, value);
        }

        public LocalIconRateLimitRetryMode IconRateLimitRetryMode
        {
            get => _iconRateLimitRetryMode;
            set => SetValue(ref _iconRateLimitRetryMode, value);
        }

        public int IconRateLimitRetryRounds
        {
            get => _iconRateLimitRetryRounds;
            set => SetValue(ref _iconRateLimitRetryRounds, Math.Max(1, Math.Min(20, value)));
        }

        public string SteamAppCacheUserId
        {
            get => _steamAppCacheUserId;
            set => SetValue(ref _steamAppCacheUserId, value ?? string.Empty);
        }

        public string CustomProviderIconPath
        {
            get => _customProviderIconPath;
            set => SetValue(ref _customProviderIconPath, value ?? string.Empty);
        }

        /// <summary>
        /// When set to a non-empty provider key (e.g. "Steam"), the Local provider will borrow that
        /// platform's vector icon and colour instead of the built-in Local icon.
        /// A custom image file (<see cref="CustomProviderIconPath"/>) takes precedence over this.
        /// </summary>
        public string BorrowedProviderIconKey
        {
            get => _borrowedProviderIconKey;
            set => SetValue(ref _borrowedProviderIconKey, value ?? string.Empty);
        }

        /// <summary>
        /// When true, shows a notification when multiple local folders are detected for the same
        /// game so the user can set a folder override to resolve the ambiguity.
        /// </summary>
        public bool WarnOnAmbiguousLocalFolder
        {
            get => _warnOnAmbiguousLocalFolder;
            set => SetValue(ref _warnOnAmbiguousLocalFolder, value);
        }

        public bool EnableGameCoverInOverlay
        {
            get => _enableGameCoverInOverlay;
            set => SetValue(ref _enableGameCoverInOverlay, value);
        }

        public LocalOverlayCoverPosition GameCoverPosition
        {
            get => _gameCoverPosition;
            set => SetValue(ref _gameCoverPosition, value);
        }

        public double GameCoverWidth
        {
            get => _gameCoverWidth;
            set => SetValue(ref _gameCoverWidth, Math.Max(40, Math.Min(200, value)));
        }

        public bool EnableGameBannerAsBackground
        {
            get => _enableGameBannerAsBackground;
            set => SetValue(ref _enableGameBannerAsBackground, value);
        }

        public double GameBannerOpacity
        {
            get => _gameBannerOpacity;
            set => SetValue(ref _gameBannerOpacity, Math.Max(0.1, Math.Min(1.0, value)));
        }

        public int GameBannerBlurRadius
        {
            get => _gameBannerBlurRadius;
            set => SetValue(ref _gameBannerBlurRadius, Math.Max(0, Math.Min(50, value)));
        }

        public Dictionary<Guid, int> SteamAppIdOverrides
        {
            get => _steamAppIdOverrides;
            set => SetValue(ref _steamAppIdOverrides, value ?? new Dictionary<Guid, int>());
        }

        public Dictionary<Guid, int> LumaPlayAppIdOverrides
        {
            get => _lumaPlayAppIdOverrides;
            set => SetValue(ref _lumaPlayAppIdOverrides, value ?? new Dictionary<Guid, int>());
        }

        public Dictionary<Guid, string> LumaPlayIniPathOverrides
        {
            get => _lumaPlayIniPathOverrides;
            set => SetValue(ref _lumaPlayIniPathOverrides, value ?? new Dictionary<Guid, string>());
        }

        public Dictionary<Guid, string> LocalFolderOverrides
        {
            get => _localFolderOverrides;
            set => SetValue(ref _localFolderOverrides, value ?? new Dictionary<Guid, string>());
        }

        public Dictionary<Guid, string> CustomSchemaPathOverrides
        {
            get => _customSchemaPathOverrides;
            set => SetValue(ref _customSchemaPathOverrides, value ?? new Dictionary<Guid, string>());
        }

        public Dictionary<Guid, bool> CustomSchemaEnabledOverrides
        {
            get => _customSchemaEnabledOverrides;
            set => SetValue(ref _customSchemaEnabledOverrides, value ?? new Dictionary<Guid, bool>());
        }

        public LocalSettings()
        {
            IsEnabled = true;
        }

        public IReadOnlyList<string> GetExtraLocalPathEntries()
        {
            return SplitExtraLocalPaths(ExtraLocalPaths).ToList();
        }

        public IReadOnlyList<string> GetExcludedLocalPathEntries()
        {
            return SplitExtraLocalPaths(ExcludedLocalPaths).ToList();
        }

        public IReadOnlyList<string> GetExtraUnlockSoundPathEntries()
        {
            return SplitUnlockSoundPaths(ExtraUnlockSoundPaths).ToList();
        }

        public IReadOnlyList<string> GetSuccessStoryImportCategoryKeyEntries()
        {
            return SplitListSetting(SuccessStoryImportCategoryKeys).ToList();
        }

        public IReadOnlyList<string> GetSuccessStoryImportExcludedCategoryKeyEntries()
        {
            return SplitListSetting(SuccessStoryImportExcludedCategoryKeys).ToList();
        }

        public void SetExtraLocalPathEntries(IEnumerable<string> paths)
        {
            ExtraLocalPaths = JoinExtraLocalPaths(paths);
        }

        public void SetExcludedLocalPathEntries(IEnumerable<string> paths)
        {
            ExcludedLocalPaths = JoinExtraLocalPaths(paths);
        }

        public void SetExtraUnlockSoundPathEntries(IEnumerable<string> paths)
        {
            ExtraUnlockSoundPaths = JoinUnlockSoundPaths(paths);
        }

        public void SetSuccessStoryImportCategoryKeyEntries(IEnumerable<string> keys)
        {
            SuccessStoryImportCategoryKeys = JoinListSetting(keys);
        }

        public void SetSuccessStoryImportExcludedCategoryKeyEntries(IEnumerable<string> keys)
        {
            SuccessStoryImportExcludedCategoryKeys = JoinListSetting(keys);
        }

        public static IEnumerable<string> SplitExtraLocalPaths(string rawPaths)
        {
            if (string.IsNullOrWhiteSpace(rawPaths))
            {
                return Enumerable.Empty<string>();
            }

            return NormalizeExtraLocalPaths(rawPaths.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        public static string JoinExtraLocalPaths(IEnumerable<string> paths)
        {
            return string.Join(";", NormalizeExtraLocalPaths(paths));
        }

        public static IEnumerable<string> SplitUnlockSoundPaths(string rawPaths)
        {
            if (string.IsNullOrWhiteSpace(rawPaths))
            {
                return Enumerable.Empty<string>();
            }

            return NormalizeUnlockSoundPaths(rawPaths.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        public static string JoinUnlockSoundPaths(IEnumerable<string> paths)
        {
            return string.Join(";", NormalizeUnlockSoundPaths(paths));
        }

        public static IEnumerable<string> SplitCustomIconLibraryPaths(string rawPaths)
        {
            if (string.IsNullOrWhiteSpace(rawPaths))
            {
                return Enumerable.Empty<string>();
            }

            return NormalizeCustomIconLibraryPaths(rawPaths.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        public static string JoinCustomIconLibraryPaths(IEnumerable<string> paths)
        {
            return string.Join(";", NormalizeCustomIconLibraryPaths(paths));
        }

        public static IEnumerable<string> SplitListSetting(string rawValue)
        {
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return Enumerable.Empty<string>();
            }

            return NormalizeListSetting(rawValue.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        public static string JoinListSetting(IEnumerable<string> values)
        {
            return string.Join(";", NormalizeListSetting(values));
        }

        private static IEnumerable<string> NormalizeExtraLocalPaths(IEnumerable<string> paths)
        {
            if (paths == null)
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawPath in paths)
            {
                var normalizedPath = rawPath?.Trim();
                if (string.IsNullOrWhiteSpace(normalizedPath) || !seen.Add(normalizedPath))
                {
                    continue;
                }

                yield return normalizedPath;
            }
        }

        private static IEnumerable<string> NormalizeCustomIconLibraryPaths(IEnumerable<string> paths)
        {
            if (paths == null)
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                var normalized = (path ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                if (seen.Add(normalized))
                {
                    yield return normalized;
                }
            }
        }

        private static IEnumerable<string> NormalizeUnlockSoundPaths(IEnumerable<string> paths)
        {
            if (paths == null)
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawPath in paths)
            {
                var normalizedPath = rawPath?.Trim();
                if (string.IsNullOrWhiteSpace(normalizedPath) || !seen.Add(normalizedPath))
                {
                    continue;
                }

                yield return normalizedPath;
            }
        }

        private static IEnumerable<string> NormalizeListSetting(IEnumerable<string> values)
        {
            if (values == null)
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawValue in values)
            {
                var normalizedValue = rawValue?.Trim();
                if (string.IsNullOrWhiteSpace(normalizedValue) || !seen.Add(normalizedValue))
                {
                    continue;
                }

                yield return normalizedValue;
            }
        }

        private string GetEffectiveBundledUnlockSoundPath()
        {
            return string.IsNullOrWhiteSpace(BundledUnlockSoundPath)
                ? DefaultBundledUnlockSoundPath
                : BundledUnlockSoundPath;
        }

        private string GetEffectiveScreenshotSaveFolder()
        {
            if (!string.IsNullOrWhiteSpace(ScreenshotSaveFolder))
            {
                return ScreenshotSaveFolder;
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "PlayniteAchievements",
                "UnlockScreenshots");
        }

        private static string NormalizeColorSetting(string value, string fallback)
        {
            var normalized = value?.Trim();
            return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
        }

        private static List<LocalCustomOverlayStyleSlot> CreateDefaultCustomOverlayStyleSlots()
        {
            return new List<LocalCustomOverlayStyleSlot>(1)
            {
                new LocalCustomOverlayStyleSlot { Name = "Slot 1", IconSize = 58, TitleTemplate = "Achievement unlocked", GameNameTemplate = "<gameName>", AchievementTemplate = "Unlocked: <achievementName>", MetaTemplate = "<provider> / Custom" }
            };
        }

        private static List<LocalCustomOverlayStyleSlot> NormalizeCustomOverlayStyleSlots(IEnumerable<LocalCustomOverlayStyleSlot> slots)
        {
            if (slots == null)
                return CreateDefaultCustomOverlayStyleSlots();

            var source = slots.ToList();
            if (source.Count == 0)
                return CreateDefaultCustomOverlayStyleSlots();

            var normalized = new List<LocalCustomOverlayStyleSlot>(source.Count);
            for (var index = 0; index < source.Count; index++)
            {
                var slot = source[index] ?? new LocalCustomOverlayStyleSlot();
                normalized.Add(new LocalCustomOverlayStyleSlot
                {
                    Name = string.IsNullOrWhiteSpace(slot.Name) ? $"Slot {index + 1}" : slot.Name.Trim(),
                    AutoResizeToContent = slot.AutoResizeToContent,
                    WrapAllText = slot.WrapAllText,
                    ShowLine1 = slot.ShowLine1,
                    ShowBorder = slot.ShowBorder,
                    ShowGameName = slot.ShowGameName,
                    ShowMeta = slot.ShowMeta,
                    IconSize = Math.Max(24, Math.Min(220, slot.IconSize <= 0 ? 58 : slot.IconSize)),
                    SecondaryIconSize = Math.Max(24, Math.Min(220, slot.SecondaryIconSize <= 0 ? (slot.IconSize <= 0 ? 58 : slot.IconSize) : slot.SecondaryIconSize)),
                    IconCornerRadius = Math.Max(0, Math.Min(110, slot.IconCornerRadius)),
                    SecondaryIconCornerRadius = Math.Max(0, Math.Min(110, slot.SecondaryIconCornerRadius)),
                    Width = Math.Max(280, Math.Min(900, slot.Width)),
                    Height = Math.Max(MinCustomOverlayHeight, Math.Min(320, slot.Height)),
                    CornerRadius = Math.Max(0, Math.Min(180, slot.CornerRadius)),
                    TitleFontSize = Math.Max(10, Math.Min(56, slot.TitleFontSize)),
                    DetailFontSize = Math.Max(9, Math.Min(44, slot.DetailFontSize)),
                    MetaFontSize = Math.Max(8, Math.Min(24, slot.MetaFontSize)),
                    Line4FontSize = Math.Max(8, Math.Min(24, slot.Line4FontSize <= 0 ? slot.MetaFontSize : slot.Line4FontSize)),
                    Line5FontSize = Math.Max(8, Math.Min(24, slot.Line5FontSize <= 0 ? slot.MetaFontSize : slot.Line5FontSize)),
                    Line6FontSize = Math.Max(8, Math.Min(24, slot.Line6FontSize <= 0 ? slot.MetaFontSize : slot.Line6FontSize)),
                    BackgroundColor = NormalizeColorSetting(slot.BackgroundColor, "#1E2430"),
                    BorderColor = NormalizeColorSetting(slot.BorderColor, "#6FA3D8"),
                    AccentColor = NormalizeColorSetting(slot.AccentColor, "#A7E0FF"),
                    TitleColor = NormalizeColorSetting(slot.TitleColor, "#FFFFFF"),
                    DetailColor = NormalizeColorSetting(slot.DetailColor, "#E7EEF7"),
                    MetaColor = NormalizeColorSetting(slot.MetaColor, "#BCD0E5"),
                    BackgroundImagePath = slot.BackgroundImagePath ?? string.Empty,
                    TitleTemplate = slot.TitleTemplate ?? "Achievement unlocked",
                    GameNameTemplate = slot.GameNameTemplate ?? "<gameName>",
                    AchievementTemplate = slot.AchievementTemplate ?? "Unlocked: <achievementName>",
                    MetaTemplate = slot.MetaTemplate ?? "<provider> / Custom",
                    Line4Template = slot.Line4Template ?? string.Empty,
                    Line5Template = slot.Line5Template ?? string.Empty,
                    Line6Template = slot.Line6Template ?? string.Empty,
                    ShowLine4 = slot.ShowLine4,
                    ShowLine5 = slot.ShowLine5,
                    ShowLine6 = slot.ShowLine6,
                    Line4Color = NormalizeColorSetting(slot.Line4Color, "#BCD0E5"),
                    Line5Color = NormalizeColorSetting(slot.Line5Color, "#BCD0E5"),
                    Line6Color = NormalizeColorSetting(slot.Line6Color, "#BCD0E5"),
                    Line1OutlineColor = NormalizeColorSetting(slot.Line1OutlineColor, "transparent"),
                    Line2OutlineColor = NormalizeColorSetting(slot.Line2OutlineColor, "transparent"),
                    Line3OutlineColor = NormalizeColorSetting(slot.Line3OutlineColor, "transparent"),
                    Line4OutlineColor = NormalizeColorSetting(slot.Line4OutlineColor, "transparent"),
                    Line5OutlineColor = NormalizeColorSetting(slot.Line5OutlineColor, "transparent"),
                    Line6OutlineColor = NormalizeColorSetting(slot.Line6OutlineColor, "transparent"),
                    Line1ShadowColor = NormalizeColorSetting(slot.Line1ShadowColor, "transparent"),
                    Line2ShadowColor = NormalizeColorSetting(slot.Line2ShadowColor, "transparent"),
                    Line3ShadowColor = NormalizeColorSetting(slot.Line3ShadowColor, "transparent"),
                    Line4ShadowColor = NormalizeColorSetting(slot.Line4ShadowColor, "transparent"),
                    Line5ShadowColor = NormalizeColorSetting(slot.Line5ShadowColor, "transparent"),
                    Line6ShadowColor = NormalizeColorSetting(slot.Line6ShadowColor, "transparent"),
                    OutlineSize = Math.Max(0, Math.Min(8, slot.OutlineSize <= 0 ? 0.6 : slot.OutlineSize)),
                    ShadowSize = Math.Max(0, Math.Min(24, slot.ShadowSize <= 0 ? 2 : slot.ShadowSize)),
                    Line1OutlineSize = Math.Max(0, Math.Min(8, slot.Line1OutlineSize <= 0 ? slot.OutlineSize : slot.Line1OutlineSize)),
                    Line2OutlineSize = Math.Max(0, Math.Min(8, slot.Line2OutlineSize <= 0 ? slot.OutlineSize : slot.Line2OutlineSize)),
                    Line3OutlineSize = Math.Max(0, Math.Min(8, slot.Line3OutlineSize <= 0 ? slot.OutlineSize : slot.Line3OutlineSize)),
                    Line4OutlineSize = Math.Max(0, Math.Min(8, slot.Line4OutlineSize <= 0 ? slot.OutlineSize : slot.Line4OutlineSize)),
                    Line5OutlineSize = Math.Max(0, Math.Min(8, slot.Line5OutlineSize <= 0 ? slot.OutlineSize : slot.Line5OutlineSize)),
                    Line6OutlineSize = Math.Max(0, Math.Min(8, slot.Line6OutlineSize <= 0 ? slot.OutlineSize : slot.Line6OutlineSize)),
                    Line1ShadowSize = Math.Max(0, Math.Min(24, slot.Line1ShadowSize <= 0 ? slot.ShadowSize : slot.Line1ShadowSize)),
                    Line2ShadowSize = Math.Max(0, Math.Min(24, slot.Line2ShadowSize <= 0 ? slot.ShadowSize : slot.Line2ShadowSize)),
                    Line3ShadowSize = Math.Max(0, Math.Min(24, slot.Line3ShadowSize <= 0 ? slot.ShadowSize : slot.Line3ShadowSize)),
                    Line4ShadowSize = Math.Max(0, Math.Min(24, slot.Line4ShadowSize <= 0 ? slot.ShadowSize : slot.Line4ShadowSize)),
                    Line5ShadowSize = Math.Max(0, Math.Min(24, slot.Line5ShadowSize <= 0 ? slot.ShadowSize : slot.Line5ShadowSize)),
                    Line6ShadowSize = Math.Max(0, Math.Min(24, slot.Line6ShadowSize <= 0 ? slot.ShadowSize : slot.Line6ShadowSize)),
                    Line1OutlineEnabled = slot.Line1OutlineEnabled,
                    Line2OutlineEnabled = slot.Line2OutlineEnabled,
                    Line3OutlineEnabled = slot.Line3OutlineEnabled,
                    Line4OutlineEnabled = slot.Line4OutlineEnabled,
                    Line5OutlineEnabled = slot.Line5OutlineEnabled,
                    Line6OutlineEnabled = slot.Line6OutlineEnabled,
                    Line1ShadowEnabled = slot.Line1ShadowEnabled,
                    Line2ShadowEnabled = slot.Line2ShadowEnabled,
                    Line3ShadowEnabled = slot.Line3ShadowEnabled,
                    Line4ShadowEnabled = slot.Line4ShadowEnabled,
                    Line5ShadowEnabled = slot.Line5ShadowEnabled,
                    Line6ShadowEnabled = slot.Line6ShadowEnabled,
                    LineSpacing = Math.Max(0, Math.Min(24, slot.LineSpacing)),
                    Line1Spacing = Math.Max(0, Math.Min(48, slot.Line1Spacing)),
                    Line2Spacing = Math.Max(0, Math.Min(48, slot.Line2Spacing)),
                    Line3Spacing = Math.Max(0, Math.Min(48, slot.Line3Spacing)),
                    Line4Spacing = Math.Max(0, Math.Min(48, slot.Line4Spacing)),
                    Line5Spacing = Math.Max(0, Math.Min(48, slot.Line5Spacing)),
                    Line6Spacing = Math.Max(0, Math.Min(48, slot.Line6Spacing)),
                    Line1View = slot.Line1View,
                    Line2View = slot.Line2View,
                    Line3View = slot.Line3View,
                    Line4View = slot.Line4View,
                    Line5View = slot.Line5View,
                    Line6View = slot.Line6View,
                    Line1Order = Math.Max(1, Math.Min(6, slot.Line1Order <= 0 ? 1 : slot.Line1Order)),
                    Line2Order = Math.Max(1, Math.Min(6, slot.Line2Order <= 0 ? 1 : slot.Line2Order)),
                    Line3Order = Math.Max(1, Math.Min(6, slot.Line3Order <= 0 ? 2 : slot.Line3Order)),
                    Line4Order = Math.Max(1, Math.Min(6, slot.Line4Order <= 0 ? 3 : slot.Line4Order)),
                    Line5Order = Math.Max(1, Math.Min(6, slot.Line5Order <= 0 ? 4 : slot.Line5Order)),
                    Line6Order = Math.Max(1, Math.Min(6, slot.Line6Order <= 0 ? 5 : slot.Line6Order)),
                    LayoutStyle = slot.LayoutStyle,
                    AnimationStyle = slot.AnimationStyle,
                    SecondaryIconSource = slot.SecondaryIconSource,
                    ShowSecondaryIcon = slot.ShowSecondaryIcon,
                    SanElementPresetId = slot.SanElementPresetId ?? string.Empty,
                    SanElementPosition = slot.SanElementPosition,
                    SanProviderLogoSource = slot.SanProviderLogoSource,
                    TransitionStyle = slot.TransitionStyle == LocalUnlockOverlayTransitionStyle.Fade && !string.IsNullOrWhiteSpace(slot.SanPresetId)
                        ? InferSanTransitionStyle(slot.SanPresetId)
                        : slot.TransitionStyle,
                    SanPresetId = slot.SanPresetId ?? string.Empty,
                    SanPresetHtml = slot.SanPresetHtml ?? string.Empty,
                    SanPresetCss = slot.SanPresetCss ?? string.Empty,
                    SanBaseCss = slot.SanBaseCss ?? string.Empty,
                    SanBaseAnimCss = slot.SanBaseAnimCss ?? string.Empty,
                    SanAssetRootPath = slot.SanAssetRootPath ?? string.Empty,
                    SanThemeJson = slot.SanThemeJson ?? string.Empty,
                    SanThemeDirectory = slot.SanThemeDirectory ?? string.Empty,
                    ManualElementCss = slot.ManualElementCss ?? string.Empty,
                    SanView1DurationMilliseconds = Math.Max(500, Math.Min(30000, slot.SanView1DurationMilliseconds <= 0 ? 5000 : slot.SanView1DurationMilliseconds)),
                    SanView2DurationMilliseconds = Math.Max(500, Math.Min(30000, slot.SanView2DurationMilliseconds <= 0 ? 5000 : slot.SanView2DurationMilliseconds)),
                    TitleBold = slot.TitleBold,
                    TitleItalic = slot.TitleItalic,
                    TitleUnderline = slot.TitleUnderline,
                    TitleStrikethrough = slot.TitleStrikethrough,
                    DetailBold = slot.DetailBold,
                    DetailItalic = slot.DetailItalic,
                    DetailUnderline = slot.DetailUnderline,
                    DetailStrikethrough = slot.DetailStrikethrough,
                    MetaBold = slot.MetaBold,
                    MetaItalic = slot.MetaItalic,
                    MetaUnderline = slot.MetaUnderline,
                    MetaStrikethrough = slot.MetaStrikethrough,
                    Line4Bold = slot.Line4Bold,
                    Line4Italic = slot.Line4Italic,
                    Line4Underline = slot.Line4Underline,
                    Line4Strikethrough = slot.Line4Strikethrough,
                    Line5Bold = slot.Line5Bold,
                    Line5Italic = slot.Line5Italic,
                    Line5Underline = slot.Line5Underline,
                    Line5Strikethrough = slot.Line5Strikethrough,
                    Line6Bold = slot.Line6Bold,
                    Line6Italic = slot.Line6Italic,
                    Line6Underline = slot.Line6Underline,
                    Line6Strikethrough = slot.Line6Strikethrough,
                    IconSource = slot.IconSource,
                    ShowIconRarityGlow = slot.ShowIconRarityGlow,
                    ShowSecondaryIconRarityGlow = slot.ShowSecondaryIconRarityGlow,
                    EnableGameCoverInOverlay = slot.EnableGameCoverInOverlay,
                    GameCoverPosition = slot.GameCoverPosition,
                    GameCoverWidth = Math.Max(40, Math.Min(260, slot.GameCoverWidth <= 0 ? 80 : slot.GameCoverWidth)),
                    EnableGameBannerAsBackground = slot.EnableGameBannerAsBackground,
                    GameBannerOpacity = Math.Max(0, Math.Min(1, slot.GameBannerOpacity)),
                    GameBannerBlurRadius = Math.Max(0, Math.Min(30, slot.GameBannerBlurRadius)),
                    CustomCoverImagePath = slot.CustomCoverImagePath ?? string.Empty,
                    CustomBannerImagePath = slot.CustomBannerImagePath ?? string.Empty
                });
            }

            return normalized;
        }

        private static LocalUnlockOverlayTransitionStyle InferSanTransitionStyle(string presetId)
        {
            switch ((presetId ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "default": return LocalUnlockOverlayTransitionStyle.SanDefault;
                case "xqjan": return LocalUnlockOverlayTransitionStyle.SanXqjan;
                case "steamdeck": return LocalUnlockOverlayTransitionStyle.SanDeck;
                case "epicgames": return LocalUnlockOverlayTransitionStyle.SanEpic;
                case "xboxone": return LocalUnlockOverlayTransitionStyle.SanXboxModern;
                case "xbox360": return LocalUnlockOverlayTransitionStyle.SanXboxClassic;
                case "ps5": return LocalUnlockOverlayTransitionStyle.SanPsModern;
                case "ps4": return LocalUnlockOverlayTransitionStyle.SanPsClassic;
                case "ps3": return LocalUnlockOverlayTransitionStyle.SanPsRetro;
                case "windows": return LocalUnlockOverlayTransitionStyle.SanSquare;
                case "gfwl": return LocalUnlockOverlayTransitionStyle.SanAero;
                default: return LocalUnlockOverlayTransitionStyle.Fade;
            }
        }
    }
}
