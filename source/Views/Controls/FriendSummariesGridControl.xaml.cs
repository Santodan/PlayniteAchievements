using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Controls
{
    public partial class FriendSummariesGridControl : SummaryGridControlBase, IDisposable
    {
        static FriendSummariesGridControl()
        {
            ColumnSettingsKeyProperty.OverrideMetadata(
                typeof(FriendSummariesGridControl),
                new PropertyMetadata("FriendsOverviewFriendSummaries"));
        }

        private static readonly ILogger Logger = LogManager.GetLogger();
        private bool _isAttached;
        private PersistedSettingsSubscription _persistedSubscription;

        private static readonly IReadOnlyDictionary<string, bool> DefaultVisibility =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["Avatar"] = true,
                ["FriendSummaryFriend"] = true,
                ["FriendSummarySharedGames"] = true,
                ["FriendSummaryUnlocks"] = true,
                ["FriendSummaryPrestigeScore"] = true,
                ["FriendSummaryCollectionScore"] = true,
                ["FriendSummaryPrestigeLevel"] = false,
                ["FriendSummaryCollectionLevel"] = false
            };

        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register(
                nameof(ItemsSource),
                typeof(IEnumerable<FriendSummaryItem>),
                typeof(FriendSummariesGridControl),
                new PropertyMetadata(null, OnItemsSourceChanged));

        public IEnumerable<FriendSummaryItem> ItemsSource
        {
            get => (IEnumerable<FriendSummaryItem>)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        private static readonly DependencyPropertyKey HasAnyFavoritesPropertyKey =
            DependencyProperty.RegisterReadOnly(
                nameof(HasAnyFavorites),
                typeof(bool),
                typeof(FriendSummariesGridControl),
                new PropertyMetadata(false));

        // True when at least one friend in ItemsSource is favorited. The star gutter in the friend
        // name column collapses entirely when this is false, so a list with no favorites shows no gutter.
        public static readonly DependencyProperty HasAnyFavoritesProperty = HasAnyFavoritesPropertyKey.DependencyProperty;

        public bool HasAnyFavorites => (bool)GetValue(HasAnyFavoritesProperty);

        private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is FriendSummariesGridControl control))
            {
                return;
            }

            if (e.OldValue is INotifyCollectionChanged oldCollection)
            {
                oldCollection.CollectionChanged -= control.OnItemsCollectionChanged;
            }

            if (e.NewValue is INotifyCollectionChanged newCollection)
            {
                newCollection.CollectionChanged += control.OnItemsCollectionChanged;
            }

            control.RecomputeHasAnyFavorites();
        }

        private void OnItemsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            RecomputeHasAnyFavorites();
        }

        private void RecomputeHasAnyFavorites()
        {
            var hasAny = ItemsSource?.Any(friend => friend?.IsFavorite == true) ?? false;
            SetValue(HasAnyFavoritesPropertyKey, hasAny);
        }

        public static readonly DependencyProperty SelectedItemProperty =
            DependencyProperty.Register(
                nameof(SelectedItem),
                typeof(FriendSummaryItem),
                typeof(FriendSummariesGridControl),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public FriendSummaryItem SelectedItem
        {
            get => (FriendSummaryItem)GetValue(SelectedItemProperty);
            set => SetValue(SelectedItemProperty, value);
        }

        // The bar leaves out the items this grid's Display Settings hide.
        private void UpdateControlBarOptions()
        {
            ControlBarHost.GridOptions = Helpers.GridSurfaceOptions.Resolve(
                Settings.Controls.GridOptionKind.FriendSummaries,
                ColumnSettingsKey);
        }

        public static readonly DependencyProperty DateDisplayModeProperty =
            DependencyProperty.Register(
                nameof(DateDisplayMode),
                typeof(DateDisplayMode),
                typeof(FriendSummariesGridControl),
                new PropertyMetadata(DateDisplayMode.DateAndTime));

        public DateDisplayMode DateDisplayMode
        {
            get => (DateDisplayMode)GetValue(DateDisplayModeProperty);
            private set => SetValue(DateDisplayModeProperty, value);
        }

        public event SelectionChangedEventHandler SelectionChanged;

        public FriendSummariesGridControl()
        {
            InitializeComponent();
            UpdateColumnHeadersVisibility();
        }

        private protected override DataGrid SummaryGrid => FriendSummariesGrid;

        private protected override GridControlBarControl ControlBarElement => ControlBarHost;

        private protected override void OnColumnSettingsKeyChanged(DependencyPropertyChangedEventArgs e)
        {
            UpdateControlBarOptions();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateControlBarOptions();
            if (_isAttached)
            {
                return;
            }

            var settings = PlayniteAchievementsPlugin.Instance?.Settings;
            if (settings?.Persisted == null || FriendSummariesGrid == null)
            {
                return;
            }

            UpdateColumnHeadersVisibility();
            UpdateRealizedRowHeights();
            UpdateDateDisplayMode(settings);

            // Tracks the current Persisted instance: CancelEdit replaces it, and a direct
            // subscription would leave this grid on the orphan, keeping the reverted date
            // mode for the rest of the session.
            if (_persistedSubscription == null)
            {
                _persistedSubscription = new PersistedSettingsSubscription(
                    settings,
                    OnPersistedSettingsChanged,
                    () => UpdateDateDisplayMode(settings));
            }

            DataGridAlignmentBehavior.SetColumnCellAlignmentOverridesProvider(
                FriendSummariesGrid,
                () => GetAlignments(settings));
            DataGridAlignmentBehavior.SetColumnCellVerticalAlignmentOverridesProvider(
                FriendSummariesGrid,
                () => GetVerticalAlignments(settings));
            DataGridAlignmentBehavior.SetColumnHeaderHorizontalAlignmentOverridesProvider(
                FriendSummariesGrid,
                () => GetHeaderAlignments(settings));

            _columnPersistence = new DataGridColumnLayoutService(
                FriendSummariesGrid,
                Logger,
                () => GetColumnLayoutOptions(settings)?.Widths,
                map =>
                {
                    var columns = GetColumnLayoutOptions(settings);
                    if (columns != null)
                    {
                        columns.Widths = map;
                    }
                },
                () => GetVisibility(settings),
                map =>
                {
                    var columns = GetColumnLayoutOptions(settings);
                    if (columns != null)
                    {
                        columns.Visibility = map;
                    }
                },
                () => SavePluginSettings(settings),
                getOrder: () => GetColumnLayoutOptions(settings)?.Order,
                setOrder: map =>
                {
                    var columns = GetColumnLayoutOptions(settings);
                    if (columns != null)
                    {
                        columns.Order = map;
                    }
                },
                getCellAlignments: () => GetAlignments(settings),
                setCellAlignments: map =>
                {
                    var columns = GetColumnLayoutOptions(settings);
                    if (columns != null)
                    {
                        columns.CellAlignments = map;
                    }
                },
                getDefaultCellAlignment: () => settings.Persisted?.GridCellAlignment ?? GridAlignment.Left,
                getCellVerticalAlignments: () => GetVerticalAlignments(settings),
                setCellVerticalAlignments: map =>
                {
                    var columns = GetColumnLayoutOptions(settings);
                    if (columns != null)
                    {
                        columns.CellVerticalAlignments = map;
                    }
                },
                getDefaultCellVerticalAlignment: () => settings.Persisted?.GridCellVerticalAlignment ?? GridVerticalAlignment.Center,
                getHeaderHorizontalAlignments: () => GetHeaderAlignments(settings),
                setHeaderHorizontalAlignments: map =>
                {
                    var columns = GetColumnLayoutOptions(settings);
                    if (columns != null)
                    {
                        columns.HeaderAlignments = map;
                    }
                },
                getDefaultHeaderHorizontalAlignment: () => settings.Persisted?.GridColumnHeaderAlignment ?? GridAlignment.Center,
                applyCellAlignments: () => DataGridAlignmentBehavior.Refresh(FriendSummariesGrid),
                getLocks: () => GetColumnLayoutOptions(settings)?.Locked,
                setLocks: map =>
                {
                    var columns = GetColumnLayoutOptions(settings);
                    if (columns != null)
                    {
                        columns.Locked = map;
                    }
                },
                getHiddenHeaders: () => GetColumnLayoutOptions(settings)?.HiddenHeaders,
                setHiddenHeaders: map =>
                {
                    var columns = GetColumnLayoutOptions(settings);
                    if (columns != null)
                    {
                        columns.HiddenHeaders = map;
                    }
                });
            _columnPersistence.DelayInitialRenderUntilNormalized = DelayInitialRenderUntilNormalized;
            _columnPersistence.Attach();
            _isAttached = true;
        }

        private Dictionary<string, bool> GetVisibility(PlayniteAchievementsSettings settings)
        {
            var columns = GetColumnLayoutOptions(settings);
            var map = columns?.Visibility;
            if (settings?.Persisted == null)
            {
                return map;
            }

            if (map == null)
            {
                map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                if (columns != null)
                {
                    columns.Visibility = map;
                }
            }

            if (map.TryGetValue("FriendSummaryScores", out var legacyScoresVisible))
            {
                if (!map.ContainsKey("FriendSummaryPrestigeScore"))
                {
                    map["FriendSummaryPrestigeScore"] = legacyScoresVisible;
                }

                if (!map.ContainsKey("FriendSummaryCollectionScore"))
                {
                    map["FriendSummaryCollectionScore"] = legacyScoresVisible;
                }
            }

            foreach (var pair in DefaultVisibility)
            {
                if (!map.ContainsKey(pair.Key))
                {
                    map[pair.Key] = pair.Value;
                }
            }

            return map;
        }

        private GridColumnLayoutOptions GetColumnLayoutOptions(PlayniteAchievementsSettings settings)
        {
            var id = GridOptionsCatalog.ResolveFriendSummariesId(ColumnSettingsKey);
            return settings?.Persisted?.GridOptions
                ?.GetFriendSummaries(id)
                ?.Columns;
        }

        private Dictionary<string, GridAlignment> GetAlignments(PlayniteAchievementsSettings settings) =>
            GetColumnLayoutOptions(settings)?.CellAlignments;

        private Dictionary<string, GridVerticalAlignment> GetVerticalAlignments(PlayniteAchievementsSettings settings) =>
            GetColumnLayoutOptions(settings)?.CellVerticalAlignments;

        private Dictionary<string, GridAlignment> GetHeaderAlignments(PlayniteAchievementsSettings settings) =>
            GetColumnLayoutOptions(settings)?.HeaderAlignments;

        private void FriendSummariesGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            ApplyFixedRowHeight(e.Row);
        }

        private void OnPersistedSettingsChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName == nameof(PersistedSettings.UnlockDateDisplayMode))
            {
                UpdateDateDisplayMode(PlayniteAchievementsPlugin.Instance?.Settings);
            }
        }

        private void UpdateDateDisplayMode(PlayniteAchievementsSettings settings)
        {
            if (settings?.Persisted != null)
            {
                DateDisplayMode = settings.Persisted.UnlockDateDisplayMode;
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Dispose();
        }

        private void DataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectionChanged?.Invoke(sender, e);
        }

        private void DataGridRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            ForwardRowMouseEvent(e, RowPreviewMouseLeftButtonDownEvent, sender);
        }

        public void Refresh()
        {
            _columnPersistence?.Refresh();
        }

        public void Dispose()
        {
            if (!_isAttached)
            {
                return;
            }

            _columnPersistence?.Dispose();
            _columnPersistence = null;
            _persistedSubscription?.Dispose();
            _persistedSubscription = null;

            DataGridAlignmentBehavior.SetColumnCellAlignmentOverridesProvider(FriendSummariesGrid, null);
            DataGridAlignmentBehavior.SetColumnCellVerticalAlignmentOverridesProvider(FriendSummariesGrid, null);
            DataGridAlignmentBehavior.SetColumnHeaderHorizontalAlignmentOverridesProvider(FriendSummariesGrid, null);
            _isAttached = false;
        }

        private static void SavePluginSettings(PlayniteAchievementsSettings settings)
        {
            var plugin = PlayniteAchievementsPlugin.Instance;
            if (plugin == null || settings == null)
            {
                return;
            }

            try
            {
                plugin.SavePluginSettings(settings);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to persist friend summaries column settings.");
            }
        }
    }
}
