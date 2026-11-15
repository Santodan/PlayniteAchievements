using ForkLog = PlayniteAchievements.Services.Logging.ForkLogger;
using System;
using System.Globalization;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.IO;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Services.ThemeMigration
{
    internal static class Ps5TrophyProgressBridge
    {
        internal const string Marker = "PlayAch.Ps5TrophyProgress";
        private static bool _registered;
        private static bool _enableNamedList;
        private static DispatcherTimer _discoveryTimer;
        private static ILogger _logger;
        private static Func<Guid, GameAchievementData> _achievementDataResolver;
        private static readonly HashSet<object> SyncedDetailViewModels = new HashSet<object>();

        internal static void SetAchievementDataResolver(Func<Guid, GameAchievementData> resolver) => _achievementDataResolver = resolver;

        internal static void Start(IPlayniteAPI api, ILogger logger)
        {
            _logger = logger;
            if (api?.ApplicationInfo?.Mode != ApplicationMode.Fullscreen || Application.Current == null) return;
            var theme = api.ApplicationSettings.FullscreenTheme;
            if (string.IsNullOrWhiteSpace(theme)) return;
            try
            {
                _enableNamedList = new[] { api.Paths.ConfigurationPath, api.Paths.ApplicationPath }
                    .Where(root => !string.IsNullOrWhiteSpace(root))
                    .Select(root => System.IO.Path.Combine(root, "Themes", "Fullscreen", theme, "Views", "Main.xaml"))
                    .Any(path => File.Exists(path) && File.ReadAllText(path).Contains(Marker));
                if (!_enableNamedList) return;
                ForkLog.Wrap(_logger)?.Info("[PS5Progress] Migrated theme detected; watching live trophy windows.");
                _discoveryTimer = new DispatcherTimer(DispatcherPriority.Background, Application.Current.Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
                _discoveryTimer.Tick += DiscoverWindows;
                _discoveryTimer.Start();
                DiscoverWindows(null, EventArgs.Empty);
            }
            catch (Exception ex) { ForkLog.Wrap(_logger)?.Error(ex, "[PS5Progress] Failed to start trophy compatibility adapter."); }
        }

        internal static void Stop()
        {
            if (_discoveryTimer != null)
            {
                _discoveryTimer.Stop();
                _discoveryTimer.Tick -= DiscoverWindows;
                _discoveryTimer = null;
            }
            _enableNamedList = false;
            _achievementDataResolver = null;
            SyncedDetailViewModels.Clear();
        }

        private static void DiscoverWindows(object sender, EventArgs args)
        {
            SyncedDetailViewModels.Clear();
            foreach (Window window in Application.Current.Windows)
                if (window.IsVisible) Discover(window);
        }

        private static void Discover(DependencyObject node)
        {
            if (_enableNamedList && node is Image completionImage) AdaptCompletionIcon(completionImage);
            if (_enableNamedList && (node is TextBlock || node is Rectangle))
                OnLoaded(node, new RoutedEventArgs(FrameworkElement.LoadedEvent, node));
            if (node is ListBox list && IsMigrated(list))
            {
                OnListLoaded(list, null);
                RefreshVisuals(list);
                return;
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Discover(VisualTreeHelper.GetChild(node, i));
        }

        private static void AdaptCompletionIcon(Image image)
        {
            if (BindingOperations.GetMultiBinding(image, UIElement.VisibilityProperty)?.Converter is CompletionIconConverter)
            {
                BindingOperations.GetMultiBindingExpression(image, UIElement.VisibilityProperty)?.UpdateTarget();
                return;
            }
            if (image.ReadLocalValue(UIElement.VisibilityProperty) != DependencyProperty.UnsetValue || image.Style == null) return;
            var paths = image.Style.Triggers.OfType<DataTrigger>().Select(trigger => (trigger.Binding as Binding)?.Path?.Path).ToList();
            var prefix = new[] { "", "AchievementsViewModel.SelectedGame.", "OverlayTrophyGame." }
                .FirstOrDefault(candidate => paths.Contains(candidate + "HasPlatinum") && paths.Contains(candidate + "HasCapstoneUnlocked"));
            if (prefix == null) return;
            var binding = new MultiBinding { Mode = BindingMode.OneWay, Converter = new CompletionIconConverter() };
            foreach (var path in new[] { "HasPlatinum", "HasCapstoneUnlocked", "PlatinumCount", "GoldCount", "SilverCount", "BronzeCount", "Total" })
                binding.Bindings.Add(new Binding(prefix + path) { Mode = BindingMode.OneWay });
            BindingOperations.SetBinding(image, UIElement.VisibilityProperty, binding);
        }

        private sealed class CompletionIconConverter : IMultiValueConverter
        {
            public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            {
                if (values.Length != 7) return DependencyProperty.UnsetValue;
                if (Equals(values[0], true) || Equals(values[1], true)) return Visibility.Visible;
                if (values.Skip(2).Any(value => value == DependencyProperty.UnsetValue)) return Visibility.Collapsed;
                var total = System.Convert.ToInt32(values[6], culture);
                var unlocked = values.Skip(2).Take(4).Sum(value => Math.Max(0, System.Convert.ToInt32(value, culture)));
                return total > 0 && unlocked >= total ? Visibility.Visible : Visibility.Collapsed;
            }
            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
        }

        private static void AdaptDetailHeader(FrameworkElement control)
        {
            SynchronizeDetailStates(control.DataContext);
            var property = control is TextBlock ? TextBlock.TextProperty : FrameworkElement.WidthProperty;
            var existing = BindingOperations.GetMultiBinding(control, property);
            if (existing?.Converter is ProgressConverter)
            {
                BindingOperations.GetMultiBindingExpression(control, property)?.UpdateTarget();
                return;
            }
            var original = BindingOperations.GetBinding(control, property);
            var path = original?.Path?.Path;
            var prefix = new[] { "AchievementsViewModel.SelectedGame", "OverlayTrophyGame" }
                .FirstOrDefault(candidate => path == candidate + ".ProgressPercent" || path == candidate + ".Unlocked" || path == candidate + ".ProgressText");
            if (prefix == null || original.Source != null || original.ElementName != null || original.RelativeSource != null) return;
            var binding = new MultiBinding
            {
                Mode = BindingMode.OneWay,
                Converter = new ProgressConverter(path.EndsWith(".ProgressText", StringComparison.Ordinal), path.EndsWith(".Unlocked", StringComparison.Ordinal), true)
            };
            foreach (var field in new[] { "PlatinumCount", "GoldCount", "SilverCount", "BronzeCount", "Total", "Provider" })
                binding.Bindings.Add(new Binding(prefix + "." + field) { Mode = BindingMode.OneWay });
            binding.Bindings.Add(new Binding(path) { Mode = BindingMode.OneWay });
            BindingOperations.SetBinding(control, property, binding);
            ForkLog.Wrap(_logger)?.Debug($"[PS5Progress] Connected game-detail header binding: {path}.");
        }

        private static void SynchronizeDetailStates(object context)
        {
            if (context == null || _achievementDataResolver == null) return;
            var vm = context.GetType().FullName == "PS5Core.AchievementsViewModel" ? context
                : context.GetType().GetProperty("AchievementsViewModel")?.GetValue(context);
            if (vm?.GetType().FullName != "PS5Core.AchievementsViewModel" || !SyncedDetailViewModels.Add(vm)) return;
            try
            {
                var selected = vm.GetType().GetProperty("SelectedGame")?.GetValue(vm);
                var idValue = selected?.GetType().GetProperty("PlayniteGameId")?.GetValue(selected);
                if (Guid.TryParse(idValue?.ToString(), out var gameId))
                    Ps5AchievementStateSynchronizer.Synchronize(vm, _achievementDataResolver(gameId), _logger);
            }
            catch (Exception ex) { ForkLog.Wrap(_logger)?.Error(ex, "[PS5Progress] Failed to synchronize selected-game achievement states."); }
        }

        private static void RefreshVisuals(DependencyObject node)
        {
            if (node is Image completionImage) AdaptCompletionIcon(completionImage);
            if (node is TextBlock || node is Rectangle)
            {
                var control = (FrameworkElement)node;
                OnLoaded(control, new RoutedEventArgs(FrameworkElement.LoadedEvent, control));
                var property = control is TextBlock ? TextBlock.TextProperty : FrameworkElement.WidthProperty;
                BindingOperations.GetMultiBindingExpression(control, property)?.UpdateTarget();
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) RefreshVisuals(VisualTreeHelper.GetChild(node, i));
        }
        private static readonly DependencyProperty ConnectionProperty = DependencyProperty.RegisterAttached(
            "Connection", typeof(RowConnection), typeof(Ps5TrophyProgressBridge));
        private static readonly DependencyProperty WatchesContextProperty = DependencyProperty.RegisterAttached(
            "WatchesContext", typeof(bool), typeof(Ps5TrophyProgressBridge), new PropertyMetadata(false));

        internal static void Register()
        {
            if (_registered) return;
            _registered = true;
            EventManager.RegisterClassHandler(typeof(ListBox), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnListLoaded), true);
            EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded), true);
            EventManager.RegisterClassHandler(typeof(Rectangle), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded), true);
        }

        private static void OnListLoaded(object sender, RoutedEventArgs args)
        {
            if (!(sender is ListBox list) || !IsMigrated(list) || list.GetValue(ConnectionProperty) != null) return;
            var connection = new RowConnection(list);
            list.SetValue(ConnectionProperty, connection);
            connection.Connect();
            ForkLog.Wrap(_logger)?.Info($"[PS5Progress] Connected trophy list: name='{list.Name}', uid='{list.Uid}', rows={list.Items.Count}.");
        }

        private static bool IsMigrated(ListBox list) => Equals(list.Tag, Marker) || list.Uid == Marker ||
            (_enableNamedList && list.Name == "PART_AchievementGamesList");

        private sealed class RowConnection
        {
            private readonly ListBox _list;
            private readonly List<INotifyPropertyChanged> _rows = new List<INotifyPropertyChanged>();
            private bool _updating;
            private readonly DispatcherTimer _timer;
            internal RowConnection(ListBox list)
            {
                _list = list;
                _timer = new DispatcherTimer(DispatcherPriority.Background, list.Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
                _timer.Tick += OnTick;
            }
            internal void Connect()
            {
                ((INotifyCollectionChanged)_list.Items).CollectionChanged += OnCollectionChanged;
                _list.Unloaded += OnUnloaded;
                ReconnectRows();
                _timer.Start();
            }
            // PS5Core 0.7.4 has setters that do not notify every dependent value.
            // Poll only while this migrated trophy window is loaded.
            private void OnTick(object sender, EventArgs args)
            {
                if (!_list.IsVisible) return;
                foreach (var row in _list.Items) Correct(row);
                RefreshVisuals(_list);
            }
            private void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs args) => ReconnectRows();
            private void ReconnectRows()
            {
                foreach (var row in _rows) row.PropertyChanged -= OnRowChanged;
                _rows.Clear();
                foreach (var row in _list.Items)
                {
                    Correct(row);
                    if (row is INotifyPropertyChanged observable)
                    {
                        _rows.Add(observable);
                        observable.PropertyChanged += OnRowChanged;
                    }
                }
                ForkLog.Wrap(_logger)?.Debug($"[PS5Progress] Trophy rows refreshed: rows={_list.Items.Count}; " +
                    string.Join("; ", _list.Items.Cast<object>().Take(3).Select(row =>
                        row.GetType().FullName + " provider=" + row.GetType().GetProperty("Provider")?.GetValue(row))));
            }
            private void OnRowChanged(object sender, PropertyChangedEventArgs args)
            {
                if (!_updating) Correct(sender);
            }
            private void Correct(object row)
            {
                if (row == null || row.GetType().FullName != "PS5Core.GameAchievementsItem") return;
                var type = row.GetType();
                // PS5Core 0.7.4 can omit Provider even for Local rows. The list
                // and detail still expose their individual unlocked trophy counts.
                var provider = type.GetProperty("Provider")?.GetValue(row) as string;
                var unlockedProperty = type.GetProperty("Unlocked");
                if (unlockedProperty?.CanWrite != true) return;
                var total = System.Convert.ToInt32(type.GetProperty("Total").GetValue(row), CultureInfo.InvariantCulture);
                var unlocked = new[] { "PlatinumCount", "GoldCount", "SilverCount", "BronzeCount" }
                    .Sum(name => Math.Max(0, System.Convert.ToInt32(type.GetProperty(name).GetValue(row), CultureInfo.InvariantCulture)));
                unlocked = Math.Min(Math.Max(0, total), unlocked);
                if (System.Convert.ToInt32(unlockedProperty.GetValue(row), CultureInfo.InvariantCulture) == unlocked) return;
                _updating = true;
                try
                {
                    unlockedProperty.SetValue(row, unlocked);
                    ForkLog.Wrap(_logger)?.Info($"[PS5Progress] Corrected trophy progress: game='{type.GetProperty("GameName")?.GetValue(row)}', provider='{provider ?? "<missing>"}', unlocked={unlocked}, total={total}.");
                }
                finally { _updating = false; }
            }
            private void OnUnloaded(object sender, RoutedEventArgs args)
            {
                ((INotifyCollectionChanged)_list.Items).CollectionChanged -= OnCollectionChanged;
                _timer.Stop();
                _timer.Tick -= OnTick;
                _list.Unloaded -= OnUnloaded;
                foreach (var row in _rows) row.PropertyChanged -= OnRowChanged;
                _rows.Clear();
                _list.ClearValue(ConnectionProperty);
            }
        }

        private static void OnLoaded(object sender, RoutedEventArgs args)
        {
            var control = sender as FrameworkElement;
            if (control == null || (args.OriginalSource ?? args.Source) != control) return;
            if (_enableNamedList && !(bool)control.GetValue(WatchesContextProperty))
            {
                control.SetValue(WatchesContextProperty, true);
                control.DataContextChanged += OnContextChanged;
                control.IsVisibleChanged += OnControlVisibilityChanged;
                control.Unloaded += OnControlUnloaded;
            }
            if (_enableNamedList) AdaptDetailHeader(control);
            DependencyObject parent = control;
            while (parent != null && !(parent is ListBox)) parent = VisualTreeHelper.GetParent(parent);
            var nativeRow = _enableNamedList && control.DataContext?.GetType().FullName == "PS5Core.GameAchievementsItem";
            if (!nativeRow && (!(parent is ListBox list) || !IsMigrated(list))) return;
            var property = control is TextBlock ? TextBlock.TextProperty : FrameworkElement.WidthProperty;
            var path = BindingOperations.GetBinding(control, property)?.Path?.Path;
            if (path != "ProgressPercent" && path != "ProgressText") return;
            var binding = new MultiBinding { Mode = BindingMode.OneWay, Converter = new ProgressConverter(path == "ProgressText") };
            foreach (var field in new[] { "PlatinumCount", "GoldCount", "SilverCount", "BronzeCount", "Total" })
                binding.Bindings.Add(new Binding(field) { Mode = BindingMode.OneWay });
            BindingOperations.SetBinding(control, property, binding);
        }

        private static void OnContextChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (!_enableNamedList) return;
            // Theme slots can load with no game and receive/recycle their context
            // later. Adapt immediately, before rendering, rather than waiting for
            // the background discovery timer to replace the initial zero values.
            SyncedDetailViewModels.Clear();
            var control = (FrameworkElement)sender;
            OnLoaded(control, new RoutedEventArgs(FrameworkElement.LoadedEvent, control));
        }

        private static void OnControlUnloaded(object sender, RoutedEventArgs args)
        {
            var control = (FrameworkElement)sender;
            if ((args.OriginalSource ?? args.Source) != control) return;
            control.DataContextChanged -= OnContextChanged;
            control.IsVisibleChanged -= OnControlVisibilityChanged;
            control.Unloaded -= OnControlUnloaded;
            control.ClearValue(WatchesContextProperty);
        }

        private static void OnControlVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (!_enableNamedList || !Equals(args.NewValue, true)) return;
            // The theme reuses hidden detail panels. Their bindings/selected game
            // can change while hidden without another Loaded/DataContextChanged.
            // Recheck at visibility change, before the next render, not on a timer.
            SyncedDetailViewModels.Clear();
            var control = (FrameworkElement)sender;
            OnLoaded(control, new RoutedEventArgs(FrameworkElement.LoadedEvent, control));
        }

        private sealed class ProgressConverter : IMultiValueConverter
        {
            private readonly bool _counter;
            private readonly bool _unlockedOnly;
            private readonly bool _providerFallback;
            internal ProgressConverter(bool counter, bool unlockedOnly = false, bool providerFallback = false)
            {
                _counter = counter;
                _unlockedOnly = unlockedOnly;
                _providerFallback = providerFallback;
            }
            public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            {
                // Provider is a platform label in some PS5Core queries (e.g.
                // "PC (Windows)"), not a reliable achievement-provider identity.
                // Use the same trophy counts as the migrated game-list display.
                if (values.Length != (_providerFallback ? 7 : 5) || values.Take(5).Any(value => value == DependencyProperty.UnsetValue)) return DependencyProperty.UnsetValue;
                var total = Math.Max(0, System.Convert.ToInt32(values[4], culture));
                var unlocked = Math.Min(total, values.Take(4).Sum(value => Math.Max(0, System.Convert.ToInt32(value, culture))));
                if (_counter) return string.Format(culture, "{0}/{1}", unlocked, total);
                if (_unlockedOnly) return targetType == typeof(string) ? (object)unlocked.ToString(culture) : unlocked;
                var percent = total > 0 ? Math.Round(unlocked * 100d / total, MidpointRounding.AwayFromZero) : 0d;
                return targetType == typeof(string) ? (object)percent.ToString(culture) : percent;
            }
            public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
        }
    }
}
