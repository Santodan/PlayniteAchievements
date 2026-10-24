using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Services.GameSearch;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// Looks a game up on one <see cref="IGameSearchSource"/> by name, or by an id or address
    /// typed into the same box, and keeps the game picked (or the games, for a source whose link
    /// can name several). Shared by the manual tracking wizard and the game search window.
    /// </summary>
    public sealed class GameSearchViewModel : ObservableObject
    {
        private readonly HashSet<string> _currentIds;
        private readonly IReadOnlyList<GameSearchResult> _current;
        private readonly string _initialQuery;
        private readonly ILogger _logger;
        private IGameSearchSource _source;
        private CancellationTokenSource _searchCts;
        private int _searchVersion;
        private string _searchText;
        private string _linkText = string.Empty;
        private bool _isSearching;
        private string _statusMessage = string.Empty;
        private GameSearchResultItem _selectedItem;

        public GameSearchViewModel(IGameSearchSource source, string query, IEnumerable<GameSearchResult> current = null, ILogger logger = null)
        {
            _source = source;
            _searchText = query ?? string.Empty;
            _logger = logger;
            var currentList = (current ?? Enumerable.Empty<GameSearchResult>())
                .Where(result => !string.IsNullOrWhiteSpace(result?.Id))
                .ToList();
            _current = currentList;
            _initialQuery = (query ?? string.Empty).Trim();
            _currentIds = new HashSet<string>(currentList.Select(result => result.Id), StringComparer.OrdinalIgnoreCase);

            // A link to several games opens with them picked, so a search keeps them; one game is
            // simply preselected in the results.
            if (source?.AllowsMultiple == true && currentList.Count > 1)
            {
                foreach (var result in currentList)
                {
                    Picked.Add(result);
                }
            }

            SearchCommand = new RelayCommand(_ => _ = SearchAsync(), _ => !string.IsNullOrWhiteSpace(SearchText));
            AddLinkCommand = new RelayCommand(_ => _ = AddLinkAsync(), _ => _source?.TryParseId(LinkText ?? string.Empty) != null);
            RemovePickCommand = new RelayCommand(parameter => Unpick(parameter as GameSearchResult));
        }

        public IGameSearchSource Source => _source;

        public bool AllowsMultiple => _source?.AllowsMultiple == true;

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetValueAndReturn(ref _searchText, value ?? string.Empty))
                {
                    SearchCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public bool IsSearching
        {
            get => _isSearching;
            private set => SetValue(ref _isSearching, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetValue(ref _statusMessage, value ?? string.Empty);
        }

        public ObservableCollection<GameSearchResultItem> Results { get; } = new ObservableCollection<GameSearchResultItem>();

        public GameSearchResultItem SelectedItem
        {
            get => _selectedItem;
            set
            {
                if (SetValueAndReturn(ref _selectedItem, value))
                {
                    OnPropertyChanged(nameof(CanAccept));
                }
            }
        }

        /// <summary>The games picked so far, in the order picked; only used when the source takes several.</summary>
        public ObservableCollection<GameSearchResult> Picked { get; } = new ObservableCollection<GameSearchResult>();

        public bool HasPicked => Picked.Count > 0;

        /// <summary>True when any result lists platforms, so the platform column is worth its width.</summary>
        public bool ShowPlatformColumn => Results.Any(item => item.Platforms != null);

        public bool CanAccept => GetSelection().Count > 0;

        public RelayCommand SearchCommand { get; }

        /// <summary>A page address or id typed into the link box, added as a result once the source reads it.</summary>
        public string LinkText
        {
            get => _linkText;
            set
            {
                if (SetValueAndReturn(ref _linkText, value ?? string.Empty))
                {
                    AddLinkCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>Adds the game the link box names; enabled only while the source reads the text.</summary>
        public RelayCommand AddLinkCommand { get; }

        public RelayCommand RemovePickCommand { get; }

        /// <summary>Searches another source with the same text; anything picked on the previous one is dropped.</summary>
        public void SetSource(IGameSearchSource source)
        {
            if (ReferenceEquals(_source, source))
            {
                return;
            }

            _source = source;
            Picked.Clear();
            ClearResults();
            OnPropertyChanged(nameof(Source));
            OnPropertyChanged(nameof(AllowsMultiple));
            AddLinkCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(HasPicked));
            OnPropertyChanged(nameof(CanAccept));
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                _ = SearchAsync();
            }
        }

        /// <summary>
        /// The games to store: the picked ones when the source takes several and any are picked,
        /// otherwise the highlighted result.
        /// </summary>
        public IReadOnlyList<GameSearchResult> GetSelection()
        {
            if (AllowsMultiple && Picked.Count > 0)
            {
                return Picked.ToList();
            }

            return SelectedItem == null ? Array.Empty<GameSearchResult>() : new[] { SelectedItem.Result };
        }

        /// <summary>Picks or unpicks a result; the highlighted one when <paramref name="item"/> is null.</summary>
        public void Toggle(GameSearchResultItem item = null)
        {
            item = item ?? SelectedItem;
            if (item != null && AllowsMultiple)
            {
                item.IsChecked = !item.IsChecked;
            }
        }

        public async Task SearchAsync()
        {
            var text = (SearchText ?? string.Empty).Trim();
            var source = _source;
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;
            var version = ++_searchVersion;
            ClearResults();
            if (text.Length == 0 || source == null)
            {
                IsSearching = false;
                StatusMessage = string.Empty;
                return;
            }

            var cts = new CancellationTokenSource();
            _searchCts = cts;
            var ct = cts.Token;
            IsSearching = true;
            StatusMessage = ResourceProvider.GetString("LOCPlayAch_Status_Refreshing");
            try
            {
                var directId = source.TryParseId(text);
                var describe = directId == null ? null : DescribeAsync(source, directId, ct);

                IReadOnlyList<GameSearchResult> found = Array.Empty<GameSearchResult>();
                string failure = null;
                try
                {
                    // An address names its game outright; searching its text would only find noise.
                    if (directId == null || !LooksLikeAddress(text))
                    {
                        found = await source.SearchAsync(text, ct) ?? Array.Empty<GameSearchResult>();
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (ManualSourceAuthenticationException ex)
                {
                    failure = ManualSourceAuthentication.ResolveLocalizedMessage(ex);
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"[GameSearch] {source.DisplayName} search for '{text}' failed");
                    failure = string.Format(ResourceProvider.GetString("LOCPlayAch_Status_Failed"), ex.Message);
                }

                var direct = describe == null ? null : await describe;
                if (version != _searchVersion)
                {
                    return;
                }

                var ranked = GameSearchRanking.Rank(text, found)
                    .Where(result => direct == null || !string.Equals(result.Id, direct.Id, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                // The search the window opens with lists the linked games first, found or not, so
                // the current choice is always in view; a typed id goes ahead of them.
                if (string.Equals(text, _initialQuery, StringComparison.OrdinalIgnoreCase))
                {
                    var linked = _current
                        .Where(current => direct == null || !string.Equals(current.Id, direct.Id, StringComparison.OrdinalIgnoreCase))
                        .Select(current => ranked.FirstOrDefault(result => string.Equals(result.Id, current.Id, StringComparison.OrdinalIgnoreCase)) ?? current)
                        .ToList();
                    ranked = linked.Concat(ranked.Where(result => !linked.Contains(result))).ToList();
                }

                if (direct != null)
                {
                    ranked.Insert(0, direct);
                }

                foreach (var result in ranked)
                {
                    Results.Add(new GameSearchResultItem(this, result));
                }

                OnPropertyChanged(nameof(ShowPlatformColumn));
                StatusMessage = Results.Count > 0
                    ? string.Format(
                        FormattingCulture.Current,
                        ResourceProvider.GetString("LOCPlayAch_ManualAchievements_Search_ResultsFormat"),
                        Results.Count)
                    : failure ?? ResourceProvider.GetString("LOCPlayAch_ManualAchievements_Search_NoResults");

                SelectedItem = Results.FirstOrDefault(item => _currentIds.Contains(item.Result.Id)) ?? Results.FirstOrDefault();
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                if (version == _searchVersion)
                {
                    IsSearching = false;
                }
            }
        }

        /// <summary>
        /// Puts the game the link box names at the top of the results and selects it (picks it,
        /// for a source that takes several), named as the source tells. The box is emptied.
        /// </summary>
        public async Task AddLinkAsync()
        {
            var source = _source;
            var id = source?.TryParseId(LinkText ?? string.Empty);
            if (id == null)
            {
                return;
            }

            var version = _searchVersion;
            GameSearchResult result;
            try
            {
                result = await DescribeAsync(source, id, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (version != _searchVersion || !ReferenceEquals(source, _source))
            {
                return;
            }

            foreach (var existing in Results.Where(item => string.Equals(item.Result.Id, result.Id, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                Results.Remove(existing);
            }

            var added = new GameSearchResultItem(this, result);
            Results.Insert(0, added);
            SelectedItem = added;
            if (AllowsMultiple)
            {
                SetPicked(result, true);
                added.RaiseCheckedChanged();
            }

            LinkText = string.Empty;
            OnPropertyChanged(nameof(ShowPlatformColumn));
        }

        public void CancelSearch()
        {
            _searchCts?.Cancel();
        }

        public void Cleanup()
        {
            _searchVersion++;
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = null;
        }

        internal bool IsPicked(GameSearchResult result) =>
            result != null && Picked.Any(picked => string.Equals(picked.Id, result.Id, StringComparison.OrdinalIgnoreCase));

        internal void SetPicked(GameSearchResult result, bool picked)
        {
            if (result == null || IsPicked(result) == picked)
            {
                return;
            }

            if (picked)
            {
                Picked.Add(result);
            }
            else
            {
                Unpick(result);
                return;
            }

            OnPickedChanged();
        }

        private void Unpick(GameSearchResult result)
        {
            var existing = result == null
                ? null
                : Picked.FirstOrDefault(picked => string.Equals(picked.Id, result.Id, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                return;
            }

            Picked.Remove(existing);
            foreach (var item in Results.Where(item => string.Equals(item.Result.Id, existing.Id, StringComparison.OrdinalIgnoreCase)))
            {
                item.RaiseCheckedChanged();
            }

            OnPickedChanged();
        }

        private void OnPickedChanged()
        {
            OnPropertyChanged(nameof(HasPicked));
            OnPropertyChanged(nameof(CanAccept));
        }

        private void ClearResults()
        {
            Results.Clear();
            SelectedItem = null;
            OnPropertyChanged(nameof(ShowPlatformColumn));
        }

        private static async Task<GameSearchResult> DescribeAsync(IGameSearchSource source, string id, CancellationToken ct)
        {
            GameSearchResult described = null;
            try
            {
                described = await source.DescribeAsync(id, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // The id still names the game; it is shown as its own name.
            }

            var result = described == null || string.IsNullOrWhiteSpace(described.Id)
                ? new GameSearchResult(id, id)
                : described;
            result.IsDirect = true;
            return result;
        }

        internal static bool LooksLikeAddress(string text) =>
            text.IndexOf('/') >= 0 || text.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One search result as a row of the picker.</summary>
    public sealed class GameSearchResultItem : ObservableObject
    {
        private readonly GameSearchViewModel _owner;

        internal GameSearchResultItem(GameSearchViewModel owner, GameSearchResult result)
        {
            _owner = owner;
            Result = result;
            // The row's checkbox reads IsChecked one way and toggles through this command, so a
            // DataGrid cell edit can never revert it.
            ToggleCommand = new RelayCommand(_ => IsChecked = !IsChecked);
        }

        public GameSearchResult Result { get; }

        public RelayCommand ToggleCommand { get; }

        public string Name => Result.Name;

        public string IconUrl => Result.IconUrl;

        public string Platforms => Result.Platforms;

        public bool IsChecked
        {
            get => _owner.IsPicked(Result);
            set
            {
                _owner.SetPicked(Result, value);
                OnPropertyChanged();
            }
        }

        internal void RaiseCheckedChanged() => OnPropertyChanged(nameof(IsChecked));
    }
}
