using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
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

    public enum LocalUnlockOverlayPosition
    {
        TopRight = 0,
        TopLeft = 1,
        BottomRight = 2,
        BottomLeft = 3
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
        public double IconSize { get; set; } = 58;
        public double Width { get; set; } = 460;
        public double Height { get; set; } = 128;
        public double CornerRadius { get; set; } = 18;
        public double TitleFontSize { get; set; } = 17;
        public double DetailFontSize { get; set; } = 13;
        public double MetaFontSize { get; set; } = 11;
        public string BackgroundColor { get; set; } = "#1E2430";
        public string BorderColor { get; set; } = "#6FA3D8";
        public string AccentColor { get; set; } = "#A7E0FF";
        public string TitleColor { get; set; } = "#FFFFFF";
        public string DetailColor { get; set; } = "#E7EEF7";
        public string MetaColor { get; set; } = "#BCD0E5";
        public string BackgroundImagePath { get; set; } = string.Empty;
    }

    public class LocalSettings : ProviderSettingsBase
    {
        public const int MinActiveGameMonitoringIntervalSeconds = 1;
        public const int MaxActiveGameMonitoringIntervalSeconds = 60;
        public const int MinScreenshotDelayMilliseconds = 0;
        public const int MaxScreenshotDelayMilliseconds = 10000;
        public const int ProviderIconMaxPixelSize = 64;
        public const string DefaultBundledUnlockSoundPath = @"Resources\Sounds\Steam.wav";
        public const string DefaultScreenshotFilenameTemplate = "<dateTime>_<gameName>_<achievementName>";
        public const string SteamAppCacheUserNone = "<none>";

        private Dictionary<Guid, int> _steamAppIdOverrides = new Dictionary<Guid, int>();
        private Dictionary<Guid, string> _localFolderOverrides = new Dictionary<Guid, string>();
        private Dictionary<Guid, string> _steamAppCacheUserOverrides = new Dictionary<Guid, string>();
        private string _steamUserdataPath = string.Empty;
        private bool _enableActiveGameMonitoring;
        private int _activeGameMonitoringIntervalSeconds = 5;
        private bool _enableUnlockScreenshots;
        private string _screenshotSaveFolder = string.Empty;
        private string _screenshotFilenameTemplate = DefaultScreenshotFilenameTemplate;
        private int _screenshotDelayMilliseconds = 750;
        private LocalUnlockScreenshotCaptureMode _screenshotCaptureMode = LocalUnlockScreenshotCaptureMode.FullDesktop;
        private LocalUnlockScreenshotImageFormat _screenshotImageFormat = LocalUnlockScreenshotImageFormat.Png;
        private LocalUnlockNotificationDeliveryMode _unlockNotificationDeliveryMode = LocalUnlockNotificationDeliveryMode.Hybrid;
        private LocalUnlockOverlayPosition _unlockOverlayPosition = LocalUnlockOverlayPosition.TopRight;
        private int _unlockOverlayDurationMilliseconds = 3400;
        private int _unlockOverlayFadeInMilliseconds = 180;
        private int _unlockOverlayFadeOutMilliseconds = 280;
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
        private double _overlayCustomWidth = 460;
        private double _overlayCustomHeight = 128;
        private double _overlayCustomCornerRadius = 18;
        private double _overlayCustomTitleFontSize = 17;
        private double _overlayCustomDetailFontSize = 13;
        private double _overlayCustomMetaFontSize = 11;
        private bool _overlayCustomAutoResizeToContent;
        private bool _overlayCustomWrapAllText;
        private string _overlayCustomBackgroundColor = "#1E2430";
        private string _overlayCustomBorderColor = "#6FA3D8";
        private string _overlayCustomAccentColor = "#A7E0FF";
        private string _overlayCustomTitleColor = "#FFFFFF";
        private string _overlayCustomDetailColor = "#E7EEF7";
        private string _overlayCustomMetaColor = "#BCD0E5";
        private string _overlayCustomBackgroundImagePath = string.Empty;
        private int _selectedCustomStyleSlot = 1;
        private List<LocalCustomOverlayStyleSlot> _customOverlayStyleSlots = CreateDefaultCustomOverlayStyleSlots();
        private bool _enableInAppUnlockNotifications = true;
        private bool _enableWindowsToastNotifications = true;
        private string _bundledUnlockSoundPath = string.Empty;
        private string _customUnlockSoundPath = string.Empty;
        private string _extraUnlockSoundPaths = string.Empty;
        private LocalSteamSchemaPreference _steamSchemaPreference = LocalSteamSchemaPreference.PreferSteam;
        private LocalImportedGameLibraryTarget _importedGameLibraryTarget = LocalImportedGameLibraryTarget.None;
        private string _importedGameCustomSourceName = string.Empty;
        private string _importedGameMetadataSourceId = string.Empty;
        private LocalExistingGameImportBehavior _existingGameImportBehavior = LocalExistingGameImportBehavior.OverwriteExisting;
        private bool _includeFoldersWithoutAchievementFilesOnImport;
        private string _steamAppCacheUserId = string.Empty;
        private string _customProviderIconPath = string.Empty;

        public override string ProviderKey => "Local";

        public string ExtraLocalPaths { get; set; } = string.Empty;

        public bool EnableActiveGameMonitoring
        {
            get => _enableActiveGameMonitoring;
            set => SetValue(ref _enableActiveGameMonitoring, value);
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

        public int UnlockOverlayDurationMilliseconds
        {
            get => _unlockOverlayDurationMilliseconds;
            set => SetValue(ref _unlockOverlayDurationMilliseconds, Math.Max(1000, Math.Min(10000, value)));
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

        public int UnlockSoundLeadMilliseconds
        {
            get => _unlockSoundLeadMilliseconds;
            set => SetValue(ref _unlockSoundLeadMilliseconds, Math.Max(0, Math.Min(3000, value)));
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

        public double OverlayCustomWidth
        {
            get => _overlayCustomWidth;
            set => SetValue(ref _overlayCustomWidth, Math.Max(280, Math.Min(900, value)));
        }

        public double OverlayCustomHeight
        {
            get => _overlayCustomHeight;
            set => SetValue(ref _overlayCustomHeight, Math.Max(90, Math.Min(320, value)));
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

        public int SelectedCustomStyleSlot
        {
            get => _selectedCustomStyleSlot;
            set => SetValue(ref _selectedCustomStyleSlot, Math.Max(1, Math.Min(10, value)));
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

        public Dictionary<Guid, int> SteamAppIdOverrides
        {
            get => _steamAppIdOverrides;
            set => SetValue(ref _steamAppIdOverrides, value ?? new Dictionary<Guid, int>());
        }

        public Dictionary<Guid, string> LocalFolderOverrides
        {
            get => _localFolderOverrides;
            set => SetValue(ref _localFolderOverrides, value ?? new Dictionary<Guid, string>());
        }

        public LocalSettings()
        {
            IsEnabled = true;
        }

        public IReadOnlyList<string> GetExtraLocalPathEntries()
        {
            return SplitExtraLocalPaths(ExtraLocalPaths).ToList();
        }

        public IReadOnlyList<string> GetExtraUnlockSoundPathEntries()
        {
            return SplitUnlockSoundPaths(ExtraUnlockSoundPaths).ToList();
        }

        public void SetExtraLocalPathEntries(IEnumerable<string> paths)
        {
            ExtraLocalPaths = JoinExtraLocalPaths(paths);
        }

        public void SetExtraUnlockSoundPathEntries(IEnumerable<string> paths)
        {
            ExtraUnlockSoundPaths = JoinUnlockSoundPaths(paths);
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
            var slots = new List<LocalCustomOverlayStyleSlot>(10);
            for (var index = 1; index <= 10; index++)
            {
                slots.Add(new LocalCustomOverlayStyleSlot
                {
                    Name = $"Slot {index}",
                    IconSize = 58
                });
            }

            return slots;
        }

        private static List<LocalCustomOverlayStyleSlot> NormalizeCustomOverlayStyleSlots(IEnumerable<LocalCustomOverlayStyleSlot> slots)
        {
            var normalized = CreateDefaultCustomOverlayStyleSlots();
            if (slots == null)
            {
                return normalized;
            }

            var source = slots.ToList();
            for (var index = 0; index < normalized.Count && index < source.Count; index++)
            {
                var slot = source[index] ?? new LocalCustomOverlayStyleSlot();
                normalized[index] = new LocalCustomOverlayStyleSlot
                {
                    Name = string.IsNullOrWhiteSpace(slot.Name) ? $"Slot {index + 1}" : slot.Name.Trim(),
                    AutoResizeToContent = slot.AutoResizeToContent,
                    WrapAllText = slot.WrapAllText,
                    IconSize = Math.Max(24, Math.Min(220, slot.IconSize <= 0 ? 58 : slot.IconSize)),
                    Width = Math.Max(280, Math.Min(900, slot.Width)),
                    Height = Math.Max(90, Math.Min(320, slot.Height)),
                    CornerRadius = Math.Max(0, Math.Min(60, slot.CornerRadius)),
                    TitleFontSize = Math.Max(10, Math.Min(34, slot.TitleFontSize)),
                    DetailFontSize = Math.Max(9, Math.Min(28, slot.DetailFontSize)),
                    MetaFontSize = Math.Max(8, Math.Min(24, slot.MetaFontSize)),
                    BackgroundColor = NormalizeColorSetting(slot.BackgroundColor, "#1E2430"),
                    BorderColor = NormalizeColorSetting(slot.BorderColor, "#6FA3D8"),
                    AccentColor = NormalizeColorSetting(slot.AccentColor, "#A7E0FF"),
                    TitleColor = NormalizeColorSetting(slot.TitleColor, "#FFFFFF"),
                    DetailColor = NormalizeColorSetting(slot.DetailColor, "#E7EEF7"),
                    MetaColor = NormalizeColorSetting(slot.MetaColor, "#BCD0E5"),
                    BackgroundImagePath = slot.BackgroundImagePath ?? string.Empty
                };
            }

            return normalized;
        }
    }
}
