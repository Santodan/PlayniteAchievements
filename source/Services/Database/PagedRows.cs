using PlayniteAchievements.Services.Cache;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

namespace PlayniteAchievements.Services.Database
{
    /// <summary>
    /// A row list that holds only some of its rows. Code that scans rows for a property of the
    /// list (a favorite, a second game, a category) reads <see cref="LoadedItems"/>, which never
    /// loads or creates anything.
    /// </summary>
    public interface IPartiallyLoadedRows
    {
        /// <summary>The rows loaded now, in index order.</summary>
        IEnumerable LoadedItems { get; }
    }

    /// <summary>
    /// The rows of one query as a list whose <see cref="Count"/> is the full row count from the
    /// first load, for a virtualizing grid. A load reads the sorted keys of every matching row
    /// and keeps them; a page is then the columns of a slice of those keys. Reading an index that
    /// is not loaded returns a placeholder and loads its page and the pages on either side; when
    /// the page arrives, each placeholder handed out is replaced in place. A query change or a
    /// touching feed change reads the keys again with the pages around the last read index
    /// and applies them in place, keeping the other loaded pages until they are read again. All
    /// state is changed on the UI thread.
    /// </summary>
    internal sealed class PagedRows<T> : IList, IReadOnlyList<T>, INotifyCollectionChanged, IPartiallyLoadedRows, IDisposable
        where T : class
    {
        public const int PageSize = 100;

        /// <summary>Pages kept loaded; past it, the page farthest from the last read index is dropped.</summary>
        internal const int MaxLoadedPages = 10;

        /// <summary>
        /// A result of at most this many rows loads whole on every reset, so every row is loaded
        /// and <see cref="LoadedItems"/> is the whole list.
        /// </summary>
        internal const int WholeLoadRows = MaxLoadedPages * PageSize;

        private readonly Func<AchievementQuery, IReadOnlyList<AchievementQuerySql.RowKey>> _loadKeys;
        private readonly Func<AchievementQuery, IReadOnlyList<AchievementQuerySql.RowKey>, IReadOnlyList<T>> _loadRows;
        private readonly Func<T> _newPlaceholder;
        private readonly IReadScheduler _scheduler;
        private readonly Action<Exception> _onError;
        private readonly IDisposable _subscription;
        private readonly int _maxRows;
        private readonly Dictionary<int, T[]> _pages = new Dictionary<int, T[]>();
        private readonly HashSet<int> _stalePages = new HashSet<int>();
        private readonly HashSet<int> _wantedPages = new HashSet<int>();
        private readonly Dictionary<int, T> _placeholders = new Dictionary<int, T>();
        private AchievementQuery _query;
        private IReadOnlyList<AchievementQuerySql.RowKey> _keys = Array.Empty<AchievementQuerySql.RowKey>();
        private int _count;
        private int _lastIndex;
        private int _version;
        private bool _pageLoading;
        private bool _resetting;
        private bool _resetAgain;
        private bool _disposed;

        /// <param name="loadKeys">Runs off the UI thread; the keys of the query's rows in its order.</param>
        /// <param name="loadRows">Runs off the UI thread; the rows of the keys, in their order.</param>
        /// <param name="newPlaceholder">A row standing in for one not loaded yet.</param>
        /// <param name="maxRows">The most rows the list holds; 0 for no cap.</param>
        public PagedRows(
            AchievementDataFeed feed,
            AchievementQuery query,
            Func<AchievementQuery, IReadOnlyList<AchievementQuerySql.RowKey>> loadKeys,
            Func<AchievementQuery, IReadOnlyList<AchievementQuerySql.RowKey>, IReadOnlyList<T>> loadRows,
            Func<T> newPlaceholder,
            IReadScheduler scheduler,
            int maxRows = 0,
            Func<TimeSpan> window = null,
            Action<Exception> onError = null)
        {
            _query = query ?? throw new ArgumentNullException(nameof(query));
            _loadKeys = loadKeys ?? throw new ArgumentNullException(nameof(loadKeys));
            _loadRows = loadRows ?? throw new ArgumentNullException(nameof(loadRows));
            _newPlaceholder = newPlaceholder ?? throw new ArgumentNullException(nameof(newPlaceholder));
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            _maxRows = Math.Max(0, maxRows);
            _onError = onError;
            _subscription = feed?.Subscribe(() => _query, scheduler, window ?? (() => LiveQuery<T>.DefaultWindow), Reset);
            Reset();
        }

        public event NotifyCollectionChangedEventHandler CollectionChanged;

        public AchievementQuery Query => _query;

        public int Count => _count;

        /// <summary>
        /// Replaces the query and reloads. With
        /// <paramref name="fromStart"/> the rows around the first index load first, for a list
        /// the grid shows from its top; otherwise those around the last read index. UI thread.
        /// </summary>
        public void SetQuery(AchievementQuery query, bool fromStart = false)
        {
            _query = query ?? throw new ArgumentNullException(nameof(query));
            if (fromStart)
            {
                _lastIndex = 0;
            }

            _version++;
            Reset();
        }

        public object this[int index]
        {
            get
            {
                if (index < 0 || index >= _count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                _lastIndex = index;
                var page = index / PageSize;
                Want(page - 1);
                Want(page);
                Want(page + 1);
                LoadNextPage();
                return Peek(index) ?? NewPlaceholder(index);
            }
            set => throw new NotSupportedException();
        }

        public void Dispose()
        {
            _disposed = true;
            _subscription?.Dispose();
            _pages.Clear();
            _placeholders.Clear();
            _wantedPages.Clear();
        }

        private void Reset()
        {
            if (_disposed)
            {
                return;
            }

            if (_resetting)
            {
                _resetAgain = true;
                return;
            }

            _resetting = true;
            var query = _query;
            var version = _version;
            var anchor = _lastIndex / PageSize;
            _scheduler.Run(() =>
            {
                IReadOnlyList<AchievementQuerySql.RowKey> keys = null;
                Dictionary<int, IReadOnlyList<T>> pages = null;
                try
                {
                    keys = _loadKeys(_maxRows > 0 ? query.Page(_maxRows) : query) ?? Array.Empty<AchievementQuerySql.RowKey>();
                    pages = new Dictionary<int, IReadOnlyList<T>>();
                    var last = Math.Max(0, (keys.Count - 1) / PageSize);

                    // Small enough to keep: every row is loaded from the start. Otherwise the
                    // pages around the anchor.
                    anchor = Math.Min(anchor, last);
                    var first = keys.Count <= WholeLoadRows ? 0 : Math.Max(0, anchor - 1);
                    var end = keys.Count <= WholeLoadRows ? keys.Count : Math.Min(keys.Count, (Math.Min(last, anchor + 1) + 1) * PageSize);
                    var rows = end == 0 ? Array.Empty<T>() : _loadRows(query, Slice(keys, first * PageSize, end));
                    for (var page = first; page * PageSize < end; page++)
                    {
                        pages[page] = rows.Skip((page - first) * PageSize).Take(PageSize).ToList();
                    }
                }
                catch (Exception ex)
                {
                    pages = null;
                    _onError?.Invoke(ex);
                }

                _scheduler.Post(() => ResetLoaded(version, keys, pages));
            });
        }

        private void ResetLoaded(int version, IReadOnlyList<AchievementQuerySql.RowKey> keys, Dictionary<int, IReadOnlyList<T>> pages)
        {
            if (_disposed)
            {
                return;
            }

            // Rows read by the grid while the result is applied wait for the flag to clear.
            if (pages != null && version == _version)
            {
                _keys = keys;
                Apply(keys.Count, pages);
            }

            _resetting = false;

            if (_resetAgain)
            {
                _resetAgain = false;
                Reset();
                return;
            }

            LoadNextPage();
        }

        // A count change of more than a page resets the list; a smaller one removes or adds rows
        // at the end, so the grid keeps its scroll position.
        private void Apply(int count, Dictionary<int, IReadOnlyList<T>> pages)
        {
            _version++;
            _wantedPages.Clear();
            if (Math.Abs(count - _count) > PageSize)
            {
                _pages.Clear();
                _stalePages.Clear();
                _placeholders.Clear();
                _count = count;
                foreach (var page in pages)
                {
                    _pages[page.Key] = Fill(new T[PageSize], page.Value);
                }

                Raise(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
                return;
            }

            while (_count > count)
            {
                var index = --_count;
                var removed = Peek(index) ?? _newPlaceholder();
                Forget(index);
                Raise(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, index));
            }

            foreach (var page in _pages.Keys.ToList())
            {
                if (page * PageSize >= count)
                {
                    _pages.Remove(page);
                    _stalePages.Remove(page);
                }
                else if (!pages.ContainsKey(page))
                {
                    _stalePages.Add(page);
                }
            }

            foreach (var page in pages)
            {
                Swap(page.Key, page.Value, count);
            }

            while (_count < count)
            {
                var index = _count++;
                Raise(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, Peek(index) ?? NewPlaceholder(index), index));
            }
        }

        private void Want(int page)
        {
            if (page >= 0 && page * PageSize < _count && (!_pages.ContainsKey(page) || _stalePages.Contains(page)))
            {
                _wantedPages.Add(page);
            }
        }

        // Loads the wanted page nearest the last read index; pages more than one away from it
        // are dropped, so a fast scroll loads only where it stops, and so are pages loaded
        // since they were wanted.
        private void LoadNextPage()
        {
            if (_pageLoading || _resetting || _disposed)
            {
                return;
            }

            var current = _lastIndex / PageSize;
            _wantedPages.RemoveWhere(page =>
                Math.Abs(page - current) > 1 ||
                page * PageSize >= _count ||
                (_pages.ContainsKey(page) && !_stalePages.Contains(page)));
            if (_wantedPages.Count == 0)
            {
                return;
            }

            var next = _wantedPages.OrderBy(page => Math.Abs(page - current)).First();
            _wantedPages.Remove(next);
            _pageLoading = true;
            var query = _query;
            var version = _version;
            var keys = Slice(_keys, next * PageSize, Math.Min(_count, (next + 1) * PageSize));
            _scheduler.Run(() =>
            {
                IReadOnlyList<T> rows = null;
                try
                {
                    rows = _loadRows(query, keys);
                }
                catch (Exception ex)
                {
                    _onError?.Invoke(ex);
                }

                _scheduler.Post(() => PageLoaded(version, next, rows));
            });
        }

        private void PageLoaded(int version, int page, IReadOnlyList<T> rows)
        {
            if (_disposed)
            {
                return;
            }

            if (rows != null && version == _version)
            {
                Swap(page, rows, _count);
                DropFarPages();
            }

            _pageLoading = false;

            LoadNextPage();
        }

        // Puts a loaded page in place, replacing what was handed out for each of its rows.
        // count is the row count the page was read for.
        private void Swap(int page, IReadOnlyList<T> rows, int count)
        {
            _pages.TryGetValue(page, out var old);
            var items = Fill(old == null ? new T[PageSize] : (T[])old.Clone(), rows);
            _pages[page] = items;
            if (rows.Count < Math.Min(PageSize, count - page * PageSize))
            {
                // The rows moved between the count and the page read; the page reloads when read.
                _stalePages.Add(page);
            }
            else
            {
                _stalePages.Remove(page);
            }

            for (var offset = 0; offset < rows.Count; offset++)
            {
                var index = page * PageSize + offset;
                if (index >= _count)
                {
                    break;
                }

                var before = old?[offset];
                if (before == null && _placeholders.TryGetValue(index, out var placeholder))
                {
                    before = placeholder;
                    _placeholders.Remove(index);
                }

                if (before != null && !ReferenceEquals(before, items[offset]))
                {
                    Raise(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, items[offset], before, index));
                }
            }
        }

