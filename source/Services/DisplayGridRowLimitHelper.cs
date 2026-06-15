using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services
{
    public static class DisplayGridRowLimitHelper
    {
        public static List<T> Limit<T>(IEnumerable<T> items, int? maxRows)
        {
            var list = items?.ToList() ?? new List<T>();
            var normalizedMaxRows = PersistedSettings.NormalizeGridMaxRows(maxRows);
            if (!normalizedMaxRows.HasValue || list.Count <= normalizedMaxRows.Value)
            {
                return list;
            }

            return list.Take(normalizedMaxRows.Value).ToList();
        }
    }
}
