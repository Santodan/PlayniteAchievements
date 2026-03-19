// --SUCCESSSTORY--
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Legacy
{
    /// <summary>
    /// SuccessStory-compatible list control for theme integration.
    /// Uses native PlayniteAchievements properties (AllAchievements, AchievementCount, etc.).
    /// Matches the original SuccessStory plugin styling and functionality.
    /// </summary>
    public partial class PluginListControl : ThemeControlBase
    {
        private CancellationTokenSource _sortCts;

        protected override bool EnableAutomaticThemeDataUpdates => true;

        protected override bool ShouldHandleSettingsDataChange(string propertyName)
        {
            return propertyName == nameof(PlayniteAchievementsSettings.ListAchievements);
        }

        protected override void OnThemeDataUpdated()
        {
            _ = RefreshFromSettingsAsync();
        }

        public BulkObservableCollection<AchievementDetail> DisplayedAchievements { get; } = new BulkObservableCollection<AchievementDetail>();

        #region Sort Icons
        private const string NameAsc = "\uea64";
        private const string NameDesc = "\uea67";
        private const string CalAsc = "\uea65";
        private const string CalDesc = "\uea66";
        private const string RarityAsc = "\uea68";
        private const string RarityDesc = "\uea69";
        #endregion

        private int _nameIndex = 1;
        private int _calIndex = 2;
        private int _rarityIndex = 3;

        #region Properties

        public static readonly DependencyProperty DisplayFilterProperty = DependencyProperty.Register(
            nameof(DisplayFilter),
            typeof(bool),
            typeof(PluginListControl),
            new FrameworkPropertyMetadata(true));

        public bool DisplayFilter
        {
            get => (bool)GetValue(DisplayFilterProperty);
            set => SetValue(DisplayFilterProperty, value);
        }

        public static readonly DependencyProperty ShowHiddenIconProperty = DependencyProperty.Register(
            nameof(ShowHiddenIcon),
            typeof(bool),
            typeof(PluginListControl),
            new FrameworkPropertyMetadata(false));

        public bool ShowHiddenIcon
        {
            get => (bool)GetValue(ShowHiddenIconProperty);
            set => SetValue(ShowHiddenIconProperty, value);
        }

        public static readonly DependencyProperty ShowHiddenTitleProperty = DependencyProperty.Register(
            nameof(ShowHiddenTitle),
            typeof(bool),
            typeof(PluginListControl),
            new FrameworkPropertyMetadata(false));

        public bool ShowHiddenTitle
        {
            get => (bool)GetValue(ShowHiddenTitleProperty);
            set => SetValue(ShowHiddenTitleProperty, value);
        }

        public static readonly DependencyProperty ShowHiddenDescriptionProperty = DependencyProperty.Register(
            nameof(ShowHiddenDescription),
            typeof(bool),
            typeof(PluginListControl),
            new FrameworkPropertyMetadata(false));

        public bool ShowHiddenDescription
        {
            get => (bool)GetValue(ShowHiddenDescriptionProperty);
            set => SetValue(ShowHiddenDescriptionProperty, value);
        }

        public static readonly DependencyProperty IconHeightProperty = DependencyProperty.Register(
            nameof(IconHeight),
            typeof(double),
            typeof(PluginListControl),
            new FrameworkPropertyMetadata(48.0));

        public double IconHeight
        {
            get => (double)GetValue(IconHeightProperty);
            set => SetValue(IconHeightProperty, value);
        }

        #endregion

        public PluginListControl()
        {
            InitializeComponent();

            DataContext = this;
            Unloaded += OnUnloaded;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            CancelPendingSort();
        }

        private void CancelPendingSort()
        {
            try
            {
                _sortCts?.Cancel();
                _sortCts?.Dispose();
            }
            catch
            {
            }
            finally
            {
                _sortCts = null;
            }
        }

        #region Sorting

        private enum SortType
        {
            Name,
            Date,
            Rarity
        }

        private enum SortOrder
        {
            Ascending,
            Descending
        }

        private SortType GetPrimarySort() => _nameIndex == 1 ? SortType.Name : (_calIndex == 1 ? SortType.Date : SortType.Rarity);
        private SortType GetSecondarySort() => _nameIndex == 2 ? SortType.Name : (_calIndex == 2 ? SortType.Date : SortType.Rarity);
        private SortType GetTertiarySort() => _nameIndex == 3 ? SortType.Name : (_calIndex == 3 ? SortType.Date : SortType.Rarity);

        private SortOrder GetPrimaryOrder() => _nameIndex == 1 ? GetOrderFromButton(PART_SortName) : (_calIndex == 1 ? GetOrderFromButton(PART_SortCal) : GetOrderFromButton(PART_SortRarity));
        private SortOrder GetSecondaryOrder() => _nameIndex == 2 ? GetOrderFromButton(PART_SortName) : (_calIndex == 2 ? GetOrderFromButton(PART_SortCal) : GetOrderFromButton(PART_SortRarity));
        private SortOrder GetTertiaryOrder() => _nameIndex == 3 ? GetOrderFromButton(PART_SortName) : (_calIndex == 3 ? GetOrderFromButton(PART_SortCal) : GetOrderFromButton(PART_SortRarity));

        private SortOrder GetOrderFromButton(Button btn)
        {
            var content = btn?.Content?.ToString() ?? string.Empty;

            if (btn == PART_SortName)
            {
                return content == NameAsc ? SortOrder.Ascending : SortOrder.Descending;
            }

            if (btn == PART_SortCal)
            {
                return content == CalAsc ? SortOrder.Ascending : SortOrder.Descending;
            }

            if (btn == PART_SortRarity)
            {
                return content == RarityAsc ? SortOrder.Ascending : SortOrder.Descending;
            }

            return SortOrder.Descending;
        }

        private async Task RefreshFromSettingsAsync()
        {
            // If the view is not fully loaded yet, avoid touching UI elements.
            if (!IsLoaded)
            {
                return;
            }

            await ApplyCurrentSortAsync().ConfigureAwait(false);
        }

        private async Task ApplyCurrentSortAsync()
        {
            // Capture all UI state on the UI thread (safe even if called from background threads).
            List<AchievementDetail> snapshot = null;
            bool groupByUnlocked = false;
            bool groupByUsesCalendarAsPrimary = false;
            SortType primarySort = SortType.Name;
            SortType secondarySort = SortType.Date;
            SortType tertiarySort = SortType.Rarity;
            SortOrder primaryOrder = SortOrder.Descending;
            SortOrder secondaryOrder = SortOrder.Descending;
            SortOrder tertiaryOrder = SortOrder.Descending;
            SortOrder calendarOrder = SortOrder.Descending;

            await Dispatcher.InvokeAsync(() =>
            {
                var source = Plugin?.Settings?.ListAchievements;
                if (source == null)
                {
                    snapshot = null;
                    return;
                }

                // IMPORTANT: don't copy the list on the UI thread.
                // ThemeIntegrationService assigns a fresh List instance per update, so it's safe
                // to read/sort this reference on a background thread.
                snapshot = source;
                groupByUnlocked = PART_SortGroupBy?.IsChecked == true;
                groupByUsesCalendarAsPrimary = (PART_SortCalOrder?.Content?.ToString() ?? string.Empty) == "1";

                primarySort = GetPrimarySort();
                secondarySort = GetSecondarySort();
                tertiarySort = GetTertiarySort();
                primaryOrder = GetPrimaryOrder();
                secondaryOrder = GetSecondaryOrder();
                tertiaryOrder = GetTertiaryOrder();
                calendarOrder = GetOrderFromButton(PART_SortCal);
            });

            if (snapshot == null)
            {
                await Dispatcher.InvokeAsync(() => DisplayedAchievements.ReplaceAll(Array.Empty<AchievementDetail>()));
                return;
            }

            CancelPendingSort();
            _sortCts = new CancellationTokenSource();
            var token = _sortCts.Token;

            List<AchievementDetail> sorted;
            try
            {
                sorted = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();

                    IEnumerable<AchievementDetail> query = snapshot;

                    if (groupByUnlocked && groupByUsesCalendarAsPrimary)
                    {
                        query = calendarOrder == SortOrder.Ascending
                            ? query.OrderBy(a => a.Unlocked).ThenBy(a => a.UnlockTimeUtc)
                            : query.OrderByDescending(a => a.Unlocked).ThenByDescending(a => a.UnlockTimeUtc);
                    }

                    query = ApplySort(query, primarySort, primaryOrder);
                    query = ApplySort(query, secondarySort, secondaryOrder);
                    query = ApplySort(query, tertiarySort, tertiaryOrder);

                    return query.ToList();
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() => DisplayedAchievements.ReplaceAll(sorted));
        }

        private IOrderedEnumerable<AchievementDetail> ApplySort(IEnumerable<AchievementDetail> source, SortType sortType, SortOrder sortOrder)
        {
            bool ascending = sortOrder == SortOrder.Ascending;
            return sortType switch
            {
                SortType.Name => ascending ? source.OrderBy(a => a.DisplayName) : source.OrderByDescending(a => a.DisplayName),
                SortType.Date => ascending ? source.OrderBy(a => a.UnlockTimeUtc ?? DateTime.MinValue) : source.OrderByDescending(a => a.UnlockTimeUtc ?? DateTime.MinValue),
                SortType.Rarity => ascending
                    ? source.OrderBy(a => a.RaritySortValue).ThenByDescending(a => a.Points ?? 0)
                    : source.OrderByDescending(a => a.RaritySortValue).ThenByDescending(a => a.Points ?? 0),
                _ => source.OrderBy(a => 0)
            };
        }

        private void ChangeIndex()
        {
            _nameIndex++;
            if (_nameIndex == 4) _nameIndex = 1;
            PART_SortNameOrder.Content = _nameIndex;

            _calIndex++;
            if (_calIndex == 4) _calIndex = 1;
            PART_SortCalOrder.Content = _calIndex;

            _rarityIndex++;
            if (_rarityIndex == 4) _rarityIndex = 1;
            PART_SortRarityOrder.Content = _rarityIndex;
        }

        private void PART_SortName_Click(object sender, RoutedEventArgs e)
        {
            if (PART_SortName.Content.ToString() == NameAsc)
            {
                PART_SortName.Content = NameDesc;
            }
            else
            {
                ChangeIndex();
                PART_SortName.Content = NameAsc;
            }
            _ = ApplyCurrentSortAsync();
        }

        private void PART_SortCal_Click(object sender, RoutedEventArgs e)
        {
            if (PART_SortCal.Content.ToString() == CalAsc)
            {
                PART_SortCal.Content = CalDesc;
            }
            else
            {
                ChangeIndex();
                PART_SortCal.Content = CalAsc;
            }
            _ = ApplyCurrentSortAsync();
        }

        private void PART_SortRarity_Click(object sender, RoutedEventArgs e)
        {
            if (PART_SortRarity.Content.ToString() == RarityAsc)
            {
                PART_SortRarity.Content = RarityDesc;
            }
            else
            {
                ChangeIndex();
                PART_SortRarity.Content = RarityAsc;
            }
            _ = ApplyCurrentSortAsync();
        }

        private void PART_SortGroupBy_Checked(object sender, RoutedEventArgs e)
        {
            _ = ApplyCurrentSortAsync();
        }

        private void PART_SortGroupBy_Unchecked(object sender, RoutedEventArgs e)
        {
            _ = ApplyCurrentSortAsync();
        }

        #endregion

        #region Tab Control (Categories)

        private void PART_TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                TabControl tb = sender as TabControl;
                if (tb?.SelectedIndex > -1)
                {
                    TabItem ti = (TabItem)tb.Items[tb.SelectedIndex];
                    if (ti.Tag != null)
                    {
                        // Filter by category
                        string categoryName = ti.Tag.ToString();
                        FilterByCategory(categoryName);
                    }
                }
            }
            catch { }
        }

        private void FilterByCategory(string categoryName)
        {
            // Category filtering isn't currently implemented in the model.
            // Avoid mutating theme-exposed collections (which causes a full UI rebind).
            _ = ApplyCurrentSortAsync();
        }

        #endregion

        #region Hidden Achievement Reveal

        private void AchievementGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is AchievementDetail achievement)
            {
                // Only toggle reveal for hidden, unlocked achievements
                if (achievement.Hidden && !achievement.Unlocked)
                {
                    var current = HiddenRevealHelper.GetIsRevealed(fe);
                    HiddenRevealHelper.SetIsRevealed(fe, !current);
                    e.Handled = true;
                }
            }
        }

        #endregion
    }
}
// --END SUCCESSSTORY--
