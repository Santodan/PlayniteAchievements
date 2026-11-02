using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Views.Converters;

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
    /// false (ShowMaxHeightRow, ShowCategoryModeRow).
    ///
    /// Hosts normally set <see cref="SurfaceKind"/> and <see cref="SurfaceKey"/> rather than the
    /// individual flags, which applies the surface's row set from
    /// <see cref="GridDisplaySurfaces"/> — the single source of truth shared with the per-grid
    /// display settings popup. Setting a flag directly still works, but a later
    /// SurfaceKind/SurfaceKey change overwrites it.
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
            editor.RebuildControlBarItemRows();
        }

        /// <summary>
        /// The grid's control bar items, which the editor lists under Show Control Bar with a
        /// checkbox each; null or empty lists none.
        /// </summary>
        public static readonly DependencyProperty ControlBarEntriesProperty = DependencyProperty.Register(
            nameof(ControlBarEntries),
            typeof(System.Collections.Generic.IReadOnlyList<PlayniteAchievements.ViewModels.Items.IGridControlBarEntry>),
            typeof(GridOptionsEditor),
            new PropertyMetadata(null, (d, e) => ((GridOptionsEditor)d).RebuildControlBarItemRows()));

        public System.Collections.Generic.IReadOnlyList<PlayniteAchievements.ViewModels.Items.IGridControlBarEntry> ControlBarEntries
        {
            get => (System.Collections.Generic.IReadOnlyList<PlayniteAchievements.ViewModels.Items.IGridControlBarEntry>)GetValue(ControlBarEntriesProperty);
            set => SetValue(ControlBarEntriesProperty, value);
        }

        private static readonly DependencyPropertyKey ControlBarItemRowsPropertyKey =
            DependencyProperty.RegisterReadOnly(
                nameof(ControlBarItemRows),
                typeof(System.Collections.Generic.IReadOnlyList<ControlBarItemRow>),
                typeof(GridOptionsEditor),
                new PropertyMetadata(null));

        /// <summary>One row per control bar item, for the record being edited.</summary>
        public System.Collections.Generic.IReadOnlyList<ControlBarItemRow> ControlBarItemRows =>
            (System.Collections.Generic.IReadOnlyList<ControlBarItemRow>)GetValue(ControlBarItemRowsPropertyKey.DependencyProperty);

        private void RebuildControlBarItemRows()
        {
            var options = CommonOptions;
            var entries = ControlBarEntries;
            SetValue(
                ControlBarItemRowsPropertyKey,
                options == null || entries == null
                    ? null
                    : entries
                        .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Id) && !string.IsNullOrWhiteSpace(entry.LabelKey))
                        .Select(entry => new ControlBarItemRow(options, entry))
                        .ToList());
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

        public static readonly DependencyProperty SurfaceKindProperty = DependencyProperty.Register(
            nameof(SurfaceKind),
            typeof(GridOptionKind?),
            typeof(GridOptionsEditor),
            new PropertyMetadata(null, OnSurfaceChanged));

        public GridOptionKind? SurfaceKind
        {
            get => (GridOptionKind?)GetValue(SurfaceKindProperty);
            set => SetValue(SurfaceKindProperty, value);
        }

        public static readonly DependencyProperty SurfaceKeyProperty = DependencyProperty.Register(
            nameof(SurfaceKey),
            typeof(string),
            typeof(GridOptionsEditor),
            new PropertyMetadata(null, OnSurfaceChanged));

        public string SurfaceKey
        {
            get => (string)GetValue(SurfaceKeyProperty);
            set => SetValue(SurfaceKeyProperty, value);
        }

        private static void OnSurfaceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var editor = (GridOptionsEditor)d;
            var kind = editor.SurfaceKind;
            var key = editor.SurfaceKey;
            if (kind == null || string.IsNullOrEmpty(key))
            {
                return;
            }

            var rows = GridDisplaySurfaces.Resolve(kind.Value, key);
            editor.ShowColumnHeadersRow = rows.ShowColumnHeadersRow;
            editor.ShowControlBarRow = rows.ShowControlBarRow;
            editor.ShowRowHeightRow = rows.ShowRowHeightRow;
            editor.ShowMaxRowsRow = rows.ShowMaxRowsRow;
            editor.ShowSortRow = rows.ShowSortRow;
            editor.ShowMaxHeightRow = rows.ShowMaxHeightRow;
            editor.ShowCoverImagesRow = rows.ShowCoverImagesRow;
            editor.ShowRarityGlowRow = rows.ShowRarityGlowRow;
            editor.ShowColorNamesRow = rows.ShowColorNamesRow;
            editor.ShowCategoryModeRow = rows.ShowCategoryModeRow;
            editor.ShowDateModeRow = rows.ShowDateModeRow;
            editor.ShowMetadataRows = rows.ShowMetadataRows;
            editor.ShowCompletionGlowRow = rows.ShowCompletionGlowRow;
            editor.ShowGameSortPinOrderChoice = rows.ShowGameSortPinOrderChoice;
            editor.ShowLibraryGameSortChoices = rows.ShowLibraryGameSortChoices;
        }

        // The Tags selector's checkable menu. Each toggle reads the record fresh, because the menu
        // stays open across clicks.
        private void TagDisplayButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || button.ContextMenu == null || AchievementOptions == null)
            {
                return;
            }

            var menu = button.ContextMenu;
            menu.Items.Clear();
            menu.Items.Add(CreateTagDisplayItem(button, AchievementTagDisplayTextConverter.IconsKey, AchievementTagDisplay.Icons));
            menu.Items.Add(CreateTagDisplayItem(button, AchievementTagDisplayTextConverter.ColorsKey, AchievementTagDisplay.Colors));
            MultiSelectMenu.Open(button, menu);
        }

        private MenuItem CreateTagDisplayItem(Button button, string labelKey, AchievementTagDisplay flag)
        {
            return MultiSelectMenu.CreateItem(
                button,
                ResourceProvider.GetString(labelKey),
                (AchievementOptions.TagDisplay & flag) != 0,
                isChecked =>
                {
                    var options = AchievementOptions;
                    if (options != null)
                    {
                        options.TagDisplay = isChecked ? options.TagDisplay | flag : options.TagDisplay & ~flag;
                    }
                });
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

        // Offers the order-preserving PinOrder game sort choice; only surfaces whose source
        // order is user-controlled (the showcase pinned-games grid) enable it.
        public static readonly DependencyProperty ShowGameSortPinOrderChoiceProperty = RegisterFlag(nameof(ShowGameSortPinOrderChoice), false);
        public bool ShowGameSortPinOrderChoice { get => (bool)GetValue(ShowGameSortPinOrderChoiceProperty); set => SetValue(ShowGameSortPinOrderChoiceProperty, value); }

        // Offers the date added, release date, playtime and remaining game sort choices, which
        // read the user's own library and achievement counts.
        public static readonly DependencyProperty ShowLibraryGameSortChoicesProperty = RegisterFlag(nameof(ShowLibraryGameSortChoices), false);
        public bool ShowLibraryGameSortChoices { get => (bool)GetValue(ShowLibraryGameSortChoicesProperty); set => SetValue(ShowLibraryGameSortChoicesProperty, value); }
    }
}
