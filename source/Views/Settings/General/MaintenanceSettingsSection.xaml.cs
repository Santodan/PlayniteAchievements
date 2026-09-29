using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Images;

namespace PlayniteAchievements.Views.Settings.General
{
    /// <summary>
    /// General settings: Maintenance section. Hosts cached data clearing, icon cache clearing,
    /// and utility actions (reset first-time setup, database export, open data folder).
    /// </summary>
    public partial class MaintenanceSettingsSection : UserControl
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly ILogger _logger;

        public MaintenanceSettingsSection()
        {
            InitializeComponent();
            InitializeCompressionOptions();
        }

        internal MaintenanceSettingsSection(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ILogger logger)
            : this()
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _logger = logger;
        }

        // -----------------------------
        // Cache actions
        // -----------------------------

        private void WipeCache_Click(object sender, RoutedEventArgs e)
        {
            string message = null;
            var image = MessageBoxImage.Information;
            Exception operationError = null;
            var progressText = L("LOCPlayAch_Settings_Cache_ProgressClearing");

            RunMaintenanceProgress(
                progressText,
                isIndeterminate: true,
                operation: progress =>
                {
                    try
                    {
                        _plugin.RefreshRuntime.Cache.ClearCache();
                        message = L("LOCPlayAch_Status_Succeeded");
                        image = MessageBoxImage.Information;
                    }
                    catch (Exception ex)
                    {
                        operationError = ex;
                    }
                });

            if (operationError != null)
            {
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", operationError.Message),
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            _plugin.PlayniteApi.Dialogs.ShowMessage(
                message ?? L("LOCPlayAch_Status_Succeeded"),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                image);
        }

        private void ClearUnownedFriendGameData_Click(object sender, RoutedEventArgs e)
        {
            var friendCache = _plugin?.RefreshRuntime?.Cache as IFriendCacheManager;
            if (friendCache == null)
            {
                return;
            }

            try
            {
                var stats = friendCache.GetUnownedFriendGameCacheStats() ?? new FriendUnownedCacheStats();
                if (stats.Games <= 0 &&
                    stats.DefinitionStates <= 0 &&
                    stats.OwnershipRows <= 0 &&
                    stats.ProgressRows <= 0 &&
                    stats.AchievementRows <= 0 &&
                    stats.Definitions <= 0)
                {
                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        L("LOCPlayAch_FriendsOverview_ClearUnowned_None"),
                        L("LOCPlayAch_Title_PluginName"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var message = LF(
                    "LOCPlayAch_FriendsOverview_ClearUnowned_Confirm",
                    stats.Games,
                    stats.Definitions,
                    stats.OwnershipRows,
                    stats.ProgressRows,
                    stats.AchievementRows,
                    stats.DefinitionStates);

                if (_plugin.PlayniteApi.Dialogs.ShowMessage(
                        message,
                        L("LOCPlayAch_Title_PluginName"),
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    return;
                }

                var result = friendCache.ClearUnownedFriendGameData();
                if (result?.Success != true)
                {
                    _plugin.PlayniteApi.Dialogs.ShowMessage(
                        LF("LOCPlayAch_Status_Failed", result?.ErrorMessage ?? "unknown"),
                        L("LOCPlayAch_Title_PluginName"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                // Remove every cached unowned cover/icon file in one pass.
                _plugin.ImageService?.ClearGameCache(FriendImageCacheFolders.Games);

                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF(
                        "LOCPlayAch_FriendsOverview_ClearUnowned_Done",
                        result.Games,
                        result.ProgressRows),
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to clear unowned friend game data.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", ex.Message),
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ClearAllIconCache_Click(object sender, RoutedEventArgs e) =>
            ClearIconCache(IconCacheClearScope.All);

        private void ClearLockedIconCache_Click(object sender, RoutedEventArgs e) =>
            ClearIconCache(IconCacheClearScope.LockedOnly);

        private void ClearIconCache(IconCacheClearScope scope)
        {
            var fileLabel = ResourceProvider.GetString(GetIconCacheFileLabelResourceKey(scope));
            var scanningText = LF(
                "LOCPlayAch_Settings_IconCache_ProgressScanning",
                fileLabel);
            var deletingTextFormat = L("LOCPlayAch_Settings_IconCache_ProgressDeletingCount");
            var deletedCount = 0;
            Exception operationError = null;

            RunMaintenanceProgress(
                scanningText,
                isIndeterminate: false,
                operation: progress =>
                {
                    try
                    {
                        UpdateMaintenanceProgress(progress, current: 0, max: 1);

                        IEnumerable<string> additionalPaths = null;
                        if (scope == IconCacheClearScope.LockedOnly)
                        {
                            additionalPaths = GetExplicitLockedIconCachePaths(progress);
                        }

                        _plugin.ImageService?.Clear();
                        deletedCount = _plugin.ImageService?.ClearDiskCache(
                            scope,
                            additionalPaths,
                            (processed, total) =>
                            {
                                var safeTotal = Math.Max(1, total);
                                var safeProcessed = total <= 0
                                    ? 1
                                    : Math.Max(0, Math.Min(total, processed));

                                var progressText = total <= 0
                                    ? LF(
                                        "LOCPlayAch_Settings_IconCache_ProgressNoFiles",
                                        fileLabel)
                                    : string.Format(
                                        deletingTextFormat,
                                        fileLabel,
                                        safeProcessed,
                                        total);

                                UpdateMaintenanceProgress(
                                    progress,
                                    text: progressText,
                                    current: safeProcessed,
                                    max: safeTotal);
                            }) ?? 0;
                    }
                    catch (Exception ex)
                    {
                        operationError = ex;
                    }
                });

            if (operationError != null)
            {
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", operationError.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            var message = L("LOCPlayAch_Status_Succeeded");

            _plugin.PlayniteApi.Dialogs.ShowMessage(
                message,
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private string GetIconCacheFileLabelResourceKey(IconCacheClearScope scope)
        {
            switch (scope)
            {
                case IconCacheClearScope.LockedOnly:
                    return "LOCPlayAch_Settings_IconCache_FileLabel_Locked";
                default:
                    return "LOCPlayAch_Settings_IconCache_FileLabel_All";
            }
        }

        // -----------------------------
        // Image compression
        // -----------------------------

        private void CompressAchievementIcons_Click(object sender, RoutedEventArgs e) =>
            CompressImages(ImageCompressionScope.AchievementIcons);

        private void CompressCategoryArt_Click(object sender, RoutedEventArgs e) =>
            CompressImages(ImageCompressionScope.CategoryDefaults);

        private void CompressFriendImages_Click(object sender, RoutedEventArgs e) =>
            CompressImages(ImageCompressionScope.FriendImages);

        private void CompressCustomIcons_Click(object sender, RoutedEventArgs e) =>
            CompressImages(ImageCompressionScope.CustomIcons);

        /// <summary>
        /// Measures the scope, asks for confirmation with the projected saving, then rewrites the
        /// oversized files. The scan runs first so the user is never asked to approve an unbounded
        /// change; both passes are cancelable because a large cache holds tens of thousands of files.
        /// </summary>
        private void CompressImages(ImageCompressionScope scope)
        {
            var diskImageService = _plugin?.DiskImageService;
            if (diskImageService == null)
            {
                return;
            }

            var maxDimension = GetSelectedMaxDimension();
            var compressor = new IconCacheCompressor(diskImageService, _logger);

            var scanThrottle = new ProgressThrottle();
            var estimate = RunCompressionPass(
                LF("LOCPlayAch_Settings_CompressImages_ProgressScanning", 0, 0),
                progress => compressor.Scan(
                    scope,
                    maxDimension,
                    (processed, total) => ReportCountedProgress(
                        progress,
                        "LOCPlayAch_Settings_CompressImages_ProgressScanning",
                        processed,
                        total,
                        scanThrottle),
                    progress.CancelToken));

            if (estimate == null)
            {
                // Either cancelled during the scan or already reported as an error.
                return;
            }

            if (estimate.Candidates <= 0)
            {
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Settings_CompressImages_NoCandidates", maxDimension),
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var confirmation = LF(
                "LOCPlayAch_Settings_CompressImages_Confirm",
                estimate.Candidates,
                estimate.Candidates + estimate.Skipped,
                maxDimension,
                FormatBytes(estimate.CurrentBytes),
                FormatBytes(estimate.EstimatedBytes));

            if (_plugin.PlayniteApi.Dialogs.ShowMessage(
                    confirmation,
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            // Reuses the candidate list the scan produced, so the cache is walked once per run.
            var compressThrottle = new ProgressThrottle();
            var result = RunCompressionPass(
                LF("LOCPlayAch_Settings_CompressImages_ProgressCompressing", 0, 0),
                progress => compressor
                    .CompressAsync(
                        estimate.Files,
                        maxDimension,
                        (processed, total) => ReportCountedProgress(
                            progress,
                            "LOCPlayAch_Settings_CompressImages_ProgressCompressing",
                            processed,
                            total,
                            compressThrottle),
                        progress.CancelToken)
                    .GetAwaiter()
                    .GetResult());

            if (result == null)
            {
                return;
            }

            var summary = result.Canceled
                ? LF(
                    "LOCPlayAch_Settings_CompressImages_ResultCanceled",
                    result.Compressed,
                    FormatBytes(result.SavedBytes))
                : LF(
                    "LOCPlayAch_Settings_CompressImages_Result",
                    result.Compressed,
                    result.Skipped,
                    result.Failed,
                    FormatBytes(result.SavedBytes));

            _plugin.PlayniteApi.Dialogs.ShowMessage(
                summary,
                L("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        /// <summary>
        /// Runs one compression pass behind a cancelable progress dialog. Returns null when the pass
        /// was cancelled or failed, having already shown the error.
        /// </summary>
        private T RunCompressionPass<T>(string initialText, Func<GlobalProgressActionArgs, T> pass)
            where T : class
        {
            T passResult = null;
            Exception operationError = null;

            // Starts indeterminate because the file count is only known once the scope has been
            // enumerated; the first counted report switches the bar over.
            RunMaintenanceProgress(
                initialText,
                isIndeterminate: true,
                operation: progress =>
                {
                    try
                    {
                        passResult = pass(progress);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        operationError = ex;
                    }
                },
                cancelable: true);

            if (operationError != null)
            {
                _logger?.Error(operationError, "Failed to compress cached images.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", operationError.Message),
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            return passResult;
        }

        private void ReportCountedProgress(
            GlobalProgressActionArgs progress,
            string textResourceKey,
            int processed,
            int total,
            ProgressThrottle throttle)
        {
            var safeTotal = Math.Max(1, total);
            var safeProcessed = Math.Max(0, Math.Min(safeTotal, processed));

            if (throttle?.ShouldReport(safeProcessed, safeTotal) == false)
            {
                return;
            }

            UpdateMaintenanceProgress(
                progress,
                text: LF(textResourceKey, safeProcessed, safeTotal),
                current: safeProcessed,
                max: safeTotal,
                isIndeterminate: false);
        }

        /// <summary>
        /// Rate-limits progress reporting to something a person can actually read.
        /// </summary>
        /// <remarks>
        /// <see cref="UpdateMaintenanceProgress"/> marshals onto the UI dispatcher and blocks until
        /// the update is applied, so reporting once per item turns a scan of tens of thousands of
        /// files into tens of thousands of synchronous cross-thread hops plus a re-layout each --
        /// which cost far more than the file work being reported on. Callers may report from several
        /// threads at once, so the gate is locked.
        /// </remarks>
        private sealed class ProgressThrottle
        {
            private const int MinimumIntervalMs = 100;

            private readonly Stopwatch _clock = Stopwatch.StartNew();
            private readonly object _sync = new object();
            private long _lastReportedMs = -1;

            internal bool ShouldReport(int processed, int total)
            {
                lock (_sync)
                {
                    var elapsed = _clock.ElapsedMilliseconds;

                    // The final item always reports, so the bar never stops short of complete.
                    if (processed >= total ||
                        _lastReportedMs < 0 ||
                        elapsed - _lastReportedMs >= MinimumIntervalMs)
                    {
                        _lastReportedMs = elapsed;
                        return true;
                    }

                    return false;
                }
            }
        }

        /// <summary>
        /// Fills the max-dimension picker. The choice is deliberately per-run rather than persisted:
        /// a sensible cap for achievement icons is not a sensible cap for cover-sized category art.
        /// </summary>
        private void InitializeCompressionOptions()
        {
            if (CompressMaxDimensionCombo == null)
            {
                return;
            }

            foreach (var dimension in ImageCompressionPlan.SelectableMaxDimensions)
            {
                CompressMaxDimensionCombo.Items.Add(
                    dimension.ToString(FormattingCulture.Current) + " px");
            }

            CompressMaxDimensionCombo.SelectedIndex = Math.Max(
                0,
                Array.IndexOf(
                    ImageCompressionPlan.SelectableMaxDimensions,
                    ImageCompressionPlan.DefaultMaxDimension));
        }

        private int GetSelectedMaxDimension()
        {
            var index = CompressMaxDimensionCombo?.SelectedIndex ?? -1;
            if (index < 0 || index >= ImageCompressionPlan.SelectableMaxDimensions.Length)
            {
                return ImageCompressionPlan.DefaultMaxDimension;
            }

            return ImageCompressionPlan.SelectableMaxDimensions[index];
        }

        private static string FormatBytes(long bytes)
        {
            var culture = FormattingCulture.Current;
            if (bytes >= 1024L * 1024L * 1024L)
            {
                return (bytes / (double)(1024L * 1024L * 1024L)).ToString("N2", culture) + " GB";
            }

            if (bytes >= 1024L * 1024L)
            {
                return (bytes / (double)(1024L * 1024L)).ToString("N1", culture) + " MB";
            }

            if (bytes >= 1024L)
            {
                return (bytes / 1024d).ToString("N0", culture) + " KB";
            }

            return bytes.ToString("N0", culture) + " B";
        }

        private void RunMaintenanceProgress(
            string initialText,
            bool isIndeterminate,
            Action<GlobalProgressActionArgs> operation,
            bool cancelable = false)
        {
            var progressOptions = new GlobalProgressOptions(initialText)
            {
                Cancelable = cancelable,
                IsIndeterminate = isIndeterminate
            };

            _plugin.PlayniteApi.Dialogs.ActivateGlobalProgress(async progress =>
            {
                UpdateMaintenanceProgress(progress, text: initialText, isIndeterminate: isIndeterminate);
                await Task.Run(() => operation?.Invoke(progress)).ConfigureAwait(false);
            }, progressOptions);
        }

        private void UpdateMaintenanceProgress(
            GlobalProgressActionArgs progress,
            string text = null,
            int? current = null,
            int? max = null,
            bool? isIndeterminate = null)
        {
            if (progress == null)
            {
                return;
            }

            Action update = () =>
            {
                if (max.HasValue)
                {
                    progress.ProgressMaxValue = max.Value;
                }

                if (current.HasValue)
                {
                    progress.CurrentProgressValue = current.Value;
                }

                if (isIndeterminate.HasValue)
                {
                    progress.IsIndeterminate = isIndeterminate.Value;
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    progress.Text = text;
                }
            };

            if (progress.MainDispatcher != null)
            {
                progress.MainDispatcher.InvokeIfNeeded(update);
            }
            else
            {
                update();
            }
        }

        private IEnumerable<string> GetExplicitLockedIconCachePaths(GlobalProgressActionArgs progress = null)
        {
            var dataService = _plugin?.AchievementDataService;
            var cachedGameIds = dataService?.GetCachedGameIds();
            if (cachedGameIds == null || cachedGameIds.Count == 0)
            {
                if (progress != null)
                {
                    UpdateMaintenanceProgress(
                        progress,
                        text: L("LOCPlayAch_Settings_IconCache_ProgressNoLockedReferences"),
                        current: 1,
                        max: 1);
                }

                return Array.Empty<string>();
            }

            var lockedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (progress != null)
            {
                UpdateMaintenanceProgress(progress, current: 0, max: cachedGameIds.Count);
            }

            for (var i = 0; i < cachedGameIds.Count; i++)
            {
                var gameId = cachedGameIds[i];
                if (progress != null)
                {
                    UpdateMaintenanceProgress(
                        progress,
                        text: LF(
                            "LOCPlayAch_Settings_IconCache_ProgressScanningLockedReferences",
                            i + 1,
                            cachedGameIds.Count),
                        current: i + 1,
                        max: cachedGameIds.Count);
                }

                var gameData = dataService?.GetRawGameAchievementData(gameId);
                var achievements = gameData?.Achievements;
                if (achievements == null)
                {
                    continue;
                }

                foreach (var achievement in achievements)
                {
                    var lockedPath = achievement?.LockedIconPath;
                    if (!DiskImageService.IsLocalIconPath(lockedPath))
                    {
                        continue;
                    }

                    var unlockedPath = achievement?.UnlockedIconPath;
                    if (!string.IsNullOrWhiteSpace(unlockedPath) &&
                        string.Equals(lockedPath.Trim(), unlockedPath.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    lockedPaths.Add(lockedPath);
                }
            }

            return lockedPaths;
        }

        private void ResetFirstTimeSetup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _logger?.Info($"Resetting FirstTimeSetupCompleted. Current value before: {_settings.Persisted.FirstTimeSetupCompleted}");

                _settings.Persisted.FirstTimeSetupCompleted = false;

                _logger?.Info($"Value after setting to false: {_settings.Persisted.FirstTimeSetupCompleted}");

                _plugin.SavePluginSettings(_settings);

                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    L("LOCPlayAch_Status_Succeeded"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to reset first-time setup.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ExportDatabase_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var exportBaseDir = _plugin.GetPluginUserDataPath();
                var exportDir = _plugin.RefreshRuntime.Cache.ExportDatabaseToCsv(exportBaseDir);

                _logger?.Info($"Database exported to: {exportDir}");

                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    L("LOCPlayAch_Status_Succeeded") + "\n" + exportDir,
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to export database.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dataPath = _plugin.GetPluginUserDataPath();

                if (!Directory.Exists(dataPath))
                {
                    Directory.CreateDirectory(dataPath);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = dataPath,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to open extension data folder.");
                _plugin.PlayniteApi.Dialogs.ShowMessage(
                    LF("LOCPlayAch_Status_Failed", ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
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
