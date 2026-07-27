using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.ViewModels.Items;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// Compare-friend selection for the self achievement surfaces (Overview selected game and
    /// the single-game achievements window): loads every friend's cached rows for one game,
    /// exposes the friends that have data as dropdown options, and applies the selected
    /// friend's unlock state onto the self display items' comparison fields. Selection is
    /// session-only and clears whenever the game changes.
    /// </summary>
    public sealed class FriendCompareController : ObservableObject
    {
        public sealed class Option
        {
            public string Key { get; set; }
            public string DisplayName { get; set; }
            public string AvatarPath { get; set; }
        }

        private readonly IFriendCacheManager _friendCache;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;

        private Guid? _gameId;
        private int _loadVersion;
        private List<FriendAchievementDisplayItem> _friendRows = new List<FriendAchievementDisplayItem>();
        private List<Option> _options = new List<Option>();
        private Option _selected;
        private IReadOnlyList<AchievementDisplayItem> _targetItems;
        private readonly List<AchievementDisplayItem> _appliedItems = new List<AchievementDisplayItem>();

        public FriendCompareController(
            IFriendCacheManager friendCache,
            PlayniteAchievementsSettings settings,
            ILogger logger)
        {
            _friendCache = friendCache;
            _settings = settings;
            _logger = logger;
        }

        public bool IsCompareAvailable => _options.Count > 0;

        public bool HasCompareSelection => _selected != null;

        public string CompareSelectionText => _selected?.DisplayName
            ?? ResourceProvider.GetString("LOCPlayAch_Compare_Label");

        public IReadOnlyList<Option> Options => _options;

        public bool IsSelected(Option option)
        {
            return _selected != null &&
                   option != null &&
                   string.Equals(_selected.Key, option.Key, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Sets the game whose friend rows feed the comparison and the self items to enrich.
        /// A game change drops the selection and reloads friend rows off-thread; a same-game
        /// call retargets the current selection onto the new item instances.
        /// </summary>
        public void SetGame(Guid? gameId, IReadOnlyList<AchievementDisplayItem> targetItems)
        {
            var gameChanged = _gameId != gameId;
            _gameId = gameId;
            _targetItems = targetItems;

            if (gameChanged)
            {
                ClearApplied();
                _selected = null;
                _friendRows = new List<FriendAchievementDisplayItem>();
                _options = new List<Option>();
                NotifyCompareStateChanged();
                BeginLoad(gameId);
                return;
            }

            ApplySelection();
        }

        public void SetTargetItems(IReadOnlyList<AchievementDisplayItem> targetItems)
        {
            _targetItems = targetItems;
            ApplySelection();
        }

        public void Select(Option option)
        {
            var next = option != null && _options.Contains(option) ? option : null;
            if (ReferenceEquals(_selected, next))
            {
                return;
            }

            _selected = next;
            NotifyCompareStateChanged();
            ApplySelection();
        }

        private void BeginLoad(Guid? gameId)
        {
            var version = Interlocked.Increment(ref _loadVersion);
            if (_friendCache == null ||
                gameId == null ||
                gameId == Guid.Empty ||
                _settings?.Persisted?.EnableFriendsFeatures != true)
            {
                return;
            }

            var targetGameId = gameId.Value;
            Task.Run(() =>
            {
                List<FriendAchievementDisplayItem> rows = null;
                try
                {
                    rows = _friendCache.LoadFriendGameAchievementData(targetGameId)?.AllAchievements;
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, $"Failed to load friend rows for compare, game {targetGameId}.");
                }

                void Apply()
                {
                    if (version != Volatile.Read(ref _loadVersion))
                    {
                        return;
                    }

                    _friendRows = rows ?? new List<FriendAchievementDisplayItem>();
                    _options = BuildOptions(_friendRows);
                    OnPropertyChanged(nameof(Options));
                    OnPropertyChanged(nameof(IsCompareAvailable));
                    ApplySelection();
                }

                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher != null)
                {
                    dispatcher.InvokeIfNeeded(Apply);
                }
                else
                {
                    Apply();
                }
            });
        }

        private static List<Option> BuildOptions(List<FriendAchievementDisplayItem> rows)
        {
            var byKey = new Dictionary<string, Option>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (row == null || string.IsNullOrWhiteSpace(row.FriendName))
                {
                    continue;
                }

                var key = FriendOverviewProjection.GetFriendScopeKey(row);
                if (FriendOverviewProjection.IsAllScope(key) || byKey.ContainsKey(key))
                {
                    continue;
                }

                byKey[key] = new Option
                {
                    Key = key,
                    DisplayName = row.FriendName,
                    AvatarPath = row.FriendAvatarPath
                };
            }

            return byKey.Values
                .OrderBy(option => option.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private void ApplySelection()
        {
            ClearApplied();
            var items = _targetItems;
            if (_selected == null || items == null || items.Count == 0)
            {
                return;
            }

            var compareRows = new Dictionary<string, FriendAchievementDisplayItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _friendRows)
            {
                if (row == null || string.IsNullOrWhiteSpace(row.ApiName))
                {
                    continue;
                }

                var key = FriendOverviewProjection.GetFriendScopeKey(row);
                if (!string.Equals(key, _selected.Key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!compareRows.TryGetValue(row.ApiName, out var existing) ||
                    (row.Unlocked && !existing.Unlocked))
                {
                    compareRows[row.ApiName] = row;
                }
            }

            foreach (var item in items)
            {
                if (item == null)
                {
                    continue;
                }

                FriendAchievementDisplayItem compareRow = null;
                if (!string.IsNullOrWhiteSpace(item.ApiName))
                {
                    compareRows.TryGetValue(item.ApiName, out compareRow);
                }

                item.ApplyComparison(
                    _selected.DisplayName,
                    _selected.AvatarPath ?? compareRow?.FriendAvatarPath,
                    compareRow?.UnlockTimeUtc,
                    compareRow?.Unlocked == true);
                _appliedItems.Add(item);
            }
        }

        private void ClearApplied()
        {
            if (_appliedItems.Count == 0)
            {
                return;
            }

            foreach (var item in _appliedItems)
            {
                item?.ClearComparison();
            }

            _appliedItems.Clear();
        }

        private void NotifyCompareStateChanged()
        {
            OnPropertyChanged(nameof(HasCompareSelection));
            OnPropertyChanged(nameof(CompareSelectionText));
            OnPropertyChanged(nameof(IsCompareAvailable));
        }
    }
}
