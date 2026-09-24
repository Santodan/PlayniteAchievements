using PlayniteAchievements.Services.Cache;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// The single ordering for cached unlock rows, shared by the three places that must agree:
    /// the SQL read (ORDER BY ua.UnlockTimeUtc DESC, lp.CacheKey, ad.ApiName), the custom
    /// achievement merge that appends to that result, and the per-game patcher that splices one
    /// game's rows into an existing one.
    ///
    /// They have to agree because a patched summary is asserted to equal a full rebuild. A sort
    /// on the timestamp alone leaves ties in whatever order the input happened to be in, which a
    /// patch cannot reproduce -- so the tie-break is part of the contract, not an implementation
    /// detail of the query.
    /// </summary>
    internal static class RecentUnlockOrder
    {
        public static readonly IComparer<CachedRecentUnlockData> Comparer = new RowComparer();

        /// <summary>
        /// Stable in the only sense that matters here: total, so no two distinct rows compare
        /// equal unless they genuinely share all three keys.
        /// </summary>
        private sealed class RowComparer : IComparer<CachedRecentUnlockData>
        {
            public int Compare(CachedRecentUnlockData x, CachedRecentUnlockData y)
            {
                if (ReferenceEquals(x, y))
                {
                    return 0;
                }

                // Nulls sort last rather than throwing; the lists these order are built from
                // query rows and merge output, and a null there is a bug elsewhere.
                if (x == null)
                {
                    return 1;
                }

                if (y == null)
                {
                    return -1;
                }

                // Newest first, matching the DESC in the query.
                var left = x.UnlockTimeUtc ?? DateTime.MinValue;
                var right = y.UnlockTimeUtc ?? DateTime.MinValue;
                var byTime = right.CompareTo(left);
                if (byTime != 0)
                {
                    return byTime;
                }

                var byCacheKey = string.CompareOrdinal(x.CacheKey ?? string.Empty, y.CacheKey ?? string.Empty);
                if (byCacheKey != 0)
                {
                    return byCacheKey;
                }

                return string.CompareOrdinal(x.ApiName ?? string.Empty, y.ApiName ?? string.Empty);
            }
        }

        /// <summary>
        /// Returns a new list in canonical order. Never sorts in place: these lists are shared
        /// between a memoized summary and the copies patched from it.
        /// </summary>
        public static List<CachedRecentUnlockData> Sorted(IEnumerable<CachedRecentUnlockData> rows)
        {
            var result = rows == null
                ? new List<CachedRecentUnlockData>()
                : new List<CachedRecentUnlockData>(rows);
            result.Sort(Comparer);
            return result;
        }
    }
}
