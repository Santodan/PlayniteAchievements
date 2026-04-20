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

    public class LocalSettings : ProviderSettingsBase
    {
        public const int MinActiveGameMonitoringIntervalSeconds = 2;
        public const int MaxActiveGameMonitoringIntervalSeconds = 60;
        public const int ProviderIconMaxPixelSize = 64;
        public const string DefaultBundledUnlockSoundPath = @"Resources\Sounds\Steam.wav";
        public const string SteamAppCacheUserNone = "<none>";

        private Dictionary<Guid, int> _steamAppIdOverrides = new Dictionary<Guid, int>();
        private Dictionary<Guid, string> _localFolderOverrides = new Dictionary<Guid, string>();
        private string _steamUserdataPath = string.Empty;
        private bool _enableActiveGameMonitoring;
        private int _activeGameMonitoringIntervalSeconds = 5;
        private string _bundledUnlockSoundPath = string.Empty;
        private string _customUnlockSoundPath = string.Empty;
        private LocalSteamSchemaPreference _steamSchemaPreference = LocalSteamSchemaPreference.PreferSteam;
        private LocalImportedGameLibraryTarget _importedGameLibraryTarget = LocalImportedGameLibraryTarget.None;
        private string _importedGameCustomSourceName = string.Empty;
        private string _importedGameMetadataSourceId = string.Empty;
        private LocalExistingGameImportBehavior _existingGameImportBehavior = LocalExistingGameImportBehavior.OverwriteExisting;
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

        public void SetExtraLocalPathEntries(IEnumerable<string> paths)
        {
            ExtraLocalPaths = JoinExtraLocalPaths(paths);
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

        private string GetEffectiveBundledUnlockSoundPath()
        {
            return string.IsNullOrWhiteSpace(BundledUnlockSoundPath)
                ? DefaultBundledUnlockSoundPath
                : BundledUnlockSoundPath;
        }
    }
}
