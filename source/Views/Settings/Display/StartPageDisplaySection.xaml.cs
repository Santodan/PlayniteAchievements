using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Views.Settings.Display
{
    /// <summary>
    /// Display settings: StartPage section. Hosts activity/progress scope selectors and grid
    /// options for the StartPage views.
    /// </summary>
    public partial class StartPageDisplaySection : UserControl, IDisposable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly PersistedSettingsSubscription _persistedSubscription;

        public StartPageDisplaySection()
        {
            InitializeComponent();
        }

        internal StartPageDisplaySection(PlayniteAchievementsSettings settings)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            _persistedSubscription = new PersistedSettingsSubscription(
                _settings,
                OnPersistedPropertyChanged,
                UpdateStartPageScopeTexts);

            UpdateStartPageScopeTexts();
        }

        public static readonly DependencyProperty StartPageActivityScopeTextProperty =
            DependencyProperty.Register(
                nameof(StartPageActivityScopeText),
                typeof(string),
                typeof(StartPageDisplaySection),
                new PropertyMetadata(string.Empty));

        public string StartPageActivityScopeText
        {
            get => (string)GetValue(StartPageActivityScopeTextProperty);
            set => SetValue(StartPageActivityScopeTextProperty, value);
        }

        public static readonly DependencyProperty StartPageProgressScopeTextProperty =
            DependencyProperty.Register(
                nameof(StartPageProgressScopeText),
                typeof(string),
                typeof(StartPageDisplaySection),
                new PropertyMetadata(string.Empty));

        public string StartPageProgressScopeText
        {
            get => (string)GetValue(StartPageProgressScopeTextProperty);
            set => SetValue(StartPageProgressScopeTextProperty, value);
        }

        private void OnPersistedPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PersistedSettings.StartPageActivityScope) ||
                e.PropertyName == nameof(PersistedSettings.StartPageProgressScope))
            {
                UpdateStartPageScopeTexts();
            }
        }

        private void StartPageActivityScopeSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            OpenStartPageActivityScopeContextMenu(sender as Button);
        }

        private void StartPageProgressScopeSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            OpenStartPageProgressScopeContextMenu(sender as Button);
        }

        private void OpenStartPageActivityScopeContextMenu(Button button)
        {
            var persisted = _settings?.Persisted;
            if (button == null || persisted == null)
            {
                return;
            }

            var options = new[]
            {
                new { Scope = GameActivityScope.Played, Label = L("LOCPlayAch_Filter_Played") },
                new { Scope = GameActivityScope.Unplayed, Label = L("LOCPlayAch_Filter_Unplayed") }
            };

            var menu = PrepareStartPageScopeContextMenu(button);
            if (menu == null)
            {
                return;
            }

            var current = persisted.StartPageActivityScope;
            foreach (var option in options)
            {
                var scope = option.Scope;
                var item = CreateStartPageScopeMenuItem(
                    button,
                    option.Label,
                    current.HasFlag(scope),
                    isChecked =>
                    {
                        var settings = _settings?.Persisted;
                        if (settings == null)
                        {
                            return;
                        }

                        settings.StartPageActivityScope = isChecked
                            ? settings.StartPageActivityScope | scope
                            : settings.StartPageActivityScope & ~scope;
                        UpdateStartPageScopeTexts();
                    });
                menu.Items.Add(item);
            }

            OpenSelectorContextMenu(button, menu);
        }

        private void OpenStartPageProgressScopeContextMenu(Button button)
        {
            var persisted = _settings?.Persisted;
            if (button == null || persisted == null)
            {
                return;
            }

            var options = new[]
            {
                new { Scope = GameProgressScope.Completed, Label = L("LOCPlayAch_Filter_Complete") },
                new { Scope = GameProgressScope.InProgress, Label = L("LOCPlayAch_Filter_InProgress") },
                new { Scope = GameProgressScope.NoProgress, Label = L("LOCPlayAch_Filter_NoProgress") }
            };

            var menu = PrepareStartPageScopeContextMenu(button);
            if (menu == null)
            {
                return;
            }

            var current = persisted.StartPageProgressScope;
            foreach (var option in options)
            {
                var scope = option.Scope;
                var item = CreateStartPageScopeMenuItem(
                    button,
                    option.Label,
                    current.HasFlag(scope),
                    isChecked =>
                    {
                        var settings = _settings?.Persisted;
                        if (settings == null)
                        {
                            return;
                        }

                        settings.StartPageProgressScope = isChecked
                            ? settings.StartPageProgressScope | scope
                            : settings.StartPageProgressScope & ~scope;
                        UpdateStartPageScopeTexts();
                    });
                menu.Items.Add(item);
            }

            OpenSelectorContextMenu(button, menu);
        }

        private static ContextMenu PrepareStartPageScopeContextMenu(Button button)
        {
            var menu = button?.ContextMenu;
            if (menu == null)
            {
                return null;
            }

            menu.Items.Clear();
            return menu;
        }

        private static MenuItem CreateStartPageScopeMenuItem(
            Button button,
            string header,
            bool isChecked,
            Action<bool> setSelection)
        {
            var item = new MenuItem
            {
                Header = header,
                IsCheckable = true,
                StaysOpenOnClick = true,
                IsChecked = isChecked
            };

            var itemStyle = button?.TryFindResource("AchievementMultiSelectMenuItemStyle") as Style;
            if (itemStyle != null)
            {
                item.Style = itemStyle;
            }

            item.Click += (_, __) => setSelection?.Invoke(item.IsChecked);
            return item;
        }

        private static void OpenSelectorContextMenu(Button button, ContextMenu menu)
        {
            if (button == null || menu == null || menu.Items.Count == 0)
            {
                return;
            }

            RoutedEventHandler onClosed = null;
            onClosed = (_, __) =>
            {
                menu.Closed -= onClosed;
                button.ReleaseMouseCapture();
            };

            menu.Closed += onClosed;
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
            menu.IsOpen = true;
        }

        private void UpdateStartPageScopeTexts()
        {
            var persisted = _settings?.Persisted;
            var activityScope = persisted?.StartPageActivityScope ??
                PersistedSettings.DefaultStartPageActivityScope;
            var progressScope = persisted?.StartPageProgressScope ??
                PersistedSettings.DefaultStartPageProgressScope;

            StartPageActivityScopeText = GetActivityScopeText(activityScope);
            StartPageProgressScopeText = GetProgressScopeText(progressScope);
        }

        private static string GetActivityScopeText(GameActivityScope scope)
        {
            scope = PersistedSettings.NormalizeStartPageActivityScope(scope);
            if (scope == GameActivityScope.None)
            {
                return L("LOCPlayAch_Filter_ActivitySelectorPlaceholder");
            }

            var labels = new List<string>();
            if (scope.HasFlag(GameActivityScope.Played))
            {
                labels.Add(L("LOCPlayAch_Filter_Played"));
            }

            if (scope.HasFlag(GameActivityScope.Unplayed))
            {
                labels.Add(L("LOCPlayAch_Filter_Unplayed"));
            }

            return labels.Count > 0
                ? string.Join(", ", labels)
                : L("LOCPlayAch_Filter_ActivitySelectorPlaceholder");
        }

        private static string GetProgressScopeText(GameProgressScope scope)
        {
            scope = PersistedSettings.NormalizeStartPageProgressScope(scope);
            if (scope == GameProgressScope.None)
            {
                return L("LOCPlayAch_Progress");
            }

            var labels = new List<string>();
            if (scope.HasFlag(GameProgressScope.Completed))
            {
                labels.Add(L("LOCPlayAch_Filter_Complete"));
            }

            if (scope.HasFlag(GameProgressScope.InProgress))
            {
                labels.Add(L("LOCPlayAch_Filter_InProgress"));
            }

            if (scope.HasFlag(GameProgressScope.NoProgress))
            {
                labels.Add(L("LOCPlayAch_Filter_NoProgress"));
            }

            return labels.Count > 0
                ? string.Join(", ", labels)
                : L("LOCPlayAch_Progress");
        }

        public void Dispose()
        {
            _persistedSubscription?.Dispose();
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }
}
