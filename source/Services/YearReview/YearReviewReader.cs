using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.YearReview;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Overview;

namespace PlayniteAchievements.Services.YearReview
{
    /// <summary>What the review keeps from the Overview snapshot; the snapshot itself is not retained.</summary>
    internal sealed class YearReviewLibrary
    {
        public IReadOnlyList<UnlockFact> Facts { get; set; } = Array.Empty<UnlockFact>();
        public IReadOnlyList<YearReviewGame> Games { get; set; } = Array.Empty<YearReviewGame>();
        public IReadOnlyList<int> Years { get; set; } = Array.Empty<int>();
    }

    /// <summary>
    /// The review's reads: the Overview snapshot once per window, then one year's unlocks and
    /// friend unlocks per year shown. Every read is bounded to the year being reviewed.
    /// </summary>
    internal sealed class YearReviewReader
    {
        private const char Separator = '\u001f';

        private readonly AchievementDataService _data;
        private readonly IFriendCacheManager _friends;
        private readonly Func<CancellationToken, Task<OverviewDataSnapshot>> _snapshot;
        private readonly Func<PersistedSettings> _settings;
        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;

        public YearReviewReader(
            AchievementDataService data,
            IFriendCacheManager friends,
            Func<CancellationToken, Task<OverviewDataSnapshot>> snapshot,
            Func<PersistedSettings> settings,
            IPlayniteAPI api,
            ILogger logger)
        {
            _data = data;
            _friends = friends;
            _snapshot = snapshot;
            _settings = settings;
            _api = api;
            _logger = logger;
        }

        public async Task<YearReviewLibrary> LoadLibraryAsync(CancellationToken cancel)
        {
            var snapshot = _snapshot != null ? await _snapshot(cancel).ConfigureAwait(false) : null;
            if (snapshot == null)
            {
                return new YearReviewLibrary();
            }

            var detailOrder = ProgressDetailOrder.EnabledStyles(_settings?.Invoke()?.ProgressDetails);
            var finishes = TimelinePlatformSeries.CompletionFinishes(snapshot);
            var games = (snapshot.GameSummaries ?? new List<ViewModels.Items.GameSummaryItem>())
                .Where(game => game?.PlayniteGameId != null)
                .Select(game =>
                {
                    var id = game.PlayniteGameId.Value;
                    var finished = finishes.TryGetValue(id, out var finish) ? finish : (DateTime?)null;
                    return new YearReviewGame
                    {
                        GameId = id,
                        Name = game.GameName,
                        CoverPath = game.GameCoverPath,
                        FinishUtc = finished,
                        Badge = finished.HasValue ? game.CompletionBadgeFor(detailOrder) : CompletionBadgeKind.Completed
                    };
                })
                .ToList();
            var facts = (snapshot.Unlocks ?? Array.Empty<UnlockFact>()).ToList();

            return new YearReviewLibrary
            {
                Facts = facts,
                Games = games,
                Years = YearReviewBuilder.AvailableYears(facts.Where(fact => fact.Day.HasValue).Select(fact => fact.Day.Value))
            };
        }

        /// <summary>The inputs of one year's review. Runs off the UI thread.</summary>
        public YearReviewInputs ReadYear(YearReviewLibrary library, int year, DateTime today)
        {
            var first = new DateTime(year, 1, 1);
            var last = new DateTime(year, 12, 31);
            return new YearReviewInputs
            {
                Unlocks = ReadUnlocks(first, last),
                Facts = library?.Facts ?? Array.Empty<UnlockFact>(),
                Games = library?.Games ?? Array.Empty<YearReviewGame>(),
                FriendUnlocks = ReadFriendUnlocks(first),
                Today = today
            };
        }

        private IReadOnlyList<YearReviewUnlock> ReadUnlocks(DateTime first, DateTime last)
        {
            if (_data == null)
            {
                return Array.Empty<YearReviewUnlock>();
            }

            var query = _data.SummaryQuery()
                .WhereUnlocked(true)
                .WhereUnlockTimeKnown()
                .WhereUnlockedBetween(first, last);
            return _data.ReadRows(query)
                .Where(row => row?.PlayniteGameId != null && row.UnlockTimeUtc.HasValue)
                .Select(row => new YearReviewUnlock
                {
                    GameId = row.PlayniteGameId.Value,
                    GameName = row.GameName,
                    AchievementName = row.DisplayName ?? row.ApiName,
                    IconPath = row.UnlockedIconPath,
                    UnlockUtc = row.UnlockTimeUtc.Value,
                    GlobalPercent = row.GlobalPercentUnlocked,
                    Rarity = row.Rarity,
                    ProviderKey = string.IsNullOrWhiteSpace(row.ProviderKey)
                        ? CustomAchievementProjectionService.ProviderKey
                        : row.ProviderKey
                })
                .ToList();
        }

        /// <summary>
        /// Friend unlocks since the start of the year: an achievement the user unlocked in the
        /// year before a friend did has the friend's unlock after that, so nothing earlier counts.
        /// </summary>
        private IReadOnlyList<YearReviewFriendUnlock> ReadFriendUnlocks(DateTime first)
        {
            var reads = _data?.CacheReads?.Reads;
            if (reads == null || _settings?.Invoke()?.EnableFriendsFeatures != true)
            {
                return Array.Empty<YearReviewFriendUnlock>();
            }

            try
            {
                var query = AchievementQuery.AllFriends
                    .WhereUnlocked(true)
                    .WhereUnlockTimeKnown()
                    .WhereUnlockedBetween(first, null);
                var result = new List<YearReviewFriendUnlock>();
                foreach (var row in reads.Rows(query))
                {
                    var friendUnlock = AchievementSqlFunctions.ParseUtc(row?.UnlockTimeUtc);
                    if (row == null || !friendUnlock.HasValue)
                    {
                        continue;
                    }

                    var gameId = Guid.TryParse(row.PlayniteGameId, out var parsed) ? parsed : (Guid?)null;
                    result.Add(new YearReviewFriendUnlock
                    {
                        FriendKey = row.UserProviderKey + Separator + row.ExternalUserId,
                        FriendName = row.UserDisplayName,
                        GameId = gameId,
                        AchievementKey = (gameId?.ToString("D") ?? row.ProviderGameKey) + Separator + row.ApiName,
                        FriendUnlockUtc = friendUnlock.Value,
                        SelfUnlockUtc = row.SelfUnlocked != 0 ? AchievementSqlFunctions.ParseUtc(row.SelfUnlockTimeUtc) : null
                    });
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Year in Review: friend unlocks could not be read.");
                return Array.Empty<YearReviewFriendUnlock>();
            }
        }

        /// <summary>The friend's avatar, as the friends views show it; null without one.</summary>
        public string FriendAvatar(string friendKey)
        {
            var parts = (friendKey ?? string.Empty).Split(Separator);
            if (parts.Length != 2 || _friends?.FriendReads == null)
            {
                return null;
            }

            try
            {
                return _friends.FriendReads
                    .Rows(AchievementQuery.AllFriends.ForFriend(parts[0], parts[1]).Page(1))
                    .FirstOrDefault()?.FriendAvatarPath;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Year in Review: friend avatar could not be read.");
                return null;
            }
        }

        /// <summary>The game's background image path; null without one.</summary>
        public string BackgroundPath(Guid gameId)
        {
            try
            {
                var background = _api?.Database?.Games?.Get(gameId)?.BackgroundImage;
                return string.IsNullOrWhiteSpace(background) || background.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : _api.Database.GetFullFilePath(background);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Year in Review: background image could not be resolved.");
                return null;
            }
        }
    }
}
