using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Overview
{
    /// <summary>
    /// Brings rows a grid already shows onto a freshly built list without replacing them.
    /// </summary>
    /// <remarks>
    /// A grid handed new instances for the same achievements tears down and re-realizes every
    /// container, and loses whatever the rows carried for the session. Keeping the instances and
    /// updating them from the fresh ones leaves a collection sync with nothing to remove or add
    /// except the achievements that actually came or went.
    /// </remarks>
    internal static class KeyedRowMerge
    {
        /// <summary>
        /// The fresh list in its own order, with each row that has a match among
        /// <paramref name="existing"/> replaced by that existing instance after
        /// <paramref name="update"/> has brought it up to date. Rows without a match - new
        /// achievements, or rows with no key - are taken from the fresh list as they are.
        /// Existing rows with no fresh counterpart are dropped.
        /// </summary>
        /// <remarks>
        /// Keys compare case-insensitively. A key held by several rows pairs them up in order, so
        /// duplicates neither collapse onto one instance nor lose one.
        /// </remarks>
        public static List<T> Merge<T>(
            IReadOnlyList<T> existing,
            IReadOnlyList<T> fresh,
            Func<T, string> keySelector,
            Action<T, T> update)
            where T : class
        {
            if (keySelector == null)
            {
                throw new ArgumentNullException(nameof(keySelector));
            }

            if (update == null)
            {
                throw new ArgumentNullException(nameof(update));
            }

            var result = new List<T>(fresh?.Count ?? 0);
            if (fresh == null)
            {
                return result;
            }

            var byKey = new Dictionary<string, Queue<T>>(StringComparer.OrdinalIgnoreCase);
            if (existing != null)
            {
                for (var i = 0; i < existing.Count; i++)
                {
                    var row = existing[i];
                    var key = row == null ? null : keySelector(row);
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    if (!byKey.TryGetValue(key, out var queue))
                    {
                        queue = new Queue<T>();
                        byKey[key] = queue;
                    }

                    queue.Enqueue(row);
                }
            }

            for (var i = 0; i < fresh.Count; i++)
            {
                var freshRow = fresh[i];
                if (freshRow == null)
                {
                    continue;
                }

                var key = keySelector(freshRow);
                if (!string.IsNullOrWhiteSpace(key) &&
                    byKey.TryGetValue(key, out var queue) &&
                    queue.Count > 0)
                {
                    var kept = queue.Dequeue();
                    update(kept, freshRow);
                    result.Add(kept);
                    continue;
                }

                result.Add(freshRow);
            }

            return result;
        }
    }
}
