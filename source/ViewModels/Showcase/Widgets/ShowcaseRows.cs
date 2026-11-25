using System;
using System.Collections.Generic;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Services.Cache;
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
        private readonly Func<AchievementDataFeed> _feed;
        private readonly Func<AchievementQuery, IReadOnlyList<AchievementDisplayItem>> _load;
        private LiveQuery<IReadOnlyList<AchievementDisplayItem>> _live;
        private string _key;

        /// <summary>The user's own achievement rows.</summary>
        /// <param name="loaded">Runs on the UI thread when a read lands.</param>
        public ShowcaseRows(Action loaded)
            : this(
                loaded,
                () => PlayniteAchievementsPlugin.Instance?.AchievementDataService?.CacheReads?.Feed,
                Load)
        {
        }

        /// <summary>The rows <paramref name="load"/> reads, read again as <paramref name="feed"/> reports changes.</summary>
        /// <param name="load">Runs off the UI thread.</param>
        public ShowcaseRows(
            Action loaded,
            Func<AchievementDataFeed> feed,
            Func<AchievementQuery, IReadOnlyList<AchievementDisplayItem>> load)
        {
            _loaded = loaded;
            _feed = feed;
            _load = load;
        }

        /// <summary>The rows of <paramref name="query"/> once read; until then the last query's.</summary>
        public IReadOnlyList<AchievementDisplayItem> For(AchievementQuery query)
        {
            var feed = _feed();
            var dispatcher = Application.Current?.Dispatcher;
            if (query == null || feed == null || dispatcher == null)
            {
                return Array.Empty<AchievementDisplayItem>();
            }

            var key = AchievementQuerySql.KeyOf(query);
            if (_live == null)
            {
                _live = new LiveQuery<IReadOnlyList<AchievementDisplayItem>>(
                    feed,
                    query,
                    _load,
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
        public static IReadOnlyList<AchievementDisplayItem> Load(AchievementQuery query)
        {
            var plugin = PlayniteAchievementsPlugin.Instance;
            return plugin?.AchievementDataService == null
                ? Array.Empty<AchievementDisplayItem>()
                : (IReadOnlyList<AchievementDisplayItem>)new OverviewDataBuilder(plugin.AchievementDataService, null, plugin.PlayniteApi, null)
                    .LoadRows(plugin.Settings, query);
        }

        public void Dispose()
        {
            _live?.Dispose();
            _live = null;
        }
    }
}
