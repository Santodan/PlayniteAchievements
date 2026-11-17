using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.UI;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Captures
{
    /// <summary>One achievement's capture files, as the plugin shows them.</summary>
    internal sealed class CaptureIndexRow
    {
        public string ApiName { get; set; }
        public string CleanPath { get; set; }
        public string NotificationPath { get; set; }
        public string FramedPath { get; set; }
        public string VideoPath { get; set; }

        public string ToSignature() =>
            string.Join("\n", ApiName, CleanPath, NotificationPath, FramedPath, VideoPath);
    }

    /// <summary>
    /// Keeps the database's capture index in step with the capture folders, so the resolved views
    /// carry each achievement's capture paths. Captures are matched to achievements the way every
    /// surface matches them (<see cref="AchievementCapturePathResolver"/>), over the game's rows as
    /// the plugin shows them.
    /// </summary>
    internal sealed class CaptureIndexSync : IDisposable
    {
        private readonly CaptureLibraryService _library;
        private readonly CacheManager _cache;
        private readonly Func<Guid, GameAchievementData> _loadShown;
        private readonly ILogger _logger;

        public CaptureIndexSync(
            CaptureLibraryService library,
            CacheManager cache,
            Func<Guid, GameAchievementData> loadShown,
            ILogger logger)
        {
            _library = library;
            _cache = cache;
            _loadShown = loadShown;
            _logger = logger;
            if (_library != null)
            {
                _library.CapturesChanged += OnCapturesChanged;
            }
        }

        /// <summary>Re-indexes every game whose folder holds captures, and drops the rest, off the caller's thread.</summary>
        public Task SyncAllAsync() => Task.Run(() => Run(SyncAll, "all games"));

        /// <summary>Re-indexes one game, off the caller's thread.</summary>
        public void QueueGame(Guid gameId)
        {
            if (gameId != Guid.Empty)
            {
                _ = Task.Run(() => Run(() => SyncGame(gameId), gameId.ToString()));
            }
        }

        public void Dispose()
        {
            if (_library != null)
            {
                _library.CapturesChanged -= OnCapturesChanged;
            }
        }

        private void OnCapturesChanged(object sender, CapturesChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(e?.GameName) && string.IsNullOrWhiteSpace(e?.FolderName))
            {
                _ = SyncAllAsync();
                return;
            }

            var folder = e.FolderName ?? UnlockScreenshotService.SanitizeCaptureGameName(e.GameName);
            foreach (var gameId in GamesInFolder(folder))
            {
                QueueGame(gameId);
            }
        }

        private void SyncAll()
        {
            var folders = new HashSet<string>(_library?.GetGameFoldersWithCaptures() ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var withCaptures = new HashSet<Guid>();
            foreach (var header in Headers())
            {
                var gameId = header.PlayniteGameId ?? Guid.Empty;
                if (gameId != Guid.Empty && folders.Contains(UnlockScreenshotService.SanitizeCaptureGameName(header.GameName ?? header.Game?.Name)))
                {
                    withCaptures.Add(gameId);
                    SyncGame(gameId);
                }
            }

            _cache?.RemoveAchievementCapturesExcept(withCaptures);
        }

        private void SyncGame(Guid gameId)
        {
            // A game whose folder holds no captures needs no full load; its rows, if any, go.
            var header = _cache?.LoadGameData(gameId.ToString());
            if (_library == null || !_library.GameFolderHasCaptures(header?.GameName ?? header?.Game?.Name))
            {
                _cache?.ReplaceAchievementCaptures(gameId, new List<CaptureIndexRow>());
                return;
            }

            var data = _loadShown?.Invoke(gameId);
            var set = AchievementCapturePathResolver.ResolveGameSet(data);
            var rows = new List<CaptureIndexRow>();
            foreach (var achievement in data?.Achievements ?? new List<AchievementDetail>())
            {
                if (set == null || string.IsNullOrWhiteSpace(achievement?.ApiName))
                {
                    continue;
                }

                var stamp = AchievementCapturePathResolver.ResolvePaths(set, achievement.DisplayName);
                if (stamp.Clean != null || stamp.Notification != null || stamp.Framed != null || stamp.Video != null)
                {
                    rows.Add(new CaptureIndexRow
                    {
                        ApiName = achievement.ApiName.Trim(),
                        CleanPath = stamp.Clean,
                        NotificationPath = stamp.Notification,
                        FramedPath = stamp.Framed,
                        VideoPath = stamp.Video
                    });
                }
            }

            _cache?.ReplaceAchievementCaptures(gameId, rows);
        }

        private IEnumerable<Guid> GamesInFolder(string folder) =>
            Headers()
                .Where(header => string.Equals(
                    UnlockScreenshotService.SanitizeCaptureGameName(header.GameName ?? header.Game?.Name),
                    folder,
                    StringComparison.OrdinalIgnoreCase))
                .Select(header => header.PlayniteGameId ?? Guid.Empty)
                .Where(id => id != Guid.Empty)
                .ToList();

        private IEnumerable<GameAchievementData> Headers() =>
            (_cache as ICacheReadOptimizations)?.LoadAllGameHeadersFast() ?? new List<GameAchievementData>();

        private void Run(Action action, string what)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[Captures] Failed to update the capture index for {what}.");
            }
        }
    }
}
