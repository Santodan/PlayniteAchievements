using System.Collections.Generic;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// Gives one widget its own reveal state. Widgets draw from the shared overview snapshot, so
    /// revealing a row on the shared object revealed it in every other widget, on the start page
    /// and in the dashboard alike. Rows that can be revealed are swapped for a per-widget clone
    /// (<see cref="AchievementDisplayItem.Clone"/> starts unrevealed and shares the underlying
    /// achievement); every other row passes through untouched, since nothing about it can differ
    /// between widgets.
    /// </summary>
    /// <remarks>
    /// A clone is reused for as long as its source row is, so re-projecting the same snapshot
    /// hands back the same objects and the grids' reference-equality checks keep holding. A data
    /// rebuild replaces the source rows, which replaces their clones: reveals reset with it.
    /// </remarks>
    internal sealed class WidgetRevealScope
    {
        // AchievementDisplayItem does not override Equals, so these keys compare by reference.
        private Dictionary<AchievementDisplayItem, AchievementDisplayItem> _clones =
            new Dictionary<AchievementDisplayItem, AchievementDisplayItem>();

        public List<AchievementDisplayItem> Map(IEnumerable<AchievementDisplayItem> rows)
        {
            var next = new Dictionary<AchievementDisplayItem, AchievementDisplayItem>();
            var result = new List<AchievementDisplayItem>();
            foreach (var row in rows ?? new List<AchievementDisplayItem>())
            {
                if (row == null)
                {
                    continue;
                }

                if (!row.CanReveal)
                {
                    result.Add(row);
                    continue;
                }

                if (!_clones.TryGetValue(row, out var clone))
                {
                    clone = row.Clone();
                }

                next[row] = clone;
                result.Add(clone);
            }

            _clones = next;
            return result;
        }
    }
}
