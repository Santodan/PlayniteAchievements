using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Providers.ImportedGameMetadata;

namespace PlayniteAchievements.Providers.Local
{
    public partial class LocalImportTargetDialog : UserControl
    {
        public ObservableCollection<string> SourceOptions { get; } = new ObservableCollection<string>();
        public ObservableCollection<ImportedGameMetadataSourceOption> MetadataSourceOptions { get; } = new ObservableCollection<ImportedGameMetadataSourceOption>();
        public ObservableCollection<LocalSteamAppCacheUserOption> SteamAppCacheUserOptions { get; } = new ObservableCollection<LocalSteamAppCacheUserOption>();

        public LocalImportedGameLibraryTarget SelectedTarget
        {
            get => (LocalImportedGameLibraryTarget)GetValue(SelectedTargetProperty);
            set => SetValue(SelectedTargetProperty, value);
        }

        public static readonly DependencyProperty SelectedTargetProperty =
            DependencyProperty.Register(
                nameof(SelectedTarget),
                typeof(LocalImportedGameLibraryTarget),
                typeof(LocalImportTargetDialog),
                new PropertyMetadata(LocalImportedGameLibraryTarget.None));

        public string CustomSourceName
        {
            get => (string)GetValue(CustomSourceNameProperty);
            set => SetValue(CustomSourceNameProperty, value);
        }

        public static readonly DependencyProperty CustomSourceNameProperty =
            DependencyProperty.Register(
                nameof(CustomSourceName),
                typeof(string),
                typeof(LocalImportTargetDialog),
                new PropertyMetadata(string.Empty));

        public string MetadataSourceId
        {
            get => (string)GetValue(MetadataSourceIdProperty);
            set => SetValue(MetadataSourceIdProperty, value);
        }

        public static readonly DependencyProperty MetadataSourceIdProperty =
            DependencyProperty.Register(
                nameof(MetadataSourceId),
                typeof(string),
                typeof(LocalImportTargetDialog),
                new PropertyMetadata(string.Empty));

        public LocalExistingGameImportBehavior ExistingGameBehavior
        {
            get => (LocalExistingGameImportBehavior)GetValue(ExistingGameBehaviorProperty);
            set => SetValue(ExistingGameBehaviorProperty, value);
        }

        public static readonly DependencyProperty ExistingGameBehaviorProperty =
            DependencyProperty.Register(
                nameof(ExistingGameBehavior),
                typeof(LocalExistingGameImportBehavior),
                typeof(LocalImportTargetDialog),
                new PropertyMetadata(LocalExistingGameImportBehavior.OverwriteExisting));

        public string SteamAppCacheUserId
        {
            get => (string)GetValue(SteamAppCacheUserIdProperty);
            set => SetValue(SteamAppCacheUserIdProperty, value);
        }

        public static readonly DependencyProperty SteamAppCacheUserIdProperty =
            DependencyProperty.Register(
                nameof(SteamAppCacheUserId),
                typeof(string),
                typeof(LocalImportTargetDialog),
                new PropertyMetadata(string.Empty));

        public bool IncludeFoldersWithoutAchievementFiles
        {
            get => (bool)GetValue(IncludeFoldersWithoutAchievementFilesProperty);
            set => SetValue(IncludeFoldersWithoutAchievementFilesProperty, value);
        }

        public static readonly DependencyProperty IncludeFoldersWithoutAchievementFilesProperty =
            DependencyProperty.Register(
                nameof(IncludeFoldersWithoutAchievementFiles),
                typeof(bool),
                typeof(LocalImportTargetDialog),
                new PropertyMetadata(false));

        public LocalIconRateLimitRetryMode IconRateLimitRetryMode
        {
            get => (LocalIconRateLimitRetryMode)GetValue(IconRateLimitRetryModeProperty);
            set => SetValue(IconRateLimitRetryModeProperty, value);
        }

        public static readonly DependencyProperty IconRateLimitRetryModeProperty =
            DependencyProperty.Register(
                nameof(IconRateLimitRetryMode),
                typeof(LocalIconRateLimitRetryMode),
                typeof(LocalImportTargetDialog),
                new PropertyMetadata(LocalIconRateLimitRetryMode.FixedRounds));

        public int IconRateLimitRetryRounds
        {
            get => (int)GetValue(IconRateLimitRetryRoundsProperty);
            set => SetValue(IconRateLimitRetryRoundsProperty, value);
        }

        public static readonly DependencyProperty IconRateLimitRetryRoundsProperty =
            DependencyProperty.Register(
                nameof(IconRateLimitRetryRounds),
                typeof(int),
                typeof(LocalImportTargetDialog),
                new PropertyMetadata(2));

