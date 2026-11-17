using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels.Settings;
using PlayniteAchievements.Views.Helpers;
using PlayniteAchievements.Views.Settings.Controls;

namespace PlayniteAchievements.Views.Settings.General
{
    /// <summary>
    /// The Achievement styles list: the rarity, trophy and points priority: drag rows to reorder, tick Trophy or Points
    /// to use them. Rarity stays on. Edits write straight to the persisted list.
    /// </summary>
    public partial class ProgressDetailPriorityEditor : UserControl, IDisposable
    {
        private const string ProgressDetailsDragFormat = "PlayniteAchievements.ProgressDetailOrder";

        private readonly PlayniteAchievementsSettings _settings;
        private readonly PersistedSettingsSubscription _persistedSubscription;

        public ProgressDetailPriorityEditor()
        {
            InitializeComponent();
        }

        internal ProgressDetailPriorityEditor(PlayniteAchievementsSettings settings)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _persistedSubscription = new PersistedSettingsSubscription(
                _settings,
                OnPersistedPropertyChanged,
                SyncProgressDetailRows);

            SyncProgressDetailRows();

            DataGridRowReorderBehavior.SetOptions(ProgressDetailsGrid, new DataGridRowReorderOptions
            {
                DragDataFormat = ProgressDetailsDragFormat,
                DropIndicator = ProgressDetailsDropLine,
                DragCountPopup = ProgressDetailsDragPopup,
                DragCountText = ProgressDetailsDragText,
                IsReorderableItem = item => item is ProgressDetailRowItem,
                ExtractDragKeys = items => items
                    .OfType<ProgressDetailRowItem>()
                    .Select(row => row.Style.ToString())
                    .ToList(),
                MoveItemsRelativeToTarget = (keys, target, insertAfter) =>
                    target is ProgressDetailRowItem targetRow &&
                    MoveProgressDetails(keys, targetRow.Style, insertAfter),
                MoveItemsToEnd = keys => MoveProgressDetails(keys, null, true)
            });
        }

        /// <summary>Rows of the priority grid, mirroring the persisted list.</summary>
        public ObservableCollection<ProgressDetailRowItem> ProgressDetailRows { get; } =
            new ObservableCollection<ProgressDetailRowItem>();

        /// <summary>
        /// Brings the rows in line with the persisted list in place (move and update rather than
        /// rebuild), so a drag in progress keeps its row instances.
        /// </summary>
        private void SyncProgressDetailRows()
        {
            var details = _settings?.Persisted?.ProgressDetails ?? ProgressDetailOrder.Default();
            for (var index = 0; index < details.Count; index++)
            {
                var entry = details[index];
                var row = ProgressDetailRows.FirstOrDefault(item => item.Style == entry.Style);
                if (row == null)
                {
                    ProgressDetailRows.Insert(index, new ProgressDetailRowItem(entry.Style, entry.Enabled));
                    continue;
                }

                var current = ProgressDetailRows.IndexOf(row);
                if (current != index)
                {
                    ProgressDetailRows.Move(current, index);
                }

                row.IsEnabled = entry.Enabled;
            }
        }

        /// <summary>
        /// Moves the dragged styles before or after <paramref name="target"/> (or to the end when
        /// it is null) and writes the new order to the persisted list.
        /// </summary>
        private bool MoveProgressDetails(List<string> keys, ProgressDetailStyle? target, bool insertAfter)
        {
            var persisted = _settings?.Persisted;
            if (persisted == null || keys == null || keys.Count == 0)
            {
                return false;
            }

            var moving = new HashSet<string>(keys, StringComparer.Ordinal);
            var details = persisted.ProgressDetails;
            var moved = details.Where(entry => moving.Contains(entry.Style.ToString())).ToList();
            if (moved.Count == 0 || (target.HasValue && moving.Contains(target.Value.ToString())))
            {
                return false;
            }

            var remaining = details.Where(entry => !moving.Contains(entry.Style.ToString())).ToList();
            var insertAt = remaining.Count;
            if (target.HasValue)
            {
                var targetIndex = remaining.FindIndex(entry => entry.Style == target.Value);
                if (targetIndex < 0)
                {
                    return false;
                }

                insertAt = insertAfter ? targetIndex + 1 : targetIndex;
            }

            remaining.InsertRange(insertAt, moved);
            persisted.ProgressDetails = remaining;
            return true;
        }

        private void ProgressDetailEnabled_Click(object sender, RoutedEventArgs e)
        {
            var persisted = _settings?.Persisted;
            if (persisted == null ||
                !(sender is CheckBox checkBox) ||
                !(checkBox.DataContext is ProgressDetailRowItem row) ||
                !row.CanToggle)
            {
                return;
            }

            var isEnabled = checkBox.IsChecked == true;
            persisted.ProgressDetails = persisted.ProgressDetails
                .Select(entry => entry.Style == row.Style
                    ? new ProgressDetailEntry { Style = entry.Style, Enabled = isEnabled }
                    : entry)
                .ToList();
        }

        private void OnPersistedPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PersistedSettings.ProgressDetails))
            {
                SyncProgressDetailRows();
            }
        }

        public void Dispose()
        {
            _persistedSubscription?.Dispose();
        }
    }
}
