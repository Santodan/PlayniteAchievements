using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Steam
{
    internal sealed class SteamHuntersCategoryEnricher
    {
        internal const string BaseCategoryType = "Base";
        internal const string DlcCategoryType = "DLC";
        internal const string UpdateCategoryType = "Update";
        internal const string MultiplayerCategoryType = "Multiplayer";

        // The group name the includeMultiplayerGroup request flag gives base-game achievements
        // tagged Multiplayer.
        private const string MultiplayerGroupName = "Multiplayer";

        // Enrichment is best-effort: after consecutive failed fetches, stop calling out for the
        // rest of the session (until ClearCache) so an unreachable steamhunters.com cannot stall
        // every game's scan.
        private const int MaxConsecutiveFailures = 2;

        private readonly SteamHuntersApiClient _apiClient;
        private readonly ILogger _logger;
        private readonly Func<DiskImageService> _diskImageServiceResolver;
        private readonly object _cacheLock = new object();
        private readonly Dictionary<int, Task<SteamHuntersAchievementGroupsResponse>> _groupsByAppId =
            new Dictionary<int, Task<SteamHuntersAchievementGroupsResponse>>();
        private readonly Dictionary<int, Task<List<SteamHuntersAchievement>>> _achievementsByAppId =
            new Dictionary<int, Task<List<SteamHuntersAchievement>>>();
        private int _consecutiveFailures;

        public SteamHuntersCategoryEnricher(
            SteamHuntersApiClient apiClient,
            ILogger logger,
            Func<DiskImageService> diskImageServiceResolver = null)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _logger = logger;
            _diskImageServiceResolver = diskImageServiceResolver;
        }

        public void ClearCache()
        {
            lock (_cacheLock)
            {
                _groupsByAppId.Clear();
                _achievementsByAppId.Clear();
            }

            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }

        public async Task EnrichAsync(
            int appId,
            string gameName,
            IList<AchievementDetail> achievements,
            Guid? playniteGameId,
            CancellationToken cancel)
        {
            if (appId <= 0 || achievements == null || achievements.Count == 0)
            {
                return;
            }

            SteamHuntersAchievementGroupsResponse response;
            try
            {
                response = await GetGroupsAsync(appId, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[SteamHunters] Failed to enrich categories for appId={appId}.");
                return;
            }

            if (response == null)
            {
                return;
            }

            ApplyGroups(achievements, response.Groups, response.GroupBy, gameName);

            // After ApplyGroups, which resets every achievement's type, so the Unobtainable tag
            // is re-derived on each refresh rather than accumulating.
            var obtainability = await GetAchievementsAsync(appId, cancel).ConfigureAwait(false);
            ApplyObtainability(achievements, obtainability);

            if (playniteGameId.HasValue && playniteGameId.Value != Guid.Empty)
            {
                await DownloadCategoryArtImagesAsync(playniteGameId.Value, response, gameName, appId, cancel)
                    .ConfigureAwait(false);
            }
        }

        // Plans one default art file per category: (normalized category label -> Steam appId).
        // Any group carrying a DlcAppId (a DLC launch or a DLC update) maps to that dlcAppId;
        // every other labeled group (the base category, base-game update groups, collection
        // sub-games) maps to the game's own appId so non-DLC categories share the base banner.
        // Dedupe is first-wins by label to match the
        // assignment order in ApplyGroups, with the base entry first so a DLC label
        // colliding with the game name cannot hijack it.
        internal static IReadOnlyList<KeyValuePair<string, int>> BuildCategoryImagePlan(
            IList<SteamHuntersAchievementGroup> groups,
            string groupBy,
            string gameName = null,
            int appId = 0)
        {
            var plan = new List<KeyValuePair<string, int>>();
            var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var baseLabel = string.IsNullOrWhiteSpace(gameName) ? null : gameName.Trim();
            if (baseLabel != null && appId > 0 && seenLabels.Add(baseLabel))
            {
                plan.Add(new KeyValuePair<string, int>(
                    AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(baseLabel),
                    appId));
            }

            if (groups == null || groups.Count == 0)
            {
                return plan;
            }

            foreach (var group in groups)
            {
                // Multiplayer achievements share the base category, which the base entry covers.
                if (IsMultiplayerGroup(groupBy, group))
                {
                    continue;
                }

                // DLC art comes from the DLC's own appId whenever a group carries one -- launch or
                // update alike -- while the "game" grouping mode always uses the base banner. Keyed
                // on the DlcAppId signal directly so DLC-update groups ("DLC|Update") still map to
                // the DLC rather than falling back to the base game.
                var isDlc = !string.Equals(groupBy, "game", StringComparison.OrdinalIgnoreCase) &&
                            group?.DlcAppId > 0;
                var entryAppId = isDlc ? group.DlcAppId.Value : appId;
                if (entryAppId <= 0)
                {
                    continue;
                }

                var label = NormalizeGroupLabel(group, gameName);
                if (label == null || !seenLabels.Add(label))
                {
                    continue;
                }

                plan.Add(new KeyValuePair<string, int>(
                    AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(label),
                    entryAppId));
            }

            return plan;
        }

        // Downloads default category art (base game + DLC groups) to deterministic per-game
        // cache paths. Best-effort: failures never fail enrichment and never count toward
        // the SteamHunters fetch backoff. Existing targets are skipped, so re-scans cost
        // nothing.
        private async Task DownloadCategoryArtImagesAsync(
            Guid playniteGameId,
            SteamHuntersAchievementGroupsResponse response,
            string gameName,
            int appId,
            CancellationToken cancel)
        {
            var diskImageService = _diskImageServiceResolver?.Invoke();
            if (diskImageService == null)
            {
                return;
            }

            var plan = BuildCategoryImagePlan(response?.Groups, response?.GroupBy, gameName, appId);
            if (plan.Count == 0)
            {
                return;
            }

            var gameIdText = playniteGameId.ToString("D");
            foreach (var entry in plan)
            {
                cancel.ThrowIfCancellationRequested();
                var label = entry.Key;
                var entryAppId = entry.Value;
                try
                {
                    var artTarget = diskImageService.GetDefaultCategoryImagePath(
                        gameIdText, label);
                    // Wide banner art is preferred for visual consistency across categories:
                    // static header, then the appdetails content-hashed header (newer apps
                    // serve art only from hashed store_item_assets URLs that cannot be derived
                    // from the appId), then portrait library art as the last downloadable tier.
                    // decodeSize 0 stores the original bytes: no square crop, original aspect.
                    var artResult = await diskImageService.GetOrDownloadIconToPathAsync(
                        SteamImageUrls.Header(entryAppId), artTarget, decodeSize: 0, cancel).ConfigureAwait(false);
                    if (artResult == null)
                    {
                        var storeUrls = await SteamImageUrls
                            .GetStoreFallbackAsync(entryAppId, cancel, _logger)
                            .ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(storeUrls?.CoverUrl))
                        {
                            artResult = await diskImageService.GetOrDownloadIconToPathAsync(
                                storeUrls.CoverUrl, artTarget, decodeSize: 0, cancel).ConfigureAwait(false);
                        }
                    }

                    if (artResult == null)
                    {
                        await diskImageService.GetOrDownloadIconToPathAsync(
                            SteamImageUrls.Cover(entryAppId), artTarget, decodeSize: 0, cancel).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"[SteamHunters] Default category art download failed for appId={entryAppId}.");
                }
            }
        }

        internal static int ApplyGroups(
            IList<AchievementDetail> achievements,
            IList<SteamHuntersAchievementGroup> groups,
            string groupBy = null,
            string gameName = null)
        {
            if (achievements == null || achievements.Count == 0)
            {
                return 0;
            }

            var baseFallbackLabel = string.IsNullOrWhiteSpace(gameName) ? null : gameName.Trim();

            var achievementsByApiName = achievements
                .Where(achievement => !string.IsNullOrWhiteSpace(achievement?.ApiName))
                .GroupBy(achievement => achievement.ApiName.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

            if (achievementsByApiName.Count == 0)
            {
                return 0;
            }

            var updated = 0;
            foreach (var achievement in achievements)
            {
                if (achievement == null)
                {
                    continue;
                }

                if (SetCategory(achievement, BaseCategoryType, baseFallbackLabel))
                {
                    updated++;
                }
            }

            var assignedApiNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < (groups?.Count ?? 0); i++)
            {
                var group = groups[i];
                var isMultiplayer = IsMultiplayerGroup(groupBy, group);
                var type = isMultiplayer
                    ? AchievementCategoryTypeHelper.Combine(new[] { BaseCategoryType, MultiplayerCategoryType })
                    : ResolveCategoryType(groupBy, group);
                // Multiplayer achievements are still base-game achievements, so they keep the
                // base category label rather than becoming a "Multiplayer" category.
                var label = isMultiplayer
                    ? baseFallbackLabel
                    : NormalizeGroupLabel(group, gameName)
                        ?? (string.Equals(type, BaseCategoryType, StringComparison.Ordinal)
                            ? baseFallbackLabel
                            : null);

                foreach (var apiName in group?.AchievementApiNames ?? Enumerable.Empty<string>())
                {
                    var normalizedApiName = NormalizeApiName(apiName);
                    if (string.IsNullOrWhiteSpace(normalizedApiName) ||
                        !assignedApiNames.Add(normalizedApiName) ||
                        !achievementsByApiName.TryGetValue(normalizedApiName, out var matches))
                    {
                        continue;
                    }

                    foreach (var achievement in matches)
                    {
                        if (SetCategory(achievement, type, label))
                        {
                            updated++;
                        }
                    }
                }
            }

            return updated;
        }

        // Adds Unobtainable to achievements SteamHunters' moderators mark Unobtainable. Broken-but-
        // obtainable and conditionally obtainable achievements can still be earned, so they get
        // no type.
        internal static int ApplyObtainability(
            IList<AchievementDetail> achievements,
            IList<SteamHuntersAchievement> steamHuntersAchievements)
        {
            if (achievements == null || achievements.Count == 0 ||
                steamHuntersAchievements == null || steamHuntersAchievements.Count == 0)
            {
                return 0;
            }

            var unobtainableApiNames = new HashSet<string>(
                steamHuntersAchievements
                    .Where(item => item?.Obtainability == SteamHuntersObtainability.Unobtainable)
                    .Select(item => NormalizeApiName(item.ApiName))
                    .Where(apiName => apiName != null),
                StringComparer.OrdinalIgnoreCase);
            if (unobtainableApiNames.Count == 0)
            {
                return 0;
            }

            var updated = 0;
            foreach (var achievement in achievements)
            {
                var apiName = NormalizeApiName(achievement?.ApiName);
                if (apiName == null || !unobtainableApiNames.Contains(apiName))
                {
                    continue;
                }

                var combined = AchievementCategoryTypeHelper.Combine(
                    new[] { achievement.CategoryType, AchievementCategoryTypeHelper.UnobtainableCategoryType });
                if (!string.Equals(achievement.CategoryType, combined, StringComparison.Ordinal))
                {
                    achievement.CategoryType = combined;
                    updated++;
                }
            }

            return updated;
        }

        // The includeMultiplayerGroup group only ever holds base-game achievements, so it has no
        // DlcAppId; the "game" grouping mode never produces it.
        internal static bool IsMultiplayerGroup(string groupBy, SteamHuntersAchievementGroup group)
        {
            return group != null &&
                   !group.DlcAppId.HasValue &&
                   !string.Equals(groupBy, "game", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(group.Name?.Trim(), MultiplayerGroupName, StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveCategoryType(string groupBy, SteamHuntersAchievementGroup group)
        {
            if (string.Equals(groupBy, "game", StringComparison.OrdinalIgnoreCase))
            {
                return BaseCategoryType;
            }

            // Two independent signals. A DlcAppId means the achievements belong to a separate
            // DLC product (DLC) rather than the base game (Base). A group Name means they were
            // added by a post-launch update (Update). So a DLC that received an update types as
            // "DLC|Update" and a base-game update as "Base|Update"; Combine emits canonical order
            // (Base/DLC precede Update).
            var ownerType = group?.DlcAppId.HasValue == true ? DlcCategoryType : BaseCategoryType;
            return string.IsNullOrWhiteSpace(group?.Name)
                ? ownerType
                : AchievementCategoryTypeHelper.Combine(new[] { ownerType, UpdateCategoryType });
        }

        private Task<SteamHuntersAchievementGroupsResponse> GetGroupsAsync(
            int appId,
            CancellationToken cancel)
        {
            lock (_cacheLock)
            {
                if (!_groupsByAppId.TryGetValue(appId, out var task))
                {
                    task = FetchGroupsBoundedAsync(appId, cancel);
                    _groupsByAppId[appId] = task;
                }

                return task;
            }
        }

        private async Task<SteamHuntersAchievementGroupsResponse> FetchGroupsBoundedAsync(
            int appId,
            CancellationToken cancel)
        {
            if (Volatile.Read(ref _consecutiveFailures) >= MaxConsecutiveFailures)
            {
                return null;
            }

            try
            {
                var response = await _apiClient
                    .GetAchievementGroupsAsync(appId, cancel)
                    .ConfigureAwait(false);
                if (response != null)
                {
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    return response;
                }

                RecordFailure();
                return null;
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[SteamHunters] Group fetch failed for appId={appId}; category enrichment skipped.");
                RecordFailure();
                return null;
            }
        }

        // Cached per app like the groups. Obtainability is optional enrichment on top of the
        // groups, so a failure here is logged by the client and never counts toward the fetch
        // backoff; the backoff does skip it once groups have stopped loading.
        private Task<List<SteamHuntersAchievement>> GetAchievementsAsync(
            int appId,
            CancellationToken cancel)
        {
            lock (_cacheLock)
            {
                if (!_achievementsByAppId.TryGetValue(appId, out var task))
                {
                    task = FetchAchievementsAsync(appId, cancel);
                    _achievementsByAppId[appId] = task;
                }

                return task;
            }
        }

        private async Task<List<SteamHuntersAchievement>> FetchAchievementsAsync(
            int appId,
            CancellationToken cancel)
        {
            if (Volatile.Read(ref _consecutiveFailures) >= MaxConsecutiveFailures)
            {
                return null;
            }

            try
            {
                return await _apiClient.GetAchievementsAsync(appId, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[SteamHunters] Obtainability fetch failed for appId={appId}.");
                return null;
            }
        }

        private void RecordFailure()
        {
            if (Interlocked.Increment(ref _consecutiveFailures) == MaxConsecutiveFailures)
            {
                _logger?.Warn("[SteamHunters] Skipping SteamHunters category enrichment for the rest of this session after repeated failures.");
            }
        }

        private static bool SetCategory(
            AchievementDetail achievement,
            string categoryType,
            string category)
        {
            if (achievement == null)
            {
                return false;
            }

            var normalizedType = string.IsNullOrWhiteSpace(categoryType) ? null : categoryType.Trim();
            var normalizedCategory = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
            var changed =
                !string.Equals(achievement.CategoryType, normalizedType, StringComparison.Ordinal) ||
                !string.Equals(achievement.Category, normalizedCategory, StringComparison.Ordinal);

            achievement.CategoryType = normalizedType;
            achievement.Category = normalizedCategory;
            return changed;
        }

        private static string NormalizeApiName(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        // A group carries two independent names: the DLC it belongs to and, for a post-launch
        // update, the update's own name. Both are present together - the Binding of Isaac's
        // "1.06.0059" is an Afterbirth update, and The Witcher 3's "Gwent" belongs to Hearts of
        // Stone - so taking the update name alone left bare version strings sitting at the root
        // with no sign of which DLC they came from, and updates to different DLC as siblings.
        //
        // A base-game update has no DlcAppName and stays a single segment, as before.
        private static string NormalizeGroupLabel(SteamHuntersAchievementGroup group, string gameName)
        {
            var dlc = StripGameNamePrefix(group?.DlcAppName?.Trim(), gameName);
            var update = StripGameNamePrefix(group?.Name?.Trim(), gameName);

            return CategoryPathHelper.JoinRaw(dlc, update);
        }

        // Steam store DLC names usually repeat the game name ("Cyberpunk 2077: Phantom
        // Liberty"); drop that prefix so category labels read as just the DLC/update name.
        // Only strips when a separator follows the game name, so labels that merely start
        // with the game name ("Cyberpunk 2077 Ultimate") are left alone.
        internal static string StripGameNamePrefix(string label, string gameName)
        {
            var normalizedGameName = gameName?.Trim();
            if (string.IsNullOrWhiteSpace(label) ||
                string.IsNullOrWhiteSpace(normalizedGameName) ||
                label.Length <= normalizedGameName.Length ||
                !label.StartsWith(normalizedGameName, StringComparison.OrdinalIgnoreCase))
            {
                return label;
            }

            var remainder = label.Substring(normalizedGameName.Length).TrimStart();
            if (remainder.Length == 0 || (remainder[0] != ':' && remainder[0] != '-' && remainder[0] != '–' && remainder[0] != '—'))
            {
                return label;
            }

            var stripped = remainder.TrimStart(':', '-', '–', '—', ' ', '\t');
            return string.IsNullOrWhiteSpace(stripped) ? label : stripped;
        }
    }
}