        private void DropFarPages()
        {
            var current = _lastIndex / PageSize;
            while (_pages.Count > MaxLoadedPages)
            {
                var far = _pages.Keys.OrderByDescending(page => Math.Abs(page - current)).First();
                var items = _pages[far];
                _pages.Remove(far);
                _stalePages.Remove(far);
                for (var offset = 0; offset < PageSize; offset++)
                {
                    var index = far * PageSize + offset;
                    if (items[offset] != null && index < _count)
                    {
                        Raise(new NotifyCollectionChangedEventArgs(
                            NotifyCollectionChangedAction.Replace, NewPlaceholder(index), items[offset], index));
                    }
                }
            }
        }

        private static T[] Fill(T[] items, IReadOnlyList<T> rows)
        {
            for (var offset = 0; offset < rows.Count && offset < PageSize; offset++)
            {
                items[offset] = rows[offset];
            }

            return items;
        }

        private static IReadOnlyList<AchievementQuerySql.RowKey> Slice(IReadOnlyList<AchievementQuerySql.RowKey> keys, int from, int to) =>
            keys.Skip(from).Take(Math.Max(0, to - from)).ToList();

        private T Peek(int index)
        {
            if (_pages.TryGetValue(index / PageSize, out var items) && items[index % PageSize] != null)
            {
                return items[index % PageSize];
            }

            return _placeholders.TryGetValue(index, out var placeholder) ? placeholder : null;
        }

