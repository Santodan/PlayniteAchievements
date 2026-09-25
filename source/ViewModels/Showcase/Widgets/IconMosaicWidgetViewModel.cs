using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// Backs the IconMosaic widget by reusing AchievementCompactItemControl for each achievement.
    /// Icon size follows density (compact 32 / standard 42 / expanded 54); every size shows the
    /// same icons. Rarity-glow appearance comes from the widget's own per-instance options.
    /// </summary>
    public sealed class IconMosaicWidgetViewModel : ShowcaseWidgetViewModelBase
    {
        private double _iconSize = 42;
        private bool _showRarityGlow = true;
        private bool _animateRarityGlows = true;
        private bool _glowWhenLocked;

        public BulkObservableCollection<AchievementDisplayItem> Items { get; } =
            new BulkObservableCollection<AchievementDisplayItem>();

        public double IconSize { get => _iconSize; private set => SetValue(ref _iconSize, value); }

        public bool ShowRarityGlow { get => _showRarityGlow; private set => SetValue(ref _showRarityGlow, value); }

        /// <summary>
        /// Whether the tiles may glow while locked. Every other source shows earned achievements,
        /// where the glow marks the unlock; Unlock Next shows nothing but locked achievements, so
        /// without this its rarity glow option would do nothing at all.
        /// </summary>
        public bool GlowWhenLocked { get => _glowWhenLocked; private set => SetValue(ref _glowWhenLocked, value); }

        public bool AnimateRarityGlows { get => _animateRarityGlows; private set => SetValue(ref _animateRarityGlows, value); }

        protected override void Refresh()
        {
            var achievements = Projection?.MosaicAchievements ?? Array.Empty<AchievementDisplayItem>();
            IconSize = Density == WidgetViewportDensity.Compact
                ? 32
                : Density == WidgetViewportDensity.Expanded ? 54 : 42;

            // Glow on/off is a per-widget option; the glow ANIMATION stays a global setting.
            ShowRarityGlow = ShowcaseWidgetOptions.GetMosaicShowRarityGlow(Projection?.Instance);
            GlowWhenLocked = ShowcaseWidgetOptions.GetMosaicSource(Projection?.Instance) ==
                ShowcaseMosaicSource.UnlockNext;
            AnimateRarityGlows =
                PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.AnimateRarityGlows ?? true;

            var next = OrderAchievements(achievements).ToList();
            CarryRevealsForward(next);
            CollectionHelper.Replace(Items, next);
        }

        /// <summary>
        /// A tile revealed by a click stays revealed when a snapshot rebuild hands the mosaic new
        /// row objects for the same achievements, as the compact lists keep theirs across item
        /// rebuilds. Rows the projection hands back unchanged already carry their state.
        /// </summary>
        private void CarryRevealsForward(IReadOnlyList<AchievementDisplayItem> next)
        {
            var revealed = new HashSet<string>(
                Items.Where(item => item?.IsRevealed == true).Select(RevealKey),
                StringComparer.OrdinalIgnoreCase);
            if (revealed.Count == 0)
            {
                return;
            }

            foreach (var item in next)
            {
                if (item != null && !item.IsRevealed && revealed.Contains(RevealKey(item)))
                {
                    item.IsRevealed = true;
                }
            }
        }

        private static string RevealKey(AchievementDisplayItem item) =>
            AchievementDisplayItem.MakeRevealKey(item.PlayniteGameId, item.ApiName, item.GameName);

        /// <summary>
        /// Applies the widget's configured sort over the projected tiles. None preserves the
        /// source order the mosaic's Source produced, so sorting stays an opt-in re-arrangement
        /// of one widget rather than a re-projection of the dashboard.
        /// </summary>
        private IEnumerable<AchievementDisplayItem> OrderAchievements(
            IEnumerable<AchievementDisplayItem> achievements)
        {
            var spec = new AchievementSortSpec(
                ShowcaseWidgetOptions.GetMosaicSort(Projection?.Instance),
                ShowcaseWidgetOptions.GetMosaicSortDescending(Projection?.Instance)
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending);
            if (spec.PreservesSourceOrder)
            {
                return achievements;
            }

            var list = achievements.Where(item => item != null).ToList();
            var comparison = AchievementSortHelper.GetComparison(
                spec.SortMemberPath,
                spec.Direction,
                AchievementSortScope.RecentAchievements);
            if (comparison == null)
            {
                return list;
            }

            list.Sort(AchievementSortHelper.WithStableOrder(
                comparison,
                AchievementSortHelper.CreateStableOrderMap(list)));
            return list;
        }
    }
}
