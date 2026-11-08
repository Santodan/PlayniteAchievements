using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels.Settings;
using PlayniteAchievements.Views.Helpers;
using PlayniteAchievements.Views.Settings.Controls;
using PlayniteAchievements.Views.Showcase;

namespace PlayniteAchievements.Views.Settings.Display
{
    /// <summary>
    /// Display settings: General section. Hosts grid defaults, the achievement icon cache options
    /// and the reset-to-defaults action. Spoiler masking lives in <see cref="SpoilersSection"/>.
    /// </summary>
    public partial class DisplayGeneralSection : UserControl, IDisposable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly ILogger _logger;
        private readonly Action _onDisplaySettingsReset;
        private readonly PersistedSettingsSubscription _persistedSubscription;

        public DisplayGeneralSection()
        {
            InitializeComponent();
        }

        internal DisplayGeneralSection(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ILogger logger,
            Action onDisplaySettingsReset)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _logger = logger;
            _onDisplaySettingsReset = onDisplaySettingsReset;

            _persistedSubscription = new PersistedSettingsSubscription(
                _settings,
                OnPersistedPropertyChanged,
                OnSettingsReloaded);

            UpdateGlowTierTexts();
            UpdateScoreCardNames();
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

        private const string ProgressDetailsDragFormat = "PlayniteAchievements.ProgressDetailOrder";

        /// <summary>Rows of the Progress bar details grid, mirroring the persisted priority list.</summary>
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
                !(checkBox.DataContext is ProgressDetailRowItem row))
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

        /// <summary>The first Overview score card's type name, labeling its show toggle.</summary>
        public static readonly DependencyProperty ScoreCard1NameProperty =
            DependencyProperty.Register(nameof(ScoreCard1Name), typeof(string),
                typeof(DisplayGeneralSection), new PropertyMetadata(string.Empty));

        public string ScoreCard1Name
        {
            get => (string)GetValue(ScoreCard1NameProperty);
            set => SetValue(ScoreCard1NameProperty, value);
        }

        /// <summary>The second Overview score card's type name, labeling its show toggle.</summary>
        public static readonly DependencyProperty ScoreCard2NameProperty =
            DependencyProperty.Register(nameof(ScoreCard2Name), typeof(string),
                typeof(DisplayGeneralSection), new PropertyMetadata(string.Empty));

        public string ScoreCard2Name
        {
            get => (string)GetValue(ScoreCard2NameProperty);
            set => SetValue(ScoreCard2NameProperty, value);
        }

        /// <summary>Summary text for the soft-glow tier selector button.</summary>
        public static readonly DependencyProperty SoftGlowTiersTextProperty =
            DependencyProperty.Register(nameof(SoftGlowTiersText), typeof(string),
                typeof(DisplayGeneralSection), new PropertyMetadata(string.Empty));

        public string SoftGlowTiersText
        {
            get => (string)GetValue(SoftGlowTiersTextProperty);
            set => SetValue(SoftGlowTiersTextProperty, value);
        }

        /// <summary>Summary text for the ray tier selector button.</summary>
        public static readonly DependencyProperty RayGlowTiersTextProperty =
            DependencyProperty.Register(nameof(RayGlowTiersText), typeof(string),
                typeof(DisplayGeneralSection), new PropertyMetadata(string.Empty));

        public string RayGlowTiersText
        {
            get => (string)GetValue(RayGlowTiersTextProperty);
            set => SetValue(RayGlowTiersTextProperty, value);
        }

        private void SoftGlowTiersButton_Click(object sender, RoutedEventArgs e)
        {
            RaritySelectorMenu.Open(
                sender as Button,
                () => _settings?.Persisted?.RarityGlowSoftTiers ?? RaritySelectionExtensions.DefaultSoftGlowTiers,
                value => { if (_settings?.Persisted != null) { _settings.Persisted.RarityGlowSoftTiers = value; } },
                UpdateGlowTierTexts,
                includeCompleted: true);
        }

        private void RayGlowTiersButton_Click(object sender, RoutedEventArgs e)
        {
            RaritySelectorMenu.Open(
                sender as Button,
                () => _settings?.Persisted?.RarityGlowRayTiers ?? RaritySelection.None,
                value => { if (_settings?.Persisted != null) { _settings.Persisted.RarityGlowRayTiers = value; } },
                UpdateGlowTierTexts,
                includeCompleted: true);
        }

        private void UpdateGlowTierTexts()
        {
            var persisted = _settings?.Persisted;
            SoftGlowTiersText = RaritySelectorMenu.Format(
                persisted?.RarityGlowSoftTiers ?? RaritySelectionExtensions.DefaultSoftGlowTiers,
                includeCompleted: true);
            RayGlowTiersText = RaritySelectorMenu.Format(
                persisted?.RarityGlowRayTiers ?? RaritySelection.None,
                includeCompleted: true);
        }

        private void OnSettingsReloaded()
        {
            UpdateGlowTierTexts();
            UpdateScoreCardNames();
            SyncProgressDetailRows();
        }

        private void UpdateScoreCardNames()
        {
            var persisted = _settings?.Persisted;
            ScoreCard1Name = ShowcaseUiText.ScoreCardTypeName(
                ShowcaseWidgetOptions.GetScoreCardType(persisted?.OverviewScoreCard1));
            ScoreCard2Name = ShowcaseUiText.ScoreCardTypeName(
                ShowcaseWidgetOptions.GetScoreCardType(persisted?.OverviewScoreCard2));
        }

        private void OnPersistedPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PersistedSettings.ShowCompletedProgressColoring))
            {
                RarityAppearanceHelper.ApplyBadgeApplicationResources(_settings?.Persisted);
            }
            else if (e.PropertyName == nameof(PersistedSettings.IconScaleMode))
            {
                PlayAchResourceService.ApplyIconScaleToApplication(_settings?.Persisted);
            }
            else if (e.PropertyName == nameof(PersistedSettings.OverviewScoreCard1) ||
                     e.PropertyName == nameof(PersistedSettings.OverviewScoreCard2))
            {
                UpdateScoreCardNames();
            }
            else if (e.PropertyName == nameof(PersistedSettings.ProgressDetails))
            {
                SyncProgressDetailRows();
            }
        }

        private void ResetDisplaySettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _logger?.Info("Resetting Display tab settings to defaults.");

                _settings.Persisted.ResetDisplaySettingsToDefaults();
                _onDisplaySettingsReset?.Invoke();

                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    L("LOCPlayAch_Status_Succeeded"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to reset Display tab settings.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        public void Dispose()
        {
            _persistedSubscription?.Dispose();
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }

        private static string LF(string key, params object[] args)
        {
            return string.Format(L(key), args);
        }
    }
}
