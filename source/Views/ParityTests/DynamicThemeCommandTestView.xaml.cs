using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services.Logging;

namespace PlayniteAchievements.Views.ParityTests
{
    public partial class DynamicThemeCommandTestView : UserControl
    {
        private static readonly ILogger _logger = PluginLogger.GetLogger(nameof(DynamicThemeCommandTestView));
        private const string AllGamesLabel = "All Games";

        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly Game _game;
        private bool _initialized;

        public string GameName { get; }
        public string GameIdText { get; }
        public bool HasSelectedGame => _game != null;
        public string SelectedGameModeHint => HasSelectedGame
            ? "Selected-game and all-games commands are enabled."
            : "All-games mode: selected-game commands are disabled.";
        public PlayniteAchievementsSettings Settings => _plugin.Settings;

        public IReadOnlyList<string> SelectedGameFilterOptions { get; } = new[]
        {
            DynamicThemeViewKeys.All,
            DynamicThemeViewKeys.Unlocked,
            DynamicThemeViewKeys.Locked
        };

        public IReadOnlyList<string> SelectedGameSortOptions { get; } = new[]
        {
            DynamicThemeViewKeys.Default,
            DynamicThemeViewKeys.UnlockTime,
            DynamicThemeViewKeys.Rarity
        };

        public IReadOnlyList<string> LibrarySortOptions { get; } = new[]
        {
            DynamicThemeViewKeys.UnlockTime,
            DynamicThemeViewKeys.Rarity
        };

        public IReadOnlyList<string> SummarySortOptions { get; } = new[]
        {
            DynamicThemeViewKeys.LastUnlock,
            DynamicThemeViewKeys.LastPlayed,
            DynamicThemeViewKeys.UnlockedCount
        };

        public IReadOnlyList<string> SortDirectionOptions { get; } = new[]
        {
            DynamicThemeViewKeys.Descending,
            DynamicThemeViewKeys.Ascending
        };

        public ObservableCollection<string> LibraryProviderOptions { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> SummaryProviderOptions { get; } = new ObservableCollection<string>();

        public DynamicThemeCommandTestView(Game game = null)
        {
            InitializeComponent();

            _plugin = PlayniteAchievementsPlugin.Instance ?? throw new InvalidOperationException("Plugin instance not available");
            _game = game;

            GameName = _game?.Name ?? AllGamesLabel;
            GameIdText = _game?.Id.ToString() ?? AllGamesLabel;

            DataContext = this;

            Loaded += DynamicThemeCommandTestView_Loaded;
            Unloaded += DynamicThemeCommandTestView_Unloaded;
        }

        private void DynamicThemeCommandTestView_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            _plugin.Settings.PropertyChanged += Settings_PropertyChanged;

            _plugin.ThemeUpdateService?.EnsureAllGamesThemeDataLoaded(includeHeavyAchievementLists: true);
            _plugin.ThemeUpdateService?.RequestUpdate(_game?.Id);
            RefreshProviderOptions();
            SyncSelectionsFromSettings();
        }

        private void DynamicThemeCommandTestView_Unloaded(object sender, RoutedEventArgs e)
        {
            if (!_initialized)
            {
                return;
            }

            _plugin.Settings.PropertyChanged -= Settings_PropertyChanged;
            _initialized = false;
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e?.PropertyName != null && !IsDynamicSurfaceProperty(e.PropertyName))
            {
                return;
            }

            Dispatcher?.BeginInvoke(new Action(() =>
            {
                RefreshProviderOptions();
                SyncSelectionsFromSettings();
            }), DispatcherPriority.Background);
        }

