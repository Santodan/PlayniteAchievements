using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PlayniteAchievements.Common
{
    public static class CollectionHelper
    {
        /// <summary>
        /// The most per-row notifications <see cref="Replace{T}"/> raises before it gives up on
        /// patching a <see cref="BulkObservableCollection{T}"/> in place and resets it instead.
        /// </summary>
        /// <remarks>
        /// A bound DataGrid handles each notification with work proportional to the rows it
        /// holds: on the friends overview's 65k-row recent-unlocks grid one Remove or Add cost
        /// about 1.5 ms (2026-09-28 log, 64 to 128 s stalls per click). This many notifications
        /// therefore costs under 200 ms there before the Reset takes over, while a delta of a
        /// hundred rows on a grid of a few thousand still goes through incrementally.
        /// </remarks>
        private const int MaxIncrementalNotifications = 128;

        /// <summary>
        /// Brings a collection to the given contents. Items that are already present are moved
        /// into place rather than re-added, so a change that keeps most row instances raises one
        /// notification per row that actually moved. A change that replaces most rows resets a
        /// <see cref="BulkObservableCollection{T}"/> in one event instead.
        /// </summary>
        /// <remarks>
        /// A bound grid answers a Reset by re-realizing its viewport, and that render lands on a
        /// later dispatcher pass -- invisible to any scope around this call, and visible only as a
        /// UI stall. The overview rebuilds one game's row per edit and leaves every other row the
        /// same instance, so a Reset paid a full viewport rebuild to move one row; that is the
        /// case the in-place sync exists for.
        ///
        /// The trade runs the other way when the rows turn over. The friends overview swaps its
        /// achievements grid between the whole recent-unlocks feed and one friend's or game's
        /// subset on every click, and its games grid to a different friend's rows, all new
        /// instances. Patching that in place is one Remove or Add per row, each handled by the
        /// grid on the UI thread, and on a 65k-row feed it froze Playnite for one to two minutes
        /// per click. So the decision is made from the diff itself before anything is mutated:
        /// when more rows come or go than survive, the one Reset is the cheaper event.
        ///
        /// A sort flip keeps every instance and moves nearly all of them, which the count above
        /// cannot see, so the in-place pass is also bounded by
        /// <see cref="MaxIncrementalNotifications"/> and finishes with a Reset past it. Both
        /// fallbacks need <see cref="BulkObservableCollection{T}.ReplaceAll"/>; a plain
        /// <see cref="ObservableCollection{T}"/> is always synchronized in full.
        /// </remarks>
        public static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
        {
            if (target == null)
            {
                return;
            }

            var sourceList = items as IList<T> ?? (items ?? Enumerable.Empty<T>()).ToList();
            if (!(target is BulkObservableCollection<T> bulk))
            {
                Synchronize(target, sourceList, int.MaxValue);
                return;
            }

            if (IsTurnover(target, sourceList) ||
                !Synchronize(target, sourceList, MaxIncrementalNotifications))
            {
                bulk.ReplaceAll(sourceList);
            }
        }

        /// <summary>
        /// True when more items would be removed or added than are kept, which is the shape of a
        /// wholesale replacement rather than an edit.
        /// </summary>
        private static bool IsTurnover<T>(ObservableCollection<T> collection, IList<T> sourceList)
        {
            var wanted = new HashSet<T>(sourceList);
            var survivors = 0;
            for (var i = 0; i < collection.Count; i++)
            {
                if (wanted.Contains(collection[i]))
                {
                    survivors++;
                }
            }

            var removed = collection.Count - survivors;
            var added = sourceList.Count - survivors;
            return removed + added > survivors;
        }

        /// <summary>
        /// Brings <paramref name="collection"/> to the contents and order of
        /// <paramref name="source"/> in place, never clearing and re-adding.
        /// </summary>
        /// <remarks>
        /// Linear in the number of items that actually moved: an item already in the right
        /// place costs a comparison and nothing else, so the common case here - one rebuilt row
        /// among hundreds of unchanged instances - raises one or two notifications.
        ///
        /// The earlier version searched forward through the collection for each misplaced item,
        /// which is quadratic once a run of items is out of order. This keeps a position map
        /// instead and repairs only the span a move or insert shifts.
        ///
        /// Items are matched by equality, so a sequence holding the same item twice is not
        /// supported; every caller here holds distinct row instances.
        /// </remarks>
        public static void SynchronizeCollection<T>(ObservableCollection<T> collection, IEnumerable<T> source)
        {
            if (collection == null)
            {
                return;
            }

            var sourceList = source as IList<T> ?? (source ?? Enumerable.Empty<T>()).ToList();
            Synchronize(collection, sourceList, int.MaxValue);
        }

        /// <summary>
        /// The in-place pass behind <see cref="SynchronizeCollection{T}"/> and
        /// <see cref="Replace{T}"/>. Returns false once it has raised more than
        /// <paramref name="maxNotifications"/> collection changes, leaving the collection
        /// partially synchronized for the caller to finish with a Reset.
        /// </summary>
        private static bool Synchronize<T>(ObservableCollection<T> collection, IList<T> sourceList, int maxNotifications)
        {
            var comparer = EqualityComparer<T>.Default;
            var notifications = 0;

            // Anything the source does not want, back to front so indices stay valid.
            if (collection.Count > 0)
            {
                var wanted = new HashSet<T>(sourceList);
                for (var i = collection.Count - 1; i >= 0; i--)
                {
                    if (!wanted.Contains(collection[i]))
                    {
                        if (++notifications > maxNotifications)
                        {
                            return false;
                        }

                        collection.RemoveAt(i);
                    }
                }
            }

            // Where each surviving item currently sits, so a misplaced one is found without
            // scanning for it.
            var positions = new Dictionary<T, int>(collection.Count, comparer);
            for (var i = 0; i < collection.Count; i++)
            {
                positions[collection[i]] = i;
            }

            for (var target = 0; target < sourceList.Count; target++)
            {
                var wantedItem = sourceList[target];
                if (target < collection.Count && comparer.Equals(collection[target], wantedItem))
                {
                    continue;
                }

                if (positions.TryGetValue(wantedItem, out var from))
                {
                    if (from == target)
                    {
                        continue;
                    }

                    if (++notifications > maxNotifications)
                    {
                        return false;
                    }

                    collection.Move(from, target);

                    // A move only shifts the span it passed over.
                    var low = Math.Min(target, from);
                    var high = Math.Max(target, from);
                    for (var i = low; i <= high; i++)
                    {
                        positions[collection[i]] = i;
                    }

                    continue;
                }

                if (++notifications > maxNotifications)
                {
                    return false;
                }

                // Appending is the cheap case and needs no repair; an insert shifts the tail.
                if (target >= collection.Count)
                {
                    collection.Add(wantedItem);
                    positions[wantedItem] = collection.Count - 1;
                    continue;
                }

                collection.Insert(target, wantedItem);
                for (var i = target; i < collection.Count; i++)
                {
                    positions[collection[i]] = i;
                }
            }

            return true;
        }

        /// <summary>
        /// Synchronizes by index so virtualized item containers can keep their existing view-model instances.
        /// </summary>
        public static void SynchronizeReferenceCollectionByPosition<T>(
            IList<T> collection,
            IList<T> source,
            Action<T, T> updateExisting)
            where T : class
        {
            if (collection == null)
            {
                return;
            }

            source ??= new List<T>();

            var sharedCount = Math.Min(collection.Count, source.Count);
            for (int i = 0; i < sharedCount; i++)
            {
                if (collection[i] != null && source[i] != null && updateExisting != null)
                {
                    updateExisting(collection[i], source[i]);
                }
                else
                {
                    collection[i] = source[i];
                }
            }

            while (collection.Count > source.Count)
            {
                collection.RemoveAt(collection.Count - 1);
            }

            for (int i = collection.Count; i < source.Count; i++)
            {
                collection.Add(source[i]);
            }
        }

        /// <summary>
        /// The fresh list in its own order, with each row that has a match among
        /// <paramref name="existing"/> replaced by that existing instance after
        /// <paramref name="updateExisting"/> has brought it up to date. Rows without a match - new
        /// items, or items with no key - are taken from the fresh list as they are, and existing
        /// rows with no fresh counterpart are dropped.
        /// </summary>
        /// <remarks>
        /// The keyed counterpart of <see cref="SynchronizeReferenceCollectionByPosition{T}"/>, for a
        /// rebuild that should keep each row's identity rather than its slot: handing a grid new
        /// instances for the same items tears down and re-realizes every container and drops what
        /// the rows carried for the session. The result is a plain list, so the caller's own
        /// <see cref="SynchronizeCollection{T}"/> then has only the items that came or went to move.
        ///
        /// Keys compare case-insensitively. A key held by several rows pairs them up in order, so
        /// duplicates neither collapse onto one instance nor lose one.
        /// </remarks>
        public static List<T> MergeByKey<T>(
            IReadOnlyList<T> existing,
            IReadOnlyList<T> fresh,
            Func<T, string> keySelector,
            Action<T, T> updateExisting)
            where T : class
        {
            if (keySelector == null)
            {
                throw new ArgumentNullException(nameof(keySelector));
            }

            if (updateExisting == null)
            {
                throw new ArgumentNullException(nameof(updateExisting));
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
                    updateExisting(kept, freshRow);
                    result.Add(kept);
                    continue;
                }

                result.Add(freshRow);
            }

            return result;
        }

        /// <summary>
        /// Efficiently synchronizes a collection of value types.
        /// </summary>
        public static void SynchronizeValueCollection<T>(IList<T> collection, IList<T> source)
        {
            // Add or update
            for (int i = 0; i < source.Count; i++)
            {
                var item = source[i];
                if (i < collection.Count)
                {
                    if (!EqualityComparer<T>.Default.Equals(collection[i], item))
                    {
                        collection[i] = item;
                    }
                }
                else
                {
                    collection.Add(item);
                }
            }

            // Remove surplus
            while (collection.Count > source.Count)
            {
                collection.RemoveAt(collection.Count - 1);
            }
        }
    }
}