        public bool? DialogResult { get; private set; }

        public event EventHandler RequestClose;

        public LocalImportTargetDialog(
            LocalImportedGameLibraryTarget selectedTarget,
            string customSourceName,
            string metadataSourceId,
            string steamAppCacheUserId,
            bool includeFoldersWithoutAchievementFiles,
            LocalExistingGameImportBehavior existingGameBehavior,
            LocalIconRateLimitRetryMode iconRateLimitRetryMode,
            int iconRateLimitRetryRounds,
            IEnumerable<string> sourceOptions,
            IEnumerable<ImportedGameMetadataSourceOption> metadataSourceOptions,
            IEnumerable<LocalSteamAppCacheUserOption> steamAppCacheUserOptions)
        {
            InitializeComponent();

            foreach (var sourceName in (sourceOptions ?? Enumerable.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                SourceOptions.Add(sourceName);
            }

            foreach (var option in metadataSourceOptions ?? Enumerable.Empty<ImportedGameMetadataSourceOption>())
            {
                if (option == null)
                {
                    continue;
                }

                MetadataSourceOptions.Add(option);
            }

            foreach (var option in steamAppCacheUserOptions ?? Enumerable.Empty<LocalSteamAppCacheUserOption>())
            {
                if (option == null)
                {
                    continue;
                }

                SteamAppCacheUserOptions.Add(option);
            }

            SelectedTarget = selectedTarget;
            CustomSourceName = ResolveSelectedSource(customSourceName);
            MetadataSourceId = ResolveSelectedMetadataSourceId(metadataSourceId);
            SteamAppCacheUserId = ResolveSelectedSteamAppCacheUserId(steamAppCacheUserId);
            IncludeFoldersWithoutAchievementFiles = includeFoldersWithoutAchievementFiles;
            ExistingGameBehavior = existingGameBehavior;
            IconRateLimitRetryMode = iconRateLimitRetryMode;
            IconRateLimitRetryRounds = Math.Max(1, Math.Min(20, iconRateLimitRetryRounds));
            DataContext = this;
            RefreshControlState();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedTarget == LocalImportedGameLibraryTarget.CustomSource && string.IsNullOrWhiteSpace(CustomSourceName))
            {
                MessageBox.Show(
                    "Select a Playnite source or choose a different target library.",
                    "Playnite Achievements",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                CustomSourceComboBox?.Focus();
                return;
            }

            IconRateLimitRetryRounds = Math.Max(1, Math.Min(20, IconRateLimitRetryRounds));

            DialogResult = true;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private void TargetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshControlState();
        }

        private void IconRetryModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshControlState();
        }

        private void RefreshControlState()
        {
            if (CustomSourceRow == null)
            {
                return;
            }

            CustomSourceRow.Visibility = SelectedTarget == LocalImportedGameLibraryTarget.CustomSource
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (IconRetryRoundsRow != null)
            {
                IconRetryRoundsRow.Visibility = IconRateLimitRetryMode == LocalIconRateLimitRetryMode.FixedRounds
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private string ResolveSelectedSource(string preferredSource)
        {
            if (!string.IsNullOrWhiteSpace(preferredSource))
            {
                var match = SourceOptions.FirstOrDefault(name => string.Equals(name, preferredSource, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match))
                {
                    return match;
                }
            }

            return SourceOptions.FirstOrDefault() ?? string.Empty;
        }

        private string ResolveSelectedMetadataSourceId(string preferredMetadataSourceId)
        {
            var normalizedId = preferredMetadataSourceId?.Trim() ?? string.Empty;
            if (MetadataSourceOptions.Any(option => string.Equals(option.Id, normalizedId, StringComparison.OrdinalIgnoreCase)))
            {
                return normalizedId;
            }

            return MetadataSourceOptions.FirstOrDefault()?.Id ?? string.Empty;
        }

        private string ResolveSelectedSteamAppCacheUserId(string preferredSteamAppCacheUserId)
        {
            var normalizedId = preferredSteamAppCacheUserId?.Trim() ?? string.Empty;
            if (SteamAppCacheUserOptions.Any(option => string.Equals(option.UserId, normalizedId, StringComparison.OrdinalIgnoreCase)))
            {
                return normalizedId;
            }

            if (!string.IsNullOrWhiteSpace(normalizedId))
            {
                return normalizedId;
            }

            return SteamAppCacheUserOptions.FirstOrDefault()?.UserId ?? string.Empty;
        }
    }
}