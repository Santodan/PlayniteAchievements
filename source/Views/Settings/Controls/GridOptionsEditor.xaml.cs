using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Views.Settings.Controls
{
    /// <summary>
    /// Reusable per-surface grid options editor. Hosts bind <see cref="Options"/> to an option
    /// object from the grid options catalog (e.g.
    /// {Binding Persisted.GridOptions.Achievement[OverviewRecent]}) and the editor edits the
    /// option object directly. The control never sets its own DataContext, so the inherited
    /// settings DataContext stays available to hosts.
    ///
    /// Typed dispatch: <see cref="AchievementOptions"/>, <see cref="GameSummaryOptions"/>,
    /// <see cref="FriendSummaryOptions"/>, <see cref="CategorySummaryOptions"/> and
    /// <see cref="CommonOptions"/> are read-only casts of <see cref="Options"/> refreshed
    /// in the Options changed callback. The XAML gates each typed row panel on its typed property
    /// being non-null, so bindings through the null typed properties stay silent. The common rows
    /// (column headers, control bar, row height, max rows) are gated on <see cref="CommonOptions"/>
    /// because <see cref="CategorySummaryGridOptions"/> does not derive from
    /// <see cref="GridCommonOptions"/>.
    ///
    /// Row capability flag defaults: every row a surface commonly shows defaults to true
    /// (ShowColumnHeadersRow, ShowControlBarRow, ShowRowHeightRow, ShowMaxRowsRow, ShowSortRow,
    /// ShowCoverImagesRow, ShowRarityGlowRow, ShowColorNamesRow, ShowDateModeRow,
    /// ShowMetadataRows, ShowCompletionGlowRow); rows only a few surfaces show default to
    /// false (ShowMaxHeightRow, ShowCategoryModeRow). Chosen so the Overview display section
    /// needs few overrides: GameSummaries.Overview needs none, Achievement.OverviewRecent only
    /// disables the sort row, Achievement.OverviewSelectedGame disables cover images and enables
    /// the category mode row.
    /// </summary>
    public partial class GridOptionsEditor : UserControl
    {
        public GridOptionsEditor()
        {
            InitializeComponent();
        }

        public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(
            nameof(Options),
            typeof(object),
            typeof(GridOptionsEditor),
            new PropertyMetadata(null, OnOptionsChanged));

        public object Options
        {
            get => GetValue(OptionsProperty);
            set => SetValue(OptionsProperty, value);
        }

        private static readonly DependencyPropertyKey AchievementOptionsPropertyKey =
            DependencyProperty.RegisterReadOnly(
                nameof(AchievementOptions),
                typeof(AchievementGridOptions),
                typeof(GridOptionsEditor),
                new PropertyMetadata(null));

        public static readonly DependencyProperty AchievementOptionsProperty =
            AchievementOptionsPropertyKey.DependencyProperty;

        public AchievementGridOptions AchievementOptions =>
            (AchievementGridOptions)GetValue(AchievementOptionsProperty);

        private static readonly DependencyPropertyKey GameSummaryOptionsPropertyKey =
            DependencyProperty.RegisterReadOnly(
                nameof(GameSummaryOptions),
                typeof(GameSummaryGridOptions),
                typeof(GridOptionsEditor),
                new PropertyMetadata(null));

        public static readonly DependencyProperty GameSummaryOptionsProperty =
            GameSummaryOptionsPropertyKey.DependencyProperty;

        public GameSummaryGridOptions GameSummaryOptions =>
            (GameSummaryGridOptions)GetValue(GameSummaryOptionsProperty);

        private static readonly DependencyPropertyKey FriendSummaryOptionsPropertyKey =
            DependencyProperty.RegisterReadOnly(
                nameof(FriendSummaryOptions),
                typeof(FriendSummaryGridOptions),
                typeof(GridOptionsEditor),
                new PropertyMetadata(null));

        public static readonly DependencyProperty FriendSummaryOptionsProperty =
            FriendSummaryOptionsPropertyKey.DependencyProperty;

        public FriendSummaryGridOptions FriendSummaryOptions =>
            (FriendSummaryGridOptions)GetValue(FriendSummaryOptionsProperty);

        private static readonly DependencyPropertyKey CategorySummaryOptionsPropertyKey =
            DependencyProperty.RegisterReadOnly(
                nameof(CategorySummaryOptions),
                typeof(CategorySummaryGridOptions),
                typeof(GridOptionsEditor),
                new PropertyMetadata(null));

        public static readonly DependencyProperty CategorySummaryOptionsProperty =
            CategorySummaryOptionsPropertyKey.DependencyProperty;

        public CategorySummaryGridOptions CategorySummaryOptions =>
            (CategorySummaryGridOptions)GetValue(CategorySummaryOptionsProperty);

        private static readonly DependencyPropertyKey CommonOptionsPropertyKey =
            DependencyProperty.RegisterReadOnly(
                nameof(CommonOptions),
                typeof(GridCommonOptions),
                typeof(GridOptionsEditor),
                new PropertyMetadata(null));

        public static readonly DependencyProperty CommonOptionsProperty =
            CommonOptionsPropertyKey.DependencyProperty;

        public GridCommonOptions CommonOptions =>
            (GridCommonOptions)GetValue(CommonOptionsProperty);

        private static void OnOptionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var editor = (GridOptionsEditor)d;
            editor.SetValue(AchievementOptionsPropertyKey, e.NewValue as AchievementGridOptions);
            editor.SetValue(GameSummaryOptionsPropertyKey, e.NewValue as GameSummaryGridOptions);
            editor.SetValue(FriendSummaryOptionsPropertyKey, e.NewValue as FriendSummaryGridOptions);
            editor.SetValue(CategorySummaryOptionsPropertyKey, e.NewValue as CategorySummaryGridOptions);
            editor.SetValue(CommonOptionsPropertyKey, e.NewValue as GridCommonOptions);
        }

        public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
            nameof(Header),
            typeof(string),
            typeof(GridOptionsEditor),
            new PropertyMetadata(null));

        public string Header
        {
            get => (string)GetValue(HeaderProperty);
            set => SetValue(HeaderProperty, value);
        }

        private static DependencyProperty RegisterFlag(string name, bool defaultValue)
        {
            return DependencyProperty.Register(name, typeof(bool), typeof(GridOptionsEditor), new PropertyMetadata(defaultValue));
        }

        public static readonly DependencyProperty ShowColumnHeadersRowProperty = RegisterFlag(nameof(ShowColumnHeadersRow), true);
        public bool ShowColumnHeadersRow { get => (bool)GetValue(ShowColumnHeadersRowProperty); set => SetValue(ShowColumnHeadersRowProperty, value); }

        public static readonly DependencyProperty ShowControlBarRowProperty = RegisterFlag(nameof(ShowControlBarRow), true);
        public bool ShowControlBarRow { get => (bool)GetValue(ShowControlBarRowProperty); set => SetValue(ShowControlBarRowProperty, value); }

        public static readonly DependencyProperty ShowRowHeightRowProperty = RegisterFlag(nameof(ShowRowHeightRow), true);
        public bool ShowRowHeightRow { get => (bool)GetValue(ShowRowHeightRowProperty); set => SetValue(ShowRowHeightRowProperty, value); }

        public static readonly DependencyProperty ShowMaxRowsRowProperty = RegisterFlag(nameof(ShowMaxRowsRow), true);
        public bool ShowMaxRowsRow { get => (bool)GetValue(ShowMaxRowsRowProperty); set => SetValue(ShowMaxRowsRowProperty, value); }

        public static readonly DependencyProperty ShowSortRowProperty = RegisterFlag(nameof(ShowSortRow), true);
        public bool ShowSortRow { get => (bool)GetValue(ShowSortRowProperty); set => SetValue(ShowSortRowProperty, value); }

        public static readonly DependencyProperty ShowMaxHeightRowProperty = RegisterFlag(nameof(ShowMaxHeightRow), false);
        public bool ShowMaxHeightRow { get => (bool)GetValue(ShowMaxHeightRowProperty); set => SetValue(ShowMaxHeightRowProperty, value); }

        public static readonly DependencyProperty ShowCoverImagesRowProperty = RegisterFlag(nameof(ShowCoverImagesRow), true);
        public bool ShowCoverImagesRow { get => (bool)GetValue(ShowCoverImagesRowProperty); set => SetValue(ShowCoverImagesRowProperty, value); }

        public static readonly DependencyProperty ShowRarityGlowRowProperty = RegisterFlag(nameof(ShowRarityGlowRow), true);
        public bool ShowRarityGlowRow { get => (bool)GetValue(ShowRarityGlowRowProperty); set => SetValue(ShowRarityGlowRowProperty, value); }

        public static readonly DependencyProperty ShowColorNamesRowProperty = RegisterFlag(nameof(ShowColorNamesRow), true);
        public bool ShowColorNamesRow { get => (bool)GetValue(ShowColorNamesRowProperty); set => SetValue(ShowColorNamesRowProperty, value); }

        public static readonly DependencyProperty ShowCategoryModeRowProperty = RegisterFlag(nameof(ShowCategoryModeRow), false);
        public bool ShowCategoryModeRow { get => (bool)GetValue(ShowCategoryModeRowProperty); set => SetValue(ShowCategoryModeRowProperty, value); }

        public static readonly DependencyProperty ShowDateModeRowProperty = RegisterFlag(nameof(ShowDateModeRow), true);
        public bool ShowDateModeRow { get => (bool)GetValue(ShowDateModeRowProperty); set => SetValue(ShowDateModeRowProperty, value); }

        public static readonly DependencyProperty ShowMetadataRowsProperty = RegisterFlag(nameof(ShowMetadataRows), true);
        public bool ShowMetadataRows { get => (bool)GetValue(ShowMetadataRowsProperty); set => SetValue(ShowMetadataRowsProperty, value); }

        public static readonly DependencyProperty ShowCompletionGlowRowProperty = RegisterFlag(nameof(ShowCompletionGlowRow), true);
        public bool ShowCompletionGlowRow { get => (bool)GetValue(ShowCompletionGlowRowProperty); set => SetValue(ShowCompletionGlowRowProperty, value); }
    }
}
