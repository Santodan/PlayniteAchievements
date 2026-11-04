using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Settings;
using PlayniteAchievements.Views.Helpers;
using PlayniteAchievements.Views.Settings.Controls;

namespace PlayniteAchievements.Views.Dialogs
{
    /// <summary>
    /// The per-grid display settings popup, opened from a grid's own context menu.
    ///
    /// Edits apply to the LIVE catalog records, so the grid behind the window changes as options are
    /// changed, and a snapshot taken on open lets Cancel put them back. Closing the window without
    /// pressing Save reverts, like any dialog with a Cancel button.
    ///
    /// Saving is delegated to <see cref="DebouncedSettingsPersist"/>, which also declines to write
    /// while a settings window holds a pending edit snapshot -- there, that window's OK/Cancel
    /// governs these records too. A settings window Cancel replaces the whole persisted instance,
    /// which this window detects and rebinds to.
    /// </summary>
    public sealed class GridDisplaySettingsDialog : UserControl
    {
        private const double WindowWidth = 480;
        private const double ContentWidth = WindowWidth - 28;
        private const double FallbackHeight = 620;
        private const double MinimumHeight = 300;

        // One key for every grid, not one per surface: the user sizes this window once and expects
        // it to stay put, and a key per surface would grow an entry for each of the twenty grids.
        // The measured height above is only the first-open default; a saved placement wins.
        private const string WindowPlacementKey = "GridDisplaySettings";

        private readonly GridOptionKind _kind;
        private readonly string _surfaceKey;
        private readonly string _categorySurfaceKey;
        private readonly System.Collections.Generic.IReadOnlyList<PlayniteAchievements.ViewModels.Items.IGridControlBarEntry> _barEntries;

        private GridOptionsEditor _primaryEditor;
        private GridOptionsEditor _categoryEditor;
        private CheckBox _hideCategoryRowCheckBox;
        private GridOptionsSnapshot _snapshot;
        private DebouncedSettingsPersist _persist;
        private PersistedSettingsSubscription _persistedSubscription;
        private bool _saved;

