using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.ViewModels.Showcase.Widgets;
using PlayniteAchievements.Views.Controls;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Showcase
{
    /// <summary>
    /// Makes mosaic tiles clickable. Achievement tiles open the View Achievements window scrolled
    /// to the achievement, as the compact lists do, and right-click to the shared achievement row
    /// menu. Game tiles open the game in the library, and right-click to the Open (game or
    /// library) menu plus, for pinned tiles, the reorder and unpin items.
    /// Both clicks run on the tunneling events: theme-provided implicit styles can consume the
    /// bubbling ones inside the list. Edit mode needs no guard here, since the dashboard turns
    /// widget bodies inert through IsHitTestVisible.
    /// </summary>
    public static class ShowcaseMosaicClickBehavior
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(ShowcaseMosaicClickBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject element) =>
            (bool)element.GetValue(IsEnabledProperty);

        public static void SetIsEnabled(DependencyObject element, bool value) =>
            element.SetValue(IsEnabledProperty, value);

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is UIElement element))
            {
                return;
            }

            element.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
            element.PreviewMouseRightButtonUp -= OnPreviewMouseRightButtonUp;
            if (e.NewValue is true)
            {
                element.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
                element.PreviewMouseRightButtonUp += OnPreviewMouseRightButtonUp;
            }
        }

        private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.Handled || e.ClickCount > 1)
            {
                return;
            }

            var plugin = PlayniteAchievementsPlugin.Instance;
            var source = e.OriginalSource as DependencyObject;
            if (plugin == null || source == null)
            {
                return;
            }

            if (TryResolveAchievement(source, out var item, out _))
            {
                // Reveal keeps priority only while the tile shows a cover (the hidden or the
                // locked-icon one): that tile is left for the item control's own preview handler,
                // which reveals it. A tile whose icon shows can still be revealable for its title
                // or description, which the tile does not draw, so it opens on the first click.
                // Handling it here also keeps the item control from spending the click on that
                // invisible reveal.
                if (item.IsIconHidden || item.IsLockedIconHidden || item.PlayniteGameId == null)
                {
                    return;
                }

                e.Handled = true;
                plugin.OpenViewAchievementsWindow(
                    item.PlayniteGameId.Value,
                    string.IsNullOrWhiteSpace(item.ApiName) ? item.DisplayName : item.ApiName);
                return;
            }

            if (TryResolveGameTile(source, out var tile, out _) && tile.GameId.HasValue)
            {
                e.Handled = true;
                // Switching Playnite to its library view takes a moment; run it after this click
                // finishes so the press itself is not held up waiting on the switch.
                var gameId = tile.GameId.Value;
                ((DependencyObject)sender).Dispatcher.BeginInvoke(
                    new Action(() => plugin.OpenGameInLibrary(gameId)),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private static void OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var owner = sender as FrameworkElement;
            var plugin = PlayniteAchievementsPlugin.Instance;
            var source = e.OriginalSource as DependencyObject;
            if (e.Handled || owner == null || plugin == null || source == null)
            {
                return;
            }

            ContextMenu menu = null;
            FrameworkElement target = null;
            if (TryResolveAchievement(source, out var item, out var itemControl))
            {
                menu = plugin.BuildStartPageRowContextMenu(item, owner, RefreshAfterRowOptionsChanged, itemControl);
                target = itemControl;
            }
            else if (TryResolveGameTile(source, out var tile, out var tileElement) && tile.GameId.HasValue)
            {
                menu = BuildGameTileMenu(plugin, owner, tile);
                target = tileElement;
            }

            if (menu == null || menu.Items.Count == 0)
            {
                return;
            }

            e.Handled = true;
            ContextMenuStyleHelper.ApplyAchievementContextMenuStyle(owner, menu);
            menu.Placement = PlacementMode.MousePoint;
            menu.PlacementTarget = target;
            menu.IsOpen = true;
        }

        private static ContextMenu BuildGameTileMenu(
            PlayniteAchievementsPlugin plugin,
            FrameworkElement owner,
            GameTileViewModel tile)
        {
            var gameId = tile.GameId.Value;
            var menu = new ContextMenu();
            menu.Items.Add(GameRowContextMenuBuilder.CreateOpenMenu(
                owner,
                gameId,
                () => plugin.OpenGameInLibrary(gameId),
                plugin.PlayniteApi,
                null));

            if (tile.IsPinnable)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateCommandItem("LOCPlayAch_Showcase_MoveEarlier", tile.MoveEarlierCommand));
                menu.Items.Add(CreateCommandItem("LOCPlayAch_Showcase_MoveLater", tile.MoveLaterCommand));
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateCommandItem("LOCPlayAch_Showcase_RemoveFromCollection", tile.UnpinCommand));
            }

            return menu;
        }

        private static MenuItem CreateCommandItem(string headerKey, ICommand command)
        {
            var item = new MenuItem { Command = command };
            item.SetResourceReference(HeaderedItemsControl.HeaderProperty, headerKey);
            return item;
        }

        private static bool TryResolveAchievement(
            DependencyObject source,
            out AchievementDisplayItem item,
            out AchievementCompactItemControl itemControl)
        {
            itemControl = source as AchievementCompactItemControl
                ?? VisualTreeHelpers.FindVisualParent<AchievementCompactItemControl>(source);
            item = itemControl?.DataContext as AchievementDisplayItem;
            return item != null;
        }

        private static bool TryResolveGameTile(
            DependencyObject source,
            out GameTileViewModel tile,
            out FrameworkElement tileElement)
        {
            var container = source as ListBoxItem
                ?? VisualTreeHelpers.FindVisualParent<ListBoxItem>(source);
            tile = container?.DataContext as GameTileViewModel;
            tileElement = container;
            return tile != null;
        }

        // Same follow-up as the showcase grid hosts: row options (exclusions, captures, display
        // settings) persist and re-project the dashboard.
        private static void RefreshAfterRowOptionsChanged()
        {
            PlayniteAchievementsPlugin.Instance?.PersistSettingsForUi();
            ShowcaseConfigurationEvents.RaiseChanged();
            PlayniteAchievementsPlugin.Instance?.InvalidateStartPageDataForUi();
        }
    }
}
