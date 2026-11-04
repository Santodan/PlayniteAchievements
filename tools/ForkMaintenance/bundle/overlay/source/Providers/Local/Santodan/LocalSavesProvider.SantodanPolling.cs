using ForkLog = PlayniteAchievements.Services.Logging.ForkLogger;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using HtmlAgilityPack;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Common;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Providers.Steam.Models;

using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Providers.Overrides;

namespace PlayniteAchievements.Providers.Local
{
    // Fork-only resolution: leave the upstream resolver and fingerprint implementation intact.
    public partial class LocalSavesProvider
    {
        private bool TryReuseSantodanPollingCategories(GameAchievementData data)
        {
            if (!PlayniteAchievements.Services.InGameMonitoring.SantodanPollingScope.IsActive ||
                ProviderRegistry.Settings<LocalSettings>()?.UseCustomPollingMetadataCache != true)
                return false;
            if (data.PlayniteGameId.HasValue)
            {
                var cached = PlayniteAchievementsPlugin.Instance?.CacheManager?
                    .LoadGameData(data.PlayniteGameId.Value.ToString());
                if (cached != null && cached.AppId == data.AppId &&
                    string.Equals(cached.ProviderKey, "Local", StringComparison.OrdinalIgnoreCase))
                    SteamHuntersCategoryEnricher.ApplyCachedCategories(data.Achievements, cached.Achievements);
            }
            return true;
        }

        private static void RecordSantodanPollingRefresh(RebuildSummary summary, Game game, GameAchievementData data)
        {
            if (!PlayniteAchievements.Services.InGameMonitoring.SantodanPollingScope.IsActive) return;
            summary.RefreshedGameIds.Add(game.Id);
            summary.GamesRefreshed++;
            if (data.HasAchievements) summary.GamesWithAchievements++;
            else summary.GamesWithoutAchievements++;
        }

        internal bool TryGetSantodanPollingSourceFingerprint(Game game, out string fingerprint,
            PlayniteAchievements.Services.InGameMonitoring.LocalPollingDirectoryCache directoryCache = null, Action<string> observeAchievementFile = null)
        {
            fingerprint = null;
            if (game == null || game.Id == Guid.Empty)
            {
                return false;
            }

            var paths = new List<string>();
            var appId = GetAppId(game, out _);
            int lumaAppId;

            if (TryGetExpandedFolderOverride(game, out var configuredFolderOverridePath) &&
                TryFindSovereignIniPathFromFolder(configuredFolderOverridePath, out var sovereignIniPath) &&
                TryExtractAppIdFromSovereignIni(sovereignIniPath, out var sovereignAppId))
            {
                appId = sovereignAppId.ToString(CultureInfo.InvariantCulture);
                AddExistingPath(paths, sovereignIniPath);
            }
            else if (TryResolveInstalledSovereignIni(game, out sovereignIniPath, out sovereignAppId))
            {
                appId = sovereignAppId.ToString(CultureInfo.InvariantCulture);
                AddExistingPath(paths, sovereignIniPath);
            }

            if (TryResolveNemirtingasPaths(game, out var epicSchemaPath, out var epicSavePath))
            {
                AddExistingPath(paths, epicSchemaPath);
                AddExistingPath(paths, epicSavePath);
            }

            if (string.IsNullOrWhiteSpace(appId))
            {
                if (TryGetLumaPlayIniPathOverride(game.Id, out var lumaOverridePath) &&
                    TryExtractUplayGameIdFromLumaPlayIni(lumaOverridePath, out lumaAppId))
                {
                    appId = lumaAppId.ToString(CultureInfo.InvariantCulture);
                    AddExistingPath(paths, lumaOverridePath);
                }
                else if (TryGetExpandedFolderOverride(game, out var overriddenFolderPath))
                {
                    if (TryFindTenokeIniPathFromFolder(overriddenFolderPath, out var tenokePath) &&
                             TryExtractAppIdFromTenokeIni(tenokePath, out var tenokeAppId))
                    {
                        appId = tenokeAppId.ToString(CultureInfo.InvariantCulture);
                        AddExistingPath(paths, tenokePath);
                    }
                }
            }

            if (TryGetCustomSchemaEnabledOverride(game.Id, out var customSchemaEnabled) &&
                customSchemaEnabled &&
                TryGetCustomSchemaPathOverride(game.Id, out var customSchemaPath))
            {
                AddExistingPath(paths, customSchemaPath);
            }

            if (TryResolveSantodanLocalFolder(game, appId, out var localFolderPath, out _, out _, out _, directoryCache) &&
                !string.IsNullOrWhiteSpace(localFolderPath))
            {
                var jsonPath = ResolveAchievementFilePath(localFolderPath, "achievements.json");
                AddExistingPath(paths, jsonPath);
                if (!string.IsNullOrEmpty(jsonPath)) observeAchievementFile?.Invoke(jsonPath);
                foreach (var localIniFileName in LocalAchievementIniFileNames)
                {
                    var iniPath = ResolveAchievementFilePath(localFolderPath, localIniFileName);
                    AddExistingPath(paths, iniPath);
                    if (!string.IsNullOrEmpty(iniPath)) observeAchievementFile?.Invoke(iniPath);
                }
            }

            if (int.TryParse(appId, out var appIdInt) && appIdInt > 0)
            {
                foreach (var path in GetSteamAppCacheSchemaFilePaths(appIdInt))
                {
                    AddExistingPath(paths, path);
                }

                foreach (var path in GetSteamAppCacheUserStatsFilePaths(appIdInt, game))
                {
                    AddExistingPath(paths, path);
                }

                foreach (var path in GetSteamLibraryCacheFilePaths(appIdInt, game))
                {
                    AddExistingPath(paths, path);
                }

                var steamLocalProgress = TryGetSteamLocalProgressSummary(appIdInt, game);
                AddExistingPath(paths, steamLocalProgress?.SourcePath);
            }

            if (paths.Count == 0)
            {
                return false;
            }

            fingerprint = string.Join("|", paths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(BuildFileFingerprint));
            // Settings can alter correlation, selected users, exclusions and definitions even
            // when the underlying files have not changed. Never skip those transitions.
            fingerprint += "|configuration=" + GetSantodanPollingConfiguration(game);
            return !string.IsNullOrWhiteSpace(fingerprint);
        }


