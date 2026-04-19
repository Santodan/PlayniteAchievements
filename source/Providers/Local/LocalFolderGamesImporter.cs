using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.Steam;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace PlayniteAchievements.Providers.Local
{
    internal sealed class LocalFolderGamesImporter
    {
        private const string UniversalSteamMetadataPluginName = "Universal Steam Metadata";
        private const string UniversalSteamMetadataManifestId = "Universal_Steam_Metadata";

        private static readonly string[] SupportedAchievementRelativeDirectories =
        {
            string.Empty,
            "Stats",
            "steam_settings",
            @"steam_settings\settings",
            @"steam_settings\stats",
            "achievement"
        };

        internal sealed class LocalImportProgressInfo
        {
            public double Percent { get; set; }
            public string Message { get; set; }
            public string Detail { get; set; }
        }

        internal sealed class ImportResult
        {
            public int RootCount { get; set; }
            public int CandidateFolderCount { get; set; }
            public int UniqueAppIdCount { get; set; }
            public int ImportedCount { get; set; }
            public int LinkedExistingCount { get; set; }
            public int SkippedCount { get; set; }
            public int FailedCount { get; set; }
        }

        private sealed class ImportCandidate
        {
            public int AppId { get; set; }
            public string FolderPath { get; set; }
            public bool HasAchievementIni { get; set; }
            public bool HasAchievementJson { get; set; }
            public bool HasSteamAppCacheSchema { get; set; }
            public bool HasSteamAppCacheUserStats { get; set; }
            public bool HasSteamLibraryCache { get; set; }
            public DateTime LastWriteUtc { get; set; }
        }

        private sealed class MetadataPluginIdentity
        {
            public MetadataPlugin Plugin { get; set; }
            public string RuntimeName { get; set; }
            public string ManifestId { get; set; }
            public string ManifestName { get; set; }
            public string AssemblyDirectory { get; set; }
        }

        private readonly IPlayniteAPI _api;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private readonly LocalSettings _localSettings;
        private readonly Dictionary<int, SteamAppImportabilityInfo> _steamAppImportabilityCache = new Dictionary<int, SteamAppImportabilityInfo>();
        private string _steamAppCacheUserIdOverride;

        private sealed class SteamAppImportabilityInfo
        {
            public bool IsImportable { get; set; }
            public string Name { get; set; }
            public string Type { get; set; }
        }

        public LocalFolderGamesImporter(
            IPlayniteAPI api,
            PlayniteAchievementsSettings settings,
            ILogger logger)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger;
            _localSettings = ProviderRegistry.Settings<LocalSettings>();
        }

        public async Task<ImportResult> ImportFromRootsAsync(
            IEnumerable<string> roots,
            LocalImportedGameLibraryTarget? importTargetOverride = null,
            string customSourceNameOverride = null,
            string metadataSourceIdOverride = null,
            LocalExistingGameImportBehavior? existingGameBehaviorOverride = null,
            CancellationToken cancellationToken = default(CancellationToken),
            IProgress<LocalImportProgressInfo> progress = null,
            string steamAppCacheUserIdOverride = null)
        {
            _steamAppCacheUserIdOverride = steamAppCacheUserIdOverride;
            var normalizedRoots = GetImportRoots(roots)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Environment.ExpandEnvironmentVariables(path.Trim()))
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var result = new ImportResult
            {
                RootCount = normalizedRoots.Count
            };

            if (normalizedRoots.Count == 0)
            {
                return result;
            }

            ReportProgress(progress, 0d, "Scanning Local roots...", "Searching for achievement evidence in default and configured Local folders.");

            var candidates = await Task.Run(() => DiscoverCandidates(normalizedRoots, cancellationToken, progress), cancellationToken).ConfigureAwait(true);
            result.CandidateFolderCount = candidates.Sum(pair => pair.Value.Count(candidate => !string.IsNullOrWhiteSpace(candidate.FolderPath)));
            result.UniqueAppIdCount = candidates.Count;

            var importTarget = importTargetOverride ?? (_localSettings?.ImportedGameLibraryTarget ?? LocalImportedGameLibraryTarget.None);
            var customSourceName = (customSourceNameOverride ?? _localSettings?.ImportedGameCustomSourceName ?? string.Empty).Trim();
            var metadataSourceId = (metadataSourceIdOverride ?? _localSettings?.ImportedGameMetadataSourceId ?? string.Empty).Trim();
            var existingGameBehavior = existingGameBehaviorOverride ?? (_localSettings?.ExistingGameImportBehavior ?? LocalExistingGameImportBehavior.OverwriteExisting);
            var selectedMetadataPlugin = ResolveMetadataPlugin(metadataSourceId) ?? ResolveAutomaticMetadataPlugin();
            _logger?.Info($"[LocalImport] Selected metadata provider id='{metadataSourceId}', resolved='{selectedMetadataPlugin?.Name ?? "<none>"}'.");
            var orderedCandidates = candidates.OrderBy(pair => pair.Key).ToList();
            var totalCandidates = orderedCandidates.Count;
            var processedCandidates = 0;
            var settingsDirty = false;

            _api.Database.Games.BeginBufferUpdate();
            try
            {
                foreach (var candidate in orderedCandidates)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var appId = candidate.Key;
                    var folderPath = ChooseBestLocalFolderCandidate(candidate.Value);
                    var chosenCandidate = ChooseBestImportCandidate(candidate.Value);
                    if (chosenCandidate == null)
                    {
                        result.SkippedCount++;
                        continue;
                    }

                    try
                    {
                        processedCandidates++;
                        var gameLabel = string.IsNullOrWhiteSpace(folderPath)
                            ? $"App {appId}"
                            : Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar));
                        ReportProgress(
                            progress,
                            40d + (processedCandidates / (double)Math.Max(1, totalCandidates) * 60d),
                            $"Importing {gameLabel} ({processedCandidates}/{Math.Max(1, totalCandidates)})...",
                            BuildCandidateDetail(chosenCandidate, processedCandidates, totalCandidates));

                        if (candidate.Value.Count > 1)
                        {
                            _logger?.Info($"[LocalImport] appId={appId} has {candidate.Value.Count} candidate folders/files. Using {DescribeCandidate(chosenCandidate)}. All candidates: {string.Join(" | ", candidate.Value.Select(DescribeCandidate))}");
                        }

                        if (!ShouldImportCandidate(appId, chosenCandidate, out var skipReason))
                        {
                            result.SkippedCount++;
                            _logger?.Info($"[LocalImport] Skipping appId={appId}: {skipReason}");
                            continue;
                        }

                        var game = FindExistingGame(appId, importTarget, customSourceName);
                        GameMetadata pendingMetadata = null;
                        if (game == null)
                        {
                            pendingMetadata = BuildMetadata(appId, importTarget, customSourceName, selectedMetadataPlugin);
                            game = FindExistingGameByName(pendingMetadata?.Name, importTarget, customSourceName);
                            if (game != null)
                            {
                                _logger?.Info($"[LocalImport] Reusing existing game by title match for appId={appId}: {DescribeGame(game)}.");
                            }
                        }

                        if (game == null)
                        {
                            game = ImportGame(appId, importTarget, customSourceName, selectedMetadataPlugin, pendingMetadata);
                            if (game == null)
                            {
                                result.FailedCount++;
                                continue;
                            }

                            ApplyImportTarget(game, importTarget, customSourceName);
                            ApplyDownloadedMetadata(game, appId, selectedMetadataPlugin);
                            _logger?.Info($"[LocalImport] Imported new game for appId={appId}: {DescribeGame(game)}.");
                            result.ImportedCount++;
                        }
                        else
                        {
                            if (existingGameBehavior == LocalExistingGameImportBehavior.SkipExisting)
                            {
                                result.SkippedCount++;
                                _logger?.Info($"[LocalImport] Skipping appId={appId} because a matching existing game was found and existing-game behavior is set to skip.");
                                continue;
                            }

                            ApplyImportTarget(game, importTarget, customSourceName);
                            ApplyDownloadedMetadata(game, appId, selectedMetadataPlugin);
                            _logger?.Info($"[LocalImport] Linked existing game for appId={appId}: {DescribeGame(game)}.");
                            result.LinkedExistingCount++;
                        }

                        game.InstallDirectory = null;
                        _api.Database.Games.Update(game);

                        settingsDirty |= PersistLocalBinding(game, appId, folderPath);

                        if (processedCandidates % 5 == 0)
                        {
                            await Task.Yield();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        result.FailedCount++;
                        _logger?.Warn(ex, $"Failed importing Local folder candidate appId={appId} from '{folderPath}'.");
                    }
                }
            }
            finally
            {
                _api.Database.Games.EndBufferUpdate();

                if (settingsDirty)
                {
                    ProviderRegistry.Write(_localSettings);
                    PersistSettingsForUi();
                }
            }

            return result;
        }

        private static void ReportProgress(
            IProgress<LocalImportProgressInfo> progress,
            double percent,
            string message,
            string detail)
        {
            progress?.Report(new LocalImportProgressInfo
            {
                Percent = percent,
                Message = message,
                Detail = detail
            });
        }

        private IEnumerable<string> GetImportRoots(IEnumerable<string> roots)
        {
            var resolvedRoots = new List<string>();
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var publicFolder = Environment.GetEnvironmentVariable("PUBLIC") ?? string.Empty;
            var includeSteamRoots = !IsSteamAutomaticRootScanningDisabled();

            resolvedRoots.Add(Environment.ExpandEnvironmentVariables(@"%APPDATA%\Goldberg SteamEmu Saves"));
            resolvedRoots.Add(Environment.ExpandEnvironmentVariables(@"%APPDATA%\GSE Saves"));
            resolvedRoots.Add(Environment.ExpandEnvironmentVariables(@"%APPDATA%\EMPRESS"));
            resolvedRoots.Add(Environment.ExpandEnvironmentVariables(@"%APPDATA%\Steam\CODEX"));
            resolvedRoots.Add(Environment.ExpandEnvironmentVariables(@"%APPDATA%\SmartSteamEmu"));
            resolvedRoots.Add(Environment.ExpandEnvironmentVariables(@"%APPDATA%\CreamAPI"));

            if (!string.IsNullOrWhiteSpace(publicFolder))
            {
                resolvedRoots.Add(Path.Combine(publicFolder, "Documents", "OnlineFix"));
                resolvedRoots.Add(Path.Combine(publicFolder, "Documents", "Steam", "RUNE"));
                resolvedRoots.Add(Path.Combine(publicFolder, "Documents", "Steam", "CODEX"));
                resolvedRoots.Add(Path.Combine(publicFolder, "EMPRESS"));
            }

            if (!string.IsNullOrWhiteSpace(documents))
            {
                resolvedRoots.Add(Path.Combine(documents, "SkidRow"));
            }

            if (includeSteamRoots && !string.IsNullOrWhiteSpace(commonAppData))
            {
                resolvedRoots.Add(Path.Combine(commonAppData, "Steam"));
            }

            if (includeSteamRoots)
            {
                resolvedRoots.Add(Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Steam"));
                resolvedRoots.Add(Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Steam"));
            }

            var steamPath = Environment.GetEnvironmentVariable("SteamPath");
            if (includeSteamRoots && !string.IsNullOrWhiteSpace(steamPath))
            {
                resolvedRoots.Add(steamPath);
                resolvedRoots.Add(Path.Combine(steamPath, "userdata"));
            }

            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                resolvedRoots.Add(Path.Combine(localAppData, "SKIDROW"));
            }

            foreach (var configuredSteamRoot in includeSteamRoots ? GetConfiguredSteamImportRoots() : Enumerable.Empty<string>())
            {
                resolvedRoots.Add(configuredSteamRoot);
            }

            foreach (var configuredPath in LocalSettings.SplitExtraLocalPaths(_settings?.Persisted?.ExtraLocalPaths ?? _localSettings?.ExtraLocalPaths))
            {
                resolvedRoots.Add(configuredPath);
            }

            foreach (var explicitRoot in roots ?? Enumerable.Empty<string>())
            {
                resolvedRoots.Add(explicitRoot);
            }

            return resolvedRoots;
        }

        private bool IsSteamAutomaticRootScanningDisabled()
        {
            var selectedSteamAppCacheUserId = (_steamAppCacheUserIdOverride ?? _localSettings?.SteamAppCacheUserId ?? string.Empty).Trim();
            return string.Equals(selectedSteamAppCacheUserId, LocalSettings.SteamAppCacheUserNone, StringComparison.OrdinalIgnoreCase);
        }

        private IEnumerable<string> GetConfiguredSteamImportRoots()
        {
            var configuredPath = (_localSettings?.SteamUserdataPath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                yield break;
            }

            var expanded = Environment.ExpandEnvironmentVariables(configuredPath);
            if (string.IsNullOrWhiteSpace(expanded))
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in ResolveConfiguredSteamImportRootCandidates(expanded))
            {
                if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
                {
                    yield return path;
                }
            }
        }

        private static IEnumerable<string> ResolveConfiguredSteamImportRootCandidates(string configuredPath)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                yield break;
            }

            yield return configuredPath;

            var trimmedPath = configuredPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var leafName = Path.GetFileName(trimmedPath);

            if (string.Equals(leafName, "userdata", StringComparison.OrdinalIgnoreCase))
            {
                var baseDirectory = Directory.GetParent(trimmedPath)?.FullName;
                if (!string.IsNullOrWhiteSpace(baseDirectory))
                {
                    yield return baseDirectory;
                }

                yield break;
            }

            if (string.Equals(leafName, "stats", StringComparison.OrdinalIgnoreCase))
            {
                var appCacheDirectory = Directory.GetParent(trimmedPath);
                var baseDirectory = appCacheDirectory?.Parent?.FullName;
                if (!string.IsNullOrWhiteSpace(baseDirectory))
                {
                    yield return baseDirectory;
                }

                yield break;
            }

            var nestedUserdata = Path.Combine(trimmedPath, "userdata");
            if (Directory.Exists(nestedUserdata))
            {
                yield return nestedUserdata;
            }

            var nestedStats = Path.Combine(trimmedPath, "appcache", "stats");
            if (Directory.Exists(nestedStats))
            {
                yield return nestedStats;
            }
        }

        private Game ImportGame(
            int appId,
            LocalImportedGameLibraryTarget importTarget,
            string customSourceName,
            MetadataPlugin selectedMetadataPlugin,
            GameMetadata metadata = null)
        {
            metadata ??= BuildMetadata(appId, importTarget, customSourceName, selectedMetadataPlugin);

            if (importTarget == LocalImportedGameLibraryTarget.Steam)
            {
                var steamLibraryPlugin = ResolveSteamLibraryPlugin();
                if (steamLibraryPlugin == null)
                {
                    _logger?.Warn($"Local import for appId={appId} requested the Steam library, but the Steam library plugin is unavailable.");
                    return null;
                }

                return _api.Database.ImportGame(metadata, steamLibraryPlugin);
            }

            return _api.Database.ImportGame(metadata);
        }

        private Dictionary<int, List<ImportCandidate>> DiscoverCandidates(
            IReadOnlyCollection<string> roots,
            CancellationToken cancellationToken,
            IProgress<LocalImportProgressInfo> progress)
        {
            var candidates = new Dictionary<int, List<ImportCandidate>>();
            var rootList = roots?.ToList() ?? new List<string>();
            var processedRoots = 0;

            foreach (var root in rootList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processedRoots++;

                ReportProgress(
                    progress,
                    processedRoots / (double)Math.Max(1, rootList.Count) * 35d,
                    $"Scanning root {processedRoots} of {rootList.Count}...",
                    root);

                try
                {
                    foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var folderName = Path.GetFileName(directory)?.Trim();
                        if (!int.TryParse(folderName, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) || appId <= 0)
                        {
                            continue;
                        }

                        var candidate = TryCreateFolderCandidate(appId, directory);
                        if (candidate == null)
                        {
                            continue;
                        }

                        AddCandidate(candidates, candidate);
                    }

                    if (!IsSteamAutomaticRootScanningDisabled())
                    {
                        foreach (var steamStatsRoot in GetSteamAppCacheStatsRoots(root))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            DiscoverSteamAppCacheCandidates(candidates, steamStatsRoot, cancellationToken);
                        }

                        foreach (var steamUserdataRoot in GetSteamUserdataRoots(root))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            DiscoverSteamLibraryCacheCandidates(candidates, steamUserdataRoot, cancellationToken);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"Failed scanning Local import root '{root}'.");
                }
            }

            return candidates;
        }

        private static void AddCandidate(Dictionary<int, List<ImportCandidate>> candidates, ImportCandidate candidate)
        {
            if (candidate == null || candidate.AppId <= 0)
            {
                return;
            }

            if (!candidates.TryGetValue(candidate.AppId, out var appCandidates))
            {
                appCandidates = new List<ImportCandidate>();
                candidates[candidate.AppId] = appCandidates;
            }

            var existing = appCandidates.FirstOrDefault(item =>
                string.Equals(item.FolderPath, candidate.FolderPath, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                appCandidates.Add(candidate);
                return;
            }

            existing.HasAchievementIni |= candidate.HasAchievementIni;
            existing.HasAchievementJson |= candidate.HasAchievementJson;
            existing.HasSteamAppCacheSchema |= candidate.HasSteamAppCacheSchema;
            existing.HasSteamAppCacheUserStats |= candidate.HasSteamAppCacheUserStats;
            existing.HasSteamLibraryCache |= candidate.HasSteamLibraryCache;
            existing.LastWriteUtc = existing.LastWriteUtc > candidate.LastWriteUtc ? existing.LastWriteUtc : candidate.LastWriteUtc;
        }

        private ImportCandidate TryCreateFolderCandidate(int appId, string directory)
        {
            var iniPath = ResolveAchievementFilePath(directory, "achievements.ini");
            var jsonPath = ResolveAchievementFilePath(directory, "achievements.json");
            if (string.IsNullOrWhiteSpace(iniPath) && string.IsNullOrWhiteSpace(jsonPath))
            {
                return null;
            }

            return new ImportCandidate
            {
                AppId = appId,
                FolderPath = directory,
                HasAchievementIni = !string.IsNullOrWhiteSpace(iniPath),
                HasAchievementJson = !string.IsNullOrWhiteSpace(jsonPath),
                LastWriteUtc = GetLatestAchievementFileWriteTime(directory)
            };
        }

        private void DiscoverSteamAppCacheCandidates(
            Dictionary<int, List<ImportCandidate>> candidates,
            string statsRoot,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(statsRoot) || !Directory.Exists(statsRoot))
            {
                return;
            }

            var selectedSteamAppCacheUserId = (_steamAppCacheUserIdOverride ?? _localSettings?.SteamAppCacheUserId ?? string.Empty).Trim();
            if (string.Equals(selectedSteamAppCacheUserId, LocalSettings.SteamAppCacheUserNone, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var allowedAppIdsFromSelectedUser = new HashSet<int>();

            foreach (var userStatsPath in Directory.EnumerateFiles(statsRoot, "UserGameStats_*_*.bin", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var parts = Path.GetFileNameWithoutExtension(userStatsPath)?.Split('_');
                if (parts == null || parts.Length < 3)
                {
                    continue;
                }

                var userId = parts[1]?.Trim();
                if (!string.IsNullOrWhiteSpace(selectedSteamAppCacheUserId) &&
                    !string.Equals(userId, selectedSteamAppCacheUserId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId) || appId <= 0)
                {
                    continue;
                }

                allowedAppIdsFromSelectedUser.Add(appId);
                AddCandidate(candidates, new ImportCandidate
                {
                    AppId = appId,
                    HasSteamAppCacheUserStats = true,
                    LastWriteUtc = File.GetLastWriteTimeUtc(userStatsPath)
                });
            }

            foreach (var schemaPath in Directory.EnumerateFiles(statsRoot, "UserGameStatsSchema_*.bin", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileName = Path.GetFileNameWithoutExtension(schemaPath);
                var appIdText = fileName?.Replace("UserGameStatsSchema_", string.Empty);
                if (!int.TryParse(appIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId) || appId <= 0)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(selectedSteamAppCacheUserId) && !allowedAppIdsFromSelectedUser.Contains(appId))
                {
                    continue;
                }

                AddCandidate(candidates, new ImportCandidate
                {
                    AppId = appId,
                    HasSteamAppCacheSchema = true,
                    LastWriteUtc = File.GetLastWriteTimeUtc(schemaPath)
                });
            }
        }

        private void DiscoverSteamLibraryCacheCandidates(
            Dictionary<int, List<ImportCandidate>> candidates,
            string userdataRoot,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(userdataRoot) || !Directory.Exists(userdataRoot))
            {
                return;
            }

            foreach (var cacheRoot in EnumerateLibraryCacheRoots(userdataRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!Directory.Exists(cacheRoot))
                {
                    continue;
                }

                foreach (var cachePath in Directory.EnumerateFiles(cacheRoot, "*.json", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var fileName = Path.GetFileNameWithoutExtension(cachePath);
                    if (!int.TryParse(fileName, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId) || appId <= 0)
                    {
                        continue;
                    }

                    AddCandidate(candidates, new ImportCandidate
                    {
                        AppId = appId,
                        HasSteamLibraryCache = true,
                        LastWriteUtc = File.GetLastWriteTimeUtc(cachePath)
                    });
                }
            }
        }

        private static IEnumerable<string> GetSteamAppCacheStatsRoots(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                yield break;
            }

            if (string.Equals(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)), "stats", StringComparison.OrdinalIgnoreCase))
            {
                yield return root;
            }

            var nestedStats = Path.Combine(root, "appcache", "stats");
            if (Directory.Exists(nestedStats))
            {
                yield return nestedStats;
            }
        }

        private static IEnumerable<string> GetSteamUserdataRoots(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                yield break;
            }

            if (string.Equals(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)), "userdata", StringComparison.OrdinalIgnoreCase))
            {
                yield return root;
            }

            var nestedUserdata = Path.Combine(root, "userdata");
            if (Directory.Exists(nestedUserdata))
            {
                yield return nestedUserdata;
            }
        }

        private static IEnumerable<string> EnumerateLibraryCacheRoots(string userdataRoot)
        {
            yield return Path.Combine(userdataRoot, "config", "librarycache");

            foreach (var userDir in Directory.EnumerateDirectories(userdataRoot))
            {
                yield return Path.Combine(userDir, "config", "librarycache");
            }
        }

        private bool PersistLocalBinding(Game game, int appId, string folderPath)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return false;
            }

            _localSettings.SteamAppIdOverrides ??= new Dictionary<Guid, int>();
            _localSettings.LocalFolderOverrides ??= new Dictionary<Guid, string>();

            var changed = false;
            if (!_localSettings.SteamAppIdOverrides.TryGetValue(game.Id, out var existingAppId) || existingAppId != appId)
            {
                _localSettings.SteamAppIdOverrides[game.Id] = appId;
                _logger?.Info($"Set Local Steam App ID override for '{game.Name}' to {appId}");
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                var normalizedPath = folderPath.Trim();
                if (!_localSettings.LocalFolderOverrides.TryGetValue(game.Id, out var existingFolderPath) ||
                    !string.Equals(existingFolderPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    _localSettings.LocalFolderOverrides[game.Id] = normalizedPath;
                    _logger?.Info($"Set Local folder override for '{game.Name}' to '{normalizedPath}'");
                    changed = true;
                }
            }

            if (_settings.Persisted.PreferredProviderOverrides == null)
            {
                _settings.Persisted.PreferredProviderOverrides = new Dictionary<Guid, string>();
            }

            if (!_settings.Persisted.PreferredProviderOverrides.TryGetValue(game.Id, out var providerOverride) ||
                !string.Equals(providerOverride, "Local", StringComparison.OrdinalIgnoreCase))
            {
                _settings.Persisted.PreferredProviderOverrides[game.Id] = "Local";
                changed = true;
            }

            return changed;
        }

        private void PersistSettingsForUi()
        {
            PlayniteAchievementsPlugin.Instance?.PersistSettingsForUi();
        }

        private Game FindExistingGame(int appId, LocalImportedGameLibraryTarget importTarget, string customSourceName)
        {
            var matchingGames = _api.Database.Games
                .Where(game => MatchesResolvedAppId(game, appId))
                .ToList();

            if (matchingGames.Count == 0)
            {
                return null;
            }

            return matchingGames.FirstOrDefault(game => MatchesRequestedImportTarget(game, importTarget, customSourceName))
                ?? matchingGames.FirstOrDefault(game => IsExistingLocalBinding(game))
                ?? matchingGames.FirstOrDefault();
        }

        private Game FindExistingGameByName(string resolvedName, LocalImportedGameLibraryTarget importTarget, string customSourceName)
        {
            var normalizedName = NormalizeGameTitleForDuplicateMatch(resolvedName);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                return null;
            }

            var matchingGames = _api.Database.Games
                .Where(game => MatchesNormalizedGameTitle(game, normalizedName))
                .ToList();

            if (matchingGames.Count == 0)
            {
                return null;
            }

            return matchingGames.FirstOrDefault(game => MatchesRequestedImportTarget(game, importTarget, customSourceName))
                ?? matchingGames.FirstOrDefault(game => game.PluginId == Guid.Empty)
                ?? matchingGames.FirstOrDefault(game => IsExistingLocalBinding(game))
                ?? matchingGames.FirstOrDefault();
        }

        private bool MatchesResolvedAppId(Game game, int appId)
        {
            if (game == null || game.Id == Guid.Empty || appId <= 0)
            {
                return false;
            }

            var appIdText = appId.ToString(CultureInfo.InvariantCulture);
            return string.Equals(game.GameId, appIdText, StringComparison.OrdinalIgnoreCase) ||
                (LocalSavesProvider.TryResolveAppId(game, out var resolvedAppId, out _) && resolvedAppId == appId);
        }

        private static bool MatchesNormalizedGameTitle(Game game, string normalizedName)
        {
            if (game == null || string.IsNullOrWhiteSpace(normalizedName))
            {
                return false;
            }

            return string.Equals(NormalizeGameTitleForDuplicateMatch(game.Name), normalizedName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(NormalizeGameTitleForDuplicateMatch(game.SortingName), normalizedName, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeGameTitleForDuplicateMatch(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = WebUtility.HtmlDecode(value).Trim().ToLowerInvariant();
            normalized = normalized.Replace('’', '\'').Replace('`', '\'');
            normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}]+", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ");
            return normalized.Trim();
        }

        private bool MatchesRequestedImportTarget(Game game, LocalImportedGameLibraryTarget importTarget, string customSourceName)
        {
            if (game == null)
            {
                return false;
            }

            var isManualOrCustomGame = game.PluginId == Guid.Empty;

            switch (importTarget)
            {
                case LocalImportedGameLibraryTarget.Steam:
                    return game.PluginId == SteamDataProvider.SteamPluginId;

                case LocalImportedGameLibraryTarget.CustomSource:
                    if (!isManualOrCustomGame)
                    {
                        return false;
                    }

                    if (string.IsNullOrWhiteSpace(customSourceName))
                    {
                        return true;
                    }

                    var sourceName = _api.Database.Sources?.FirstOrDefault(source => source?.Id == game.SourceId)?.Name;
                    return string.Equals(sourceName?.Trim(), customSourceName.Trim(), StringComparison.OrdinalIgnoreCase);

                case LocalImportedGameLibraryTarget.None:
                default:
                    return isManualOrCustomGame;
            }
        }

        private bool IsExistingLocalBinding(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return false;
            }

            if (_settings?.Persisted?.PreferredProviderOverrides != null &&
                _settings.Persisted.PreferredProviderOverrides.TryGetValue(game.Id, out var preferredProvider) &&
                string.Equals(preferredProvider, "Local", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return LocalSavesProvider.TryGetAppIdOverride(game.Id, out _) ||
                LocalSavesProvider.TryGetFolderOverride(game.Id, out _);
        }

        private static ImportCandidate ChooseBestImportCandidate(IEnumerable<ImportCandidate> candidates)
        {
            return candidates?
                .Where(candidate => candidate != null)
                .OrderByDescending(GetImportCandidateScore)
                .ThenByDescending(candidate => candidate.LastWriteUtc)
                .ThenBy(candidate => candidate.FolderPath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private static string ChooseBestLocalFolderCandidate(IEnumerable<ImportCandidate> candidates)
        {
            return candidates?
                .Where(candidate => candidate != null && !string.IsNullOrWhiteSpace(candidate.FolderPath))
                .OrderByDescending(GetImportCandidateScore)
                .ThenByDescending(candidate => candidate.LastWriteUtc)
                .ThenBy(candidate => candidate.FolderPath, StringComparer.OrdinalIgnoreCase)
                .Select(candidate => candidate.FolderPath)
                .FirstOrDefault();
        }

        private static int GetImportCandidateScore(ImportCandidate candidate)
        {
            var score = 0;
            if (candidate == null)
            {
                return score;
            }

            if (candidate.HasAchievementIni)
            {
                score += 2;
            }

            if (candidate.HasAchievementJson)
            {
                score += 1;
            }

            if (candidate.HasSteamAppCacheSchema)
            {
                score += 2;
            }

            if (candidate.HasSteamAppCacheUserStats)
            {
                score += 1;
            }

            if (candidate.HasSteamLibraryCache)
            {
                score += 1;
            }

            return score;
        }

        private static DateTime GetLatestAchievementFileWriteTime(string folderPath)
        {
            var latest = DateTime.MinValue;
            foreach (var fileName in new[] { "achievements.ini", "achievements.json" })
            {
                var filePath = ResolveAchievementFilePath(folderPath, fileName);
                if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
                {
                    var lastWrite = File.GetLastWriteTimeUtc(filePath);
                    if (lastWrite > latest)
                    {
                        latest = lastWrite;
                    }
                }
            }

            return latest;
        }

        private static string ResolveAchievementFilePath(string folderPath, string fileName)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            foreach (var relativeDirectory in SupportedAchievementRelativeDirectories)
            {
                var candidatePath = string.IsNullOrWhiteSpace(relativeDirectory)
                    ? Path.Combine(folderPath, fileName)
                    : Path.Combine(folderPath, relativeDirectory, fileName);

                if (File.Exists(candidatePath))
                {
                    return candidatePath;
                }
            }

            return null;
        }

        private static string BuildCandidateDetail(ImportCandidate candidate, int index, int total)
        {
            if (candidate == null)
            {
                return $"Processing item {index} of {total}.";
            }

            var evidence = new List<string>();
            if (candidate.HasAchievementIni)
            {
                evidence.Add("achievements.ini");
            }

            if (candidate.HasAchievementJson)
            {
                evidence.Add("achievements.json");
            }

            if (candidate.HasSteamAppCacheSchema)
            {
                evidence.Add("UserGameStatsSchema_<appid>.bin");
            }

            if (candidate.HasSteamAppCacheUserStats)
            {
                evidence.Add("UserGameStats_*_<appid>.bin");
            }

            if (candidate.HasSteamLibraryCache)
            {
                evidence.Add("userdata/config/librarycache/<appid>.json");
            }

            var evidenceText = evidence.Count > 0 ? string.Join(", ", evidence) : "unknown achievement evidence";
            return $"Processing item {index} of {total}. Evidence: {evidenceText}.";
        }

        private static string DescribeCandidate(ImportCandidate candidate)
        {
            if (candidate == null)
            {
                return "<null>";
            }

            var evidence = new List<string>();
            if (candidate.HasAchievementIni)
            {
                evidence.Add("ini");
            }

            if (candidate.HasAchievementJson)
            {
                evidence.Add("json");
            }

            if (candidate.HasSteamAppCacheSchema)
            {
                evidence.Add("schema-cache");
            }

            if (candidate.HasSteamAppCacheUserStats)
            {
                evidence.Add("userstats-cache");
            }

            if (candidate.HasSteamLibraryCache)
            {
                evidence.Add("library-cache");
            }

            var evidenceText = evidence.Count > 0 ? string.Join(",", evidence) : "none";
            var folderText = string.IsNullOrWhiteSpace(candidate.FolderPath) ? "<no folder>" : candidate.FolderPath;
            return $"folder='{folderText}', evidence={evidenceText}, lastWriteUtc={candidate.LastWriteUtc:O}";
        }

        private static string DescribeGame(Game game)
        {
            if (game == null)
            {
                return "<null>";
            }

            var sourceName = game.Source?.Name;
            return $"name='{game.Name}', id={game.Id}, gameId='{game.GameId}', pluginId={game.PluginId}, source='{sourceName}'";
        }

        private void ApplyDownloadedMetadata(
            Game importedGame,
            int appId,
            MetadataPlugin selectedMetadataPlugin)
        {
            if (importedGame == null)
            {
                return;
            }

            try
            {
                if (selectedMetadataPlugin != null)
                {
                    ApplyMetadataPlugin(importedGame, appId, selectedMetadataPlugin);
                    NormalizeImportedGameMetadata(importedGame, appId);
                    _api.Database.Games.Update(importedGame);
                    return;
                }
                _logger?.Info($"[LocalImport] No metadata plugin resolved for appId={appId}; skipping metadata download.");
                NormalizeImportedGameMetadata(importedGame, appId);
                _api.Database.Games.Update(importedGame);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[LocalAch] Failed applying downloaded Steam metadata for imported game '{importedGame?.Name}'.");
            }
        }

        private bool ApplyMetadataPlugin(Game importedGame, int appId, MetadataPlugin metadataPlugin)
        {
            if (importedGame == null || appId <= 0 || metadataPlugin == null)
            {
                return false;
            }

            try
            {
                ClearImportedMetadataForOverwrite(importedGame);
                var lookupGame = CreateMetadataPluginLookupGame(importedGame, appId, metadataPlugin);

                using (var provider = metadataPlugin.GetMetadataProvider(new MetadataRequestOptions(lookupGame, true)))
                {
                    if (provider == null)
                    {
                        return false;
                    }

                    var args = new GetMetadataFieldArgs();
                    var applied = false;

                    var name = provider.GetName(args);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        importedGame.Name = name;
                        importedGame.SortingName = name;
                        applied = true;
                    }

                    var description = provider.GetDescription(args);
                    if (!string.IsNullOrWhiteSpace(description))
                    {
                        importedGame.Description = PrepareProviderMetadataDescription(description);
                        applied = true;
                    }

                    var iconId = PersistMetadataFile(importedGame.Id, provider.GetIcon(args));
                    if (!string.IsNullOrWhiteSpace(iconId))
                    {
                        importedGame.Icon = iconId;
                        applied = true;
                    }

                    var coverId = PersistMetadataFile(importedGame.Id, provider.GetCoverImage(args));
                    if (!string.IsNullOrWhiteSpace(coverId))
                    {
                        importedGame.CoverImage = coverId;
                        applied = true;
                    }

                    var backgroundId = PersistMetadataFile(importedGame.Id, provider.GetBackgroundImage(args));
                    if (!string.IsNullOrWhiteSpace(backgroundId))
                    {
                        importedGame.BackgroundImage = backgroundId;
                        applied = true;
                    }

                    var releaseDate = provider.GetReleaseDate(args);
                    if (releaseDate != null)
                    {
                        importedGame.ReleaseDate = releaseDate;
                        applied = true;
                    }

                    var criticScore = provider.GetCriticScore(args);
                    if (criticScore.HasValue)
                    {
                        importedGame.CriticScore = criticScore;
                        applied = true;
                    }

                    var communityScore = provider.GetCommunityScore(args);
                    if (communityScore.HasValue)
                    {
                        importedGame.CommunityScore = communityScore;
                        applied = true;
                    }

                    var platforms = provider.GetPlatforms(args)?.ToList();
                    var genres = provider.GetGenres(args)?.ToList();
                    var developers = provider.GetDevelopers(args)?.ToList();
                    var publishers = provider.GetPublishers(args)?.ToList();
                    var tags = provider.GetTags(args)?.ToList();
                    var features = provider.GetFeatures(args)?.ToList();
                    var ageRatings = provider.GetAgeRatings(args)?.ToList();
                    var regions = provider.GetRegions(args)?.ToList();
                    var series = provider.GetSeries(args)?.ToList();

                    _logger?.Info(
                        $"[LocalImport] Metadata plugin '{metadataPlugin.Name}' field counts for appId={appId}: " +
                        $"platforms={platforms?.Count ?? 0}, genres={genres?.Count ?? 0}, developers={developers?.Count ?? 0}, " +
                        $"publishers={publishers?.Count ?? 0}, tags={tags?.Count ?? 0}, features={features?.Count ?? 0}, " +
                        $"ageRatings={ageRatings?.Count ?? 0}, regions={regions?.Count ?? 0}, series={series?.Count ?? 0}.");

                    if (platforms?.Count > 0)
                    {
                        ReplaceCollection(importedGame.Platforms, _api.Database.Platforms.Add(platforms));
                        applied = true;
                    }

                    if (genres?.Count > 0)
                    {
                        ReplaceCollection(importedGame.Genres, _api.Database.Genres.Add(genres));
                        applied = true;
                    }

                    if (developers?.Count > 0)
                    {
                        ReplaceCollection(importedGame.Developers, _api.Database.Companies.Add(developers));
                        applied = true;
                    }

                    if (publishers?.Count > 0)
                    {
                        ReplaceCollection(importedGame.Publishers, _api.Database.Companies.Add(publishers));
                        applied = true;
                    }

                    if (tags?.Count > 0)
                    {
                        ReplaceCollection(importedGame.Tags, _api.Database.Tags.Add(tags));
                        applied = true;
                    }

                    if (features?.Count > 0)
                    {
                        ReplaceCollection(importedGame.Features, _api.Database.Features.Add(features));
                        applied = true;
                    }

                    if (ageRatings?.Count > 0)
                    {
                        ReplaceCollection(importedGame.AgeRatings, _api.Database.AgeRatings.Add(ageRatings));
                        applied = true;
                    }

                    if (regions?.Count > 0)
                    {
                        ReplaceCollection(importedGame.Regions, _api.Database.Regions.Add(regions));
                        applied = true;
                    }

                    if (series?.Count > 0)
                    {
                        ReplaceCollection(importedGame.Series, _api.Database.Series.Add(series));
                        applied = true;
                    }

                    var links = provider.GetLinks(args)?.ToList();
                    if (links?.Count > 0)
                    {
                        ReplaceCollection(importedGame.Links, links);
                        applied = true;
                    }

                    if (applied)
                    {
                        _logger?.Info($"[LocalImport] Applied metadata plugin '{metadataPlugin.Name}' for appId={appId}.");
                    }

                    return applied;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[LocalImport] Failed applying metadata plugin '{metadataPlugin?.Name}' for appId={appId}.");
                return false;
            }
        }

        private void ApplyImportTarget(Game game, LocalImportedGameLibraryTarget importTarget, string customSourceName)
        {
            if (game == null || game.Id == Guid.Empty || game.PluginId != Guid.Empty)
            {
                return;
            }

            if (importTarget == LocalImportedGameLibraryTarget.CustomSource && !string.IsNullOrWhiteSpace(customSourceName))
            {
                var source = EnsureGameSource(customSourceName.Trim());
                if (source != null)
                {
                    game.SourceId = source.Id;
                }
            }
        }

        private GameSource EnsureGameSource(string sourceName)
        {
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                return null;
            }

            var existing = _api.Database.Sources.FirstOrDefault(source =>
                source != null &&
                string.Equals(source.Name?.Trim(), sourceName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                return existing;
            }

            try
            {
                var createdSource = new GameSource(sourceName.Trim());
                _api.Database.Sources.Add(createdSource);
                return _api.Database.Sources.FirstOrDefault(source =>
                    source != null &&
                    string.Equals(source.Name?.Trim(), sourceName.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[LocalImport] Failed creating Playnite source '{sourceName}'.");
                return null;
            }
        }

        private void ApplySteamStoreFallbackMetadata(Game importedGame, int appId)
        {
            if (importedGame == null || appId <= 0)
            {
                return;
            }

            try
            {
                var requestUrl = $"https://store.steampowered.com/api/appdetails?appids={appId}";
                using (var webClient = new WebClient())
                {
                    webClient.Headers[HttpRequestHeader.UserAgent] =
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
                    webClient.Headers[HttpRequestHeader.Accept] = "application/json";
                    webClient.Encoding = Encoding.UTF8;

                    var json = webClient.DownloadString(requestUrl);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        return;
                    }

                    var root = JObject.Parse(json);
                    var envelope = root[appId.ToString(CultureInfo.InvariantCulture)] as JObject;
                    if (envelope?["success"]?.Value<bool>() != true)
                    {
                        return;
                    }

                    var data = envelope["data"] as JObject;
                    if (data == null)
                    {
                        return;
                    }

                    var resolvedName = data["name"]?.Value<string>()?.Trim();
                    if (!string.IsNullOrWhiteSpace(resolvedName))
                    {
                        importedGame.Name = resolvedName;
                        importedGame.SortingName = resolvedName;
                    }

                    if (string.IsNullOrWhiteSpace(importedGame.Description))
                    {
                        var description = data["about_the_game"]?.Value<string>()?.Trim();
                        if (string.IsNullOrWhiteSpace(description))
                        {
                            description = data["detailed_description"]?.Value<string>()?.Trim();
                        }

                        if (string.IsNullOrWhiteSpace(description))
                        {
                            description = data["short_description"]?.Value<string>()?.Trim();
                        }

                        importedGame.Description = NormalizeMetadataDescription(description);
                    }

                    var iconUrl = data["capsule_image"]?.Value<string>()?.Trim();
                    if (string.IsNullOrWhiteSpace(iconUrl))
                    {
                        iconUrl = data["header_image"]?.Value<string>()?.Trim();
                    }

                    var coverUrl = data["header_image"]?.Value<string>()?.Trim();
                    var backgroundUrl = data["background_raw"]?.Value<string>()?.Trim();
                    if (string.IsNullOrWhiteSpace(backgroundUrl))
                    {
                        backgroundUrl = data["background"]?.Value<string>()?.Trim();
                    }

                    var iconId = PersistMetadataFile(importedGame.Id, string.IsNullOrWhiteSpace(iconUrl) ? null : new MetadataFile(iconUrl));
                    if (!string.IsNullOrWhiteSpace(iconId))
                    {
                        importedGame.Icon = iconId;
                    }

                    var coverId = PersistMetadataFile(importedGame.Id, string.IsNullOrWhiteSpace(coverUrl) ? null : new MetadataFile(coverUrl));
                    if (!string.IsNullOrWhiteSpace(coverId))
                    {
                        importedGame.CoverImage = coverId;
                    }

                    var backgroundId = PersistMetadataFile(importedGame.Id, string.IsNullOrWhiteSpace(backgroundUrl) ? null : new MetadataFile(backgroundUrl));
                    if (!string.IsNullOrWhiteSpace(backgroundId))
                    {
                        importedGame.BackgroundImage = backgroundId;
                    }

                    var websiteUrl = data["website"]?.Value<string>()?.Trim();
                    var links = importedGame.Links?.ToList() ?? new List<Link>();
                    AddLinkIfMissing(links, "Steam Store", $"https://store.steampowered.com/app/{appId}/");
                    AddLinkIfMissing(links, "Community Hub", $"https://steamcommunity.com/app/{appId}");
                    AddLinkIfMissing(links, "Discussions", $"https://steamcommunity.com/app/{appId}/discussions/");
                    AddLinkIfMissing(links, "Guides", $"https://steamcommunity.com/app/{appId}/guides/");
                    AddLinkIfMissing(links, "News", $"https://store.steampowered.com/news/app/{appId}");

                    var categoryIds = data["categories"]?
                        .Values<JObject>()
                        .Select(category => category?["id"]?.Value<int>() ?? 0)
                        .Where(id => id > 0)
                        .ToHashSet() ?? new HashSet<int>();
                    if (categoryIds.Contains(22))
                    {
                        AddLinkIfMissing(links, "Achievements", $"https://steamcommunity.com/stats/{appId}/achievements/");
                    }

                    if (categoryIds.Contains(30))
                    {
                        AddLinkIfMissing(links, "Workshop", $"https://steamcommunity.com/app/{appId}/workshop/");
                    }

                    if (!string.IsNullOrWhiteSpace(websiteUrl))
                    {
                        AddLinkIfMissing(links, "Website", websiteUrl);
                    }

                    if (links.Count > 0)
                    {
                        ReplaceCollection(importedGame.Links, links);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[LocalImport] Failed applying Steam Store fallback metadata for appId={appId}.");
            }
        }

        private static bool IsPlaceholderSteamName(string value, int appId)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            return string.Equals(
                value.Trim(),
                $"Steam App {appId}",
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldApplySteamStoreFallback(Game importedGame, int appId)
        {
            if (importedGame == null)
            {
                return false;
            }

            if (IsPlaceholderSteamName(importedGame.Name, appId) || IsPlaceholderSteamName(importedGame.SortingName, appId))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(importedGame.Description))
            {
                return true;
            }

            return importedGame.Links == null || importedGame.Links.Count == 0;
        }

        private Game CreateMetadataPluginLookupGame(Game importedGame, int appId, MetadataPlugin metadataPlugin)
        {
            if (importedGame == null)
            {
                return null;
            }

            var isUniversalSteamMetadata = IsUniversalSteamMetadataPlugin(metadataPlugin);
            var lookupName = isUniversalSteamMetadata
                ? BuildUniversalSteamMetadataLookupName(importedGame.Name, appId)
                : importedGame.Name;

            return new Game
            {
                GameId = appId > 0 ? appId.ToString(CultureInfo.InvariantCulture) : importedGame.GameId,
                PluginId = isUniversalSteamMetadata ? SteamDataProvider.SteamPluginId : importedGame.PluginId,
                Name = lookupName,
                SortingName = lookupName,
                SourceId = importedGame.SourceId
            };
        }


                    private bool ShouldImportCandidate(int appId, ImportCandidate candidate, out string reason)
                    {
                        reason = null;
                        if (appId <= 0 || candidate == null)
                        {
                            return false;
                        }

                        var isSteamCacheCandidate = candidate.HasSteamAppCacheSchema ||
                            candidate.HasSteamAppCacheUserStats ||
                            candidate.HasSteamLibraryCache;
                        if (!isSteamCacheCandidate)
                        {
                            return true;
                        }

                        var importability = GetSteamAppImportability(appId);
                        if (importability == null || importability.IsImportable)
                        {
                            return true;
                        }

                        var namePart = string.IsNullOrWhiteSpace(importability.Name)
                            ? string.Empty
                            : $" '{importability.Name}'";
                        var typePart = string.IsNullOrWhiteSpace(importability.Type)
                            ? string.Empty
                            : $" (type '{importability.Type}')";
                        reason = $"Steam app{namePart} is not an importable game{typePart}.";
                        return false;
                    }

                    private SteamAppImportabilityInfo GetSteamAppImportability(int appId)
                    {
                        if (appId <= 0)
                        {
                            return null;
                        }

                        if (_steamAppImportabilityCache.TryGetValue(appId, out var cachedInfo))
                        {
                            return cachedInfo;
                        }

                        var info = new SteamAppImportabilityInfo
                        {
                            IsImportable = !IsExcludedSteamAppId(appId)
                        };

                        try
                        {
                            var requestUrl = $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic";
                            using (var webClient = new WebClient())
                            {
                                webClient.Headers[HttpRequestHeader.UserAgent] =
                                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
                                webClient.Headers[HttpRequestHeader.Accept] = "application/json";
                                webClient.Encoding = Encoding.UTF8;

                                var json = webClient.DownloadString(requestUrl);
                                if (!string.IsNullOrWhiteSpace(json))
                                {
                                    var root = JObject.Parse(json);
                                    var envelope = root[appId.ToString(CultureInfo.InvariantCulture)] as JObject;
                                    if (envelope?["success"]?.Value<bool>() == true)
                                    {
                                        var data = envelope["data"] as JObject;
                                        if (data != null)
                                        {
                                            info.Name = data["name"]?.Value<string>()?.Trim();
                                            info.Type = data["type"]?.Value<string>()?.Trim();
                                            info.IsImportable = !IsExcludedSteamAppId(appId) &&
                                                IsImportableSteamAppType(info.Type) &&
                                                !IsExcludedSteamAppName(info.Name);
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger?.Debug(ex, $"[LocalImport] Failed checking Steam app type for appId={appId}. Allowing import.");
                        }

                        _steamAppImportabilityCache[appId] = info;
                        return info;
                    }

                    private static bool IsImportableSteamAppType(string type)
                    {
                        if (string.IsNullOrWhiteSpace(type))
                        {
                            return true;
                        }

                        return string.Equals(type, "game", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(type, "demo", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(type, "mod", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(type, "episode", StringComparison.OrdinalIgnoreCase);
                    }

                    private static bool IsExcludedSteamAppName(string name)
                    {
                        return string.Equals(name?.Trim(), "Steam Game Notes", StringComparison.OrdinalIgnoreCase);
                    }

                    private static bool IsExcludedSteamAppId(int appId)
                    {
                        return appId == 228980 || appId == 2371090;
                    }

        private static string BuildUniversalSteamMetadataLookupName(string currentName, int appId)
        {
            var trimmedName = (currentName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmedName) || IsPlaceholderSteamName(trimmedName, appId))
            {
                return appId.ToString(CultureInfo.InvariantCulture);
            }

            if (trimmedName.IndexOf(appId.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return trimmedName;
            }

            return $"{trimmedName} {appId}";
        }

        private static void ClearImportedMetadataForOverwrite(Game importedGame)
        {
            if (importedGame == null)
            {
                return;
            }

            importedGame.Description = null;
            importedGame.Icon = null;
            importedGame.CoverImage = null;
            importedGame.BackgroundImage = null;
            importedGame.ReleaseDate = null;
            importedGame.CriticScore = null;
            importedGame.CommunityScore = null;

            importedGame.Platforms?.Clear();
            importedGame.Genres?.Clear();
            importedGame.Developers?.Clear();
            importedGame.Publishers?.Clear();
            importedGame.Tags?.Clear();
            importedGame.Features?.Clear();
            importedGame.AgeRatings?.Clear();
            importedGame.Regions?.Clear();
            importedGame.Series?.Clear();
            importedGame.Links?.Clear();
        }

        private MetadataPlugin ResolveMetadataPlugin(string metadataSourceId)
        {
            if (string.IsNullOrWhiteSpace(metadataSourceId))
            {
                return null;
            }

            try
            {
                var metadataPlugins = GetMetadataPluginIdentities();

                if (metadataSourceId.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                {
                    var pluginName = metadataSourceId.Substring(5).Trim();
                    var matchedByName = metadataPlugins.FirstOrDefault(plugin =>
                        MatchesPluginIdentity(plugin, pluginName));
                    if (matchedByName != null)
                    {
                        return matchedByName.Plugin;
                    }
                }

                if (Guid.TryParse(metadataSourceId, out var pluginId))
                {
                    return metadataPlugins.FirstOrDefault(plugin => plugin.Plugin?.Id == pluginId)?.Plugin;
                }

                var matchedByToken = metadataPlugins.FirstOrDefault(plugin =>
                    MatchesPluginIdentity(plugin, metadataSourceId));
                if (matchedByToken != null)
                {
                    return matchedByToken.Plugin;
                }

                LogMetadataPluginIdentities(metadataPlugins, metadataSourceId);
                return null;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[LocalImport] Failed resolving metadata plugin '{metadataSourceId}'.");
                return null;
            }
        }

        private MetadataPlugin ResolveAutomaticMetadataPlugin()
        {
            var universalSteamMetadataPlugin = ResolveUniversalSteamMetadataPlugin();
            if (universalSteamMetadataPlugin != null)
            {
                return universalSteamMetadataPlugin;
            }

            return null;
        }

        private static void AddLinkIfMissing(ICollection<Link> links, string name, string url)
        {
            if (links == null || string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            if (links.Any(link => link != null && string.Equals(link.Url, url, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            links.Add(new Link(name, url));
        }

        private static string NormalizeMetadataDescription(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = Regex.Replace(value, @"(?i)<br\s*/?>", "\n");
            normalized = Regex.Replace(normalized, @"(?i)</p\s*>", "\n\n");
            normalized = Regex.Replace(normalized, @"(?i)</div\s*>", "\n");
            normalized = Regex.Replace(normalized, @"(?i)</li\s*>", "\n");
            normalized = Regex.Replace(normalized, @"<[^>]+>", string.Empty);
            normalized = WebUtility.HtmlDecode(normalized ?? string.Empty);
            normalized = normalized.Replace("\r\n", "\n").Replace('\r', '\n');
            normalized = Regex.Replace(normalized, @"[ \t]+\n", "\n");
            normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");
            return normalized.Trim();
        }

        private static void NormalizeImportedGameMetadata(Game importedGame, int appId)
        {
            if (importedGame == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(importedGame.SortingName) || IsPlaceholderSteamName(importedGame.SortingName, appId))
            {
                importedGame.SortingName = importedGame.Name;
            }
        }

        private static string PrepareProviderMetadataDescription(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        }

        private static void ReplaceCollection<T>(ICollection<T> target, IEnumerable<T> source)
        {
            if (target == null || source == null)
            {
                return;
            }

            target.Clear();
            foreach (var item in source)
            {
                target.Add(item);
            }
        }

        private string PersistMetadataFile(Guid gameId, MetadataFile metadataFile)
        {
            if (gameId == Guid.Empty || metadataFile == null || !metadataFile.HasImageData)
            {
                return null;
            }

            string tempFilePath = null;
            try
            {
                if (metadataFile.HasContent && metadataFile.Content?.Length > 0)
                {
                    var fileName = string.IsNullOrWhiteSpace(metadataFile.FileName)
                        ? $"local_meta_{Guid.NewGuid():N}{GetExtensionFromPath(metadataFile.Path)}"
                        : metadataFile.FileName.Trim();
                    tempFilePath = Path.Combine(CreateMetadataTempDirectory(), SanitizeFileName(fileName));
                    File.WriteAllBytes(tempFilePath, metadataFile.Content);
                    return _api.Database.AddFile(tempFilePath, gameId);
                }

                if (!string.IsNullOrWhiteSpace(metadataFile.Path) && File.Exists(metadataFile.Path))
                {
                    return _api.Database.AddFile(metadataFile.Path, gameId);
                }

                if (!string.IsNullOrWhiteSpace(metadataFile.Path) && Uri.TryCreate(metadataFile.Path, UriKind.Absolute, out var uri))
                {
                    var targetExtension = GetExtensionFromPath(uri.AbsolutePath);
                    var fileName = string.IsNullOrWhiteSpace(metadataFile.FileName)
                        ? $"local_meta_{Guid.NewGuid():N}{targetExtension}"
                        : metadataFile.FileName.Trim();
                    tempFilePath = Path.Combine(CreateMetadataTempDirectory(), SanitizeFileName(fileName));

                    using (var webClient = new System.Net.WebClient())
                    {
                        webClient.DownloadFile(uri, tempFilePath);
                    }

                    return _api.Database.AddFile(tempFilePath, gameId);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[LocalAch] Failed persisting metadata file for gameId={gameId} from '{metadataFile?.Path}'.");
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(tempFilePath) && File.Exists(tempFilePath))
                    {
                        File.Delete(tempFilePath);
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static string CreateMetadataTempDirectory()
        {
            var directory = Path.Combine(Path.GetTempPath(), "PlayniteAchievements", "LocalMetadataImport");
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static string GetExtensionFromPath(string path)
        {
            try
            {
                var extension = Path.GetExtension(path ?? string.Empty);
                return string.IsNullOrWhiteSpace(extension) ? ".img" : extension;
            }
            catch
            {
                return ".img";
            }
        }

        private static string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return $"local_meta_{Guid.NewGuid():N}.img";
            }

            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string(fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
            return string.IsNullOrWhiteSpace(sanitized) ? $"local_meta_{Guid.NewGuid():N}.img" : sanitized;
        }

        private GameMetadata BuildMetadata(
            int appId,
            LocalImportedGameLibraryTarget importTarget,
            string customSourceName,
            MetadataPlugin selectedMetadataPlugin)
        {
            var name = $"Steam App {appId}";
            var metadata = new GameMetadata
            {
                Name = name,
                SortingName = null,
                GameId = appId.ToString(CultureInfo.InvariantCulture),
                IsInstalled = false,
                InstallDirectory = null
            };

            if (importTarget == LocalImportedGameLibraryTarget.Steam)
            {
                metadata.Source = new MetadataNameProperty("Steam");
            }
            else if (importTarget == LocalImportedGameLibraryTarget.CustomSource && !string.IsNullOrWhiteSpace(customSourceName))
            {
                metadata.Source = new MetadataNameProperty(customSourceName);
            }

            if (selectedMetadataPlugin != null)
            {
                ApplyMetadataPluginToMetadata(metadata, appId, selectedMetadataPlugin);
            }

            return metadata;
        }

        private bool ApplyMetadataPluginToMetadata(GameMetadata metadata, int appId, MetadataPlugin metadataPlugin)
        {
            if (metadata == null || appId <= 0 || metadataPlugin == null)
            {
                return false;
            }

            try
            {
                var temporaryGame = new Game
                {
                    GameId = metadata.GameId,
                    Name = metadata.Name,
                    SortingName = metadata.SortingName
                };
                var lookupGame = CreateMetadataPluginLookupGame(temporaryGame, appId, metadataPlugin);

                using (var provider = metadataPlugin.GetMetadataProvider(new MetadataRequestOptions(lookupGame, true)))
                {
                    if (provider == null)
                    {
                        return false;
                    }

                    var args = new GetMetadataFieldArgs();
                    var applied = false;

                    var name = provider.GetName(args);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        metadata.Name = name;
                        metadata.SortingName = name;
                        applied = true;
                    }

                    var description = provider.GetDescription(args);
                    if (!string.IsNullOrWhiteSpace(description))
                    {
                        metadata.Description = PrepareProviderMetadataDescription(description);
                        applied = true;
                    }

                    var releaseDate = provider.GetReleaseDate(args);
                    if (releaseDate != null)
                    {
                        metadata.ReleaseDate = releaseDate;
                        applied = true;
                    }

                    var criticScore = provider.GetCriticScore(args);
                    if (criticScore.HasValue)
                    {
                        metadata.CriticScore = criticScore;
                        applied = true;
                    }

                    var communityScore = provider.GetCommunityScore(args);
                    if (communityScore.HasValue)
                    {
                        metadata.CommunityScore = communityScore;
                        applied = true;
                    }

                    var platforms = provider.GetPlatforms(args)?.ToList();
                    var genres = provider.GetGenres(args)?.ToList();
                    var developers = provider.GetDevelopers(args)?.ToList();
                    var publishers = provider.GetPublishers(args)?.ToList();
                    var tags = provider.GetTags(args)?.ToList();
                    var features = provider.GetFeatures(args)?.ToList();
                    var ageRatings = provider.GetAgeRatings(args)?.ToList();
                    var regions = provider.GetRegions(args)?.ToList();
                    var series = provider.GetSeries(args)?.ToList();
                    var links = provider.GetLinks(args)?.ToList();

                    _logger?.Info(
                        $"[LocalImport] Metadata plugin '{metadataPlugin.Name}' field counts for appId={appId}: " +
                        $"platforms={platforms?.Count ?? 0}, genres={genres?.Count ?? 0}, developers={developers?.Count ?? 0}, " +
                        $"publishers={publishers?.Count ?? 0}, tags={tags?.Count ?? 0}, features={features?.Count ?? 0}, " +
                        $"ageRatings={ageRatings?.Count ?? 0}, regions={regions?.Count ?? 0}, series={series?.Count ?? 0}.");

                    if (platforms?.Count > 0)
                    {
                        metadata.Platforms = new HashSet<MetadataProperty>(platforms);
                        applied = true;
                    }

                    if (genres?.Count > 0)
                    {
                        metadata.Genres = new HashSet<MetadataProperty>(genres);
                        applied = true;
                    }

                    if (developers?.Count > 0)
                    {
                        metadata.Developers = new HashSet<MetadataProperty>(developers);
                        applied = true;
                    }

                    if (publishers?.Count > 0)
                    {
                        metadata.Publishers = new HashSet<MetadataProperty>(publishers);
                        applied = true;
                    }

                    if (tags?.Count > 0)
                    {
                        metadata.Tags = new HashSet<MetadataProperty>(tags);
                        applied = true;
                    }

                    if (features?.Count > 0)
                    {
                        metadata.Features = new HashSet<MetadataProperty>(features);
                        applied = true;
                    }

                    if (ageRatings?.Count > 0)
                    {
                        metadata.AgeRatings = new HashSet<MetadataProperty>(ageRatings);
                        applied = true;
                    }

                    if (regions?.Count > 0)
                    {
                        metadata.Regions = new HashSet<MetadataProperty>(regions);
                        applied = true;
                    }

                    if (series?.Count > 0)
                    {
                        metadata.Series = new HashSet<MetadataProperty>(series);
                        applied = true;
                    }

                    if (links?.Count > 0)
                    {
                        metadata.Links = links;
                        applied = true;
                    }

                    return applied;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[LocalImport] Failed applying metadata plugin '{metadataPlugin?.Name}' to import metadata for appId={appId}.");
                return false;
            }
        }

        private LibraryPlugin ResolveSteamLibraryPlugin()
        {
            try
            {
                return _api.Addons?.Plugins?
                    .OfType<LibraryPlugin>()
                    .FirstOrDefault(plugin => plugin != null && plugin.Id == SteamDataProvider.SteamPluginId);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[LocalAch] Failed resolving Steam library plugin instance.");
                return null;
            }
        }

        private MetadataPlugin ResolveUniversalSteamMetadataPlugin()
        {
            try
            {
                return GetMetadataPluginIdentities()
                    .FirstOrDefault(plugin => IsUniversalSteamMetadataPlugin(plugin))
                    ?.Plugin;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[LocalImport] Failed resolving Universal Steam Metadata plugin instance.");
                return null;
            }
        }

        private IReadOnlyList<MetadataPluginIdentity> GetMetadataPluginIdentities()
        {
            try
            {
                return (_api.Addons?.Plugins?
                    .OfType<MetadataPlugin>()
                    .Where(plugin => plugin != null)
                    .Select(plugin =>
                    {
                        var manifest = GetPluginManifestIdentity(plugin);
                        return new MetadataPluginIdentity
                        {
                            Plugin = plugin,
                            RuntimeName = plugin.Name?.Trim(),
                            ManifestId = manifest.Item1,
                            ManifestName = manifest.Item2,
                            AssemblyDirectory = manifest.Item3
                        };
                    })
                    .ToList() ?? new List<MetadataPluginIdentity>());
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[LocalImport] Failed enumerating loaded metadata plugins.");
                return Array.Empty<MetadataPluginIdentity>();
            }
        }

        private Tuple<string, string, string> GetPluginManifestIdentity(MetadataPlugin plugin)
        {
            if (plugin == null)
            {
                return Tuple.Create<string, string, string>(null, null, null);
            }

            try
            {
                var assemblyDirectory = Path.GetDirectoryName(plugin.GetType().Assembly.Location);
                if (string.IsNullOrWhiteSpace(assemblyDirectory))
                {
                    return Tuple.Create<string, string, string>(null, null, null);
                }

                for (var directory = new DirectoryInfo(assemblyDirectory); directory != null; directory = directory.Parent)
                {
                    var manifestPath = Path.Combine(directory.FullName, "extension.yaml");
                    if (!File.Exists(manifestPath))
                    {
                        continue;
                    }

                    string manifestId = null;
                    string manifestName = null;
                    foreach (var line in File.ReadLines(manifestPath))
                    {
                        if (line.StartsWith("Id:", StringComparison.OrdinalIgnoreCase))
                        {
                            manifestId = line.Substring(3).Trim();
                        }
                        else if (line.StartsWith("Name:", StringComparison.OrdinalIgnoreCase))
                        {
                            manifestName = line.Substring(5).Trim();
                        }
                    }

                    return Tuple.Create(manifestId, manifestName, directory.FullName);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[LocalImport] Failed reading manifest identity for metadata plugin '{plugin?.Name}'.");
            }

            return Tuple.Create<string, string, string>(null, null, null);
        }

        private static bool MatchesPluginIdentity(MetadataPluginIdentity plugin, string value)
        {
            if (plugin == null || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var token = value.Trim();
            return string.Equals(plugin.RuntimeName, token, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(plugin.ManifestName, token, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(plugin.ManifestId, token, StringComparison.OrdinalIgnoreCase);
        }

        private void LogMetadataPluginIdentities(IEnumerable<MetadataPluginIdentity> plugins, string requestedValue)
        {
            var summary = string.Join(" | ", (plugins ?? Enumerable.Empty<MetadataPluginIdentity>()).Select(plugin =>
                $"runtime='{plugin.RuntimeName ?? "<null>"}', manifestName='{plugin.ManifestName ?? "<null>"}', manifestId='{plugin.ManifestId ?? "<null>"}', pluginId='{plugin.Plugin?.Id.ToString() ?? "<null>"}', assembly='{plugin.AssemblyDirectory ?? "<null>"}'"));
            _logger?.Info($"[LocalImport] Failed to resolve metadata provider '{requestedValue}'. Loaded metadata plugins: {summary}");
        }

        private static bool IsUniversalSteamMetadataPlugin(MetadataPluginIdentity plugin)
        {
            if (plugin == null)
            {
                return false;
            }

            return string.Equals(plugin.ManifestId, UniversalSteamMetadataManifestId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(plugin.ManifestName, UniversalSteamMetadataPluginName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(plugin.RuntimeName, UniversalSteamMetadataPluginName, StringComparison.OrdinalIgnoreCase);
        }

        private bool IsUniversalSteamMetadataPlugin(MetadataPlugin plugin)
        {
            if (plugin == null)
            {
                return false;
            }

            var manifest = GetPluginManifestIdentity(plugin);
            return string.Equals(manifest.Item1, UniversalSteamMetadataManifestId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(manifest.Item2, UniversalSteamMetadataPluginName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(plugin.Name, UniversalSteamMetadataPluginName, StringComparison.OrdinalIgnoreCase);
        }
    }
}