        private T NewPlaceholder(int index)
        {
            var placeholder = _newPlaceholder();
            _placeholders[index] = placeholder;
            return placeholder;
        }

        private void Forget(int index)
        {
            _placeholders.Remove(index);
            if (_pages.TryGetValue(index / PageSize, out var items))
            {
                items[index % PageSize] = null;
            }
        }

        private void Raise(NotifyCollectionChangedEventArgs args) => CollectionChanged?.Invoke(this, args);

        T IReadOnlyList<T>.this[int index] => (T)this[index];

        public IEnumerable LoadedItems
        {
            get
            {
                foreach (var page in _pages.OrderBy(pair => pair.Key))
                {
                    for (var offset = 0; offset < PageSize && page.Key * PageSize + offset < _count; offset++)
                    {
                        if (page.Value[offset] != null)
                        {
                            yield return page.Value[offset];
                        }
                    }
                }
            }
        }

        // IList for WPF: read-only, indexed access; enumeration hands out what is loaded and, for
        // every other index, one shared placeholder that is neither kept nor loaded for. Only
        // reading an index, as a virtualizing grid does for the rows it shows, loads.
        public bool IsReadOnly => true;

        public bool IsFixedSize => true;

        public bool IsSynchronized => false;

        public object SyncRoot => this;

        public int IndexOf(object value)
        {
            if (value is T item)
            {
                foreach (var page in _pages)
                {
                    var offset = Array.IndexOf(page.Value, item);
                    if (offset >= 0 && page.Key * PageSize + offset < _count)
                    {
                        return page.Key * PageSize + offset;
                    }
                }

                foreach (var placeholder in _placeholders)
                {
                    if (ReferenceEquals(placeholder.Value, item))
                    {
                        return placeholder.Key;
                    }
                }
            }

            return -1;
        }

        public bool Contains(object value) => IndexOf(value) >= 0;

        public IEnumerator<T> GetEnumerator()
        {
            for (var index = 0; index < _count; index++)
            {
                yield return Peek(index) ?? SharedPlaceholder;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private T _sharedPlaceholder;

        private T SharedPlaceholder => _sharedPlaceholder ?? (_sharedPlaceholder = _newPlaceholder());

        public void CopyTo(Array array, int index)
        {
            foreach (var item in this)
            {
                array.SetValue(item, index++);
            }
        }

        public int Add(object value) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public void Insert(int index, object value) => throw new NotSupportedException();

        public void Remove(object value) => throw new NotSupportedException();

        public void RemoveAt(int index) => throw new NotSupportedException();
    }
}
