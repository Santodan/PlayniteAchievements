using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Counts how many instances of a tracked kind are still reachable after a collection.
    /// Process totals say memory grew; this says which object graph is still rooted. Tracking
    /// holds only weak references, so it never keeps an instance alive itself.
    /// Gated by <see cref="MemoryDiagnostics.Enabled"/>.
    /// </summary>
    internal static class LeakWatch
    {
        // Per kind, so one chatty kind cannot crowd the others out.
        private const int MaxTrackedPerKind = 256;

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, List<WeakReference>> Tracked =
            new Dictionary<string, List<WeakReference>>(StringComparer.Ordinal);

        // Total ever tracked per kind. The weak-reference list is trimmed at
        // MaxTrackedPerKind, so its length stops being the number created as soon as a kind
        // passes the cap -- a leaking kind would read "256/256" and hide how fast it grows.
        private static readonly Dictionary<string, long> SeenTotals =
            new Dictionary<string, long>(StringComparer.Ordinal);

        /// <summary>
        /// Tracks every instance in a set under one kind, taking the lock once. Use where the
        /// interesting question is "did any of these survive", not "did this one survive": a
        /// single-instance canary reports nothing at all when the set it sampled is empty, which
        /// is how the canary for a replaced row set stayed silent through a session that grew the
        /// heap by 790 MB.
        /// </summary>
#if TEST
        /// <summary>Test-only: clears the static tracking state so cases do not bleed.</summary>
        internal static void ResetForTests()
        {
            lock (Sync)
            {
                Tracked.Clear();
                SeenTotals.Clear();
            }
        }
#endif

        public static void TrackAll(string kind, System.Collections.IEnumerable instances)
        {
            if (!MemoryDiagnostics.Enabled || instances == null || string.IsNullOrWhiteSpace(kind))
            {
                return;
            }

            lock (Sync)
            {
                foreach (var instance in instances)
                {
                    if (instance != null)
                    {
                        AddLocked(kind, instance);
                    }
                }
            }
        }

        public static void Track(string kind, object instance)
        {
            if (!MemoryDiagnostics.Enabled || instance == null || string.IsNullOrWhiteSpace(kind))
            {
                return;
            }

            lock (Sync)
            {
                AddLocked(kind, instance);
            }
        }

        private static void AddLocked(string kind, object instance)
        {
            if (!Tracked.TryGetValue(kind, out var list))
            {
                list = new List<WeakReference>();
                Tracked[kind] = list;
            }

            list.Add(new WeakReference(instance));
            SeenTotals.TryGetValue(kind, out var seen);
            SeenTotals[kind] = seen + 1;
            if (list.Count <= MaxTrackedPerKind)
            {
                return;
            }

            // Drop collected entries first; only trim live ones if still over budget, and
            // from the oldest end (an old instance still alive is the interesting one, but
            // an unbounded list would itself become a memory problem).
            list.RemoveAll(reference => !reference.IsAlive);
            if (list.Count > MaxTrackedPerKind)
            {
                list.RemoveRange(0, list.Count - MaxTrackedPerKind);
            }
        }

        /// <summary>
        /// Live instance count per tracked kind, as "kind:live/created", where created is the
        /// running total ever tracked rather than the trimmed list length. Call after forcing a
        /// collection so the counts mean "still rooted" rather than "not yet collected".
        /// </summary>
        public static string DescribeLive()
        {
            if (!MemoryDiagnostics.Enabled)
            {
                return string.Empty;
            }

            lock (Sync)
            {
                if (Tracked.Count == 0)
                {
                    return "none";
                }

                var parts = new List<string>();
                foreach (var pair in Tracked.OrderBy(entry => entry.Key, StringComparer.Ordinal))
                {
                    var live = pair.Value.Count(reference => reference.IsAlive);
                    SeenTotals.TryGetValue(pair.Key, out var seen);
                    parts.Add($"{pair.Key}:{live}/{seen}");
                }

                return string.Join(",", parts);
            }
        }
    }
}
