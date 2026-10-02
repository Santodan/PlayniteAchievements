using System;
using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Views.Showcase
{
    /// <summary>
    /// Hosts the shared game-summaries grid inside showcase widget bodies, contributing the
    /// pin reorder items for rows that are pinned games.
    /// </summary>
    public partial class ShowcaseGameGridControl : ShowcaseGridHostBase
    {
        public static readonly DependencyProperty ShowMetadataPlatformProperty =
            DependencyProperty.Register(
                nameof(ShowMetadataPlatform),
                typeof(bool),
                typeof(ShowcaseGameGridControl),
                new PropertyMetadata(true));

        public bool ShowMetadataPlatform
        {
            get => (bool)GetValue(ShowMetadataPlatformProperty);
            set => SetValue(ShowMetadataPlatformProperty, value);
        }

        public static readonly DependencyProperty ShowMetadataPlaytimeProperty =
            DependencyProperty.Register(
                nameof(ShowMetadataPlaytime),
                typeof(bool),
                typeof(ShowcaseGameGridControl),
                new PropertyMetadata(true));

        public bool ShowMetadataPlaytime
        {
            get => (bool)GetValue(ShowMetadataPlaytimeProperty);
            set => SetValue(ShowMetadataPlaytimeProperty, value);
        }

        public static readonly DependencyProperty ShowMetadataRegionProperty =
            DependencyProperty.Register(
                nameof(ShowMetadataRegion),
                typeof(bool),
                typeof(ShowcaseGameGridControl),
                new PropertyMetadata(true));

        public bool ShowMetadataRegion
        {
            get => (bool)GetValue(ShowMetadataRegionProperty);
            set => SetValue(ShowMetadataRegionProperty, value);
        }

        public static readonly DependencyProperty ShowCompletionGlowProperty =
            DependencyProperty.Register(
                nameof(ShowCompletionGlow),
                typeof(bool),
                typeof(ShowcaseGameGridControl),
                new PropertyMetadata(true));

        public bool ShowCompletionGlow
        {
            get => (bool)GetValue(ShowCompletionGlowProperty);
            set => SetValue(ShowCompletionGlowProperty, value);
        }

        public ShowcaseGameGridControl()
        {
            InitializeComponent();
        }

        protected override FrameworkElement GridElement => InnerGrid;

        protected override void RefreshGrid() => InnerGrid?.Refresh();

        public override void Dispose() => InnerGrid?.Dispose();

        protected override void AppendPinReorderItems(ContextMenu menu, object data)
        {
            if (!EnablePinReorder ||
                data is FriendGameSummaryItem ||
                !(data is GameSummaryItem game) ||
                !game.PlayniteGameId.HasValue)
            {
                return;
            }

            var gameId = game.PlayniteGameId.Value;
            var showcase = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.Showcase;
            if (showcase == null ||
                !ShowcasePinService.IsGamePinned(showcase, PinCollectionId, gameId))
            {
                return;
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMoveItem("LOCPlayAch_Showcase_MoveEarlier", () => MovePin(gameId, -1)));
            menu.Items.Add(CreateMoveItem("LOCPlayAch_Showcase_MoveLater", () => MovePin(gameId, 1)));
        }

        private void MovePin(Guid gameId, int direction)
        {
            var showcase = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.Showcase;
            if (showcase != null &&
                ShowcasePinService.MoveGame(showcase, PinCollectionId, gameId, direction))
            {
                ShowcaseConfigurationCommit.Commit();
            }
        }
    }
}
