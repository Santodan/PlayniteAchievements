using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.ViewModels.Items
{
    /// <summary>
    /// Rows standing in for a grid's rows until its first read lands; the grid cell styles draw a
    /// skeleton shape in each of their cells. They are never sorted, searched, selected or counted.
    /// </summary>
    public static class PlaceholderRows
    {
        /// <summary>Enough rows to fill a grid's viewport.</summary>
        public const int Count = 12;

        public static List<T> Create<T>(Func<T> create) =>
            Enumerable.Range(0, Count).Select(_ => create()).ToList();
    }
}
