using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Editable ordered list of provider paths: one row per path with its own validation glyph,
    /// Browse and Remove, plus an Add button. The owning settings view supplies the validator and
    /// the browse dialog, and writes <see cref="GetPaths"/> back to its settings on
    /// <see cref="PathsChanged"/>.
    /// </summary>
    public partial class ProviderPathListEditor : UserControl
    {
        private readonly ObservableCollection<ProviderPathListRow> _rows = new ObservableCollection<ProviderPathListRow>();
        private Func<string, ProviderPathValidation> _validate;
        private Func<string> _browse;

        public ProviderPathListEditor()
        {
            InitializeComponent();
            RowsHost.ItemsSource = _rows;
        }

        /// <summary>
        /// Raised after a row is added, removed, or its path is committed.
        /// </summary>
        public event EventHandler PathsChanged;

        public void Configure(Func<string, ProviderPathValidation> validate, Func<string> browse)
        {
            _validate = validate;
            _browse = browse;
        }

        /// <summary>
        /// Replaces the rows with <paramref name="paths"/>. Does not raise <see cref="PathsChanged"/>.
        /// </summary>
        public void SetPaths(IEnumerable<string> paths)
        {
            foreach (var row in _rows)
            {
                row.PropertyChanged -= Row_PropertyChanged;
            }

            _rows.Clear();
            foreach (var path in ProviderPathList.Normalize(paths))
            {
                AddRow(path);
            }

            EnsureEditableRow();
        }

        public List<string> GetPaths()
        {
            return ProviderPathList.Normalize(_rows.Select(row => row.Path));
        }

        /// <summary>
        /// Re-runs validation on every row, for when files on disk may have changed.
        /// </summary>
        public void Revalidate()
        {
            foreach (var row in _rows)
            {
                Validate(row);
            }
        }

        private ProviderPathListRow AddRow(string path)
        {
            var row = new ProviderPathListRow(path);
            Validate(row);
            row.PropertyChanged += Row_PropertyChanged;
            _rows.Add(row);
            return row;
        }

        // An empty list still shows one blank row to type into.
        private void EnsureEditableRow()
        {
            if (_rows.Count == 0)
            {
                AddRow(string.Empty);
            }
        }

        private void Validate(ProviderPathListRow row)
        {
            var path = (row.Path ?? string.Empty).Trim();
            if (path.Length == 0 || _validate == null)
            {
                row.HasStatus = false;
                row.IsValid = false;
                row.StatusText = null;
                return;
            }

            var result = _validate(path) ?? ProviderPathValidation.Invalid("LOCPlayAch_InvalidPath");
            row.HasStatus = true;
            row.IsValid = result.IsValid;
            row.StatusText = ResourceProvider.GetString(result.IsValid ? "LOCPlayAch_Status_Succeeded" : result.MessageKey);
        }

        private void Row_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ProviderPathListRow.Path) || !(sender is ProviderPathListRow row))
            {
                return;
            }

            Validate(row);
            PathsChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            // Reuse a trailing blank row rather than stacking empty ones.
            if (_rows.Count > 0 && string.IsNullOrWhiteSpace(_rows[_rows.Count - 1].Path))
            {
                return;
            }

            AddRow(string.Empty);
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is ProviderPathListRow row))
            {
                return;
            }

            row.PropertyChanged -= Row_PropertyChanged;
            _rows.Remove(row);
            EnsureEditableRow();
            PathsChanged?.Invoke(this, EventArgs.Empty);
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is ProviderPathListRow row))
            {
                return;
            }

            var selectedPath = _browse?.Invoke();
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                row.Path = selectedPath;
            }
        }

        private void PathTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || !(sender is TextBox textBox))
            {
                return;
            }

            e.Handled = true;
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            textBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
    }
}
