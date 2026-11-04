using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// Base for control bar adapters whose filter state outlives the view models showing it: a
    /// showcase widget keeps one adapter across every view model it swaps through. Listeners are
    /// held weakly, so a discarded view model is not kept alive by the adapter; each listener's
    /// owner keeps the delegate itself alive for as long as it wants the notifications.
    /// </summary>
    public abstract class SharedControlBarAdapter : PlayniteAchievements.Common.ObservableObject
    {
        private readonly List<WeakReference<Action>> _filterListeners = new List<WeakReference<Action>>();

        protected SharedControlBarAdapter()
        {
            GameFilter = new ControlBarGameFilter(RaiseFilterChanged);
        }

        public abstract GridControlBarViewModel ControlBar { get; }

        /// <summary>The bar's Game filter, which the derived bar places after its platform filter.</summary>
        public ControlBarGameFilter GameFilter { get; }

        /// <summary>
        /// The bar's search box and items in bar order: what a widget shows or hides one by one,
        /// and what its settings list with their editors.
        /// </summary>
        public IReadOnlyList<IGridControlBarEntry> Entries
        {
            get
            {
                var entries = new List<IGridControlBarEntry>();
                var bar = ControlBar;
                if (bar == null)
                {
                    return entries;
                }

                if (!string.IsNullOrWhiteSpace(bar.Search?.Id))
                {
                    entries.Add(bar.Search);
                }

                foreach (var item in bar.Items)
                {
                    if (!string.IsNullOrWhiteSpace(item?.Id))
                    {
                        entries.Add(item);
                    }
                }

                return entries;
            }
        }

        /// <summary>Names this adapter's saved state among a widget's options; one per adapter type.</summary>
        public abstract string StateKey { get; }

        /// <summary>The current search text and filter selections, in a form that can be saved.</summary>
        public abstract ControlBarFilterState CaptureState();

        /// <summary>
        /// Restores saved state without raising a filter change. Platform selections wait for the
        /// next options rebuild, since the dropdown's groups are built from the games.
        /// </summary>
        public abstract void RestoreState(ControlBarFilterState state);

        /// <summary>Restored platform selections not yet folded into a rebuild of the groups.</summary>
        protected Dictionary<string, List<string>> PendingPlatformSelections { get; set; }

        protected Dictionary<string, List<string>> CapturePlatformSelections(IEnumerable<ProviderFilterGroup> groups)
        {
            var result = PendingPlatformSelections != null
                ? new Dictionary<string, List<string>>(PendingPlatformSelections, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups ?? new List<ProviderFilterGroup>())
            {
                if (group == null || string.IsNullOrWhiteSpace(group.ProviderKey))
                {
                    continue;
                }

                var selected = new List<string>(group.SelectedPlatformNames);
                if (selected.Count > 0)
                {
                    result[group.ProviderKey] = selected;
                }
                else
                {
                    result.Remove(group.ProviderKey);
                }
            }

            return result;
        }

        /// <summary>
        /// Drops the restored platform selections a rebuild of <paramref name="groups"/> seeded.
        /// Selections for providers the groups do not cover yet stay pending, so a first pass
        /// over an empty or partial game list does not lose them.
        /// </summary>
        protected void SettlePendingPlatformSelections(IEnumerable<ProviderFilterGroup> groups)
        {
            if (PendingPlatformSelections == null)
            {
                return;
            }

            // Saved state deserializes with the default comparer; provider keys match ignoring case.
            PendingPlatformSelections = new Dictionary<string, List<string>>(
                PendingPlatformSelections,
                StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups ?? Enumerable.Empty<ProviderFilterGroup>())
            {
                if (!string.IsNullOrWhiteSpace(group?.ProviderKey))
                {
                    PendingPlatformSelections.Remove(group.ProviderKey.Trim());
                }
            }

            if (PendingPlatformSelections.Count == 0)
            {
                PendingPlatformSelections = null;
            }
        }

        /// <summary>Adds a filter-change listener. The caller must hold a strong reference to it.</summary>
        public void AddFilterListener(Action listener)
        {
            if (listener == null)
            {
                return;
            }

            _filterListeners.RemoveAll(entry => !entry.TryGetTarget(out var alive) || alive == listener);
            _filterListeners.Add(new WeakReference<Action>(listener));
        }

        public void RemoveFilterListener(Action listener)
        {
            _filterListeners.RemoveAll(entry => !entry.TryGetTarget(out var alive) || alive == listener);
        }

        /// <summary>
        /// Has every listener filter again, for a change outside the bar that alters what the bar
        /// filters (a score card switching platform).
        /// </summary>
        public void Reapply() => RaiseFilterChanged();

        protected void RaiseFilterChanged()
        {
            var live = new List<Action>(_filterListeners.Count);
            _filterListeners.RemoveAll(entry =>
            {
                if (entry.TryGetTarget(out var listener))
                {
                    live.Add(listener);
                    return false;
                }

                return true;
            });

            foreach (var listener in live)
            {
                listener();
            }
        }
    }

    /// <summary>
    /// A showcase control bar's Game filter. It lists the games last given to
    /// <see cref="UpdateGames"/> by name, limited to <see cref="SetScope"/>'s games when the widget
    /// has a collection, and to the picked platforms' games when the bar's Platform filter has
    /// picks. Selections are game IDs, so a rename keeps them, and a selected game stays selected
    /// while it is not listed, unless a collection or the picked platforms leave it out.
    /// </summary>
    public sealed class ControlBarGameFilter : PlayniteAchievements.Common.ObservableObject
    {
        private readonly Action _changed;
        private readonly HashSet<Guid> _selected = new HashSet<Guid>();
        private List<GameSummaryItem> _games;
        private HashSet<Guid> _scope;
        private IReadOnlyList<ProviderFilterGroup> _platformGroups;
        private Dictionary<Guid, string> _names = new Dictionary<Guid, string>();
        private List<string> _options = new List<string>();

        public ControlBarGameFilter(Action changed)
        {
            _changed = changed;
            Item = new GridMultiSelectFilter(
                this,
                nameof(DisplayText),
                () => DisplayText,
                () => _options,
                IsSelected,
                SetSelected,
                GetName)
            {
                Id = PlayniteAchievements.Models.ShowcaseControlBarItems.Game,
                LabelKey = "LOCPlayAch_Column_Game",
                Width = 170,
                ClearAll = Clear
            };
        }

        public GridMultiSelectFilter Item { get; }

        public bool HasSelection => _selected.Count > 0;

        public IReadOnlyCollection<Guid> SelectedGameIds => _selected;

        public string DisplayText
        {
            get
            {
                var names = _options
                    .Where(IsSelected)
                    .Select(GetName)
                    .ToList();
                return names.Count == 0
                    ? Playnite.SDK.ResourceProvider.GetString("LOCPlayAch_Column_Game")
                    : string.Join(", ", names);
            }
        }

        /// <summary>True for every game while nothing is selected.</summary>
        public bool Matches(Guid? gameId) =>
            _selected.Count == 0 || (gameId.HasValue && _selected.Contains(gameId.Value));

        /// <summary>The games to list; the same games by ID and name leave the list alone.</summary>
        public void UpdateGames(IEnumerable<GameSummaryItem> games)
        {
            var list = (games ?? Enumerable.Empty<GameSummaryItem>())
                .Where(game => game?.PlayniteGameId != null)
                .ToList();
            if (_games != null &&
                _games.Count == list.Count &&
                !_games.Where((game, index) =>
                    game.PlayniteGameId != list[index].PlayniteGameId ||
                    !string.Equals(game.GameName, list[index].GameName, StringComparison.Ordinal)).Any())
            {
                return;
            }

            _games = list;
            Rebuild();
        }

        /// <summary>
        /// Limits the listed games to <paramref name="gameIds"/>, and unpicks any game outside
        /// them; null lists them all. A widget sets it during its filter pass, which applies the
        /// unpicked games itself, so only <paramref name="notify"/> raises a filter change for
        /// them (saving them and refreshing the widget).
        /// </summary>
        public void SetScope(IEnumerable<Guid> gameIds, bool notify = false)
        {
            var scope = gameIds != null ? new HashSet<Guid>(gameIds) : null;
            if (scope == null ? _scope == null : _scope != null && _scope.SetEquals(scope))
            {
                return;
            }

            _scope = scope;
            var unpicked = scope != null && _selected.RemoveWhere(id => !scope.Contains(id)) > 0;
            Rebuild();
            if (unpicked && notify)
            {
                _changed?.Invoke();
            }
        }

        /// <summary>
        /// Limits the listed games to the platforms picked in <paramref name="groups"/>, the bar's
        /// Platform filter, and unpicks games on other platforms; no picks list every platform.
        /// The bar calls it as its platform picks change, before raising its own filter change.
        /// </summary>
        public void SetPlatforms(IEnumerable<ProviderFilterGroup> groups)
        {
            _platformGroups = groups?.Where(group => group != null).ToList();
            Rebuild();
        }

        public List<string> Capture() =>
            _selected.Count == 0 ? null : _selected.Select(id => id.ToString("D")).ToList();

        public void Restore(IEnumerable<string> gameIds)
        {
            _selected.Clear();
            foreach (var raw in gameIds ?? Enumerable.Empty<string>())
            {
                if (Guid.TryParse(raw, out var id))
                {
                    _selected.Add(id);
                }
            }

            OnPropertyChanged(nameof(DisplayText));
        }

        /// <summary>Unpicks every game, listed or not.</summary>
        public void Clear()
        {
            if (_selected.Count == 0)
            {
                return;
            }

            _selected.Clear();
            OnPropertyChanged(nameof(DisplayText));
            _changed?.Invoke();
        }

        private bool IsSelected(string option) =>
            Guid.TryParse(option, out var id) && _selected.Contains(id);

        private void SetSelected(string option, bool isSelected)
        {
            if (!Guid.TryParse(option, out var id) ||
                !(isSelected ? _selected.Add(id) : _selected.Remove(id)))
            {
                return;
            }

            OnPropertyChanged(nameof(DisplayText));
            _changed?.Invoke();
        }

        private string GetName(string option) =>
            Guid.TryParse(option, out var id) && _names.TryGetValue(id, out var name) ? name : option;

        private void Rebuild()
        {
            var games = (IEnumerable<GameSummaryItem>)(_games ?? new List<GameSummaryItem>());
            var platformPicked = _platformGroups?.Any(group => group.HasAnySelected) == true;
            if (platformPicked)
            {
                games = PlayniteAchievements.Services.Overview.OverviewGameSummaryFilters
                    .ApplyProviderPlatformFilter(games, _platformGroups)
                    .ToList();
            }

            var names = new Dictionary<Guid, string>();
            foreach (var game in games)
            {
                if (game.PlayniteGameId is Guid id &&
                    (_scope == null || _scope.Contains(id)) &&
                    !names.ContainsKey(id))
                {
                    names[id] = game.GameName ?? string.Empty;
                }
            }

            // Picks on other platforms go once the games are known; before then every pick waits.
            if (platformPicked && _games?.Count > 0)
            {
                var onPlatforms = new HashSet<Guid>(games
                    .Where(game => game.PlayniteGameId.HasValue)
                    .Select(game => game.PlayniteGameId.Value));
                _selected.RemoveWhere(id => !onPlatforms.Contains(id));
            }

            _names = names;
            _options = names
                .OrderBy(pair => pair.Value, StringComparer.CurrentCultureIgnoreCase)
                .Select(pair => pair.Key.ToString("D"))
                .ToList();
            OnPropertyChanged(nameof(DisplayText));
        }
    }

    /// <summary>
    /// A control bar's saved state. Platforms map a provider key to its selected platform names.
    /// Progress and Activity hold option positions rather than labels, so the saved choice
    /// survives a change of Playnite language.
    /// </summary>
    public sealed class ControlBarFilterState
    {
        public string SearchText { get; set; }

        public Dictionary<string, List<string>> Platforms { get; set; }

        /// <summary>Selected games, as Playnite game IDs.</summary>
        public List<string> Games { get; set; }

        /// <summary>Selected achievement tags, as tag keys, for achievement control bars.</summary>
        public List<string> Tags { get; set; }

        /// <summary>The Screenshot Slideshow's screenshot style (a ShowcaseScreenshotVariant name); null for every style.</summary>
        public string Variant { get; set; }

        public List<int> Progress { get; set; }

        public List<int> Activity { get; set; }

        /// <summary>An unlock date range's start day, for achievement control bars; null when open.</summary>
        public DateTime? UnlockedFrom { get; set; }

        /// <summary>An unlock date range's end day; null when open.</summary>
        public DateTime? UnlockedTo { get; set; }

        /// <summary>Selected rarity tiers, for achievement control bars.</summary>
        public List<int> Rarities { get; set; }

        /// <summary>True when the Rarity filter keeps only capstones; null otherwise.</summary>
        public bool? Capstones { get; set; }

        /// <summary>Selected trophy types ("platinum", "gold", ...), for achievement control bars.</summary>
        public List<string> Trophies { get; set; }

        /// <summary>Selected category paths, for achievement control bars showing one game.</summary>
        public List<string> Categories { get; set; }
    }
}
