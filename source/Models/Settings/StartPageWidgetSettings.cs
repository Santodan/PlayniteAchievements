using System.ComponentModel;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Models.Settings
{
    public sealed class StartPageGameSummariesGridSettings : ObservableObject
    {
        private GameSummaryGridOptions _options;

        public StartPageGameSummariesGridSettings()
            : this(new GameSummaryGridOptions
            {
                Columns = GridColumnLayoutOptions.CreateWithProgressRightAlignment(),
                MaxRows = PersistedSettings.DefaultStartPageGridMaxRows,
                ShowControlBar = false
            })
        {
        }

        internal StartPageGameSummariesGridSettings(GameSummaryGridOptions options)
        {
            SetOptions(options);
        }

        public bool ShowMetadataPlatform
        {
            get => Options.ShowMetadataPlatform;
            set => Options.ShowMetadataPlatform = value;
        }

        public bool ShowMetadataPlaytime
        {
            get => Options.ShowMetadataPlaytime;
            set => Options.ShowMetadataPlaytime = value;
        }

        public bool ShowMetadataRegion
        {
            get => Options.ShowMetadataRegion;
            set => Options.ShowMetadataRegion = value;
        }

        public bool UseCoverImages
        {
            get => Options.UseCoverImages;
            set => Options.UseCoverImages = value;
        }

        public bool ShowCompletionGlow
        {
            get => Options.ShowCompletionGlow;
            set => Options.ShowCompletionGlow = value;
        }

        public bool ColorRarityColumnsByRarity
        {
            get => Options.ColorRarityColumnsByRarity;
            set => Options.ColorRarityColumnsByRarity = value;
        }

        public bool ShowNameAboveProgress
        {
            get => Options.ShowNameAboveProgress;
            set => Options.ShowNameAboveProgress = value;
        }

        public bool ShowColumnHeaders
        {
            get => Options.ShowColumnHeaders;
            set => Options.ShowColumnHeaders = value;
        }

        public bool ShowControlBar
        {
            get => Options.ShowControlBar;
            set => Options.ShowControlBar = value;
        }

        public double? RowHeight
        {
            get => Options.RowHeight;
            set => Options.RowHeight = value;
        }

        public int? MaxRows
        {
            get => Options.MaxRows;
            set => Options.MaxRows = value;
        }

        public GameSummariesSortMode SortMode
        {
            get => Options.SortMode;
            set => Options.SortMode = value;
        }

        public bool SortDescending
        {
            get => Options.SortDescending;
            set => Options.SortDescending = value;
        }

        public StartPageGameSummariesGridSettings Clone()
        {
            return new StartPageGameSummariesGridSettings(Options.Clone());
        }

        internal void CopyTo(GameSummaryGridOptions target)
        {
            if (target == null)
            {
                return;
            }

            target.ShowMetadataPlatform = ShowMetadataPlatform;
            target.ShowMetadataPlaytime = ShowMetadataPlaytime;
            target.ShowMetadataRegion = ShowMetadataRegion;
            target.UseCoverImages = UseCoverImages;
            target.ShowCompletionGlow = ShowCompletionGlow;
            target.ColorRarityColumnsByRarity = ColorRarityColumnsByRarity;
            target.ShowNameAboveProgress = ShowNameAboveProgress;
            target.ShowColumnHeaders = ShowColumnHeaders;
            target.ShowControlBar = ShowControlBar;
            target.RowHeight = RowHeight;
            target.MaxRows = MaxRows;
            target.SortMode = SortMode;
            target.SortDescending = SortDescending;
        }

        internal void SetOptions(GameSummaryGridOptions options)
        {
            if (ReferenceEquals(_options, options))
            {
                return;
            }

            if (_options != null)
            {
                _options.PropertyChanged -= OnOptionsPropertyChanged;
            }

            _options = options ?? new GameSummaryGridOptions();
            _options.PropertyChanged += OnOptionsPropertyChanged;
        }

        private GameSummaryGridOptions Options => _options ?? (_options = new GameSummaryGridOptions());

        private void OnOptionsPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(e.PropertyName);
        }
    }

    public class StartPageRecentUnlocksGridSettings : ObservableObject
    {
        private AchievementGridOptions _options;

        public StartPageRecentUnlocksGridSettings()
            : this(new AchievementGridOptions
            {
                MaxRows = PersistedSettings.DefaultStartPageGridMaxRows,
                ShowControlBar = false
            })
        {
        }

        internal StartPageRecentUnlocksGridSettings(AchievementGridOptions options)
        {
            SetOptions(options);
        }

        public bool UseCoverImages
        {
            get => Options.UseCoverImages;
            set => Options.UseCoverImages = value;
        }

        public bool ShowRarityGlow
        {
            get => Options.ShowRarityGlow;
            set => Options.ShowRarityGlow = value;
        }

        public bool ColorNamesByRarity
        {
            get => Options.ColorNamesByRarity;
            set => Options.ColorNamesByRarity = value;
        }

        public bool ColorRarityColumnsByRarity
        {
            get => Options.ColorRarityColumnsByRarity;
            set => Options.ColorRarityColumnsByRarity = value;
        }

        public bool ShowColumnHeaders
        {
            get => Options.ShowColumnHeaders;
            set => Options.ShowColumnHeaders = value;
        }

        public bool ShowControlBar
        {
            get => Options.ShowControlBar;
            set => Options.ShowControlBar = value;
        }

        public double? RowHeight
        {
            get => Options.RowHeight;
            set => Options.RowHeight = value;
        }

        public int? MaxRows
        {
            get => Options.MaxRows;
            set => Options.MaxRows = value;
        }

        public CompactListSortMode SortMode
        {
            get => Options.SortMode;
            set => Options.SortMode = value;
        }

        public bool SortDescending
        {
            get => Options.SortDescending;
            set => Options.SortDescending = value;
        }

        public StartPageRecentUnlocksGridSettings Clone()
        {
            return new StartPageRecentUnlocksGridSettings(Options.Clone());
        }

        internal void CopyTo(AchievementGridOptions target)
        {
            if (target == null)
            {
                return;
            }

            target.UseCoverImages = UseCoverImages;
            target.ShowRarityGlow = ShowRarityGlow;
            target.ColorNamesByRarity = ColorNamesByRarity;
            target.ColorRarityColumnsByRarity = ColorRarityColumnsByRarity;
            target.ShowColumnHeaders = ShowColumnHeaders;
            target.ShowControlBar = ShowControlBar;
            target.RowHeight = RowHeight;
            target.MaxRows = MaxRows;
            target.SortMode = SortMode;
            target.SortDescending = SortDescending;
        }

        internal void SetOptions(AchievementGridOptions options)
        {
            if (ReferenceEquals(_options, options))
            {
                return;
            }

            if (_options != null)
            {
                _options.PropertyChanged -= OnOptionsPropertyChanged;
            }

            _options = options ?? new AchievementGridOptions();
            _options.PropertyChanged += OnOptionsPropertyChanged;
        }

        protected AchievementGridOptions Options => _options ?? (_options = new AchievementGridOptions());

        private void OnOptionsPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(e.PropertyName);
        }
    }

    public sealed class StartPageFriendsRecentUnlocksGridSettings : StartPageRecentUnlocksGridSettings
    {
        public StartPageFriendsRecentUnlocksGridSettings()
            : this(new AchievementGridOptions
            {
                MaxRows = PersistedSettings.DefaultStartPageGridMaxRows,
                ShowControlBar = false
            })
        {
        }

        internal StartPageFriendsRecentUnlocksGridSettings(AchievementGridOptions options)
            : base(options)
        {
        }

        public new StartPageFriendsRecentUnlocksGridSettings Clone()
        {
            return new StartPageFriendsRecentUnlocksGridSettings(Options.Clone());
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