        private string GetSantodanPollingConfiguration(Game game) =>
            PlayniteAchievements.Services.InGameMonitoring.SantodanPollingConfiguration.Build(
                ProviderRegistry.Settings<LocalSettings>(), game.Id,
                _pluginSettings?.Persisted?.ExtraLocalPaths,
                _pluginSettings?.Persisted?.ExcludedLocalPaths, game.InstallDirectory, game.Name,
                _pluginSettings?.Persisted?.PreserveCachedUnlocksOnRefresh == true);

        private IEnumerable<string> GetSantodanInstallLocalAchievementFolders(Game game,
            PlayniteAchievements.Services.InGameMonitoring.LocalPollingDirectoryCache directoryCache = null)
        {
            if (game == null)
            {
                yield break;
            }

            var installDirectory = PathExpansion.ExpandGamePath(_api, game, game.InstallDirectory)?.Trim();
            var excludedPaths = GetExcludedLocalPathEntries(game).ToList();
            if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory) ||
                IsPathExcludedFromLocalScan(installDirectory, excludedPaths))
            {
                yield break;
            }

            if (HasLocalAchievementFiles(installDirectory))
            {
                yield return installDirectory;
            }

            // Include expanded exclusions in the key so settings changes cannot reuse stale topology.
            var topologyKey = JsonConvert.SerializeObject(new { installDirectory, excludedPaths });
            var parents = directoryCache == null
                ? EnumerateSantodanSteamSettingsParents(installDirectory, excludedPaths)
                : directoryCache.GetOrDiscover(topologyKey, observe =>
                    EnumerateSantodanSteamSettingsParents(installDirectory, excludedPaths, observe));
            foreach (var folder in parents)
            {
                if (HasLocalAchievementFiles(folder))
                {
                    yield return folder;
                }
            }
        }

        private static IEnumerable<string> EnumerateSantodanSteamSettingsParents(string root, IReadOnlyList<string> excludedPaths, Action<string> observeDirectory = null)
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                string[] children;
                try
                {
                    observeDirectory?.Invoke(current);
                    children = Directory.GetDirectories(current);
                }
                catch
                {
                    observeDirectory?.Invoke(null);
                    continue;
                }

                foreach (var child in children)
                {
                    try
                    {
                        if (IsPathExcludedFromLocalScan(child, excludedPaths) ||
                            (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        observeDirectory?.Invoke(null);
                        continue;
                    }

                    if (string.Equals(Path.GetFileName(child), "steam_settings", StringComparison.OrdinalIgnoreCase))
                    {
                        // The existing resolver understands steam_settings and its settings/stats
                        // children relative to the containing folder. The root was checked above.
                        if (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
                        {
                            yield return current;
                        }
                        continue;
                    }

                    pending.Push(child);
                }
            }
        }


        private bool TryResolveSantodanLocalFolder(
            Game game,
            string appId,
            out string folderPath,
            out IReadOnlyList<string> candidateFolders,
            out bool isOverridden,
            out bool isAmbiguous,
            PlayniteAchievements.Services.InGameMonitoring.LocalPollingDirectoryCache directoryCache = null)
        {
            using var pollingStage = PlayniteAchievements.Services.Logging.PollingDebugLog.Stage("local.folder-discovery");
            folderPath = null;
            candidateFolders = Array.Empty<string>();
            isOverridden = false;
            isAmbiguous = false;

            var candidates = string.IsNullOrWhiteSpace(appId)
                ? new List<string>()
                : FindLocalFolders(appId, game);
            foreach (var installLocalFolder in GetSantodanInstallLocalAchievementFolders(game, directoryCache))
            {
                if (!candidates.Contains(installLocalFolder, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(installLocalFolder);
                }
            }

            if (game != null && TryGetExpandedFolderOverride(game, out var overriddenFolderPath))
            {
                if (Directory.Exists(overriddenFolderPath) || File.Exists(overriddenFolderPath))
                {
                    folderPath = overriddenFolderPath;
                    candidateFolders = candidates;
                    isOverridden = true;
                    isAmbiguous = candidates.Count > 1;
                    return true;
                }

                ForkLog.Wrap(_logger)?.Warn($"Local save folder or file override for '{game.Name}' no longer exists: {overriddenFolderPath}");
            }

            candidateFolders = candidates;

            if (candidates.Count == 0)
            {
                return false;
            }

            if (candidates.Count == 1)
            {
                folderPath = candidates[0];
                return true;
            }

            isAmbiguous = true;
            folderPath = ChooseBestLocalFolderCandidate(candidates);
            NotifyAmbiguousFolderSelection(game, appId, candidates, folderPath);
            return !string.IsNullOrWhiteSpace(folderPath);
        }

    }
}
