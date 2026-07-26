using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PlayniteAchievements.Views.Settings.Navigation
{
    /// <summary>
    /// Shared master-detail host for settings tabs: a grouped, optionally searchable left
    /// navigation list of <see cref="SettingsNavigationItem"/>s and a detail pane showing the
    /// selected item's lazily created view.
    /// </summary>
    public partial class SettingsMasterDetailControl : UserControl
    {
        private ICollectionView _itemsView;

        public SettingsMasterDetailControl()
        {
            InitializeComponent();
        }

        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register(
                nameof(ItemsSource),
                typeof(IEnumerable),
                typeof(SettingsMasterDetailControl),
                new PropertyMetadata(null, OnItemsSourceChanged));

        public IEnumerable ItemsSource
        {
            get => (IEnumerable)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public static readonly DependencyProperty SelectedItemProperty =
            DependencyProperty.Register(
                nameof(SelectedItem),
                typeof(SettingsNavigationItem),
                typeof(SettingsMasterDetailControl),
                new FrameworkPropertyMetadata(
                    null,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnSelectedItemChanged));

        public SettingsNavigationItem SelectedItem
        {
            get => (SettingsNavigationItem)GetValue(SelectedItemProperty);
            set => SetValue(SelectedItemProperty, value);
        }

        public static readonly DependencyProperty SearchTextProperty =
            DependencyProperty.Register(
                nameof(SearchText),
                typeof(string),
                typeof(SettingsMasterDetailControl),
                new PropertyMetadata(string.Empty, OnSearchTextChanged));

        public string SearchText
        {
            get => (string)GetValue(SearchTextProperty);
            set => SetValue(SearchTextProperty, value);
        }

        public static readonly DependencyProperty ShowSearchProperty =
            DependencyProperty.Register(
                nameof(ShowSearch),
                typeof(bool),
                typeof(SettingsMasterDetailControl),
                new PropertyMetadata(true));

        public bool ShowSearch
        {
            get => (bool)GetValue(ShowSearchProperty);
            set => SetValue(ShowSearchProperty, value);
        }

        public static readonly DependencyProperty EnableGroupingProperty =
            DependencyProperty.Register(
                nameof(EnableGrouping),
                typeof(bool),
                typeof(SettingsMasterDetailControl),
                new PropertyMetadata(false, OnEnableGroupingChanged));

        public bool EnableGrouping
        {
            get => (bool)GetValue(EnableGroupingProperty);
            set => SetValue(EnableGroupingProperty, value);
        }

        private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SettingsMasterDetailControl control)
            {
                control.ConfigureItemsView();
            }
        }

        private static void OnEnableGroupingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SettingsMasterDetailControl control)
            {
                control.ConfigureItemsView();
            }
        }

        private void ConfigureItemsView()
        {
            _itemsView = ItemsSource == null ? null : CollectionViewSource.GetDefaultView(ItemsSource);
            if (_itemsView == null)
            {
                return;
            }

            _itemsView.Filter = FilterItem;
            _itemsView.GroupDescriptions.Clear();
            if (EnableGrouping)
            {
                _itemsView.GroupDescriptions.Add(
                    new PropertyGroupDescription(nameof(SettingsNavigationItem.GroupName)));
            }

            _itemsView.Refresh();
        }

        private bool FilterItem(object item)
        {
            if (!(item is SettingsNavigationItem navigationItem))
            {
                return false;
            }

            var searchText = SearchText;
            if (string.IsNullOrWhiteSpace(searchText))
            {
                return true;
            }

            return ContainsSearchText(navigationItem.DisplayName, searchText) ||
                ContainsSearchText(navigationItem.Key, searchText) ||
                ContainsSearchText(navigationItem.GroupName, searchText) ||
                ContainsSearchText(navigationItem.Subtitle, searchText);
        }

        private static bool ContainsSearchText(string value, string searchText)
            => !string.IsNullOrWhiteSpace(value) &&
                value.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0;

        private static void OnSearchTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SettingsMasterDetailControl control)
            {
                control._itemsView?.Refresh();
                control.SelectFirstVisibleItemIfNeeded();
            }
        }

        private void SelectFirstVisibleItemIfNeeded()
        {
            if (_itemsView == null)
            {
                return;
            }

            if (SelectedItem != null && _itemsView.Contains(SelectedItem))
            {
                return;
            }

            SelectedItem = _itemsView.Cast<SettingsNavigationItem>().FirstOrDefault();
        }

        private static void OnSelectedItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SettingsMasterDetailControl control && e.NewValue is SettingsNavigationItem item)
            {
                control.OnSelectedItemChangedInternal(item);
            }
        }

        private void OnSelectedItemChangedInternal(SettingsNavigationItem item)
        {
            if (item == null)
            {
                return;
            }

            if (item.IsRedirect)
            {
                var redirectTarget = ItemsSource?
                    .Cast<SettingsNavigationItem>()
                    .FirstOrDefault(x => string.Equals(x.Key, item.RedirectKey, StringComparison.OrdinalIgnoreCase));

                if (redirectTarget != null && !ReferenceEquals(item, redirectTarget))
                {
                    SearchText = string.Empty;
                    SelectedItem = redirectTarget;
                }

                return;
            }

            item.EnsureView();
        }
    }
}
