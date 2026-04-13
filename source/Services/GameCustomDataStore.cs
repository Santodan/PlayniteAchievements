using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace PlayniteAchievements.Services
{
    public sealed class PortableGameCustomDataExportResult
    {
        public string DestinationPath { get; set; }

        public int OmittedLocalIconOverrideCount { get; set; }

        public bool HasOmittedLocalIconOverrides => OmittedLocalIconOverrideCount > 0;
    }

    public sealed class PortableGameCustomDataImportResult
    {
        public GameCustomDataFile ImportedData { get; set; }

        public int IgnoredPackageImageCount { get; set; }

        public bool HasIgnoredPackageImages => IgnoredPackageImageCount > 0;
    }

    public sealed class GameCustomDataChangedEventArgs : EventArgs
    {
        public GameCustomDataChangedEventArgs(Guid playniteGameId)
        {
            PlayniteGameId = playniteGameId;
        }

        public Guid PlayniteGameId { get; }
    }

    /// <summary>
    /// Orchestrates per-game custom data persistence and migration.
    /// </summary>
    public sealed class GameCustomDataStore
    {
        private const string DatabaseFileName = "game_custom_data.db";
        public const string PortableFileExtension = ".pa";
        public const string PortablePackageFileExtension = ".pa.zip";
        public const string PortablePackageManifestEntryName = "custom-data.pa";
        private const string PortablePackageImagesFolderName = "images";

        private readonly ILogger _logger;
        private readonly JsonSerializerSettings _writeSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore
        };
        private readonly object _cacheSync = new object();

        private readonly GameCustomDataLegacyMigration _legacyMigration = new GameCustomDataLegacyMigration();
        private readonly GameCustomDataRepository _repository;
        private ManagedCustomIconService _managedCustomIconService;
        private AchievementDataService _achievementDataService;
        private Dictionary<Guid, GameCustomDataFile> _cacheByGameId;
        private HashSet<Guid> _missingGameIds;

        public event EventHandler<GameCustomDataChangedEventArgs> CustomDataChanged;

        public GameCustomDataStore(string pluginUserDataPath, ILogger logger = null)
        {
            _logger = logger;
            var databasePath = Path.Combine(pluginUserDataPath ?? string.Empty, DatabaseFileName);
            _repository = new GameCustomDataRepository(databasePath, _writeSettings, logger);
        }

        public string DatabasePath => _repository.DatabasePath;

        public void AttachManagedCustomIconService(ManagedCustomIconService managedCustomIconService)
        {
            _managedCustomIconService = managedCustomIconService;
        }

        public void AttachAchievementDataService(AchievementDataService achievementDataService)
        {
            _achievementDataService = achievementDataService;
        }

        public void AttachRuntimeSettings(PlayniteAchievementsSettings settings)
        {
            _ = settings;
        }

        public bool TryLoad(Guid playniteGameId, out GameCustomDataFile data)
        {
            data = null;
            if (playniteGameId == Guid.Empty)
            {
                return false;
            }

            lock (_cacheSync)
            {
                if (_cacheByGameId != null)
                {
                    if (_cacheByGameId.TryGetValue(playniteGameId, out var cached))
                    {
                        data = cached?.Clone();
                        return data != null;
                    }

                    if (_missingGameIds != null && _missingGameIds.Contains(playniteGameId))
                    {
                        return false;
                    }
                }
            }

            var found = _repository.TryLoad(playniteGameId, out var loaded);
            lock (_cacheSync)
            {
                EnsureCacheCollections();
                if (found && loaded != null)
                {
                    _cacheByGameId[playniteGameId] = loaded.Clone();
                    _missingGameIds.Remove(playniteGameId);
                    data = loaded.Clone();
                    return true;
                }

                _cacheByGameId.Remove(playniteGameId);
                _missingGameIds.Add(playniteGameId);
                return false;
            }
        }

        public GameCustomDataFile LoadOrDefault(Guid playniteGameId)
        {
            return _repository.LoadOrDefault(playniteGameId);
        }

        public void Update(Guid playniteGameId, Action<GameCustomDataFile> mutate)
        {
            if (playniteGameId == Guid.Empty)
            {
                throw new ArgumentException("Game ID is required.", nameof(playniteGameId));
            }

            if (mutate == null)
            {
                throw new ArgumentNullException(nameof(mutate));
            }

            var data = _repository.LoadOrDefault(playniteGameId);
            mutate(data);
            Save(playniteGameId, data);
        }

        public void Save(Guid playniteGameId, GameCustomDataFile data)
        {
            var normalized = GameCustomDataNormalizer.NormalizeInternal(data, playniteGameId);
            _repository.Save(playniteGameId, normalized);
            RefreshCachedEntry(playniteGameId);
            SyncManagedCustomIconCache(playniteGameId, normalized);
            RaiseCustomDataChanged(playniteGameId);
        }

        public void Delete(Guid playniteGameId)
        {
            _repository.Delete(playniteGameId);
            lock (_cacheSync)
            {
                EnsureCacheCollections();
                _cacheByGameId.Remove(playniteGameId);
                _missingGameIds.Add(playniteGameId);
            }

            _managedCustomIconService?.ClearGameCustomCache(playniteGameId.ToString("D"));
            RaiseCustomDataChanged(playniteGameId);
        }

        public IReadOnlyList<GameCustomDataFile> LoadAll()
        {
            lock (_cacheSync)
            {
                if (_cacheByGameId != null && _missingGameIds != null)
                {
                    return _cacheByGameId.Values
                        .Select(data => data?.Clone())
                        .Where(data => data != null)
                        .ToList();
                }
            }

            var rows = _repository.EnumerateAllNormalized().ToList();
            lock (_cacheSync)
            {
                _cacheByGameId = rows
                    .Where(data => data?.PlayniteGameId != Guid.Empty)
                    .ToDictionary(
                        data => data.PlayniteGameId,
                        data => data.Clone());
                _missingGameIds = new HashSet<Guid>();
                return _cacheByGameId.Values
                    .Select(data => data?.Clone())
                    .Where(data => data != null)
                    .ToList();
            }
        }

        public HashSet<Guid> GetExcludedRefreshGameIds(ISet<Guid> fallbackIds = null)
        {
            return GetExcludedGameIds(fallbackIds, data => data?.ExcludedFromRefreshes == true);
        }

        public HashSet<Guid> GetExcludedSummaryGameIds(ISet<Guid> fallbackIds = null)
        {
            return GetExcludedGameIds(fallbackIds, data => data?.ExcludedFromSummaries == true);
        }

        public void Export(Guid playniteGameId, string destinationPath)
        {
            if (playniteGameId == Guid.Empty)
            {
                throw new ArgumentException("Game ID is required.", nameof(playniteGameId));
            }

            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                throw new ArgumentException("Destination path is required.", nameof(destinationPath));
            }

            var internalData = _repository.LoadOrDefault(playniteGameId);
            var normalized = GameCustomDataNormalizer.NormalizeInternal(internalData, playniteGameId);
            if (!GameCustomDataNormalizer.HasPortableData(normalized))
            {
                throw new InvalidOperationException("No exportable custom data exists for this game.");
            }

            var portable = GameCustomDataNormalizer.NormalizePortable(normalized.ToPortable(), playniteGameId);
            if (!GameCustomDataNormalizer.HasPortableData(portable))
            {
                throw new InvalidOperationException("No exportable custom data exists for this game.");
            }

            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonConvert.SerializeObject(portable, _writeSettings);
            File.WriteAllText(destinationPath, json);
        }

        public PortableGameCustomDataExportResult ExportPortablePa(Guid playniteGameId, string destinationPath)
        {
            EnsurePortableFileExtension(destinationPath);

            var portable = LoadNormalizedPortableOrThrow(playniteGameId);
            var filteredPortable = portable.Clone();
            var omittedLocalOverrides = FilterToPortablePa(filteredPortable);
            if (!GameCustomDataNormalizer.HasPortableData(filteredPortable))
            {
                throw new InvalidOperationException("No .PA-compatible custom data exists for this game. Use .PA.ZIP to export bundled images.");
            }

            EnsureDestinationDirectory(destinationPath);
            var json = JsonConvert.SerializeObject(filteredPortable, _writeSettings);
            File.WriteAllText(destinationPath, json);

            return new PortableGameCustomDataExportResult
            {
                DestinationPath = destinationPath,
                OmittedLocalIconOverrideCount = omittedLocalOverrides
            };
        }

        public void ExportPortablePackage(Guid playniteGameId, string destinationPath)
        {
            EnsurePortablePackageExtension(destinationPath);

            var portable = LoadNormalizedPortableOrThrow(playniteGameId);
            var fileStems = AchievementIconCachePathBuilder.BuildFileStems(
                EnumeratePortableIconApiNames(portable));
            var imageSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            RewritePortableIconsForPackage(
                playniteGameId,
                portable.AchievementUnlockedIconOverrides,
                fileStems,
                AchievementIconVariant.Unlocked,
                imageSources);
            RewritePortableIconsForPackage(
                playniteGameId,
                portable.AchievementLockedIconOverrides,
                fileStems,
                AchievementIconVariant.Locked,
                imageSources);

            EnsureDestinationDirectory(destinationPath);
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            using (var archive = ZipFile.Open(destinationPath, ZipArchiveMode.Create))
            {
                var manifestEntry = archive.CreateEntry(PortablePackageManifestEntryName, CompressionLevel.Optimal);
                using (var writer = new StreamWriter(manifestEntry.Open()))
                {
                    writer.Write(JsonConvert.SerializeObject(portable, _writeSettings));
                }

                foreach (var pair in imageSources.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(pair.Value) || !File.Exists(pair.Value))
                    {
                        throw new InvalidOperationException($"Missing bundled icon file: {pair.Value ?? pair.Key}");
                    }

                    var imageEntry = archive.CreateEntry(pair.Key, CompressionLevel.Optimal);
                    using (var source = File.OpenRead(pair.Value))
                    using (var destination = imageEntry.Open())
                    {
                        source.CopyTo(destination);
                    }
                }
            }
        }

        public PortableGameCustomDataImportResult ImportReplacePortable(Guid playniteGameId, string sourcePath)
        {
            if (playniteGameId == Guid.Empty)
            {
                throw new ArgumentException("Game ID is required.", nameof(playniteGameId));
            }

            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException("Source path is required.", nameof(sourcePath));
            }

            if (IsPortablePackagePath(sourcePath))
            {
                return ImportReplacePortablePackage(playniteGameId, sourcePath);
            }

            if (!IsPortableFilePath(sourcePath))
            {
                throw new InvalidOperationException("Only .PA and .PA.ZIP files are supported.");
            }

            var portable = JsonConvert.DeserializeObject<GameCustomDataPortableFile>(File.ReadAllText(sourcePath));
            RejectLocalIconOverridesInPortableFile(portable);
            return new PortableGameCustomDataImportResult
            {
                ImportedData = ImportPortableReplace(
                    playniteGameId,
                    portable,
                    invalidDataMessage: "Imported .PA does not contain any portable custom data.")
            };
        }

        public GameCustomDataFile ImportReplace(Guid playniteGameId, string sourcePath)
        {
            if (playniteGameId == Guid.Empty)
            {
                throw new ArgumentException("Game ID is required.", nameof(playniteGameId));
            }

            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException("Source path is required.", nameof(sourcePath));
            }

            var json = File.ReadAllText(sourcePath);
            var portable = JsonConvert.DeserializeObject<GameCustomDataPortableFile>(json);
            return ImportPortableReplace(playniteGameId, portable, "Imported JSON does not contain any portable custom data.");
        }

        public string MigrateLegacyConfig(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return rawJson;
            }

            try
            {
                var migration = _legacyMigration.Parse(rawJson);
                var rowsToSave = new System.Collections.Generic.List<GameCustomDataFile>();
                foreach (var pair in migration.LegacyByGame)
                {
                    var legacy = GameCustomDataNormalizer.NormalizeInternal(pair.Value, pair.Key);
                    var existing = _repository.TryLoad(pair.Key, out var existingData) ? existingData : null;
                    var merged = GameCustomDataNormalizer.MergePreferExisting(existing, legacy);
                    if (GameCustomDataNormalizer.HasInternalData(merged))
                    {
                        rowsToSave.Add(merged);
                    }
                }

                _repository.SaveMany(rowsToSave);
                InvalidateCache();
                return migration.CleanedJson;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed migrating legacy per-game custom data. Using original settings JSON.");
                return rawJson;
            }
        }

        public bool HasPortableData(Guid playniteGameId)
        {
            return GameCustomDataNormalizer.HasPortableData(_repository.LoadOrDefault(playniteGameId));
        }

        public void SyncRuntimeCaches()
        {
            // Per-game custom data is read directly from the database at runtime.
        }

        private GameCustomDataPortableFile LoadNormalizedPortableOrThrow(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty)
            {
                throw new ArgumentException("Game ID is required.", nameof(playniteGameId));
            }

            var internalData = _repository.LoadOrDefault(playniteGameId);
            var normalized = GameCustomDataNormalizer.NormalizeInternal(internalData, playniteGameId);
            if (!GameCustomDataNormalizer.HasPortableData(normalized))
            {
                throw new InvalidOperationException("No exportable custom data exists for this game.");
            }

            var portable = GameCustomDataNormalizer.NormalizePortable(normalized.ToPortable(), playniteGameId);
            if (!GameCustomDataNormalizer.HasPortableData(portable))
            {
                throw new InvalidOperationException("No exportable custom data exists for this game.");
            }

            return portable;
        }

        private PortableGameCustomDataImportResult ImportReplacePortablePackage(Guid playniteGameId, string sourcePath)
        {
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("Package file not found.", sourcePath);
            }

            using (var archive = ZipFile.OpenRead(sourcePath))
            {
                var entriesByName = archive.Entries
                    .Select(entry => new { Entry = entry, Name = NormalizeArchiveEntryName(entry.FullName) })
                    .Where(item => item.Entry != null &&
                                   !string.IsNullOrWhiteSpace(item.Entry.Name) &&
                                   !string.IsNullOrWhiteSpace(item.Name))
                    .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().Entry, StringComparer.OrdinalIgnoreCase);

                if (entriesByName.TryGetValue(PortablePackageManifestEntryName, out var manifestEntry))
                {
                    GameCustomDataPortableFile portable;
                    using (var reader = new StreamReader(manifestEntry.Open()))
                    {
                        portable = JsonConvert.DeserializeObject<GameCustomDataPortableFile>(reader.ReadToEnd());
                    }

                    RewritePackageImageOverrides(playniteGameId, entriesByName, portable?.AchievementUnlockedIconOverrides, AchievementIconVariant.Unlocked);
                    RewritePackageImageOverrides(playniteGameId, entriesByName, portable?.AchievementLockedIconOverrides, AchievementIconVariant.Locked);

                    return new PortableGameCustomDataImportResult
                    {
                        ImportedData = ImportPortableReplace(
                            playniteGameId,
                            portable,
                            "Imported .PA.ZIP does not contain any portable custom data.")
                    };
                }

                return ImportReplacePortableImageOnlyPackage(playniteGameId, entriesByName);
            }
        }

        private PortableGameCustomDataImportResult ImportReplacePortableImageOnlyPackage(
            Guid playniteGameId,
            IReadOnlyDictionary<string, ZipArchiveEntry> entriesByName)
        {
            var achievementApiNames = LoadAchievementApiNamesForImageOnlyPackageOrThrow(playniteGameId);
            var achievementApiNameSet = new HashSet<string>(achievementApiNames, StringComparer.OrdinalIgnoreCase);
            var fileStems = AchievementIconCachePathBuilder.BuildFileStems(achievementApiNames);
            var portable = new GameCustomDataPortableFile();
            var ignoredPackageImages = 0;

            foreach (var pair in entriesByName.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!TryParseImageOnlyPackageEntry(pair.Key, out var apiName, out var variant))
                {
                    continue;
                }

                if (!achievementApiNameSet.Contains(apiName))
                {
                    ignoredPackageImages++;
                    continue;
                }

                if (!fileStems.TryGetValue(apiName, out var fileStem) || string.IsNullOrWhiteSpace(fileStem))
                {
                    ignoredPackageImages++;
                    continue;
                }

                var managedPath = ImportPackageImageToManagedPath(
                    playniteGameId,
                    pair.Value,
                    fileStem,
                    variant);
                if (variant == AchievementIconVariant.Locked)
                {
                    if (portable.AchievementLockedIconOverrides == null)
                    {
                        portable.AchievementLockedIconOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }

                    portable.AchievementLockedIconOverrides[apiName] = managedPath;
                }
                else
                {
                    if (portable.AchievementUnlockedIconOverrides == null)
                    {
                        portable.AchievementUnlockedIconOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }

                    portable.AchievementUnlockedIconOverrides[apiName] = managedPath;
                }
            }

            if (!GameCustomDataNormalizer.HasPortableData(portable))
            {
                throw new InvalidOperationException("Image-only .PA.ZIP did not contain any images matching this game's achievement API names.");
            }

            return new PortableGameCustomDataImportResult
            {
                ImportedData = ImportPortableReplace(
                    playniteGameId,
                    portable,
                    "Imported .PA.ZIP does not contain any portable custom data."),
                IgnoredPackageImageCount = ignoredPackageImages
            };
        }

        private void RewritePortableIconsForPackage(
            Guid playniteGameId,
            Dictionary<string, string> overrides,
            IReadOnlyDictionary<string, string> fileStems,
            AchievementIconVariant variant,
            IDictionary<string, string> imageSources)
        {
            if (overrides == null || overrides.Count == 0)
            {
                return;
            }

            foreach (var pair in overrides.ToList())
            {
                var apiName = NormalizeText(pair.Key);
                var overrideValue = NormalizeText(pair.Value);
                if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(overrideValue))
                {
                    overrides.Remove(pair.Key);
                    continue;
                }

                if (!fileStems.TryGetValue(apiName, out var fileStem) || string.IsNullOrWhiteSpace(fileStem))
                {
                    throw new InvalidOperationException($"Could not determine a bundled icon name for '{apiName}'.");
                }

                var bundledSource = ResolveBundledIconSourcePath(playniteGameId, overrideValue, fileStem, variant);
                var relativeEntryName = BuildPackageImageEntryName(fileStem, variant);
                overrides[apiName] = relativeEntryName;
                imageSources[relativeEntryName] = bundledSource;
            }
        }

        private void RewritePackageImageOverrides(
            Guid playniteGameId,
            IReadOnlyDictionary<string, ZipArchiveEntry> entriesByName,
            Dictionary<string, string> overrides,
            AchievementIconVariant variant)
        {
            if (overrides == null || overrides.Count == 0)
            {
                return;
            }

            var managedIcons = GetManagedCustomIconServiceOrThrow();
            var fileStems = AchievementIconCachePathBuilder.BuildFileStems(overrides.Keys);
            var gameIdText = playniteGameId.ToString("D");

            foreach (var pair in overrides.ToList())
            {
                var apiName = NormalizeText(pair.Key);
                var overrideValue = NormalizeText(pair.Value);
                if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(overrideValue))
                {
                    overrides.Remove(pair.Key);
                    continue;
                }

                if (IsHttpUrl(overrideValue))
                {
                    overrides[apiName] = overrideValue;
                    continue;
                }

                var normalizedEntryName = NormalizePackageImagePathOrThrow(overrideValue);
                if (!entriesByName.TryGetValue(normalizedEntryName, out var imageEntry))
                {
                    throw new InvalidOperationException($"Package is missing bundled icon entry '{overrideValue}'.");
                }

                if (!fileStems.TryGetValue(apiName, out var fileStem) || string.IsNullOrWhiteSpace(fileStem))
                {
                    throw new InvalidOperationException($"Could not determine a managed custom icon path for '{apiName}'.");
                }

                var targetPath = managedIcons.GetAchievementCustomIconPath(gameIdText, fileStem, variant);
                var targetDirectory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                using (var source = imageEntry.Open())
                using (var destination = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    source.CopyTo(destination);
                }

                overrides[apiName] = targetPath;
            }
        }

        private string ResolveBundledIconSourcePath(
            Guid playniteGameId,
            string overrideValue,
            string fileStem,
            AchievementIconVariant variant)
        {
            var normalizedValue = NormalizeText(overrideValue);
            if (string.IsNullOrWhiteSpace(normalizedValue))
            {
                throw new InvalidOperationException("Cannot bundle an empty custom icon override.");
            }

            var managedIcons = GetManagedCustomIconServiceOrThrow();
            var gameIdText = playniteGameId.ToString("D");
            if (managedIcons.IsManagedCustomIconPath(normalizedValue, gameIdText) && File.Exists(normalizedValue))
            {
                return normalizedValue;
            }

            var bundledSource = managedIcons
                .MaterializeCustomIconAsync(
                    normalizedValue,
                    gameIdText,
                    fileStem,
                    variant,
                    CancellationToken.None,
                    overwriteExistingTarget: false)
                .GetAwaiter()
                .GetResult();

            if (string.IsNullOrWhiteSpace(bundledSource) || !File.Exists(bundledSource))
            {
                throw new InvalidOperationException($"Failed to bundle custom icon override '{normalizedValue}'.");
            }

            return bundledSource;
        }

        private IReadOnlyList<string> LoadAchievementApiNamesForImageOnlyPackageOrThrow(Guid playniteGameId)
        {
            if (_achievementDataService == null)
            {
                throw new InvalidOperationException("Achievement data service is not available.");
            }

            var apiNames = _achievementDataService
                .GetGameAchievementData(playniteGameId)?
                .Achievements?
                .Where(achievement => achievement != null && !string.IsNullOrWhiteSpace(achievement.ApiName))
                .Select(achievement => achievement.ApiName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (apiNames == null || apiNames.Count == 0)
            {
                throw new InvalidOperationException("Image-only .PA.ZIP imports require cached achievements for the target game.");
            }

            return apiNames;
        }

        private string ImportPackageImageToManagedPath(
            Guid playniteGameId,
            ZipArchiveEntry imageEntry,
            string fileStem,
            AchievementIconVariant variant)
        {
            if (imageEntry == null)
            {
                throw new InvalidOperationException("Package image entry is missing.");
            }

            var managedIcons = GetManagedCustomIconServiceOrThrow();
            var extension = Path.GetExtension(imageEntry.Name);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".png";
            }

            var tempDirectory = Path.Combine(Path.GetTempPath(), "PlayniteAchievements", "PortableImports");
            Directory.CreateDirectory(tempDirectory);

            var tempPath = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + extension);
            try
            {
                using (var source = imageEntry.Open())
                using (var destination = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    source.CopyTo(destination);
                }

                var managedPath = managedIcons
                    .MaterializeCustomIconAsync(
                        tempPath,
                        playniteGameId.ToString("D"),
                        fileStem,
                        variant,
                        CancellationToken.None,
                        overwriteExistingTarget: true)
                    .GetAwaiter()
                    .GetResult();

                if (string.IsNullOrWhiteSpace(managedPath) || !File.Exists(managedPath))
                {
                    throw new InvalidOperationException($"Failed to import packaged image '{imageEntry.FullName}'.");
                }

                return managedPath;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                }
            }
        }

        private GameCustomDataFile ImportPortableReplace(
            Guid playniteGameId,
            GameCustomDataPortableFile portable,
            string invalidDataMessage)
        {
            var normalizedPortable = GameCustomDataNormalizer.NormalizePortable(portable, playniteGameId);
            if (!GameCustomDataNormalizer.HasPortableData(normalizedPortable))
            {
                throw new InvalidOperationException(invalidDataMessage);
            }

            var current = _repository.LoadOrDefault(playniteGameId);
            var merged = GameCustomDataFile.FromPortable(
                normalizedPortable,
                playniteGameId,
                current.ExcludedFromRefreshes,
                current.ExcludedFromSummaries);

            Save(playniteGameId, merged);
            return LoadOrDefault(playniteGameId);
        }

        private static int FilterToPortablePa(GameCustomDataPortableFile portable)
        {
            var omitted = 0;
            omitted += FilterLocalIconOverrides(portable?.AchievementUnlockedIconOverrides);
            omitted += FilterLocalIconOverrides(portable?.AchievementLockedIconOverrides);
            if (portable?.AchievementUnlockedIconOverrides != null && portable.AchievementUnlockedIconOverrides.Count == 0)
            {
                portable.AchievementUnlockedIconOverrides = null;
            }

            if (portable?.AchievementLockedIconOverrides != null && portable.AchievementLockedIconOverrides.Count == 0)
            {
                portable.AchievementLockedIconOverrides = null;
            }

            return omitted;
        }

        private static int FilterLocalIconOverrides(Dictionary<string, string> overrides)
        {
            if (overrides == null || overrides.Count == 0)
            {
                return 0;
            }

            var omitted = 0;
            foreach (var pair in overrides.ToList())
            {
                if (IsHttpUrl(pair.Value))
                {
                    continue;
                }

                overrides.Remove(pair.Key);
                omitted++;
            }

            return omitted;
        }

        private static void RejectLocalIconOverridesInPortableFile(GameCustomDataPortableFile portable)
        {
            RejectLocalIconOverridesInPortableMap(portable?.AchievementUnlockedIconOverrides);
            RejectLocalIconOverridesInPortableMap(portable?.AchievementLockedIconOverrides);
        }

        private static void RejectLocalIconOverridesInPortableMap(IReadOnlyDictionary<string, string> overrides)
        {
            if (overrides == null)
            {
                return;
            }

            foreach (var pair in overrides)
            {
                var value = NormalizeText(pair.Value);
                if (string.IsNullOrWhiteSpace(value) || IsHttpUrl(value))
                {
                    continue;
                }

                throw new InvalidOperationException("Plain .PA files cannot contain local icon paths. Use .PA.ZIP for bundled images.");
            }
        }

        private void SyncManagedCustomIconCache(Guid playniteGameId, GameCustomDataFile normalizedData)
        {
            if (_managedCustomIconService == null)
            {
                return;
            }

            var gameIdText = playniteGameId.ToString("D");
            if (!GameCustomDataNormalizer.HasInternalData(normalizedData))
            {
                _managedCustomIconService.ClearGameCustomCache(gameIdText);
                return;
            }

            _managedCustomIconService.PruneGameCustomCache(
                gameIdText,
                EnumerateManagedCustomIconPaths(playniteGameId, normalizedData));
        }

        private IEnumerable<string> EnumerateManagedCustomIconPaths(Guid playniteGameId, GameCustomDataFile data)
        {
            if (_managedCustomIconService == null || data == null)
            {
                yield break;
            }

            var gameIdText = playniteGameId.ToString("D");
            var fileStems = AchievementIconCachePathBuilder.BuildFileStems(
                EnumeratePortableIconApiNames(data.ToPortable()));

            foreach (var retainedPath in EnumerateManagedCustomIconPaths(
                gameIdText,
                data.AchievementUnlockedIconOverrides,
                fileStems,
                AchievementIconVariant.Unlocked))
            {
                yield return retainedPath;
            }

            foreach (var retainedPath in EnumerateManagedCustomIconPaths(
                gameIdText,
                data.AchievementLockedIconOverrides,
                fileStems,
                AchievementIconVariant.Locked))
            {
                yield return retainedPath;
            }
        }

        private static IEnumerable<string> EnumerateIconOverrideValues(IReadOnlyDictionary<string, string> overrides)
        {
            return overrides == null
                ? Enumerable.Empty<string>()
                : overrides.Values.Where(value => !string.IsNullOrWhiteSpace(value));
        }

        private IEnumerable<string> EnumerateManagedCustomIconPaths(
            string gameIdText,
            IReadOnlyDictionary<string, string> overrides,
            IReadOnlyDictionary<string, string> fileStems,
            AchievementIconVariant variant)
        {
            if (overrides == null || overrides.Count == 0)
            {
                yield break;
            }

            foreach (var pair in overrides)
            {
                var apiName = NormalizeText(pair.Key);
                var value = NormalizeText(pair.Value);
                if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (_managedCustomIconService.IsManagedCustomIconPath(value, gameIdText))
                {
                    yield return value;
                    continue;
                }

                if (!IsHttpUrl(value))
                {
                    continue;
                }

                if (!fileStems.TryGetValue(apiName, out var fileStem) || string.IsNullOrWhiteSpace(fileStem))
                {
                    continue;
                }

                yield return _managedCustomIconService.GetAchievementCustomIconPath(
                    gameIdText,
                    fileStem,
                    variant);
            }
        }

        private static IEnumerable<string> EnumeratePortableIconApiNames(GameCustomDataPortableFile portable)
        {
            return (portable?.AchievementUnlockedIconOverrides?.Keys ?? Enumerable.Empty<string>())
                .Concat(portable?.AchievementLockedIconOverrides?.Keys ?? Enumerable.Empty<string>())
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private ManagedCustomIconService GetManagedCustomIconServiceOrThrow()
        {
            if (_managedCustomIconService != null)
            {
                return _managedCustomIconService;
            }

            throw new InvalidOperationException("Managed custom icon service is not available.");
        }

        private static string BuildPackageImageEntryName(string fileStem, AchievementIconVariant variant)
        {
            var fileName = variant == AchievementIconVariant.Locked
                ? fileStem + ".locked.png"
                : fileStem + ".png";
            return PortablePackageImagesFolderName + "/" + fileName;
        }

        private static bool TryParseImageOnlyPackageEntry(
            string normalizedEntryName,
            out string apiName,
            out AchievementIconVariant variant)
        {
            apiName = null;
            variant = AchievementIconVariant.Unlocked;

            var normalized = NormalizeArchiveEntryName(normalizedEntryName);
            if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains(".."))
            {
                return false;
            }

            var fileName = normalized;
            var slashIndex = fileName.LastIndexOf('/');
            if (slashIndex >= 0)
            {
                fileName = fileName.Substring(slashIndex + 1);
            }

            if (string.IsNullOrWhiteSpace(fileName) || !IsSupportedPackageImageExtension(Path.GetExtension(fileName)))
            {
                return false;
            }

            var stem = NormalizeText(Path.GetFileNameWithoutExtension(fileName));
            if (string.IsNullOrWhiteSpace(stem))
            {
                return false;
            }

            if (stem.EndsWith(".locked", StringComparison.OrdinalIgnoreCase))
            {
                stem = NormalizeText(stem.Substring(0, stem.Length - ".locked".Length));
                variant = AchievementIconVariant.Locked;
            }

            apiName = string.IsNullOrWhiteSpace(stem) ? null : stem;
            return !string.IsNullOrWhiteSpace(apiName);
        }

        private static void EnsurePortableFileExtension(string path)
        {
            if (!IsPortableFilePath(path))
            {
                throw new InvalidOperationException("Destination path must end with .pa.");
            }
        }

        private static void EnsurePortablePackageExtension(string path)
        {
            if (!IsPortablePackagePath(path))
            {
                throw new InvalidOperationException("Destination path must end with .pa.zip.");
            }
        }

        private static void EnsureDestinationDirectory(string destinationPath)
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private static bool IsPortableFilePath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   path.EndsWith(PortableFileExtension, StringComparison.OrdinalIgnoreCase) &&
                   !path.EndsWith(PortablePackageFileExtension, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPortablePackagePath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   path.EndsWith(PortablePackageFileExtension, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeArchiveEntryName(string value)
        {
            var normalized = NormalizeText(value)?.Replace('\\', '/').TrimStart('/');
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static string NormalizePackageImagePathOrThrow(string value)
        {
            var normalized = NormalizeArchiveEntryName(value);
            if (string.IsNullOrWhiteSpace(normalized) ||
                normalized.Contains("..") ||
                !normalized.StartsWith(PortablePackageImagesFolderName + "/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Invalid bundled icon path '{value}'.");
            }

            var fileName = normalized.Substring((PortablePackageImagesFolderName + "/").Length);
            if (string.IsNullOrWhiteSpace(fileName) ||
                fileName.Contains("/") ||
                fileName.Contains("\\"))
            {
                throw new InvalidOperationException($"Invalid bundled icon path '{value}'.");
            }

            return normalized;
        }

        private static bool IsSupportedPackageImageExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return false;
            }

            switch (extension.Trim().ToLowerInvariant())
            {
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".bmp":
                case ".gif":
                case ".tif":
                case ".tiff":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsHttpUrl(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeText(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private void RefreshCachedEntry(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty)
            {
                return;
            }

            var found = _repository.TryLoad(playniteGameId, out var loaded);
            lock (_cacheSync)
            {
                EnsureCacheCollections();
                if (found && loaded != null)
                {
                    _cacheByGameId[playniteGameId] = loaded.Clone();
                    _missingGameIds.Remove(playniteGameId);
                    return;
                }

                _cacheByGameId.Remove(playniteGameId);
                _missingGameIds.Add(playniteGameId);
            }
        }

        private void InvalidateCache()
        {
            lock (_cacheSync)
            {
                _cacheByGameId = null;
                _missingGameIds = null;
            }
        }

        private void EnsureCacheCollections()
        {
            if (_cacheByGameId == null)
            {
                _cacheByGameId = new Dictionary<Guid, GameCustomDataFile>();
            }

            if (_missingGameIds == null)
            {
                _missingGameIds = new HashSet<Guid>();
            }
        }

        private HashSet<Guid> GetExcludedGameIds(
            ISet<Guid> fallbackIds,
            Func<GameCustomDataFile, bool> selector)
        {
            if (selector == null)
            {
                throw new ArgumentNullException(nameof(selector));
            }

            EnsureCacheLoaded();

            lock (_cacheSync)
            {
                var result = fallbackIds != null
                    ? new HashSet<Guid>(fallbackIds)
                    : new HashSet<Guid>();

                foreach (var pair in _cacheByGameId)
                {
                    if (selector(pair.Value))
                    {
                        result.Add(pair.Key);
                    }
                    else
                    {
                        result.Remove(pair.Key);
                    }
                }

                return result;
            }
        }

        private void EnsureCacheLoaded()
        {
            lock (_cacheSync)
            {
                if (_cacheByGameId != null && _missingGameIds != null)
                {
                    return;
                }
            }

            _ = LoadAll();
        }

        private void RaiseCustomDataChanged(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty)
            {
                return;
            }

            CustomDataChanged?.Invoke(this, new GameCustomDataChangedEventArgs(playniteGameId));
        }
    }
}
