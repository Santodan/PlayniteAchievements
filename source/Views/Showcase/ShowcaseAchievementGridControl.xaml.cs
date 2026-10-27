using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Views.Showcase
{
    /// <summary>
    /// Hosts the shared achievement grid inside showcase widget bodies, contributing the
    /// pin reorder items for rows that are pinned achievements.
    /// </summary>
    public partial class ShowcaseAchievementGridControl : ShowcaseGridHostBase
    {
        public static readonly DependencyProperty ShowRarityGlowProperty =
            DependencyProperty.Register(
                nameof(ShowRarityGlow),
                typeof(bool),
                typeof(ShowcaseAchievementGridControl),
                new PropertyMetadata(true));

        public bool ShowRarityGlow
        {
            get => (bool)GetValue(ShowRarityGlowProperty);
            set => SetValue(ShowRarityGlowProperty, value);
        }

        // The widget's own grid options record, which carries the name color, tag display and
        // status color; showcase records live outside the grid's settings catalog lookup.
        public static readonly DependencyProperty AppearanceOptionsProperty =
            DependencyProperty.Register(
                nameof(AppearanceOptions),
                typeof(AchievementGridOptions),
                typeof(ShowcaseAchievementGridControl),
                new PropertyMetadata(null));

        public AchievementGridOptions AppearanceOptions
        {
            get => (AchievementGridOptions)GetValue(AppearanceOptionsProperty);
            set => SetValue(AppearanceOptionsProperty, value);
        }

        public static readonly DependencyProperty ColorRarityColumnsByRarityProperty =
            DependencyProperty.Register(
                nameof(ColorRarityColumnsByRarity),
                typeof(bool),
                typeof(ShowcaseAchievementGridControl),
                new PropertyMetadata(false));

        public bool ColorRarityColumnsByRarity
        {
            get => (bool)GetValue(ColorRarityColumnsByRarityProperty);
            set => SetValue(ColorRarityColumnsByRarityProperty, value);
        }

        /// <summary>
        /// The whole achievement list of the one game the rows show; set, the grid offers category
        /// mode while its rows stay within that game, and totals and drills from this list.
        /// </summary>
        public static readonly DependencyProperty CategoryItemsSourceProperty =
            DependencyProperty.Register(
                nameof(CategoryItemsSource),
                typeof(IEnumerable<AchievementDisplayItem>),
                typeof(ShowcaseAchievementGridControl),
                new PropertyMetadata(null, OnCategoryItemsSourceChanged));

        // Category mode only where a whole-game list backs it; without one the grid's totals
        // would count only the shown rows.
        private static void OnCategoryItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var host = (ShowcaseAchievementGridControl)d;
            if (host.InnerGrid != null)
            {
                host.InnerGrid.EnableCategoryMode = e.NewValue != null;
            }
        }

        public IEnumerable<AchievementDisplayItem> CategoryItemsSource
        {
            get => (IEnumerable<AchievementDisplayItem>)GetValue(CategoryItemsSourceProperty);
            set => SetValue(CategoryItemsSourceProperty, value);
        }

        public ShowcaseAchievementGridControl()
        {
            InitializeComponent();
            InnerGrid.EnableCategoryMode = CategoryItemsSource != null;
        }

        protected override FrameworkElement GridElement => InnerGrid;

        protected override void RefreshGrid() => InnerGrid?.Refresh();

        public override void Dispose() => InnerGrid?.Dispose();

        protected override void AppendPinReorderItems(ContextMenu menu, object data)
        {
            if (!EnablePinReorder ||
                !ShowcasePinService.TryGetAchievementIdentity(
                    data,
                    out var gameId,
                    out var apiName,
                    out _,
                    out _,
                    out _))
            {
                return;
            }

            var showcase = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.Showcase;
            if (showcase == null ||
                !ShowcasePinService.IsAchievementPinned(showcase, PinCollectionId, gameId, apiName))
            {
                return;
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMoveItem(
                "LOCPlayAch_Showcase_MoveEarlier",
                () => MovePin(gameId, apiName, -1)));
            menu.Items.Add(CreateMoveItem(
                "LOCPlayAch_Showcase_MoveLater",
                () => MovePin(gameId, apiName, 1)));
        }

        private void MovePin(Guid gameId, string apiName, int direction)
        {
            var showcase = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.Showcase;
            if (showcase != null &&
                ShowcasePinService.MoveAchievement(
                    showcase,
                    PinCollectionId,
                    gameId,
                    apiName,
                    direction))
            {
                ShowcaseConfigurationCommit.Commit();
            }
        }
    }
}
