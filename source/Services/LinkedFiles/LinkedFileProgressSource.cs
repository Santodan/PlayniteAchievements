using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.LinkedFiles
{
    /// <summary>
    /// A running game's linked file as the monitor's fast source: the file is watched, and each
    /// change reports the rows that are unlocked or carry progress. Reports unlocks only; a row
    /// that locks an achievement waits for the session-end read, which is authoritative.
    /// </summary>
    internal sealed class LinkedFileProgressSource : IInGameProgressSource
    {
        public const string SourceKey = "LinkedFile";

        private readonly GameCustomDataStore _store;
        private readonly Func<Guid, IReadOnlyList<AchievementDetail>> _loadAchievements;
        private readonly ILogger _logger;

        internal sealed class SourceState
        {
            public LinkedFileLink Link { get; set; }

            public string Directory { get; set; }

            public long LastLength { get; set; } = -1;

            public DateTime LastWriteUtc { get; set; }

            public bool HasRead { get; set; }
        }

        public LinkedFileProgressSource(
            GameCustomDataStore store,
            Func<Guid, IReadOnlyList<AchievementDetail>> loadAchievements,
            ILogger logger)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _loadAchievements = loadAchievements ?? (_ => Array.Empty<AchievementDetail>());
            _logger = logger;
        }

        public InGameProgressRegistration TryRegister(Game game, GameAchievementData cachedSchema)
        {
            if (game == null || game.Id == Guid.Empty ||
                !_store.TryLoad(game.Id, out var data) ||
                !LinkedFileSyncService.IsEligible(data))
            {
                return null;
            }

            var link = data.LinkedFile;
            if (link?.HasFilePath != true || !LinkedFileSchemaValidator.IsValid(link.Schema))
            {
                return null;
            }

            // The file need not exist yet: some programs write it only at the first unlock. The
            // watcher and the safety re-read pick it up when it appears.
            string directory;
            try
            {
                directory = Path.GetDirectoryName(Path.GetFullPath(link.FilePath));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return null;
            }

            return new InGameProgressRegistration
            {
                ProviderKey = SourceKey,
                WatchTargets = new[] { link.FilePath },
                PollInterval = InGameProgressRegistration.FileWatchSafetyPollInterval,
                // The file's own stamps come from another program's clock, so a clip anchors on
                // when the change was seen.
                UnlockAnchorPolicy = InGameUnlockAnchorPolicy.SourceObservation,
                State = new SourceState { Link = link.Clone(), Directory = directory }
            };
        }

        public Task<IReadOnlyList<InGameProgressQueryResult>> QueryAsync(
            IReadOnlyList<InGameTrackingContext> games,
            CancellationToken cancellationToken)
        {
            return Task.Run<IReadOnlyList<InGameProgressQueryResult>>(() =>
            {
                var results = new List<InGameProgressQueryResult>();
                foreach (var context in games ?? Array.Empty<InGameTrackingContext>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var gameId = context?.Game?.Id ?? Guid.Empty;
                    if (gameId == Guid.Empty || !(context.Registration?.State is SourceState state))
                    {
                        continue;
                    }

                    results.Add(Query(gameId, state));
                }

                return results;
            }, cancellationToken);
        }

        private InGameProgressQueryResult Query(Guid gameId, SourceState state)
        {
            try
            {
                var info = new FileInfo(state.Link.FilePath);
                if (!info.Exists)
                {
                    // Nothing yet, which is a successful read of nothing: the first read is the
                    // silent baseline, so the unlocks in a file that appears later are announced.
                    state.LastLength = -1;
                    state.HasRead = false;
                    return InGameProgressQueryResult.Succeeded(gameId, Array.Empty<AchievementProgressObservation>(), isDelta: true);
                }

                // The safety re-read runs every half second; an untouched file has nothing new.
                if (state.HasRead && info.Length == state.LastLength && info.LastWriteTimeUtc == state.LastWriteUtc)
                {
                    return InGameProgressQueryResult.Succeeded(gameId, Array.Empty<AchievementProgressObservation>(), isDelta: true);
                }

                var read = LinkedFileTableReaders.Read(state.Link.FilePath, state.Link.Schema);
                if (!read.Success)
                {
                    return InGameProgressQueryResult.Failed(gameId, read.Status.ToString());
                }

                var achievements = _loadAchievements(gameId) ?? Array.Empty<AchievementDetail>();
                var match = LinkedFileMatcher.Match(read.Table, state.Link.Schema, achievements, state.Link.Pairings);
                var observations = new List<AchievementProgressObservation>();
                var nowUtc = DateTime.UtcNow;
                foreach (var rowMatch in match.Matches)
                {
                    var values = LinkedFileRowValues.Parse(read.Table, rowMatch.RowIndex, state.Link.Schema, state.Directory, nowUtc);
                    if (values.Errors.Count > 0)
                    {
                        continue;
                    }

                    var unlocked = values.HasUnlockInfo && values.Unlocked;
                    if (!unlocked && !values.ProgressNum.HasValue && !values.ProgressDenom.HasValue)
                    {
                        continue;
                    }

                    observations.Add(new AchievementProgressObservation
                    {
                        ApiName = rowMatch.Achievement.ApiName,
                        Unlocked = unlocked,
                        UnlockTimeUtc = unlocked ? values.UnlockTimeUtc : null,
                        ProgressNum = values.ProgressNum,
                        ProgressDenom = values.ProgressDenom
                    });
                }

                state.LastLength = info.Length;
                state.LastWriteUtc = info.LastWriteTimeUtc;
                state.HasRead = true;
                return InGameProgressQueryResult.Succeeded(gameId, observations);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[LinkedFile] In-game read failed for game {gameId}.");
                return InGameProgressQueryResult.Failed(gameId, LinkedFileReadStatus.ReadFailed.ToString());
            }
        }
    }
}
