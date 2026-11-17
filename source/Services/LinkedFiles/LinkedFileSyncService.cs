using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.LinkedFiles
{
    public enum LinkedFileSyncReason
    {
        Refresh,
        SessionEnd,
        UserRequest
    }

    /// <summary>A read and match without writing, for the mapping window.</summary>
    public sealed class LinkedFilePreview
    {
        public LinkedFileReadResult Read { get; set; }

        public LinkedFileMatchResult Match { get; set; }

        public LinkedFileApplyPlan Plan { get; set; }

        public IReadOnlyList<AchievementDetail> Achievements { get; set; } = Array.Empty<AchievementDetail>();
    }

    /// <summary>
    /// Reads linked files and writes what they say: unlocks for custom achievements onto their
    /// definitions, unlocks for linked definitions onto the manual link (then re-projected onto
    /// the cache), progress for linked definitions onto the cache. The file wins for the rows it
    /// contains. A file that is missing, half-written or unreadable writes nothing.
    /// </summary>
    public sealed class LinkedFileSyncService
    {
        private readonly GameCustomDataStore _store;
        private readonly AchievementOverridesService _overrides;
        private readonly AchievementDataService _dataService;
        private readonly ICacheManager _cacheManager;
        private readonly ManualSourceRegistry _manualSources;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private readonly LinkedFileIcons _icons;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly ConcurrentDictionary<Guid, LinkedFileApplyReport> _lastReports =
            new ConcurrentDictionary<Guid, LinkedFileApplyReport>();

        /// <summary>Raised on the syncing thread after a game's file was read, with the game id.</summary>
        public event Action<Guid> ReportUpdated;

        public LinkedFileSyncService(
            GameCustomDataStore store,
            AchievementOverridesService overrides,
            AchievementDataService dataService,
            ICacheManager cacheManager,
            ManualSourceRegistry manualSources,
            PlayniteAchievementsSettings settings,
            ILogger logger,
            ManagedCustomIconService managedIcons = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _overrides = overrides ?? throw new ArgumentNullException(nameof(overrides));
            _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
            _cacheManager = cacheManager;
            _manualSources = manualSources;
            _settings = settings;
            _logger = logger;
            _icons = managedIcons == null ? null : new LinkedFileIcons(managedIcons, logger);
        }

        /// <summary>A file can be linked to a custom-tracked game or one with custom achievements.</summary>
        public static bool IsEligible(GameCustomDataFile data)
        {
            // A stored link stays eligible on its own, so a file linked to a game with no
            // achievements yet can create them.
            return data != null &&
                   (data.ManualLink != null ||
                    data.LinkedFile != null ||
                    CustomAchievementProjectionService.HasCustomAchievements(data));
        }

        public IReadOnlyList<Guid> FindLinkedGameIds()
        {
            return _store.QueryAll(records => records
                .Where(record => record?.LinkedFile?.HasFilePath == true && IsEligible(record))
                .Select(record => record.PlayniteGameId)
                .Where(id => id != Guid.Empty)
                .ToList());
        }

        public bool TryGetLastReport(Guid gameId, out LinkedFileApplyReport report)
        {
            return _lastReports.TryGetValue(gameId, out report);
        }

        /// <summary>
        /// Reads the linked files a refresh covers, off the calling thread. Returns the games
        /// whose data a file changed.
        /// </summary>
        public Task<IReadOnlyList<Guid>> RunPassAsync(LinkedFilePassScope scope, CancellationToken cancel)
        {
            if (scope == null)
            {
                return Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());
            }

            return Task.Run(async () =>
            {
                var linked = FindLinkedGameIds();
                if (linked.Count == 0)
                {
                    return (IReadOnlyList<Guid>)Array.Empty<Guid>();
                }

                IEnumerable<Guid> targets = scope.IsLibraryWide
                    ? linked
                    : linked.Intersect(scope.GameIds);
                if (scope.RespectExclusions)
                {
                    var excluded = GameCustomDataLookup.GetExcludedRefreshGameIds(_settings?.Persisted, _store);
                    targets = targets.Where(id => !excluded.Contains(id));
                }

                return await SyncGamesAsync(targets.ToList(), LinkedFileSyncReason.Refresh, cancel).ConfigureAwait(false);
            }, cancel);
        }

        /// <summary>Reads each game's linked file in turn; returns the games whose data a file changed.</summary>
        public async Task<IReadOnlyList<Guid>> SyncGamesAsync(IReadOnlyList<Guid> gameIds, LinkedFileSyncReason reason, CancellationToken cancel)
        {
            var changed = new List<Guid>();
            foreach (var gameId in gameIds ?? Array.Empty<Guid>())
            {
                cancel.ThrowIfCancellationRequested();
                var report = await Task.Run(() => SyncGame(gameId, reason), cancel).ConfigureAwait(false);
                if (report?.ChangedAnything == true)
                {
                    changed.Add(gameId);
                }
            }

            return changed;
        }

        /// <summary>Reads one game's linked file and writes what it says.</summary>
        public LinkedFileApplyReport SyncGame(Guid gameId, LinkedFileSyncReason reason)
        {
            _gate.Wait();
            try
            {
                return SyncGameCore(gameId, reason);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"[LinkedFile] Sync failed for game {gameId} ({reason}).");
                return Record(gameId, null, new LinkedFileApplyReport { Status = LinkedFileReadStatus.ReadFailed, ReadUtc = DateTime.UtcNow });
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>Reads and matches a candidate link without writing anything.</summary>
        public LinkedFilePreview Preview(Guid gameId, LinkedFileLink candidate)
        {
            var preview = new LinkedFilePreview();
            if (candidate?.Schema == null)
            {
                preview.Read = LinkedFileReadResult.Failed(LinkedFileReadStatus.SchemaInvalid, "The link has no mapping.");
                return preview;
            }

            preview.Read = LinkedFileTableReaders.Read(candidate.FilePath, candidate.Schema);
            var achievements = LoadAchievements(gameId);
            preview.Achievements = achievements;
            if (!preview.Read.Success)
            {
                return preview;
            }

            _store.TryLoad(gameId, out var data);
            preview.Match = LinkedFileMatcher.Match(preview.Read.Table, candidate.Schema, achievements, candidate.Pairings);
            preview.Plan = LinkedFileApplier.Build(
                preview.Read.Table,
                candidate.Schema,
                SafeDirectory(candidate.FilePath),
                achievements,
                data?.ManualLink,
                data?.CustomAchievements,
                DateTime.UtcNow,
                pairings: candidate.Pairings);
            return preview;
        }

        private LinkedFileApplyReport SyncGameCore(Guid gameId, LinkedFileSyncReason reason)
        {
            var nowUtc = DateTime.UtcNow;
            if (gameId == Guid.Empty || !_store.TryLoad(gameId, out var data) || data?.LinkedFile == null)
            {
                return new LinkedFileApplyReport { Status = LinkedFileReadStatus.NoFilePath, ReadUtc = nowUtc };
            }

            var link = data.LinkedFile;
            if (!IsEligible(data) || !link.HasFilePath)
            {
                return Record(gameId, link, new LinkedFileApplyReport { Status = LinkedFileReadStatus.NoFilePath, ReadUtc = nowUtc });
            }

            if (!LinkedFileSchemaValidator.IsValid(link.Schema))
            {
                return Record(gameId, link, new LinkedFileApplyReport { Status = LinkedFileReadStatus.SchemaInvalid, ReadUtc = nowUtc, FilePath = link.FilePath });
            }

            var read = LinkedFileTableReaders.Read(link.FilePath, link.Schema);
            if (!read.Success)
            {
                var failed = new LinkedFileApplyReport { Status = read.Status, ReadUtc = nowUtc, FilePath = link.FilePath };
                failed.AddMessage(read.Message);
                _logger?.Info($"[LinkedFile] game={gameId} reason={reason} {failed.Summarize()} message={read.Message}");
                return Record(gameId, link, failed);
            }

            var achievements = LoadAchievements(gameId);
            var plan = LinkedFileApplier.Build(
                read.Table,
                link.Schema,
                SafeDirectory(link.FilePath),
                achievements,
                data.ManualLink,
                data.CustomAchievements,
                nowUtc,
                _icons == null ? (Func<string, string, bool>)null : (stored, source) => _icons.IsSameIcon(gameId, stored, source),
                link.Pairings);
            plan.Report.FilePath = link.FilePath;
            foreach (var warning in read.Warnings)
            {
                plan.Report.AddMessage(warning);
            }

            if (plan.Report.Status == LinkedFileReadStatus.Ok)
            {
                ApplyPlan(gameId, plan, data.CustomAchievements);
            }

            var level = plan.Report.ChangedAnything || plan.Report.ParseErrors > 0 ? "Info" : "Debug";
            var line = $"[LinkedFile] game={gameId} reason={reason} {plan.Report.Summarize()}";
            if (level == "Info")
            {
                _logger?.Info(line);
            }
            else
            {
                _logger?.Debug(line);
            }

            return Record(gameId, link, plan.Report);
        }

        private void ApplyPlan(Guid gameId, LinkedFileApplyPlan plan, IReadOnlyList<CustomAchievementDefinition> previousCustom)
        {
            if (plan.CustomChanged)
            {
                _icons?.Materialize(gameId, previousCustom, plan.CustomDefinitions);
                _overrides.SetCustomAchievements(gameId, plan.CustomDefinitions);
            }

            // Progress lands on the cache first, so re-projecting the link below keeps it.
            if (plan.ManualProgress.Count > 0 && _cacheManager is IInGameProgressCacheWriter writer)
            {
                var observations = plan.ManualProgress
                    .Select(update => new AchievementProgressObservation
                    {
                        ApiName = update.ApiName,
                        Unlocked = false,
                        ProgressNum = update.ProgressNum,
                        ProgressDenom = update.ProgressDenom
                    })
                    .ToList();
                var result = writer.ApplyInGameProgress(gameId.ToString(), CustomProviders.CustomProviderKeys.FallbackProviderKey, observations);
                if (result?.Success != true)
                {
                    _logger?.Debug($"[LinkedFile] Progress for game {gameId} was not written: {result?.ErrorCode}.");
                }
            }

            if (plan.ManualChanged && _overrides.SetManualUnlockStates(gameId, plan.ManualUnlocks))
            {
                if (_store.TryLoad(gameId, out var updated) && updated?.ManualLink != null)
                {
                    var source = _manualSources?.GetSourceByKey(updated.ManualLink.SourceKey);
                    new ManualLinkCacheApplier(_dataService, _cacheManager, _settings, _logger).Apply(gameId, updated.ManualLink, source);
                }
            }
        }

        private LinkedFileApplyReport Record(Guid gameId, LinkedFileLink link, LinkedFileApplyReport report)
        {
            _lastReports[gameId] = report;
            var status = report.Status.ToString();
            if (link != null &&
                (!string.Equals(link.LastReadStatus, status, StringComparison.Ordinal) || report.ChangedAnything || !link.LastReadUtc.HasValue))
            {
                try
                {
                    _store.Update(
                        gameId,
                        record =>
                        {
                            if (record.LinkedFile == null)
                            {
                                return;
                            }

                            record.LinkedFile.LastReadUtc = report.ReadUtc;
                            record.LinkedFile.LastReadStatus = status;
                        },
                        affectsSummaryData: false,
                        affectsOverrideMirror: false);
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"[LinkedFile] Could not store the read status for game {gameId}.");
                }
            }

            try
            {
                ReportUpdated?.Invoke(gameId);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[LinkedFile] A report listener failed.");
            }

            return report;
        }

        private IReadOnlyList<AchievementDetail> LoadAchievements(Guid gameId)
        {
            try
            {
                return _dataService.GetGameAchievementData(gameId)?.Achievements?
                           .Where(achievement => achievement != null && !string.IsNullOrWhiteSpace(achievement.ApiName))
                           .ToList()
                       ?? (IReadOnlyList<AchievementDetail>)Array.Empty<AchievementDetail>();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[LinkedFile] Could not load achievements for game {gameId}.");
                return Array.Empty<AchievementDetail>();
            }
        }

        private static string SafeDirectory(string path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}
