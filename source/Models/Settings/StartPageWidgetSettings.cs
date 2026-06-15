using PlayniteAchievements.Common;

namespace PlayniteAchievements.Models.Settings
{
    public sealed class StartPageGameSummariesGridSettings : ObservableObject
    {
        private bool _showGameMetadata = true;
        private bool _useCoverImages = true;
        private bool _showCompletionBorder = true;
        private bool _showColumnHeaders = true;
        private double? _rowHeight;
        private int? _maxRows = PersistedSettings.DefaultStartPageGridMaxRows;
        private GameSummariesSortMode _sortMode = GameSummariesSortMode.RecentUnlock;
        private bool _sortDescending = true;

        public bool ShowGameMetadata
        {
            get => _showGameMetadata;
            set => SetValue(ref _showGameMetadata, value);
        }

        public bool UseCoverImages
        {
            get => _useCoverImages;
            set => SetValue(ref _useCoverImages, value);
        }

        public bool ShowCompletionBorder
        {
            get => _showCompletionBorder;
            set => SetValue(ref _showCompletionBorder, value);
        }

        public bool ShowColumnHeaders
        {
            get => _showColumnHeaders;
            set => SetValue(ref _showColumnHeaders, value);
        }

        public double? RowHeight
        {
            get => _rowHeight;
            set => SetValue(ref _rowHeight, PersistedSettings.NormalizeGridRowHeight(value));
        }

        public int? MaxRows
        {
            get => _maxRows;
            set => SetValue(ref _maxRows, PersistedSettings.NormalizeGridMaxRows(value));
        }

        public GameSummariesSortMode SortMode
        {
            get => _sortMode;
            set => SetValue(ref _sortMode, value);
        }

        public bool SortDescending
        {
            get => _sortDescending;
            set => SetValue(ref _sortDescending, value);
        }

        public StartPageGameSummariesGridSettings Clone()
        {
            return new StartPageGameSummariesGridSettings
            {
                ShowGameMetadata = ShowGameMetadata,
                UseCoverImages = UseCoverImages,
                ShowCompletionBorder = ShowCompletionBorder,
                ShowColumnHeaders = ShowColumnHeaders,
                RowHeight = RowHeight,
                MaxRows = MaxRows,
                SortMode = SortMode,
                SortDescending = SortDescending
            };
        }
    }

    public sealed class StartPageRecentUnlocksGridSettings : ObservableObject
    {
        private bool _useCoverImages = true;
        private bool _showColumnHeaders = true;
        private double? _rowHeight;
        private int? _maxRows = PersistedSettings.DefaultStartPageGridMaxRows;
        private CompactListSortMode _sortMode = CompactListSortMode.UnlockTime;
        private bool _sortDescending = true;

        public bool UseCoverImages
        {
            get => _useCoverImages;
            set => SetValue(ref _useCoverImages, value);
        }

        public bool ShowColumnHeaders
        {
            get => _showColumnHeaders;
            set => SetValue(ref _showColumnHeaders, value);
        }

        public double? RowHeight
        {
            get => _rowHeight;
            set => SetValue(ref _rowHeight, PersistedSettings.NormalizeGridRowHeight(value));
        }

        public int? MaxRows
        {
            get => _maxRows;
            set => SetValue(ref _maxRows, PersistedSettings.NormalizeGridMaxRows(value));
        }

        public CompactListSortMode SortMode
        {
            get => _sortMode;
            set => SetValue(ref _sortMode, value);
        }

        public bool SortDescending
        {
            get => _sortDescending;
            set => SetValue(ref _sortDescending, value);
        }

        public StartPageRecentUnlocksGridSettings Clone()
        {
            return new StartPageRecentUnlocksGridSettings
            {
                UseCoverImages = UseCoverImages,
                ShowColumnHeaders = ShowColumnHeaders,
                RowHeight = RowHeight,
                MaxRows = MaxRows,
                SortMode = SortMode,
                SortDescending = SortDescending
            };
        }
    }

    public sealed class StartPagePieWidgetSettings : ObservableObject
    {
        private bool _showCenterPercentage = true;
        private OverviewPieSmallSliceMode _smallSliceMode = OverviewPieSmallSliceMode.Round;

        public bool ShowCenterPercentage
        {
            get => _showCenterPercentage;
            set => SetValue(ref _showCenterPercentage, value);
        }

        public OverviewPieSmallSliceMode SmallSliceMode
        {
            get => _smallSliceMode;
            set => SetValue(ref _smallSliceMode, value);
        }

        public StartPagePieWidgetSettings Clone()
        {
            return new StartPagePieWidgetSettings
            {
                ShowCenterPercentage = ShowCenterPercentage,
                SmallSliceMode = SmallSliceMode
            };
        }
    }
}