        private static bool IsDynamicSurfaceProperty(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(PlayniteAchievementsSettings.DynamicAchievements):
                case nameof(PlayniteAchievementsSettings.DynamicAchievementsFilterKey):
                case nameof(PlayniteAchievementsSettings.DynamicAchievementsSortKey):
                case nameof(PlayniteAchievementsSettings.DynamicAchievementsSortDirectionKey):
                case nameof(PlayniteAchievementsSettings.DynamicLibraryAchievements):
                case nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsProviderKey):
                case nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsSortKey):
                case nameof(PlayniteAchievementsSettings.DynamicLibraryAchievementsSortDirectionKey):
                case nameof(PlayniteAchievementsSettings.DynamicGameSummaries):
                case nameof(PlayniteAchievementsSettings.DynamicGameSummariesProviderKey):
                case nameof(PlayniteAchievementsSettings.DynamicGameSummariesSortKey):
                case nameof(PlayniteAchievementsSettings.DynamicGameSummariesSortDirectionKey):
                    return true;
                default:
                    return false;
            }
        }

        private void RequestUpdate_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _plugin.ThemeUpdateService?.EnsureAllGamesThemeDataLoaded(includeHeavyAchievementLists: true);
                _plugin.ThemeUpdateService?.RequestUpdate(_game?.Id);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to request dynamic theme update from test view.");
                _plugin?.PlayniteApi?.Dialogs?.ShowErrorMessage(
                    $"Failed to request update: {ex.Message}",
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"));
            }
        }

        private void OpenGameView_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!HasSelectedGame)
                {
                    return;
                }

                _plugin.OpenSingleGameAchievementsView(_game.Id);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to open single-game achievements view from test view.");
                _plugin?.PlayniteApi?.Dialogs?.ShowErrorMessage(
                    $"Failed to open game view: {ex.Message}",
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"));
            }
        }

        private void ApplySelectedGameFilter_Click(object sender, RoutedEventArgs e)
        {
            if (!HasSelectedGame)
            {
                return;
            }

            ExecuteCommand(_plugin.Settings.SetDynamicAchievementsFilterCommand, SelectedGameFilterCombo?.SelectedItem, nameof(PlayniteAchievementsSettings.SetDynamicAchievementsFilterCommand));
        }

        private void ApplySelectedGameSort_Click(object sender, RoutedEventArgs e)
        {
            if (!HasSelectedGame)
            {
                return;
            }

            ExecuteCommand(_plugin.Settings.SortDynamicAchievementsCommand, SelectedGameSortCombo?.SelectedItem, nameof(PlayniteAchievementsSettings.SortDynamicAchievementsCommand));
        }

        private void ApplySelectedGameDirection_Click(object sender, RoutedEventArgs e)
        {
            if (!HasSelectedGame)
            {
                return;
            }

            ExecuteCommand(_plugin.Settings.SetDynamicAchievementsSortDirectionCommand, SelectedGameDirectionCombo?.SelectedItem, nameof(PlayniteAchievementsSettings.SetDynamicAchievementsSortDirectionCommand));
        }

        private void ApplyLibraryProvider_Click(object sender, RoutedEventArgs e)
        {
            ExecuteCommand(_plugin.Settings.FilterDynamicLibraryAchievementsByProviderCommand, LibraryProviderCombo?.SelectedItem, nameof(PlayniteAchievementsSettings.FilterDynamicLibraryAchievementsByProviderCommand));
        }

        private void ApplyLibrarySort_Click(object sender, RoutedEventArgs e)
        {
            ExecuteCommand(_plugin.Settings.SortDynamicLibraryAchievementsCommand, LibrarySortCombo?.SelectedItem, nameof(PlayniteAchievementsSettings.SortDynamicLibraryAchievementsCommand));
        }

        private void ApplyLibraryDirection_Click(object sender, RoutedEventArgs e)
        {
            ExecuteCommand(_plugin.Settings.SetDynamicLibraryAchievementsSortDirectionCommand, LibraryDirectionCombo?.SelectedItem, nameof(PlayniteAchievementsSettings.SetDynamicLibraryAchievementsSortDirectionCommand));
        }

        private void ApplySummaryProvider_Click(object sender, RoutedEventArgs e)
        {
            ExecuteCommand(_plugin.Settings.FilterDynamicGameSummariesByProviderCommand, SummaryProviderCombo?.SelectedItem, nameof(PlayniteAchievementsSettings.FilterDynamicGameSummariesByProviderCommand));
        }

        private void ApplySummarySort_Click(object sender, RoutedEventArgs e)
        {
            ExecuteCommand(_plugin.Settings.SortDynamicGameSummariesCommand, SummarySortCombo?.SelectedItem, nameof(PlayniteAchievementsSettings.SortDynamicGameSummariesCommand));
        }

        private void ApplySummaryDirection_Click(object sender, RoutedEventArgs e)
        {
            ExecuteCommand(_plugin.Settings.SetDynamicGameSummariesSortDirectionCommand, SummaryDirectionCombo?.SelectedItem, nameof(PlayniteAchievementsSettings.SetDynamicGameSummariesSortDirectionCommand));
        }

        private void ExecuteCommand(ICommand command, object selectedValue, string commandName)
        {
            try
            {
                var parameter = selectedValue?.ToString();
                if (string.IsNullOrWhiteSpace(parameter))
                {
                    return;
                }

                if (command == null)
                {
                    _logger.Debug($"Command '{commandName}' is not available in dynamic test view.");
                    return;
                }

                if (!command.CanExecute(parameter))
                {
                    _logger.Debug($"Command '{commandName}' rejected parameter '{parameter}'.");
                    return;
                }

                command.Execute(parameter);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to execute dynamic command '{commandName}'.");
                _plugin?.PlayniteApi?.Dialogs?.ShowErrorMessage(
                    $"Failed to execute command: {ex.Message}",
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"));
            }
        }

        private void RefreshProviderOptions()
        {
            UpdateProviderOptions(
                LibraryProviderOptions,
                Settings.DynamicLibraryAchievements?.Select(item => item?.ProviderKey),
                Settings.DynamicLibraryAchievementsProviderKey);

            UpdateProviderOptions(
                SummaryProviderOptions,
                Settings.DynamicGameSummaries?.Select(item => item?.ProviderKey),
                Settings.DynamicGameSummariesProviderKey);
        }

        private static void UpdateProviderOptions(
            ObservableCollection<string> target,
            IEnumerable<string> sourceKeys,
            string selectedKey)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                DynamicThemeViewKeys.All
            };

            foreach (var key in sourceKeys ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    keys.Add(key);
                }
            }

            if (!string.IsNullOrWhiteSpace(selectedKey))
            {
                keys.Add(selectedKey);
            }

            var orderedKeys = keys
                .OrderBy(key => string.Equals(key, DynamicThemeViewKeys.All, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            target.Clear();
            foreach (var key in orderedKeys)
            {
                target.Add(key);
            }
        }

        private void SyncSelectionsFromSettings()
        {
            SetComboSelection(SelectedGameFilterCombo, Settings.DynamicAchievementsFilterKey);
            SetComboSelection(SelectedGameSortCombo, Settings.DynamicAchievementsSortKey);
            SetComboSelection(SelectedGameDirectionCombo, Settings.DynamicAchievementsSortDirectionKey);

            SetComboSelection(LibraryProviderCombo, Settings.DynamicLibraryAchievementsProviderKey);
            SetComboSelection(LibrarySortCombo, Settings.DynamicLibraryAchievementsSortKey);
            SetComboSelection(LibraryDirectionCombo, Settings.DynamicLibraryAchievementsSortDirectionKey);

            SetComboSelection(SummaryProviderCombo, Settings.DynamicGameSummariesProviderKey);
            SetComboSelection(SummarySortCombo, Settings.DynamicGameSummariesSortKey);
            SetComboSelection(SummaryDirectionCombo, Settings.DynamicGameSummariesSortDirectionKey);
        }

        private static void SetComboSelection(ComboBox combo, string value)
        {
            if (combo == null || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            object selected = null;
            foreach (var item in combo.Items)
            {
                if (string.Equals(item?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    selected = item;
                    break;
                }
            }

            combo.SelectedItem = selected ?? value;
        }
    }
}
