using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services
{
    public static class DisplayGridRowLimitHelper
    {
        public static List<T> Limit<T>(IEnumerable<T> items, int? maxRows)
        {
            if (items == null)
            {
                return new List<T>();
            }

            // Take before materializing. Copying the whole sequence first and then trimming it
            // allocated the full library twice on every overview delta tick, which is the same
            // work the row limit exists to avoid. Take yields everything when the sequence is
            // shorter than the limit, so the no-limit and under-limit results are unchanged.
            var normalizedMaxRows = PersistedSettings.NormalizeGridMaxRows(maxRows);
            return normalizedMaxRows.HasValue
                ? items.Take(normalizedMaxRows.Value).ToList()
                : items.ToList();
        }
    }
}
