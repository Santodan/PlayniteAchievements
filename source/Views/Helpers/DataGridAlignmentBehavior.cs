using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.Helpers
{
    public static class DataGridAlignmentBehavior
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(DataGridAlignmentBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static readonly DependencyProperty HeaderHorizontalAlignmentProperty =
            DependencyProperty.RegisterAttached(
                "HeaderHorizontalAlignment",
                typeof(HorizontalAlignment),
                typeof(DataGridAlignmentBehavior),
                new PropertyMetadata(HorizontalAlignment.Center));

        public static readonly DependencyProperty CellHorizontalAlignmentProperty =
            DependencyProperty.RegisterAttached(
                "CellHorizontalAlignment",
                typeof(HorizontalAlignment),
                typeof(DataGridAlignmentBehavior),
                new PropertyMetadata(HorizontalAlignment.Left));

        public static readonly DependencyProperty CellVerticalAlignmentProperty =
            DependencyProperty.RegisterAttached(
                "CellVerticalAlignment",
                typeof(VerticalAlignment),
                typeof(DataGridAlignmentBehavior),
                new PropertyMetadata(VerticalAlignment.Center));

        public static readonly DependencyProperty CellTextAlignmentProperty =
            DependencyProperty.RegisterAttached(
                "CellTextAlignment",
                typeof(TextAlignment),
                typeof(DataGridAlignmentBehavior),
                new PropertyMetadata(TextAlignment.Left));

        public static readonly DependencyProperty ColumnCellAlignmentOverridesProviderProperty =
            DependencyProperty.RegisterAttached(
                "ColumnCellAlignmentOverridesProvider",
                typeof(Func<Dictionary<string, GridAlignment>>),
                typeof(DataGridAlignmentBehavior),
                new PropertyMetadata(null, OnColumnAlignmentOverridesProviderChanged));

        public static readonly DependencyProperty ColumnCellVerticalAlignmentOverridesProviderProperty =
            DependencyProperty.RegisterAttached(
                "ColumnCellVerticalAlignmentOverridesProvider",
                typeof(Func<Dictionary<string, GridVerticalAlignment>>),
                typeof(DataGridAlignmentBehavior),
                new PropertyMetadata(null, OnColumnAlignmentOverridesProviderChanged));

        public static readonly DependencyProperty ColumnHeaderHorizontalAlignmentOverridesProviderProperty =
            DependencyProperty.RegisterAttached(
                "ColumnHeaderHorizontalAlignmentOverridesProvider",
                typeof(Func<Dictionary<string, GridAlignment>>),
                typeof(DataGridAlignmentBehavior),
                new PropertyMetadata(null, OnColumnAlignmentOverridesProviderChanged));

        private static readonly DependencyProperty StateProperty =
            DependencyProperty.RegisterAttached(
                "State",
                typeof(AlignmentState),
                typeof(DataGridAlignmentBehavior),
                new PropertyMetadata(null));

        public static bool GetIsEnabled(DependencyObject obj)
        {
            return (bool)obj.GetValue(IsEnabledProperty);
        }

        public static void SetIsEnabled(DependencyObject obj, bool value)
        {
            obj.SetValue(IsEnabledProperty, value);
        }

        public static HorizontalAlignment GetHeaderHorizontalAlignment(DependencyObject obj)
        {
            return (HorizontalAlignment)obj.GetValue(HeaderHorizontalAlignmentProperty);
        }

        public static void SetHeaderHorizontalAlignment(DependencyObject obj, HorizontalAlignment value)
        {
            obj.SetValue(HeaderHorizontalAlignmentProperty, value);
        }

        public static HorizontalAlignment GetCellHorizontalAlignment(DependencyObject obj)
        {
            return (HorizontalAlignment)obj.GetValue(CellHorizontalAlignmentProperty);
        }

        public static void SetCellHorizontalAlignment(DependencyObject obj, HorizontalAlignment value)
        {
            obj.SetValue(CellHorizontalAlignmentProperty, value);
        }

        public static VerticalAlignment GetCellVerticalAlignment(DependencyObject obj)
        {
            return (VerticalAlignment)obj.GetValue(CellVerticalAlignmentProperty);
        }

        public static void SetCellVerticalAlignment(DependencyObject obj, VerticalAlignment value)
        {
            obj.SetValue(CellVerticalAlignmentProperty, value);
        }

        public static TextAlignment GetCellTextAlignment(DependencyObject obj)
        {
            return (TextAlignment)obj.GetValue(CellTextAlignmentProperty);
        }

        public static void SetCellTextAlignment(DependencyObject obj, TextAlignment value)
        {
            obj.SetValue(CellTextAlignmentProperty, value);
        }

        public static Func<Dictionary<string, GridAlignment>> GetColumnCellAlignmentOverridesProvider(
            DependencyObject obj)
        {
            return (Func<Dictionary<string, GridAlignment>>)obj.GetValue(
                ColumnCellAlignmentOverridesProviderProperty);
        }

        public static void SetColumnCellAlignmentOverridesProvider(
            DependencyObject obj,
            Func<Dictionary<string, GridAlignment>> value)
        {
            obj.SetValue(ColumnCellAlignmentOverridesProviderProperty, value);
        }

        public static Func<Dictionary<string, GridVerticalAlignment>> GetColumnCellVerticalAlignmentOverridesProvider(
            DependencyObject obj)
        {
            return (Func<Dictionary<string, GridVerticalAlignment>>)obj.GetValue(
                ColumnCellVerticalAlignmentOverridesProviderProperty);
        }

        public static void SetColumnCellVerticalAlignmentOverridesProvider(
            DependencyObject obj,
            Func<Dictionary<string, GridVerticalAlignment>> value)
        {
            obj.SetValue(ColumnCellVerticalAlignmentOverridesProviderProperty, value);
        }

        public static Func<Dictionary<string, GridAlignment>> GetColumnHeaderHorizontalAlignmentOverridesProvider(
            DependencyObject obj)
        {
            return (Func<Dictionary<string, GridAlignment>>)obj.GetValue(
                ColumnHeaderHorizontalAlignmentOverridesProviderProperty);
        }

        public static void SetColumnHeaderHorizontalAlignmentOverridesProvider(
            DependencyObject obj,
            Func<Dictionary<string, GridAlignment>> value)
        {
            obj.SetValue(ColumnHeaderHorizontalAlignmentOverridesProviderProperty, value);
        }

        public static void Refresh(DependencyObject obj)
        {
            if (obj is DataGrid grid)
            {
                GetState(grid)?.ApplyAlignment();
            }
        }

        private static AlignmentState GetState(DependencyObject obj)
        {
            return (AlignmentState)obj.GetValue(StateProperty);
        }

        private static void SetState(DependencyObject obj, AlignmentState value)
        {
            obj.SetValue(StateProperty, value);
        }

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is DataGrid grid))
            {
                return;
            }

            var isEnabled = e.NewValue is bool value && value;
            var state = GetState(grid);

            if (isEnabled)
            {
                if (state == null)
                {
                    state = new AlignmentState(grid);
                    SetState(grid, state);
                }

                state.Attach();
                return;
            }

            state?.Detach();
            SetState(grid, null);
        }

        private static void OnColumnAlignmentOverridesProviderChanged(
            DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            Refresh(d);
        }

        private static PlayniteAchievementsSettings ResolveSettingsSource(DependencyObject source)
        {
            var current = source;
            while (current != null)
            {
                if (current is FrameworkElement element)
                {
                    var settings = ResolveFromDataContext(element.DataContext);
                    if (settings != null)
                    {
                        return settings;
                    }
                }

                current = GetParent(current);
            }

            return PlayniteAchievementsPlugin.Instance?.Settings;
        }

        private static PlayniteAchievementsSettings ResolveFromDataContext(object dataContext)
        {
            if (dataContext is PlayniteAchievementsSettings settings)
            {
                return settings;
            }

            if (dataContext is ThemePreviewContext previewContext)
            {
                return previewContext.Settings;
            }

            return null;
        }

        private static DependencyObject GetParent(DependencyObject current)
        {
            if (current == null)
            {
                return null;
            }

            DependencyObject parent = null;
            try
            {
                parent = VisualTreeHelper.GetParent(current);
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }

            return parent ?? LogicalTreeHelper.GetParent(current);
        }

        private sealed class AlignmentState
        {
            private readonly DataGrid _grid;
            private bool _isAttached;
            private PlayniteAchievementsSettings _settings;
            private PersistedSettings _persisted;

            public AlignmentState(DataGrid grid)
            {
                _grid = grid;
            }

            public void Attach()
            {
                if (_isAttached)
                {
                    return;
                }

                _grid.Loaded += OnLoaded;
                _grid.Unloaded += OnUnloaded;
                _grid.DataContextChanged += OnDataContextChanged;
                _grid.Columns.CollectionChanged += OnColumnsChanged;

                _isAttached = true;

                AttachSettings();
                ApplyAlignment();
            }

            public void Detach()
            {
                if (!_isAttached)
                {
                    return;
                }

                _grid.Loaded -= OnLoaded;
                _grid.Unloaded -= OnUnloaded;
                _grid.DataContextChanged -= OnDataContextChanged;
                _grid.Columns.CollectionChanged -= OnColumnsChanged;

                DetachSettings();
                ClearColumnAlignments();

                _isAttached = false;
            }

            private void OnLoaded(object sender, RoutedEventArgs e)
            {
                AttachSettings();
                ApplyAlignment();
            }

            private void OnUnloaded(object sender, RoutedEventArgs e)
            {
                DetachSettings();
            }

            private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
            {
                AttachSettings();
                ApplyAlignment();
            }

            private void OnColumnsChanged(object sender, NotifyCollectionChangedEventArgs e)
            {
                ApplyAlignment();
            }

            private void AttachSettings()
            {
                var settings = ResolveSettingsSource(_grid);
                var persisted = settings?.Persisted;

                if (ReferenceEquals(settings, _settings) &&
                    ReferenceEquals(persisted, _persisted))
                {
                    return;
                }

                DetachSettings();

                _settings = settings;
                _persisted = persisted;

                if (_settings != null)
                {
                    _settings.PropertyChanged += Settings_PropertyChanged;
                }

                if (_persisted != null)
                {
                    _persisted.PropertyChanged += Persisted_PropertyChanged;
                }
            }

            private void DetachSettings()
            {
                if (_settings != null)
                {
                    _settings.PropertyChanged -= Settings_PropertyChanged;
                }

                if (_persisted != null)
                {
                    _persisted.PropertyChanged -= Persisted_PropertyChanged;
                }

                _settings = null;
                _persisted = null;
            }

            private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
            {
                if (string.IsNullOrEmpty(e?.PropertyName) ||
                    e.PropertyName == nameof(PlayniteAchievementsSettings.Persisted))
                {
                    AttachSettings();
                    ApplyAlignment();
                }
            }

            private void Persisted_PropertyChanged(object sender, PropertyChangedEventArgs e)
            {
                if (string.IsNullOrEmpty(e?.PropertyName) ||
                    e.PropertyName == nameof(PersistedSettings.GridColumnHeaderAlignment) ||
                    e.PropertyName == nameof(PersistedSettings.GridCellAlignment) ||
                    e.PropertyName == nameof(PersistedSettings.GridCellVerticalAlignment) ||
                    IsColumnCellAlignmentProperty(e.PropertyName) ||
                    IsColumnCellVerticalAlignmentProperty(e.PropertyName) ||
                    IsColumnHeaderHorizontalAlignmentProperty(e.PropertyName))
                {
                    ApplyAlignment();
                }
            }

            public void ApplyAlignment()
            {
                if (_grid?.Columns == null)
                {
                    return;
                }

                var defaultHeaderAlignment =
                    _persisted?.GridColumnHeaderAlignment ?? GridAlignment.Center;

                var defaultCellHorizontalAlignment =
                    _persisted?.GridCellAlignment ?? GridAlignment.Left;

                var defaultCellVerticalAlignment =
                    _persisted?.GridCellVerticalAlignment ?? GridVerticalAlignment.Center;

                var cellHorizontalOverrides =
                    GetColumnCellAlignmentOverridesProvider(_grid)?.Invoke();

                var cellVerticalOverrides =
                    GetColumnCellVerticalAlignmentOverridesProvider(_grid)?.Invoke();

                var headerHorizontalOverrides =
                    GetColumnHeaderHorizontalAlignmentOverridesProvider(_grid)?.Invoke();

                foreach (var column in _grid.Columns)
                {
                    ApplyColumnAlignment(
                        column,
                        defaultHeaderAlignment,
                        defaultCellHorizontalAlignment,
                        defaultCellVerticalAlignment,
                        headerHorizontalOverrides,
                        cellHorizontalOverrides,
                        cellVerticalOverrides);
                }
            }

            private static void ApplyColumnAlignment(
                DataGridColumn column,
                GridAlignment defaultHeaderAlignment,
                GridAlignment defaultCellHorizontalAlignment,
                GridVerticalAlignment defaultCellVerticalAlignment,
                IReadOnlyDictionary<string, GridAlignment> headerHorizontalOverrides,
                IReadOnlyDictionary<string, GridAlignment> cellHorizontalOverrides,
                IReadOnlyDictionary<string, GridVerticalAlignment> cellVerticalOverrides)
            {
                if (column == null)
                {
                    return;
                }

                var key = ResolveColumnKey(column);

                var effectiveHeaderAlignment = ResolveHorizontalAlignment(
                    key,
                    defaultHeaderAlignment,
                    headerHorizontalOverrides);

                var effectiveCellHorizontalAlignment = ResolveHorizontalAlignment(
                    key,
                    defaultCellHorizontalAlignment,
                    cellHorizontalOverrides);

                var effectiveCellVerticalAlignment = ResolveVerticalAlignment(
                    key,
                    defaultCellVerticalAlignment,
                    cellVerticalOverrides);

                SetHeaderHorizontalAlignment(
                    column,
                    GridAlignmentMapping.ToHorizontalAlignment(effectiveHeaderAlignment));

                SetCellHorizontalAlignment(
                    column,
                    GridAlignmentMapping.ToHorizontalAlignment(effectiveCellHorizontalAlignment));

                SetCellTextAlignment(
                    column,
                    GridAlignmentMapping.ToTextAlignment(effectiveCellHorizontalAlignment));

                SetCellVerticalAlignment(
                    column,
                    GridAlignmentMapping.ToVerticalAlignment(effectiveCellVerticalAlignment));
            }

            private static GridAlignment ResolveHorizontalAlignment(
                string key,
                GridAlignment defaultAlignment,
                IReadOnlyDictionary<string, GridAlignment> overrides)
            {
                if (!string.IsNullOrWhiteSpace(key) &&
                    overrides != null &&
                    overrides.TryGetValue(key, out var overrideAlignment))
                {
                    return overrideAlignment;
                }

                return defaultAlignment;
            }

            private static GridVerticalAlignment ResolveVerticalAlignment(
                string key,
                GridVerticalAlignment defaultAlignment,
                IReadOnlyDictionary<string, GridVerticalAlignment> overrides)
            {
                if (!string.IsNullOrWhiteSpace(key) &&
                    overrides != null &&
                    overrides.TryGetValue(key, out var overrideAlignment))
                {
                    return overrideAlignment;
                }

                return defaultAlignment;
            }

            private void ClearColumnAlignments()
            {
                if (_grid?.Columns == null)
                {
                    return;
                }

                foreach (var column in _grid.Columns)
                {
                    column.ClearValue(HeaderHorizontalAlignmentProperty);
                    column.ClearValue(CellHorizontalAlignmentProperty);
                    column.ClearValue(CellVerticalAlignmentProperty);
                    column.ClearValue(CellTextAlignmentProperty);
                }
            }

            private static string ResolveColumnKey(DataGridColumn column)
            {
                if (column == null)
                {
                    return null;
                }

                var key = ColumnVisibilityHelper.GetColumnKey(column);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    return key;
                }

                return !string.IsNullOrWhiteSpace(column.SortMemberPath)
                    ? column.SortMemberPath
                    : null;
            }

            private static bool IsColumnCellAlignmentProperty(string propertyName)
            {
                return propertyName == nameof(PersistedSettings.OverviewRecentAchievementColumnAlignments) ||
                       propertyName == nameof(PersistedSettings.OverviewSelectedGameAchievementColumnAlignments) ||
                       propertyName == nameof(PersistedSettings.SingleGameColumnAlignments) ||
                       propertyName == nameof(PersistedSettings.DesktopThemeColumnAlignments) ||
                       propertyName == nameof(PersistedSettings.OverviewGameSummariesColumnAlignments) ||
                       propertyName == nameof(PersistedSettings.StartPageAchievementColumnAlignments) ||
                       propertyName == nameof(PersistedSettings.StartPageGameSummariesColumnAlignments);
            }

            private static bool IsColumnCellVerticalAlignmentProperty(string propertyName)
            {
                return propertyName == "OverviewRecentAchievementColumnVerticalAlignments" ||
                       propertyName == "OverviewSelectedGameAchievementColumnVerticalAlignments" ||
                       propertyName == "SingleGameColumnVerticalAlignments" ||
                       propertyName == "DesktopThemeColumnVerticalAlignments" ||
                       propertyName == "OverviewGameSummariesColumnVerticalAlignments" ||
                       propertyName == "StartPageAchievementColumnVerticalAlignments" ||
                       propertyName == "StartPageGameSummariesColumnVerticalAlignments";
            }

            private static bool IsColumnHeaderHorizontalAlignmentProperty(string propertyName)
            {
                return propertyName == "OverviewRecentAchievementColumnHeaderAlignments" ||
                       propertyName == "OverviewSelectedGameAchievementColumnHeaderAlignments" ||
                       propertyName == "SingleGameColumnHeaderAlignments" ||
                       propertyName == "DesktopThemeColumnHeaderAlignments" ||
                       propertyName == "OverviewGameSummariesColumnHeaderAlignments" ||
                       propertyName == "StartPageAchievementColumnHeaderAlignments" ||
                       propertyName == "StartPageGameSummariesColumnHeaderAlignments";
            }
        }
    }
}