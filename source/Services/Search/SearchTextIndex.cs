using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Search
{
    public sealed class SearchTextIndex<T>
    {
        private readonly Func<T, string> _textBuilder;
        private readonly IEqualityComparer<T> _comparer;
        private readonly Dictionary<T, string> _textByItem;

        public SearchTextIndex(Func<T, string> textBuilder)
            : this(textBuilder, null)
        {
        }

        public SearchTextIndex(Func<T, string> textBuilder, IEqualityComparer<T> comparer)
        {
            _textBuilder = textBuilder ?? throw new ArgumentNullException(nameof(textBuilder));
            _comparer = comparer ?? EqualityComparer<T>.Default;
            _textByItem = new Dictionary<T, string>(_comparer);
        }

        public void Rebuild(IEnumerable<T> items)
        {
            LoadEntries(BuildEntries(items));
        }

        /// <summary>
        /// Builds the normalized search-text map for the given items without touching this
        /// index's live state, so it can safely run off the UI thread (e.g. inside the
        /// background snapshot build). Pair with <see cref="LoadEntries"/> on the UI thread.
        /// </summary>
        public Dictionary<T, string> BuildEntries(IEnumerable<T> items)
        {
            var map = new Dictionary<T, string>(_comparer);

            foreach (var item in items ?? Enumerable.Empty<T>())
            {
                if (item == null)
                {
                    continue;
                }

                map[item] = Normalize(_textBuilder(item));
            }

            return map;
        }

        /// <summary>
        /// Replaces this index's contents with a prebuilt map (from <see cref="BuildEntries"/>).
        /// Cheap enough to run on the UI thread since no text is computed here.
        /// </summary>
        public void LoadEntries(Dictionary<T, string> entries)
        {
            _textByItem.Clear();

            if (entries == null)
            {
                return;
            }

            foreach (var pair in entries)
            {
                _textByItem[pair.Key] = pair.Value;
            }
        }

        public void Clear()
        {
            _textByItem.Clear();
        }

        public void Invalidate(T item)
        {
            if (item == null)
            {
                return;
            }

            _textByItem.Remove(item);
        }

        public bool Matches(T item, SearchQuery query)
        {
            if (!query.HasValue)
            {
                return true;
            }

            if (item == null)
            {
                return false;
            }

            return query.Matches(GetText(item));
        }

        private string GetText(T item)
        {
            if (!_textByItem.TryGetValue(item, out var text))
            {
                text = Normalize(_textBuilder(item));
                _textByItem[item] = text;
            }

            return text;
        }

        private static string Normalize(string value)
        {
            return value ?? string.Empty;
        }
    }
}
