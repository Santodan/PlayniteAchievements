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
    /// has a collection. Selections are game IDs, so a rename keeps them, and a selected game
    /// stays selected while it is not listed.
    /// </summary>
    public sealed class ControlBarGameFilter : PlayniteAchievements.Common.ObservableObject
    {
        private readonly Action _changed;
        private readonly HashSet<Guid> _selected = new HashSet<Guid>();
        private List<GameSummaryItem> _games;
        private HashSet<Guid> _scope;
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
                Width = 170
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

        /// <summary>Limits the listed games to <paramref name="gameIds"/>; null lists them all.</summary>
        public void SetScope(IEnumerable<Guid> gameIds)
        {
            var scope = gameIds != null ? new HashSet<Guid>(gameIds) : null;
            if (scope == null ? _scope == null : _scope != null && _scope.SetEquals(scope))
            {
                return;
            }

            _scope = scope;
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
            var names = new Dictionary<Guid, string>();
            foreach (var game in _games ?? new List<GameSummaryItem>())
            {
                if (game.PlayniteGameId is Guid id &&
                    (_scope == null || _scope.Contains(id)) &&
                    !names.ContainsKey(id))
                {
                    names[id] = game.GameName ?? string.Empty;
                }
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

        /// <summary>Selected trophy types ("platinum", "gold", ...), for achievement control bars.</summary>
        public List<string> Trophies { get; set; }
    }
}
