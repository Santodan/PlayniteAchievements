using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
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
        private Thickness _tileMargin = new Thickness(6);
        private bool _isSeamless;
        private bool _showRarityGlow = true;
        private bool _showRarityBar;
        private bool _animateRarityGlows = true;
        private bool _glowWhenLocked;
        private readonly WidgetRevealScope _reveals = new WidgetRevealScope();

        public BulkObservableCollection<AchievementDisplayItem> Items { get; } =
            new BulkObservableCollection<AchievementDisplayItem>();

        public double IconSize { get => _iconSize; private set => SetValue(ref _iconSize, value); }

        /// <summary>Space around each tile, from the widget's spacing option.</summary>
        public Thickness TileMargin { get => _tileMargin; private set => SetValue(ref _tileMargin, value); }

        /// <summary>
        /// Zero spacing: the wrap panel also stops spreading leftover row width between tiles,
        /// so the tiles touch.
        /// </summary>
        public bool IsSeamless { get => _isSeamless; private set => SetValue(ref _isSeamless, value); }

        public bool ShowRarityGlow { get => _showRarityGlow; private set => SetValue(ref _showRarityGlow, value); }

        /// <summary>This widget's rarity bar choice, applied through the tile rather than the shared rows.</summary>
        public bool ShowRarityBar { get => _showRarityBar; private set => SetValue(ref _showRarityBar, value); }

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
            // A set size is fixed; unset follows density (standard density is the default size).
            IconSize = ShowcaseWidgetOptions.GetMosaicIconSizeOverride(Projection?.Instance)
                ?? (Density == WidgetViewportDensity.Compact
                    ? 32
                    : Density == WidgetViewportDensity.Expanded ? 54 : ShowcaseWidgetOptions.DefaultMosaicIconSize);
            var spacing = ShowcaseWidgetOptions.GetMosaicSpacing(Projection?.Instance);
            TileMargin = new Thickness(spacing);
            IsSeamless = spacing == 0;

            // Glow on/off is a per-widget option; the glow ANIMATION stays a global setting.
            ShowRarityGlow = ShowcaseWidgetOptions.GetMosaicShowRarityGlow(Projection?.Instance);
            ShowRarityBar = ShowcaseWidgetOptions.GetMosaicShowRarityBar(Projection?.Instance);
            GlowWhenLocked = ShowcaseWidgetOptions.GetMosaicSource(Projection?.Instance) ==
                ShowcaseMosaicSource.UnlockNext;
            AnimateRarityGlows =
                PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.AnimateRarityGlows ?? true;

            // The widget's own copies of any revealable tiles, so a reveal stays in this widget.
            CollectionHelper.Replace(Items, _reveals.Map(OrderAchievements(achievements)));
        }

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
