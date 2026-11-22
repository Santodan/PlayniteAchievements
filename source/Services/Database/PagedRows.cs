using PlayniteAchievements.Services.Cache;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
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

    /// <summary>A row list a virtualizing grid places by the rows it realizes.</summary>
    internal interface IRealizedRows
    {
        /// <summary>The grid realized the row at the index. UI thread.</summary>
        void Realized(int index);
    }

    /// <summary>
    /// The rows of one query as a list whose <see cref="Count"/> is the full row count from the
    /// first load. A load reads the sorted keys of every matching row and keeps them; a page is
    /// then the columns of a slice of those keys. Reading an index that is not loaded hands out a
    /// placeholder kept for that index and loads its page; when the page arrives, each
    /// placeholder handed out is brought up to date as the row (an <c>Item[]</c> change), or
    /// replaced when the list has no update.
    /// <para>
    /// How reads load: a read moves the anchor, and pages more than one page from the anchor are
    /// neither loaded nor kept past <see cref="MaxLoadedPages"/>, so a scrolled grid loads only
    /// where it stops. Pages a consumer holds every row of are kept instead: those of a sweep
    /// (more than <see cref="WholeLoadRows"/> consecutive indices read before any result is
    /// applied, as a list that does not virtualize reads), those enumeration hands placeholders
    /// out for, and those a binding reads again when the list announces a change. Kept pages
    /// load from the anchor onward and are never dropped.
    /// </para>
    /// <para>
    /// A virtualizing grid whose rows differ in height reads every index on each layout pass, so
    /// once it reports a realized row (<see cref="Realized"/>) only realized rows move the anchor
    /// and want pages, and its reads neither move it nor count as a sweep. For such a grid a
    /// landed page fills the placeholders it handed out one per idle turn, top down from the
    /// first row of the grid's latest run of realized rows: filling a row builds what shows it,
    /// and a whole page at once would do that in the frame of a drag step. Until its turn a
    /// placeholder stands in as before.
    /// </para>
    /// <para>
    /// A query change or a touching feed change reads the keys again with the pages around the
    /// anchor and every loaded page, and applies them in place: a row whose key is unchanged
    /// keeps its object, brought up to date, and any other row handed out is replaced. All state
    /// is changed on the UI thread.
    /// </para>
    /// </summary>
    internal sealed class PagedRows<T> : IList, IReadOnlyList<T>, INotifyCollectionChanged, INotifyPropertyChanged, IPartiallyLoadedRows, IRealizedRows, IDisposable, Common.IRetentionProbe
        where T : class
    {
        public const int PageSize = 100;

        /// <summary>Pages kept loaded for reads near the anchor; past it, the farthest is dropped.</summary>
        internal const int MaxLoadedPages = 10;

        /// <summary>
        /// A result of at most this many rows loads whole on every reset, so every row is loaded
        /// and <see cref="LoadedItems"/> is the whole list.
        /// </summary>
        internal const int WholeLoadRows = MaxLoadedPages * PageSize;

        private readonly Func<AchievementQuery, IReadOnlyList<AchievementQuerySql.RowKey>> _loadKeys;
        private readonly Func<AchievementQuery, IReadOnlyList<AchievementQuerySql.RowKey>, IReadOnlyList<T>> _loadRows;
        private readonly Func<T> _newPlaceholder;
        private readonly Action<T, T> _update;
        private readonly Action<T> _placed;
        private readonly IReadScheduler _scheduler;
        private readonly Action<Exception> _onError;
        private readonly IDisposable _subscription;
        private readonly int _maxRows;
        private readonly Dictionary<int, T[]> _pages = new Dictionary<int, T[]>();
        private readonly HashSet<int> _stalePages = new HashSet<int>();
        private readonly HashSet<int> _wantedPages = new HashSet<int>();
        private readonly HashSet<int> _keptPages = new HashSet<int>();
        private readonly Dictionary<int, T> _placeholders = new Dictionary<int, T>();
        // Rows read for placeholders handed out to a grid, waiting for their idle turn.
        private readonly Dictionary<int, T> _fills = new Dictionary<int, T>();
        private bool _filling;
        private AchievementQuery _query;
        private IReadOnlyList<AchievementQuerySql.RowKey> _keys = Array.Empty<AchievementQuerySql.RowKey>();
        private int _count;
        private int _anchor;
        private int _lastIndex = -1;
        // The grid realizes rows top down, so the anchor ends on the last one; fills start at the
        // first row of that run instead.
        private int _fillFrom;
        private int _lastRealized = -1;
        private int _runStart;
        private int _runApplies;
        private int _applies;
        private int _version;
        private bool _pageLoading;
        private bool _resetting;
        private bool _resetAgain;
        private bool _notifying;
        private bool _changed;
        private bool _realizing;
        private bool _disposed;
        private bool _keysLoaded;

        /// <param name="loadKeys">Runs off the UI thread; the keys of the query's rows in its order.</param>
        /// <param name="loadRows">Runs off the UI thread; the rows of the keys, in their order.</param>
        /// <param name="newPlaceholder">A row standing in for one not loaded yet.</param>
        /// <param name="maxRows">The most rows the list holds; 0 for no cap.</param>
        /// <param name="update">Brings a row or placeholder handed out up to date from a new read of its key; without it the row is replaced.</param>
        /// <param name="pendingRows">Placeholders the list holds until the first read lands; they read nothing.</param>
        /// <param name="gate">Holds back reloads for feed changes while closed (<see cref="AchievementDataFeed.Subscribe"/>).</param>
        /// <param name="placed">
        /// Called on the UI thread for each read row the list puts in place as itself, rather than
        /// copying it into a row or placeholder already handed out with <paramref name="update"/>.
        /// </param>
        public PagedRows(
            AchievementDataFeed feed,
            AchievementQuery query,
            Func<AchievementQuery, IReadOnlyList<AchievementQuerySql.RowKey>> loadKeys,
            Func<AchievementQuery, IReadOnlyList<AchievementQuerySql.RowKey>, IReadOnlyList<T>> loadRows,
            Func<T> newPlaceholder,
            IReadScheduler scheduler,
            int maxRows = 0,
            Func<TimeSpan> window = null,
            Action<Exception> onError = null,
            Action<T, T> update = null,
            int pendingRows = 0,
            FeedGate gate = null,
            Action<T> placed = null)
        {
            _query = query ?? throw new ArgumentNullException(nameof(query));
            _loadKeys = loadKeys ?? throw new ArgumentNullException(nameof(loadKeys));
            _loadRows = loadRows ?? throw new ArgumentNullException(nameof(loadRows));
            Common.RetentionProbes.Register(this);
            _newPlaceholder = newPlaceholder ?? throw new ArgumentNullException(nameof(newPlaceholder));
            _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            _maxRows = Math.Max(0, maxRows);
            _onError = onError;
            _update = update;
            _placed = placed;
            _count = Math.Max(0, pendingRows);
            _subscription = feed?.Subscribe(() => _query, scheduler, window ?? (() => LiveQuery<T>.DefaultWindow), Reset, gate);
            Reset();
        }

        public event NotifyCollectionChangedEventHandler CollectionChanged;

        public event PropertyChangedEventHandler PropertyChanged;

        public AchievementQuery Query => _query;

        public int Count => _count;

        /// <summary>Whether the first read has not landed yet; until then every row is a placeholder.</summary>
        public bool IsPending => !_keysLoaded;

        /// <summary>
        /// Replaces the query and reloads. With <paramref name="fromStart"/> the rows around the
        /// first index load first, for a list the grid shows from its top; otherwise those around
        /// the anchor. UI thread.
        /// </summary>
        public void SetQuery(AchievementQuery query, bool fromStart = false)
        {
            _query = query ?? throw new ArgumentNullException(nameof(query));
            if (fromStart)
            {
                _anchor = 0;
            }

            _version++;
            Reset();
        }

        /// <summary>Reads the same query again, for a list with no feed whose data changed. UI thread.</summary>
        public void Refresh() => Reset();

        public object this[int index]
        {
            get
            {
                if (index < 0 || index >= _count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                // Before the first read lands there are no keys to load pages of.
                if (!_keysLoaded)
                {
                    return Peek(index) ?? NewPlaceholder(index);
                }

                // A read only looks for a load when it wants or keeps a page it did not before.
                var page = index / PageSize;
                var load = false;
                if (_notifying)
                {
                    // A binding reading again because the list announced a change: the index is bound.
                    load = _keptPages.Add(page);
                }
                else if (_realizing)
                {
                    return Peek(index) ?? NewPlaceholder(index);
                }
                else
                {
                    if (index != _lastIndex + 1 || _runApplies != _applies)
                    {
                        _runStart = index;
                        _runApplies = _applies;
                    }

                    _lastIndex = index;
                    _anchor = index;
                    if (index - _runStart >= WholeLoadRows)
                    {
                        // A sweep: the consumer holds every row it read, from the first.
                        for (var swept = index - _runStart == WholeLoadRows ? _runStart / PageSize : page; swept <= page; swept++)
                        {
                            load |= _keptPages.Add(swept);
                        }

                        _anchor = _runStart;
                    }
                }

                if (Want(page - 1) | Want(page) | Want(page + 1) | load)
                {
                    LoadNextPage();
                }

                return Peek(index) ?? NewPlaceholder(index);
            }
            set => throw new NotSupportedException();
        }

        public void Realized(int index)
        {
            _realizing = true;
            if (index != _lastRealized + 1)
            {
                _fillFrom = index;
            }

            _lastRealized = index;
            if (!_keysLoaded || index < 0 || index >= _count)
            {
                return;
            }

            _anchor = index;
            var page = index / PageSize;
            if (Want(page - 1) | Want(page) | Want(page + 1))
            {
                LoadNextPage();
            }
        }

        public void Dispose()
        {
            _disposed = true;
            Common.RetentionProbes.Unregister(this);
            _subscription?.Dispose();
            _pages.Clear();
            _placeholders.Clear();
            _fills.Clear();
            _wantedPages.Clear();
            _keptPages.Clear();
        }

        string Common.IRetentionProbe.RetentionProbeName => "pagedRows";

        string Common.IRetentionProbe.DescribeRetention() =>
            $"rows={_count} pages={_pages.Count} placeholders={_placeholders.Count}";

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
            var anchor = _anchor / PageSize;
            var count = _count;
            var loaded = _pages.Keys.ToList();
            _scheduler.Run(() =>
            {
                IReadOnlyList<AchievementQuerySql.RowKey> keys = null;
                Dictionary<int, IReadOnlyList<T>> pages = null;
                try
                {
                    keys = _loadKeys(_maxRows > 0 ? query.Page(_maxRows) : query) ?? Array.Empty<AchievementQuerySql.RowKey>();
                    var last = Math.Max(0, (keys.Count - 1) / PageSize);
                    anchor = Math.Min(anchor, last);

                    // Small enough to keep: every row. Otherwise the pages around the anchor, and
                    // unless the list resets, every page loaded now so none shows old rows.
                    var read = keys.Count <= WholeLoadRows
                        ? Enumerable.Range(0, last + 1)
                        : Enumerable.Range(anchor - 1, 3).Concat(Math.Abs(keys.Count - count) > PageSize ? Enumerable.Empty<int>() : loaded);
                    pages = ReadPages(query, keys, read);
                }
                catch (Exception ex)
                {
                    pages = null;
                    _onError?.Invoke(ex);
                }

                _scheduler.Post(() => ResetLoaded(version, keys, pages));
            });
        }

        // The rows of the pages, a read for each run of consecutive pages.
        private Dictionary<int, IReadOnlyList<T>> ReadPages(AchievementQuery query, IReadOnlyList<AchievementQuerySql.RowKey> keys, IEnumerable<int> read)
        {
            var pages = new Dictionary<int, IReadOnlyList<T>>();
            var wanted = read.Where(page => page >= 0 && page * PageSize < keys.Count).Distinct().OrderBy(page => page).ToList();
            for (var at = 0; at < wanted.Count;)
            {
                var first = wanted[at];
                var last = first;
                while (++at < wanted.Count && wanted[at] == last + 1)
                {
                    last = wanted[at];
                }

                var rows = _loadRows(query, Slice(keys, first * PageSize, (last + 1) * PageSize)) ?? Array.Empty<T>();
                for (var page = first; page <= last; page++)
                {
                    pages[page] = rows.Skip((page - first) * PageSize).Take(PageSize).ToList();
                }
            }

            return pages;
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
                Apply(keys, pages);
            }
            else if (pages == null && !_keysLoaded)
            {
                // A failed first read leaves no placeholders standing.
                Apply(Array.Empty<AchievementQuerySql.RowKey>(), new Dictionary<int, IReadOnlyList<T>>());
            }

            _resetting = false;
            _applies++;

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
        private void Apply(IReadOnlyList<AchievementQuerySql.RowKey> keys, Dictionary<int, IReadOnlyList<T>> pages)
        {
            var oldKeys = _keys;
            var count = keys.Count;
            var oldCount = _count;
            _keys = keys;
            _keysLoaded = true;
            _version++;
            if (Math.Abs(count - _count) > PageSize)
            {
                _pages.Clear();
                _stalePages.Clear();
                _wantedPages.Clear();
                _keptPages.Clear();
                _placeholders.Clear();
                _fills.Clear();
                _count = count;
                foreach (var page in pages)
                {
                    _pages[page.Key] = Fill(new T[PageSize], page.Value);
                    if (_placed != null)
                    {
                        foreach (var row in page.Value.Take(PageSize))
                        {
                            _placed(row);
                        }
                    }
                }

                Raise(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
                Notify(oldCount);
                return;
            }

            while (_count > count)
            {
                var index = --_count;

                // Never handed out, the row is in no consumer; the placeholder the index reads stands in.
                var removed = Peek(index) ?? _newPlaceholder();
                Forget(index);
                Raise(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, index));
            }

            _keptPages.RemoveWhere(page => page * PageSize >= count);
            foreach (var page in _pages.Keys.ToList())
            {
                if (page * PageSize >= count)
                {
                    _pages.Remove(page);
                    _stalePages.Remove(page);
                }
                else if (!pages.ContainsKey(page))
                {
                    // Loaded while the keys were read: read again.
                    _stalePages.Add(page);
                    _wantedPages.Add(page);
                }
            }

            foreach (var page in pages)
            {
                Put(page.Key, page.Value, index =>
                    index < oldKeys.Count && SameKey(oldKeys[index], keys[index]));
            }

            while (_count < count)
            {
                var index = _count++;
                Raise(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, Peek(index) ?? NewPlaceholder(index), index));
            }

            Notify(oldCount);
        }

        private static bool SameKey(AchievementQuerySql.RowKey a, AchievementQuerySql.RowKey b) =>
            a.UserId == b.UserId && a.GameId == b.GameId && a.DefinitionId == b.DefinitionId &&
            a.PlayniteGameId == b.PlayniteGameId && a.ApiName == b.ApiName;

        // True when the page was not wanted yet.
        private bool Want(int page) =>
            page >= 0 && page * PageSize < _count && !IsLoaded(page) && _wantedPages.Add(page);

        private bool IsLoaded(int page) => _pages.ContainsKey(page) && !_stalePages.Contains(page);

        // Loads the page nearest the anchor among those wanted near it and the kept pages not
        // loaded yet, then the kept pages in order; other wanted pages are dropped, so a fast
        // scroll loads only where it stops.
        private void LoadNextPage()
        {
            if (_pageLoading || _resetting || _disposed)
            {
                return;
            }

            var anchor = _anchor / PageSize;
            _wantedPages.RemoveWhere(page =>
                page * PageSize >= _count ||
                IsLoaded(page) ||
                (Math.Abs(page - anchor) > 1 && !_keptPages.Contains(page)));
            var next = _wantedPages
                .Concat(_keptPages.Where(page => !_pages.ContainsKey(page) && page * PageSize < _count))
                .OrderBy(page => Math.Min(Math.Abs(page - anchor), 2))
                .ThenBy(page => page)
                .Select(page => (int?)page)
                .FirstOrDefault();
            if (next == null)
            {
                return;
            }

            var load = next.Value;
            _wantedPages.Remove(load);
            _pageLoading = true;
            var query = _query;
            var version = _version;
            var keys = Slice(_keys, load * PageSize, (load + 1) * PageSize);
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

                _scheduler.Post(() => PageLoaded(version, load, rows));
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
                Put(page, rows, null);
                DropFarPages();
                Notify(_count);
            }

            _pageLoading = false;
            _applies++;
            LoadNextPage();
        }

        // Puts a read page in place. A row handed out whose key is unchanged is brought up to
        // date; any other row handed out is replaced.
        private void Put(int page, IReadOnlyList<T> rows, Func<int, bool> sameKey)
        {
            _pages.TryGetValue(page, out var old);
            var items = old == null ? new T[PageSize] : (T[])old.Clone();
            _pages[page] = items;
            if (rows.Count < Math.Min(PageSize, _keys.Count - page * PageSize))
            {
                // The rows moved between the keys and the page read; the page reloads when read.
                _stalePages.Add(page);
            }
            else
            {
                _stalePages.Remove(page);
            }

            for (var offset = 0; offset < rows.Count && offset < PageSize; offset++)
            {
                var index = page * PageSize + offset;
                var kept = old?[offset];
                var before = kept;
                if (before == null && _placeholders.TryGetValue(index, out var placeholder))
                {
                    before = placeholder;
                }

                if (kept == null && before != null && _update != null && index < _count && _realizing)
                {
                    // The placeholder stays where it is until its turn.
                    _fills[index] = rows[offset];
                    continue;
                }

                _placeholders.Remove(index);
                _fills.Remove(index);
                if (kept != null && _update != null && sameKey?.Invoke(index) == true)
                {
                    _update(kept, rows[offset]);
                    continue;
                }

                if (kept == null && before != null && _update != null && index < _count)
                {
                    // A placeholder handed out becomes the row, so no consumer rebuilds what shows it.
                    _update(before, rows[offset]);
                    items[offset] = before;
                    _changed = true;
                    continue;
                }

                items[offset] = rows[offset];
                _placed?.Invoke(rows[offset]);
                if (before != null && index < _count && !ReferenceEquals(before, rows[offset]))
                {
                    Raise(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, rows[offset], before, index));
                }
            }

            ScheduleFill();
        }

        private void ScheduleFill()
        {
            if (!_filling && _fills.Count > 0)
            {
                _filling = true;
                _scheduler.PostIdle(FillNext);
            }
        }

        // Fills the waiting placeholders top down: first those from the start of the latest
        // realized run, then the rest. One whose page was dropped or read again since is left to that.
        private void FillNext()
        {
            _filling = false;
            if (_disposed)
            {
                return;
            }

            var index = -1;
            foreach (var waiting in _fills.Keys)
            {
                var inRun = waiting >= _fillFrom;
                if (index < 0 || (inRun && index < _fillFrom) || (inRun == (index >= _fillFrom) && waiting < index))
                {
                    index = waiting;
                }
            }

            if (index < 0)
            {
                return;
            }

            var row = _fills[index];
            _fills.Remove(index);
            if (index < _count &&
                _placeholders.TryGetValue(index, out var placeholder) &&
                _pages.TryGetValue(index / PageSize, out var items) &&
                items[index % PageSize] == null)
            {
                _placeholders.Remove(index);
                _update(placeholder, row);
                items[index % PageSize] = placeholder;
                _changed = true;
            }

            if (_fills.Count > 0)
            {
                ScheduleFill();
            }
            else
            {
                Notify(_count);
            }
        }

        // Far pages go back to unloaded without an event: no virtualizing consumer holds rows
        // that far from the anchor, and a kept page is never dropped.
        private void DropFarPages()
        {
            var anchor = _anchor / PageSize;
            foreach (var far in _pages.Keys
                .Where(page => !_keptPages.Contains(page))
                .OrderBy(page => Math.Abs(page - anchor))
                .Skip(MaxLoadedPages)
                .ToList())
            {
                _pages.Remove(far);
                _stalePages.Remove(far);
                foreach (var waiting in _fills.Keys.Where(index => index / PageSize == far).ToList())
                {
                    _fills.Remove(waiting);
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
            keys.Skip(from).Take(Math.Max(0, Math.Min(keys.Count, to) - from)).ToList();

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
            _fills.Remove(index);
            if (_pages.TryGetValue(index / PageSize, out var items))
            {
                items[index % PageSize] = null;
            }
        }

        // Bindings that read the list again while it announces a change mark what they read as bound.
        private void Raise(NotifyCollectionChangedEventArgs args)
        {
            _changed = true;
            _notifying = true;
            try
            {
                CollectionChanged?.Invoke(this, args);
            }
            finally
            {
                _notifying = false;
            }
        }

        // Count and indexer bindings, after a change of what the indices hold.
        private void Notify(int oldCount)
        {
            if (!_changed)
            {
                return;
            }

            _changed = false;
            _notifying = true;
            try
            {
                if (oldCount != _count)
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
                }

                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            }
            finally
            {
                _notifying = false;
            }
        }

        T IReadOnlyList<T>.this[int index] => (T)this[index];

        public IEnumerable LoadedItems
        {
            get
            {
                foreach (var page in _pages.Where(pair => !_stalePages.Contains(pair.Key)).OrderBy(pair => pair.Key))
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

        // IList for WPF: read-only, indexed access.
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

        // Enumeration hands out what the indexer would and keeps the pages of the placeholders
        // it hands out, as a consumer that copies the list holds every row.
        public IEnumerator<T> GetEnumerator()
        {
            for (var index = 0; index < _count; index++)
            {
                var item = Peek(index);
                if (item == null)
                {
                    item = NewPlaceholder(index);
                    if (_keysLoaded && _keptPages.Add(index / PageSize))
                    {
                        LoadNextPage();
                    }
                }

                yield return item;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

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
