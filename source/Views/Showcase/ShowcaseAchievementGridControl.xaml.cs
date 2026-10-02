using System;
using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Services.Showcase;

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

        public static readonly DependencyProperty ColorNamesByRarityProperty =
            DependencyProperty.Register(
                nameof(ColorNamesByRarity),
                typeof(bool),
                typeof(ShowcaseAchievementGridControl),
                new PropertyMetadata(false));

        public bool ColorNamesByRarity
        {
            get => (bool)GetValue(ColorNamesByRarityProperty);
            set => SetValue(ColorNamesByRarityProperty, value);
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

        public ShowcaseAchievementGridControl()
        {
            InitializeComponent();
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