        private GridDisplaySettingsDialog(
            GridOptionKind kind,
            string surfaceKey,
            string categorySurfaceKey,
            System.Collections.Generic.IReadOnlyList<PlayniteAchievements.ViewModels.Items.IGridControlBarEntry> barEntries)
        {
            _kind = kind;
            _surfaceKey = surfaceKey;
            _categorySurfaceKey = categorySurfaceKey;
            _barEntries = barEntries;

            // The editor merges its own dictionaries, but this control's own header, checkbox and
            // buttons need the settings window's keyed and implicit styles too.
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/PlayniteAchievements;component/Resources/SettingsScopedControlStyles.xaml",
                    UriKind.Absolute)
            });

            _persist = new DebouncedSettingsPersist(
                this,
                () => PlayniteAchievementsPlugin.Instance?.PersistSettingsForUi(),
                () => PlayniteAchievementsPlugin.Instance?.IsSettingsEditSessionActive == true);

            Content = BuildContent();
            FormattingCulture.Apply(this);

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        /// <summary>
        /// Opens the display settings for one grid surface. <paramref name="categorySurfaceKey"/>
        /// adds the grid's nested category sub-grid section, and applies only to achievement
        /// surfaces.
        /// </summary>
        /// <param name="barEntries">The grid's control bar items, listed under Show Control Bar.</param>
        public static void Show(
            GridOptionKind kind,
            string surfaceKey,
            string categorySurfaceKey,
            System.Collections.Generic.IReadOnlyList<PlayniteAchievements.ViewModels.Items.IGridControlBarEntry> barEntries = null)
        {
            if (string.IsNullOrEmpty(surfaceKey))
            {
                return;
            }

            var editor = new GridDisplaySettingsDialog(kind, surfaceKey, categorySurfaceKey, barEntries);
            var title = string.Format(
                FormattingCulture.Current,
                Localize("LOCPlayAch_Showcase_WidgetSettingsTitle"),
                Localize(GridDisplaySurfaces.ResolveTitleKey(kind, surfaceKey)));

            var window = PlayniteUiProvider.CreateExtensionWindow(
                title,
                editor,
                new WindowOptions
                {
                    Width = WindowWidth,
                    Height = SettingsDialogSizing.MeasureHeight(
                        editor,
                        ContentWidth,
                        MinimumHeight,
                        FallbackHeight),
                    CanBeResizable = true,
                    ShowCloseButton = true,
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false
                });

            window.MinWidth = 360;
            window.MinHeight = MinimumHeight;
            WindowPlacementPersistenceService.Attach(window, WindowPlacementKey);

            // Closing by the window chrome is a cancel: the edits are already applied to the live
            // records, so without this the X would silently keep them and Cancel would be the only
            // way out.
            window.Closed += (_, __) =>
            {
                if (!editor._saved)
                {
                    editor.Revert();
                }

                editor._persist?.Dispose();
                editor._persist = null;
            };

            window.ShowDialog();
        }

        private static string Localize(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            var value = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? key : value;
        }

        private static GridOptionsCatalog Catalog =>
            PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.GridOptions;

        private UIElement BuildContent()
        {
            // The editors' dropdown rows share one control height across both editors, as in the
            // showcase widget settings.
            var panel = new StackPanel();
            Grid.SetIsSharedSizeScope(panel, true);

            _primaryEditor = new GridOptionsEditor
            {
                SurfaceKind = _kind,
                SurfaceKey = _surfaceKey,
                ControlBarEntries = _barEntries
            };
            panel.Children.Add(_primaryEditor);

            // Only achievement grids host a category sub-grid, and only that grid's own record
            // carries the hide-after-selection flag.
            if (_kind == GridOptionKind.Achievement && !string.IsNullOrEmpty(_categorySurfaceKey))
            {
                AppendCategorySection(panel, _categorySurfaceKey, out _hideCategoryRowCheckBox, out _categoryEditor);
            }

            BindRecords();

            var root = new Grid();
            root.SetResourceReference(MarginProperty, "PlayAch.Thickness.CardPadding");
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = panel
            };
            Grid.SetRow(scroller, 0);
            root.Children.Add(scroller);

            var buttons = BuildButtons();
            Grid.SetRow(buttons, 1);
            root.Children.Add(buttons);

            return root;
        }

        /// <summary>
        /// Appends an achievement grid's category grid section: its heading, the hide-after-
        /// selection row (bound by the caller to the achievement grid's own record) and the
        /// category grid's editor (whose record the caller sets). Shared with the showcase grid
        /// widget's settings, so the two show the same rows.
        /// </summary>
        internal static void AppendCategorySection(
            Panel panel,
            string categorySurfaceKey,
            out CheckBox hideCategoryRowCheckBox,
            out GridOptionsEditor categoryEditor)
        {
            var header = new TextBlock
            {
                Text = Localize("LOCPlayAch_Settings_CategoryGrid")
            };
            header.SetResourceReference(StyleProperty, "SubSectionHeaderStyle");
            header.SetResourceReference(MarginProperty, "PlayAch.Thickness.SubSectionHeading");
            panel.Children.Add(header);

            hideCategoryRowCheckBox = new CheckBox
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(BuildOptionRow(
                Localize("LOCPlayAch_Settings_HideCategorySummaryRow"),
                hideCategoryRowCheckBox));

            categoryEditor = new GridOptionsEditor
            {
                SurfaceKind = GridOptionKind.CategorySummaries,
                SurfaceKey = categorySurfaceKey
            };
            panel.Children.Add(categoryEditor);
        }

        // Laid out like the editor's option rows: a semibold label in the left two fifths, the
        // control in the right three.
        private static UIElement BuildOptionRow(string label, UIElement control)
        {
            var row = new Grid();
            row.SetResourceReference(MarginProperty, "PlayAch.Thickness.Bottom.Md");
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });

            var labelBlock = new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 0, 8, 0),
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            labelBlock.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
            row.Children.Add(labelBlock);

            Grid.SetColumn(control, 1);
            row.Children.Add(control);
            return row;
        }

        private UIElement BuildButtons()
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            row.SetResourceReference(MarginProperty, "PlayAch.Thickness.Top.Md");

            var cancel = new Button
            {
                Content = Localize("LOCPlayAch_Common_Cancel"),
                MinWidth = 90,
                IsCancel = true
            };
            cancel.SetResourceReference(MarginProperty, "PlayAch.Thickness.Right.Sm");
            cancel.Click += (_, __) => CloseWindow();

            var save = new Button
            {
                Content = Localize("LOCPlayAch_Common_Save"),
                MinWidth = 90,
                IsDefault = true
            };
            save.Click += (_, __) =>
            {
                _saved = true;
                _persist?.Flush();
                CloseWindow();
            };

            row.Children.Add(cancel);
            row.Children.Add(save);
            return row;
        }

        private void CloseWindow()
        {
            Window.GetWindow(this)?.Close();
        }

        /// <summary>
        /// Points the editors at the current catalog records and snapshots their values. Called
        /// again when the persisted instance is replaced, so the window keeps editing the tree that
        /// is actually live and its snapshot describes that tree.
        /// </summary>
        private void BindRecords()
        {
            var catalog = Catalog;
            if (catalog == null)
            {
                return;
            }

            _persist?.ClearRecords();

            var primary = ResolveRecord(catalog, _kind, _surfaceKey);
            if (_primaryEditor != null)
            {
                _primaryEditor.Options = primary;
            }

            object category = null;
            if (_categoryEditor != null)
            {
                category = catalog.GetCategorySummaries(_categorySurfaceKey);
                _categoryEditor.Options = category;
            }

            _snapshot = new GridOptionsSnapshot(primary, category);
            _persist?.Watch(primary);
            _persist?.Watch(category);

            if (_hideCategoryRowCheckBox != null)
            {
                // Rebound rather than left pointing at a replaced record's property.
                BindingOperations.ClearBinding(_hideCategoryRowCheckBox, ToggleButton.IsCheckedProperty);
                if (primary != null)
                {
                    _hideCategoryRowCheckBox.SetBinding(ToggleButton.IsCheckedProperty, new Binding(
                        nameof(AchievementGridOptions.HideCategorySummaryRow))
                    {
                        Source = primary,
                        Mode = BindingMode.TwoWay
                    });
                }
            }
        }

        /// <summary>
        /// Puts the records back as they were when the window opened, then writes, because a
        /// debounced save may already have committed an intermediate state to disk.
        /// </summary>
        private void Revert()
        {
            // Dropped before restoring so the restore's own property changes cannot schedule a
            // save of the values being undone.
            _persist?.ClearRecords();
            _snapshot?.Restore();
            _persist?.PersistNow();
        }

        private static object ResolveRecord(GridOptionsCatalog catalog, GridOptionKind kind, string surfaceKey)
        {
            switch (kind)
            {
                case GridOptionKind.Achievement:
                    return catalog.GetAchievement(surfaceKey);
                case GridOptionKind.GameSummaries:
                    return catalog.GetGameSummaries(surfaceKey);
                case GridOptionKind.FriendSummaries:
                    return catalog.GetFriendSummaries(surfaceKey);
                case GridOptionKind.CategorySummaries:
                    return catalog.GetCategorySummaries(surfaceKey);
                default:
                    return null;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var settings = PlayniteAchievementsPlugin.Instance?.Settings;
            if (settings != null && _persistedSubscription == null)
            {
                _persistedSubscription = new PersistedSettingsSubscription(
                    settings,
                    null,
                    OnPersistedInstanceChanged);
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _persistedSubscription?.Dispose();
            _persistedSubscription = null;
        }

        /// <summary>
        /// A settings window Cancel replaces the whole persisted instance, orphaning the records
        /// these editors were bound to. Rebind to the new tree so the window shows the reverted
        /// values and its own Cancel refers to them, and drop the stale pending write.
        /// </summary>
        private void OnPersistedInstanceChanged()
        {
            _persist?.Cancel();
            BindRecords();
        }
    }
}
