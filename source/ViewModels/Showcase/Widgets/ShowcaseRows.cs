using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// A widget's achievement rows read through a query: the rows of the last read, read again
    /// off the UI thread when the query changes or the data under it does.
    /// </summary>
    internal sealed class ShowcaseRows : IDisposable
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        private readonly Action _loaded;
        private LiveQuery<IReadOnlyList<AchievementDisplayItem>> _live;
        private string _key;
        private IReadOnlyDictionary<Guid, GameSummaryItem> _games;

        /// <param name="loaded">Runs on the UI thread when a read lands.</param>
        public ShowcaseRows(Action loaded)
        {
            _loaded = loaded;
        }

        /// <summary>The rows of <paramref name="query"/> once read; until then the last query's.</summary>
        /// <param name="games">The library's games, which give authored rows their provider.</param>
        public IReadOnlyList<AchievementDisplayItem> For(AchievementQuery query, IEnumerable<GameSummaryItem> games)
        {
            var feed = PlayniteAchievementsPlugin.Instance?.AchievementDataService?.CacheReads?.Feed;
            var dispatcher = Application.Current?.Dispatcher;
            if (query == null || feed == null || dispatcher == null)
            {
                return Array.Empty<AchievementDisplayItem>();
            }

            _games = ById(games);
            var key = AchievementQuerySql.KeyOf(query);
            if (_live == null)
            {
                _live = new LiveQuery<IReadOnlyList<AchievementDisplayItem>>(
                    feed,
                    query,
                    read => Load(read, _games),
                    new DispatcherReadScheduler(dispatcher),
                    onError: ex => Logger.Warn(ex, "Failed to read a widget's achievement rows."));
                _live.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(LiveQuery<object>.Result))
                    {
                        _loaded?.Invoke();
                    }
                };
            }
            else if (!string.Equals(key, _key, StringComparison.Ordinal))
            {
                _live.SetQuery(query);
            }

            _key = key;
            return _live.Result ?? Array.Empty<AchievementDisplayItem>();
        }

        /// <summary>The display rows of <paramref name="query"/>, read on the calling thread.</summary>
        public static IReadOnlyList<AchievementDisplayItem> Load(AchievementQuery query, IReadOnlyDictionary<Guid, GameSummaryItem> games)
        {
            var plugin = PlayniteAchievementsPlugin.Instance;
            return plugin?.AchievementDataService == null
                ? Array.Empty<AchievementDisplayItem>()
                : (IReadOnlyList<AchievementDisplayItem>)new OverviewDataBuilder(plugin.AchievementDataService, null, plugin.PlayniteApi, null)
                    .LoadRows(plugin.Settings, query, games: games);
        }

        public static IReadOnlyDictionary<Guid, GameSummaryItem> ById(IEnumerable<GameSummaryItem> games) =>
            (games ?? Enumerable.Empty<GameSummaryItem>())
                .Where(game => game?.PlayniteGameId != null)
                .GroupBy(game => game.PlayniteGameId.Value)
                .ToDictionary(group => group.Key, group => group.First());

        public void Dispose()
        {
            _live?.Dispose();
            _live = null;
        }
    }
}
