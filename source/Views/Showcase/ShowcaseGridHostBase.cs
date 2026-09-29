using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Showcase
{
    /// <summary>
    /// Shared plumbing for the showcase widgets that host one of the reusable data grids:
    /// the bindable surface the widget templates use, and the row context menu (the shared
    /// row options plus the pin reorder items each host contributes).
    /// Disposable because the hosted grids subscribe the app-lifetime PersistedSettings and
    /// only unhook in their Dispose; the owning ShowcaseWidgetControl disposes hosts when a
    /// body content swap or widget discard drops them. They must not self-dispose on
    /// Unloaded: widget hosts are re-parented across dashboard rebuilds, and the inner
    /// grids' ItemsSource observation only re-attaches on an ItemsSource change.
    /// </summary>
    public abstract class ShowcaseGridHostBase : UserControl, System.IDisposable
    {
        private DataGridRow _pendingRightClickRow;

        public abstract void Dispose();

        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register(
                nameof(ItemsSource),
                typeof(IEnumerable),
                typeof(ShowcaseGridHostBase),
                new PropertyMetadata(null));

        public IEnumerable ItemsSource
        {
            get => (IEnumerable)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public static readonly DependencyProperty ColumnSettingsKeyProperty =
            DependencyProperty.Register(
                nameof(ColumnSettingsKey),
                typeof(string),
                typeof(ShowcaseGridHostBase),
                new PropertyMetadata(null));

        public string ColumnSettingsKey
        {
            get => (string)GetValue(ColumnSettingsKeyProperty);
            set => SetValue(ColumnSettingsKeyProperty, value);
        }

        public static readonly DependencyProperty UseCoverImagesProperty =
            DependencyProperty.Register(
                nameof(UseCoverImages),
                typeof(bool),
                typeof(ShowcaseGridHostBase),
                new PropertyMetadata(false));

        public bool UseCoverImages
        {
            get => (bool)GetValue(UseCoverImagesProperty);
            set => SetValue(UseCoverImagesProperty, value);
        }

        public static readonly DependencyProperty ShowColumnHeadersProperty =
            DependencyProperty.Register(
                nameof(ShowColumnHeaders),
                typeof(bool),
                typeof(ShowcaseGridHostBase),
                new PropertyMetadata(true));

        public bool ShowColumnHeaders
        {
            get => (bool)GetValue(ShowColumnHeadersProperty);
            set => SetValue(ShowColumnHeadersProperty, value);
        }

        public static readonly DependencyProperty FixedRowHeightProperty =
            DependencyProperty.Register(
                nameof(FixedRowHeight),
                typeof(double?),
                typeof(ShowcaseGridHostBase),
                new PropertyMetadata(null));

        public double? FixedRowHeight
        {
            get => (double?)GetValue(FixedRowHeightProperty);
            set => SetValue(FixedRowHeightProperty, value);
        }

        public static readonly DependencyProperty ControlBarProperty =
            DependencyProperty.Register(
                nameof(ControlBar),
                typeof(PlayniteAchievements.ViewModels.Items.GridControlBarViewModel),
                typeof(ShowcaseGridHostBase),
                new PropertyMetadata(null));

        public PlayniteAchievements.ViewModels.Items.GridControlBarViewModel ControlBar
        {
            get => (PlayniteAchievements.ViewModels.Items.GridControlBarViewModel)GetValue(ControlBarProperty);
            set => SetValue(ControlBarProperty, value);
        }

        public static readonly DependencyProperty ShowControlBarProperty =
            DependencyProperty.Register(
                nameof(ShowControlBar),
                typeof(bool),
                typeof(ShowcaseGridHostBase),
                new PropertyMetadata(false));

        public bool ShowControlBar
        {
            get => (bool)GetValue(ShowControlBarProperty);
            set => SetValue(ShowControlBarProperty, value);
        }

        public static readonly DependencyProperty EnablePinReorderProperty =
            DependencyProperty.Register(
                nameof(EnablePinReorder),
                typeof(bool),
                typeof(ShowcaseGridHostBase),
                new PropertyMetadata(false));

        /// <summary>Adds move earlier/later items for rows that are current showcase pins.</summary>
        public bool EnablePinReorder
        {
            get => (bool)GetValue(EnablePinReorderProperty);
            set => SetValue(EnablePinReorderProperty, value);
        }

        public static readonly DependencyProperty PinCollectionIdProperty =
            DependencyProperty.Register(
                nameof(PinCollectionId),
                typeof(string),
                typeof(ShowcaseGridHostBase),
                new PropertyMetadata(null));

        /// <summary>Collection whose ordered pins are rendered by this grid.</summary>
        public string PinCollectionId
        {
            get => (string)GetValue(PinCollectionIdProperty);
            set => SetValue(PinCollectionIdProperty, value);
        }

        /// <summary>The hosted grid, used for context-menu styling and post-edit refreshes.</summary>
        protected abstract FrameworkElement GridElement { get; }

        /// <summary>Appends the host's pin reorder items for the row, when it is a live pin.</summary>
        protected abstract void AppendPinReorderItems(ContextMenu menu, object data);

        protected abstract void RefreshGrid();

        protected void GridRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (TryResolveContextMenuRow(sender, e, out var row))
            {
                e.Handled = true;
                _pendingRightClickRow = row;
            }
        }

        protected void GridRow_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!TryResolveContextMenuRow(sender, e, out var row))
            {
                return;
            }

            e.Handled = true;
            var targetRow = _pendingRightClickRow ?? row;
            _pendingRightClickRow = null;
            OpenContextMenuForRow(targetRow);
        }

        /// <summary>Builds a move-pin menu item that commits the reorder when clicked.</summary>
        protected MenuItem CreateMoveItem(string headerKey, System.Action move)
        {
            var item = new MenuItem();
            item.SetResourceReference(HeaderedItemsControl.HeaderProperty, headerKey);
            item.Click += (_, __) => move();
            return item;
        }

        private static bool TryResolveContextMenuRow(object sender, MouseButtonEventArgs e, out DataGridRow row)
        {
            row = sender as DataGridRow
                  ?? e?.Source as DataGridRow
                  ?? VisualTreeHelpers.FindVisualParent<DataGridRow>(e?.OriginalSource as DependencyObject);
            return row != null;
        }

        private void OpenContextMenuForRow(DataGridRow row)
        {
            if (row == null || !row.IsLoaded || row.DataContext == null)
            {
                return;
            }

            var menu = PlayniteAchievementsPlugin.Instance?.BuildStartPageRowContextMenu(
                row.DataContext,
                this,
                RefreshAfterRowOptionsChanged,
                row);
            if (menu == null)
            {
                return;
            }

            AppendPinReorderItems(menu, row.DataContext);
            if (menu.Items.Count == 0)
            {
                return;
            }

            ContextMenuStyleHelper.ApplyAchievementContextMenuStyle(GridElement, menu);
            row.ContextMenu = menu;
            menu.Placement = PlacementMode.MousePoint;
            menu.PlacementTarget = row;
            menu.IsOpen = true;
        }

        private void RefreshAfterRowOptionsChanged()
        {
            PlayniteAchievementsPlugin.Instance?.PersistSettingsForUi();
            ShowcaseConfigurationEvents.RaiseChanged();
            PlayniteAchievementsPlugin.Instance?.InvalidateStartPageDataForUi();
            RefreshGrid();
        }
    }
}
