using PlayniteAchievements.Services.Cache;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;

namespace PlayniteAchievements.Services.Database
{
    /// <summary>Rows whose order a grid's column header sets through the query.</summary>
    internal interface ISortableRows
    {
        /// <summary>Orders by <paramref name="sort"/>, or by the query's own order when null.</summary>
        void SortBy(AchievementSort? sort, bool descending);
    }

    /// <summary>
    /// The rows of one query as a list whose <see cref="Count"/> is the full row count from the
    /// first load, for a virtualizing grid. Reading an index that is not loaded returns a
    /// placeholder and loads its page and the pages on either side; when the page arrives, each
    /// placeholder handed out is replaced in place. A query change, a sort or a touching feed
    /// change reloads the count and the pages around the last read index in one read and applies
    /// them in place, keeping the other loaded pages until they are read again. All state is
    /// changed on the UI thread.
    /// </summary>
    internal sealed class PagedRows<T> : IList, INotifyCollectionChanged, ISortableRows, IDisposable
        where T : class
    {
        public const int PageSize = 100;

        /// <summary>Pages kept loaded; past it, the page farthest from the last read index is dropped.</summary>
        internal const int MaxLoadedPages = 10;

        private readonly Func<AchievementQuery, int> _countRows;
        private readonly Func<AchievementQuery, IReadOnlyList<T>> _loadRows;
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
        private AchievementQuery _effectiveQuery;
        private AchievementSort? _sort;
        private bool _descending;
        private int _count;
        private int _lastIndex;
        private int _version;
        private bool _pageLoading;
        private bool _resetting;
        private bool _resetAgain;
        private bool _disposed;

        /// <param name="countRows">Runs off the UI thread; the query's row count, ignoring its page.</param>
        /// <param name="loadRows">Runs off the UI thread; the rows of the query's page.</param>
        /// <param name="newPlaceholder">A row standing in for one not loaded yet.</param>
        /// <param name="maxRows">The most rows the list holds; 0 for no cap.</param>
        public PagedRows(
            AchievementDataFeed feed,
            AchievementQuery query,
            Func<AchievementQuery, int> countRows,
            Func<AchievementQuery, IReadOnlyList<T>> loadRows,
            Func<T> newPlaceholder,
            IReadScheduler scheduler,
            int maxRows = 0,
            TimeSpan? window = null,
            Action<Exception> onError = null)
        {
            _query = _effectiveQuery = query ?? throw new ArgumentNullException(nameof(query));
            _countRows = countRows ?? throw new ArgumentNullException(nameof(countRows));
            _loadRows = loadRows ?? throw new ArgumentNullException(nameof(loadRows));
            _newPlaceholder = newPlaceholder ?? throw new ArgumentNullException(nameof(newPlaceholder));
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            _maxRows = Math.Max(0, maxRows);
            _onError = onError;
            _subscription = feed?.Subscribe(() => _effectiveQuery, scheduler, window ?? LiveQuery<T>.DefaultWindow, Reset);
            Reset();
        }

        public event NotifyCollectionChangedEventHandler CollectionChanged;

        /// <summary>The query as given, without a column sort.</summary>
        public AchievementQuery Query => _query;

        public int Count => _count;

        /// <summary>Replaces the query and reloads, keeping the column sort. UI thread.</summary>
        public void SetQuery(AchievementQuery query)
        {
            _query = query ?? throw new ArgumentNullException(nameof(query));
            Requery();
        }

        public void SortBy(AchievementSort? sort, bool descending)
        {
            _sort = sort;
            _descending = descending;
            Requery();
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

        private void Requery()
        {
            _effectiveQuery = _sort.HasValue ? _query.OrderBy(_sort.Value, _descending) : _query;
            _version++;
            Reset();
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
            var query = _effectiveQuery;
            var version = _version;
            var anchor = _lastIndex / PageSize;
            _scheduler.Run(() =>
            {
                var count = 0;
                Dictionary<int, IReadOnlyList<T>> pages = null;
                try
                {
                    count = Cap(_countRows(query));
                    pages = new Dictionary<int, IReadOnlyList<T>>();
                    var last = Math.Max(0, (count - 1) / PageSize);
                    anchor = Math.Min(anchor, last);
                    for (var page = Math.Max(0, anchor - 1); page <= Math.Min(last, anchor + 1) && page * PageSize < count; page++)
                    {
                        pages[page] = _loadRows(PageOf(query, page, count));
                    }
                }
                catch (Exception ex)
                {
                    pages = null;
                    _onError?.Invoke(ex);
                }

                _scheduler.Post(() => ResetLoaded(version, count, pages));
            });
        }

        private void ResetLoaded(int version, int count, Dictionary<int, IReadOnlyList<T>> pages)
        {
            if (_disposed)
            {
                return;
            }

            // Rows read by the grid while the result is applied wait for the flag to clear.
            if (pages != null && version == _version)
            {
                Apply(count, pages);
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
            var query = _effectiveQuery;
            var version = _version;
            var count = _count;
            _scheduler.Run(() =>
            {
                IReadOnlyList<T> rows = null;
                try
                {
                    rows = _loadRows(PageOf(query, next, count));
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

        private int Cap(int count) => _maxRows > 0 ? Math.Min(Math.Max(0, count), _maxRows) : Math.Max(0, count);

        private static AchievementQuery PageOf(AchievementQuery query, int page, int count) =>
            query.Page(Math.Min(PageSize, count - page * PageSize), page * PageSize);

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

        // IList for WPF: read-only, indexed access; enumeration hands out what is loaded and
        // placeholders without loading anything.
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

        public IEnumerator GetEnumerator()
        {
            for (var index = 0; index < _count; index++)
            {
                yield return Peek(index) ?? NewPlaceholder(index);
            }
        }

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
