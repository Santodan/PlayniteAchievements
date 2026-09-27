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
    public sealed class IconMosaicWidgetViewModel : ShowcaseMosaicWidgetViewModelBase
    {
        private readonly CrossGameAchievementControlBarAdapter _controlBarAdapter =
            new CrossGameAchievementControlBarAdapter();
        private double _iconSize = 42;
        private Thickness _tileMargin = new Thickness(6);
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

        public IconMosaicWidgetViewModel()
        {
            _controlBarAdapter.FilterChanged += (_, __) => RefreshTiles();
            ControlBar = _controlBarAdapter.ControlBar;
        }

        protected override void RefreshLayout()
        {
            // A set size is fixed; unset follows density (standard density is the default size).
            IconSize = ShowcaseWidgetOptions.GetMosaicIconSizeOverride(Projection?.Instance)
                ?? (Density == WidgetViewportDensity.Compact
                    ? 32
                    : Density == WidgetViewportDensity.Expanded ? 54 : ShowcaseWidgetOptions.DefaultMosaicIconSize);
            var spacing = ShowcaseWidgetOptions.GetMosaicSpacing(Projection?.Instance);
            TileMargin = new Thickness(spacing);

            // Glow on/off is a per-widget option; the glow ANIMATION stays a global setting.
            ShowRarityGlow = ShowcaseWidgetOptions.GetMosaicShowRarityGlow(Projection?.Instance);
            ShowRarityBar = ShowcaseWidgetOptions.GetMosaicShowRarityBar(Projection?.Instance);
            GlowWhenLocked = ShowcaseWidgetOptions.GetMosaicSource(Projection?.Instance) ==
                ShowcaseMosaicSource.UnlockNext;
            AnimateRarityGlows =
                PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.AnimateRarityGlows ?? true;
        }

        protected override void RefreshTiles()
        {
            IEnumerable<AchievementDisplayItem> achievements =
                Projection?.MosaicAchievements ?? Array.Empty<AchievementDisplayItem>();
            if (ShowControlBar)
            {
                var list = achievements.Where(item => item != null).ToList();
                _controlBarAdapter.UpdateGames(Projection?.Snapshot?.GameSummaries);
                _controlBarAdapter.UpdateOptions(list);
                achievements = _controlBarAdapter.Apply(list);
            }

            var capped = achievements.Take(ShowcaseWidgetOptions.GetMosaicCount(Projection?.Instance)).ToList();

            // The widget's own copies of any revealable tiles, so a reveal stays in this widget.
            CollectionHelper.Replace(Items, _reveals.Map(OrderAchievements(capped)));
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
            // Unlock Next ranks during selection and hides the sort rows, so a sort stored while
            // another source was selected must not re-arrange its tiles.
            if (spec.PreservesSourceOrder ||
                ShowcaseWidgetOptions.GetMosaicSource(Projection?.Instance) == ShowcaseMosaicSource.UnlockNext)
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
