using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PlayniteAchievements.Common
{
    public static class CollectionHelper
    {
        /// <summary>
        /// Brings a collection to the given contents in place, raising one notification per item
        /// that actually moved rather than a single Reset.
        /// </summary>
        /// <remarks>
        /// This used to call <see cref="BulkObservableCollection{T}.ReplaceAll"/> when it could,
        /// which raises one Reset. A bound grid answers a Reset by re-realizing its viewport, and
        /// that render lands on a later dispatcher pass -- invisible to any scope around this
        /// call, and visible only as a UI stall. The overview rebuilds one game's row per edit
        /// and leaves every other row the same instance, so a Reset paid a full viewport rebuild
        /// to move one row.
        ///
        /// The trade runs the other way for a wholesale replacement, where every item differs:
        /// there the Reset is one event and this is one per row. <see cref="SynchronizeCollection"/>
        /// is linear in the number of items that moved, so it stays proportional to the real
        /// change either way, but a full turnover is still a notification per row.
        /// </remarks>
        public static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
        {
            SynchronizeCollection(target, items);
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
            var comparer = EqualityComparer<T>.Default;

            // Anything the source does not want, back to front so indices stay valid.
            if (collection.Count > 0)
            {
                var wanted = new HashSet<T>(sourceList);
                for (var i = collection.Count - 1; i >= 0; i--)
                {
                    if (!wanted.Contains(collection[i]))
                    {
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
