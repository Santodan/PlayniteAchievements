using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels
{
    public sealed class GameOptionsAchievementIconsViewModel : ObservableObject
    {
        private readonly Guid _gameId;
        private readonly string _gameIdText;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly GameOptionsDataSnapshotProvider _gameDataSnapshotProvider;
        private readonly ManagedCustomIconService _managedCustomIconService;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;

        private bool _hasAchievements;
        private bool _hasChanges;
        private bool _hasAnyOverrides;
        private bool _hasValidationErrors;
        private bool _isSaving;
        private string _saveStatusText;
        private bool _saveStatusIsError;

        public GameOptionsAchievementIconsViewModel(
            Guid gameId,
            AchievementOverridesService achievementOverridesService,
            GameOptionsDataSnapshotProvider gameDataSnapshotProvider,
            ManagedCustomIconService managedCustomIconService,
            PlayniteAchievementsSettings settings,
            ILogger logger)
        {
            _gameId = gameId;
            _gameIdText = gameId.ToString("D");
            _achievementOverridesService = achievementOverridesService ?? throw new ArgumentNullException(nameof(achievementOverridesService));
            _gameDataSnapshotProvider = gameDataSnapshotProvider ?? throw new ArgumentNullException(nameof(gameDataSnapshotProvider));
            _managedCustomIconService = managedCustomIconService ?? throw new ArgumentNullException(nameof(managedCustomIconService));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger;

            AchievementRows = new ObservableCollection<AchievementIconOverrideItem>();
            SaveCommand = new RelayCommand(_ => Save(), _ => CanSave);
            RevertChangesCommand = new RelayCommand(_ => RevertChanges(), _ => HasChanges && !IsSaving);
            ClearAllCommand = new RelayCommand(_ => ClearAllOverrides(), _ => HasAchievements && HasAnyOverrides && !IsSaving);
            OpenIconsFolderCommand = new RelayCommand(_ => OpenIconsFolder(), _ => !IsSaving);

            ForceReloadData();
        }

        public event EventHandler IconOverridesSaved;

        public ObservableCollection<AchievementIconOverrideItem> AchievementRows { get; }

        public RelayCommand SaveCommand { get; }
        public RelayCommand RevertChangesCommand { get; }
        public RelayCommand ClearAllCommand { get; }
        public RelayCommand OpenIconsFolderCommand { get; }

        public bool HasAchievements
        {
            get => _hasAchievements;
            private set
            {
                if (SetValueAndReturn(ref _hasAchievements, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        public bool HasChanges
        {
            get => _hasChanges;
            private set
            {
                if (SetValueAndReturn(ref _hasChanges, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        public bool HasAnyOverrides
        {
            get => _hasAnyOverrides;
            private set
            {
                if (SetValueAndReturn(ref _hasAnyOverrides, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        public bool HasValidationErrors
        {
            get => _hasValidationErrors;
            private set
            {
                if (SetValueAndReturn(ref _hasValidationErrors, value))
                {
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(StatusIsError));
                    OnPropertyChanged(nameof(HasStatusText));
                    RaiseCommandStates();
                }
            }
        }

        public bool IsSaving
        {
            get => _isSaving;
            private set
            {
                if (SetValueAndReturn(ref _isSaving, value))
                {
                    RaiseCommandStates();
                }
            }
        }

        public bool CanSave => HasAchievements && HasChanges && !HasValidationErrors && !IsSaving;

        public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

        public string StatusText
        {
            get
            {
                if (HasValidationErrors)
                {
                    return L(
                        "LOCPlayAch_GameOptions_CustomIcons_ValidationError",
                        "One or more icon values are invalid. Use absolute http:// or https:// URLs, or managed local files copied into plugin data.");
                }

                return _saveStatusText;
            }
        }

        public bool StatusIsError => HasValidationErrors || _saveStatusIsError;

        public void RefreshData()
        {
            if (HasChanges)
            {
                return;
            }

            ForceReloadData();
        }

        public void Cleanup()
        {
            for (var i = 0; i < AchievementRows.Count; i++)
            {
                var row = AchievementRows[i];
                if (row == null || !row.HasTransientManagedState)
                {
                    continue;
                }

                row.DiscardTransientManagedState();
            }
        }

        public async Task ApplyLocalFileOverrideAsync(
            AchievementIconOverrideItem row,
            AchievementIconVariant variant,
            string localFilePath)
        {
            if (row == null)
            {
                return;
            }

            var normalizedPath = NormalizeText(localFilePath);
            if (string.IsNullOrWhiteSpace(normalizedPath) || !File.Exists(normalizedPath))
            {
                SetSaveStatus(
                    L(
                        "LOCPlayAch_GameOptions_CustomIcons_LocalFileMissing",
                        "The selected image file no longer exists."),
                    isError: true);
                return;
            }

            try
            {
                row.PrepareManagedLocalOverride(variant);
                var managedPath = await _managedCustomIconService
                    .MaterializeCustomIconAsync(
                        normalizedPath,
                        _gameIdText,
                        row.FileStem,
                        variant,
                        CancellationToken.None,
                        overwriteExistingTarget: true)
                    .ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(managedPath) || !File.Exists(managedPath))
                {
                    throw new InvalidOperationException("The image file could not be copied into plugin data.");
                }

                row.ApplyManagedLocalOverride(variant, managedPath);
                SetSaveStatus(null, isError: false);
                RefreshComputedState();
            }
            catch (Exception ex)
            {
                row.RevertFailedManagedLocalOverride(variant);
                _logger?.Error(ex, $"Failed copying custom icon file for gameId={_gameId}, apiName={row.ApiName}, variant={variant}.");
                SetSaveStatus(
                    string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message),
                    isError: true);
                RefreshComputedState();
            }
        }

        public void ForceReloadData()
        {
            try
            {
                var hydratedGameData = _gameDataSnapshotProvider.GetHydratedGameData();
                var rawGameData = _gameDataSnapshotProvider.GetRawGameData();
                var displayGameData = hydratedGameData ?? rawGameData;
                var orderedAchievements = BuildOrderedAchievements(rawGameData, hydratedGameData);
                var rawByApiName = rawGameData?.Achievements?
                    .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                    .GroupBy(a => NormalizeText(a.ApiName), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, AchievementDetail>(StringComparer.OrdinalIgnoreCase);
                var unlockedOverrides = GameCustomDataLookup.GetAchievementUnlockedIconOverrides(_gameId);
                var lockedOverrides = GameCustomDataLookup.GetAchievementLockedIconOverrides(_gameId);
                var fileStems = AchievementIconCachePathBuilder.BuildFileStems(
                    orderedAchievements.Select(achievement => achievement?.ApiName));

                var rows = new List<AchievementIconOverrideItem>();
                for (var i = 0; i < orderedAchievements.Count; i++)
                {
                    var achievement = orderedAchievements[i];
                    var projected = AchievementDisplayItem.Create(
                        displayGameData,
                        achievement,
                        _settings,
                        playniteGameIdOverride: _gameId);
                    if (projected == null || string.IsNullOrWhiteSpace(projected.ApiName))
                    {
                        continue;
                    }

                    var apiName = NormalizeText(projected.ApiName);
                    if (!fileStems.TryGetValue(apiName, out var fileStem) || string.IsNullOrWhiteSpace(fileStem))
                    {
                        continue;
                    }

                    unlockedOverrides.TryGetValue(apiName, out var unlockedOverride);
                    lockedOverrides.TryGetValue(apiName, out var lockedOverride);
                    rawByApiName.TryGetValue(apiName, out var rawAchievement);
                    var unlockedSource = ExcludeManagedCustomSource(rawAchievement?.UnlockedIconPath);
                    var lockedSource = ExcludeManagedCustomSource(rawAchievement?.LockedIconPath);
                    var originalUnlockedPreview = ResolveDefaultCachedPreviewPath(fileStem, AchievementIconVariant.Unlocked) ??
                                                 AchievementIconResolver.GetUnlockedDisplayIcon(unlockedSource);
                    var originalLockedPreview = ResolveDefaultCachedPreviewPath(fileStem, AchievementIconVariant.Locked) ??
                                               AchievementIconResolver.GetLockedDisplayIcon(originalUnlockedPreview, lockedSource);
                    rows.Add(AchievementIconOverrideItem.Create(
                        projected,
                        unlockedOverride,
                        lockedOverride,
                        originalUnlockedPreview,
                        originalLockedPreview,
                        _gameIdText,
                        fileStem,
                        _managedCustomIconService));
                }

                ReplaceRows(rows);
                HasAchievements = rows.Count > 0;
                SetSaveStatus(null, isError: false);
                RefreshComputedState();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed loading custom icon rows for gameId={_gameId}");
                ReplaceRows(Array.Empty<AchievementIconOverrideItem>());
                HasAchievements = false;
                SetSaveStatus(
                    string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message),
                    isError: true);
                RefreshComputedState();
            }
        }

        private void Save()
        {
            if (!CanSave)
            {
                return;
            }

            try
            {
                IsSaving = true;

                var unlockedOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var lockedOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var retainedManagedPaths = new List<string>();
                for (var i = 0; i < AchievementRows.Count; i++)
                {
                    var row = AchievementRows[i];
                    var apiName = NormalizeText(row?.ApiName);
                    if (string.IsNullOrWhiteSpace(apiName))
                    {
                        continue;
                    }

                    var unlockedOverride = row.GetNormalizedUnlockedOverrideValue();
                    var lockedOverride = row.GetNormalizedLockedOverrideValue();
                    if (!string.IsNullOrWhiteSpace(unlockedOverride))
                    {
                        unlockedOverrides[apiName] = unlockedOverride;
                        TrackManagedOverridePath(retainedManagedPaths, unlockedOverride);
                    }

                    if (!string.IsNullOrWhiteSpace(lockedOverride))
                    {
                        lockedOverrides[apiName] = lockedOverride;
                        TrackManagedOverridePath(retainedManagedPaths, lockedOverride);
                    }
                }

                _achievementOverridesService.SetAchievementIconOverrides(_gameId, unlockedOverrides, lockedOverrides);
                _managedCustomIconService.PruneGameCustomCache(_gameIdText, retainedManagedPaths);

                for (var i = 0; i < AchievementRows.Count; i++)
                {
                    AchievementRows[i]?.CommitCurrentOverridesAsBaseline();
                }

                SetSaveStatus(
                    L("LOCPlayAch_Status_Succeeded", "Success!"),
                    isError: false);
                RefreshComputedState();
                IconOverridesSaved?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed saving custom icon overrides for gameId={_gameId}");
                SetSaveStatus(
                    string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message),
                    isError: true);
            }
            finally
            {
                IsSaving = false;
            }
        }

        private void RevertChanges()
        {
            for (var i = 0; i < AchievementRows.Count; i++)
            {
                AchievementRows[i]?.ResetToBaseline();
            }

            SetSaveStatus(null, isError: false);
            RefreshComputedState();
        }

        private void ClearAllOverrides()
        {
            if (!HasAchievements)
            {
                return;
            }

            for (var i = 0; i < AchievementRows.Count; i++)
            {
                AchievementRows[i]?.ClearAllOverrides();
            }

            SetSaveStatus(null, isError: false);
            RefreshComputedState();
        }

        private void OpenIconsFolder()
        {
            try
            {
                var pluginDataPath = PlayniteAchievementsPlugin.Instance?.GetPluginUserDataPath();
                if (string.IsNullOrWhiteSpace(pluginDataPath))
                {
                    SetSaveStatus(
                        string.Format(
                            L("LOCPlayAch_Status_Failed", "Error: {0}"),
                            L("LOCPlayAch_GameOptions_CustomIcons_OpenFolderUnavailable", "The extension data path is unavailable.")),
                        isError: true);
                    return;
                }

                var iconsFolderPath = Path.Combine(pluginDataPath, "icon_cache", _gameIdText);
                Directory.CreateDirectory(iconsFolderPath);

                Process.Start(new ProcessStartInfo
                {
                    FileName = iconsFolderPath,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed opening icon cache folder for gameId={_gameId}.");
                SetSaveStatus(
                    string.Format(L("LOCPlayAch_Status_Failed", "Error: {0}"), ex.Message),
                    isError: true);
            }
        }

        private void ReplaceRows(IEnumerable<AchievementIconOverrideItem> rows)
        {
            foreach (var row in AchievementRows)
            {
                row.PropertyChanged -= Row_PropertyChanged;
            }

            AchievementRows.Clear();
            foreach (var row in rows ?? Enumerable.Empty<AchievementIconOverrideItem>())
            {
                row.PropertyChanged += Row_PropertyChanged;
                AchievementRows.Add(row);
            }
        }

        private void Row_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (e.PropertyName == nameof(AchievementIconOverrideItem.UnlockedOverrideValue) ||
                e.PropertyName == nameof(AchievementIconOverrideItem.LockedOverrideValue))
            {
                SetSaveStatus(null, isError: false);
            }

            RefreshComputedState();
        }

        private void RefreshComputedState()
        {
            var hasChanges = false;
            var hasAnyOverrides = false;
            var hasValidationErrors = false;

            for (var i = 0; i < AchievementRows.Count; i++)
            {
                var row = AchievementRows[i];
                if (row == null)
                {
                    continue;
                }

                hasChanges |= row.HasChanges;
                hasAnyOverrides |= row.HasAnyOverrideValue;
                hasValidationErrors |= row.HasValidationErrors;
            }

            HasChanges = hasChanges;
            HasAnyOverrides = hasAnyOverrides;
            HasValidationErrors = hasValidationErrors;
        }

        private void SetSaveStatus(string text, bool isError)
        {
            _saveStatusText = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            _saveStatusIsError = isError && !string.IsNullOrWhiteSpace(_saveStatusText);
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusIsError));
            OnPropertyChanged(nameof(HasStatusText));
        }

        private void RaiseCommandStates()
        {
            SaveCommand?.RaiseCanExecuteChanged();
            RevertChangesCommand?.RaiseCanExecuteChanged();
            ClearAllCommand?.RaiseCanExecuteChanged();
            OpenIconsFolderCommand?.RaiseCanExecuteChanged();
        }

        private void TrackManagedOverridePath(ICollection<string> retainedManagedPaths, string overrideValue)
        {
            var normalized = NormalizeText(overrideValue);
            if (string.IsNullOrWhiteSpace(normalized) ||
                !_managedCustomIconService.IsManagedCustomIconPath(normalized, _gameIdText))
            {
                return;
            }

            retainedManagedPaths?.Add(normalized);
        }

        private static List<AchievementDetail> BuildOrderedAchievements(
            GameAchievementData rawGameData,
            GameAchievementData hydratedGameData)
        {
            var hydratedAchievements = hydratedGameData?.Achievements?
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                .ToList() ?? new List<AchievementDetail>();
            var hydratedByApiName = hydratedAchievements
                .GroupBy(a => NormalizeText(a.ApiName), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var rawAchievements = rawGameData?.Achievements?
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                .ToList() ?? new List<AchievementDetail>();

            if (hydratedGameData?.AchievementOrder != null && hydratedGameData.AchievementOrder.Count > 0)
            {
                var orderSource = rawAchievements.Count > 0 ? rawAchievements : hydratedAchievements;
                var ordered = AchievementOrderHelper.ApplyOrder(
                    orderSource,
                    achievement => achievement.ApiName,
                    hydratedGameData.AchievementOrder);
                return ordered
                    .Select(achievement => ResolveHydratedAchievement(achievement, hydratedByApiName))
                    .ToList();
            }

            if (rawAchievements.Count > 0)
            {
                return rawAchievements
                    .Select(achievement => ResolveHydratedAchievement(achievement, hydratedByApiName))
                    .ToList();
            }

            return hydratedAchievements;
        }

        private static AchievementDetail ResolveHydratedAchievement(
            AchievementDetail achievement,
            IReadOnlyDictionary<string, AchievementDetail> hydratedByApiName)
        {
            var apiName = NormalizeText(achievement?.ApiName);
            if (!string.IsNullOrWhiteSpace(apiName) &&
                hydratedByApiName != null &&
                hydratedByApiName.TryGetValue(apiName, out var hydratedAchievement))
            {
                return hydratedAchievement;
            }

            return achievement;
        }

        private static string NormalizeText(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private string ExcludeManagedCustomSource(string source)
        {
            var normalized = NormalizeText(source);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            if (Path.IsPathRooted(normalized) &&
                File.Exists(normalized) &&
                _managedCustomIconService.IsManagedCustomIconPath(normalized, _gameIdText))
            {
                return null;
            }

            return normalized;
        }

        private string ResolveDefaultCachedPreviewPath(string fileStem, AchievementIconVariant variant)
        {
            if (string.IsNullOrWhiteSpace(fileStem))
            {
                return null;
            }

            try
            {
                var disk = PlayniteAchievementsPlugin.Instance?.DiskImageService;
                if (disk == null)
                {
                    return null;
                }

                var preserveOriginalResolution = _settings?.Persisted?.PreserveAchievementIconResolution ?? false;

                var preferred = disk.GetAchievementIconCachePath(
                    _gameIdText,
                    preserveOriginalResolution,
                    fileStem,
                    variant);
                if (!string.IsNullOrWhiteSpace(preferred) && File.Exists(preferred))
                {
                    return preferred;
                }

                var fallback = disk.GetAchievementIconCachePath(
                    _gameIdText,
                    !preserveOriginalResolution,
                    fileStem,
                    variant);
                return !string.IsNullOrWhiteSpace(fallback) && File.Exists(fallback)
                    ? fallback
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static string L(string key, string fallback)
        {
            var value = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }

    public sealed class AchievementIconOverrideItem : AchievementDisplayItem
    {
        private const string PreviewHttpPrefix = "previewhttp:";

        private readonly ManagedCustomIconService _managedCustomIconService;
        private readonly string _gameIdText;
        private readonly string _unlockedManagedTargetPath;
        private readonly string _lockedManagedTargetPath;
        private string _originalUnlockedPreviewPath;
        private string _originalLockedPreviewPath;

        private string _baselineUnlockedOverrideValue;
        private string _baselineLockedOverrideValue;
        private string _unlockedOverrideValue = string.Empty;
        private string _lockedOverrideValue = string.Empty;

        private string _unlockedBackupPath;
        private bool _restoreUnlockedBackupOnReset;
        private bool _hasUnlockedContentChange;

        private string _lockedBackupPath;
        private bool _restoreLockedBackupOnReset;
        private bool _hasLockedContentChange;

        private AchievementIconOverrideItem(
            string gameIdText,
            string fileStem,
            ManagedCustomIconService managedCustomIconService)
        {
            _gameIdText = gameIdText ?? Guid.Empty.ToString("D");
            FileStem = string.IsNullOrWhiteSpace(fileStem) ? "achievement" : fileStem.Trim();
            _managedCustomIconService = managedCustomIconService ?? throw new ArgumentNullException(nameof(managedCustomIconService));
            _unlockedManagedTargetPath = _managedCustomIconService.GetAchievementCustomIconPath(_gameIdText, FileStem, AchievementIconVariant.Unlocked);
            _lockedManagedTargetPath = _managedCustomIconService.GetAchievementCustomIconPath(_gameIdText, FileStem, AchievementIconVariant.Locked);
            PropertyChanged += AchievementIconOverrideItem_PropertyChanged;
        }

        public string FileStem { get; }

        public string UnlockedOverrideValue
        {
            get => _unlockedOverrideValue;
            set => SetOverrideValue(AchievementIconVariant.Unlocked, value);
        }

        public string UnlockedOverrideText
        {
            get => GetDisplayOverrideValue(AchievementIconVariant.Unlocked);
            set => SetOverrideValue(AchievementIconVariant.Unlocked, value);
        }

        public string LockedOverrideValue
        {
            get => _lockedOverrideValue;
            set => SetOverrideValue(AchievementIconVariant.Locked, value);
        }

        public string LockedOverrideText
        {
            get => GetDisplayOverrideValue(AchievementIconVariant.Locked);
            set => SetOverrideValue(AchievementIconVariant.Locked, value);
        }

        public string UnlockedOverrideToolTip => GetCurrentOverrideValue(AchievementIconVariant.Unlocked);

        public string LockedOverrideToolTip => GetCurrentOverrideValue(AchievementIconVariant.Locked);

        public bool HasUnlockedOverrideValidationError => !IsValidOverrideValueOrBlank(UnlockedOverrideValue);

        public bool HasLockedOverrideValidationError => !IsValidOverrideValueOrBlank(LockedOverrideValue);

        public bool HasValidationErrors => HasUnlockedOverrideValidationError || HasLockedOverrideValidationError;

        public bool HasAnyOverrideValue =>
            !string.IsNullOrWhiteSpace(GetNormalizedUnlockedOverrideValue()) ||
            !string.IsNullOrWhiteSpace(GetNormalizedLockedOverrideValue());

        public bool HasChanges =>
            _hasUnlockedContentChange ||
            _hasLockedContentChange ||
            !string.Equals(GetNormalizedUnlockedOverrideValue(), _baselineUnlockedOverrideValue, StringComparison.Ordinal) ||
            !string.Equals(GetNormalizedLockedOverrideValue(), _baselineLockedOverrideValue, StringComparison.Ordinal);

        public bool HasTransientManagedState =>
            HasPendingManagedLocalOverride(AchievementIconVariant.Unlocked) ||
            HasPendingManagedLocalOverride(AchievementIconVariant.Locked);

        public string UnlockedPreviewPath => BuildPreviewPath(ResolveUnlockedPreviewSource());

        public string LockedPreviewPath => BuildPreviewPath(ResolveLockedPreviewSource());

        public string GetNormalizedUnlockedOverrideValue()
        {
            return NormalizeOverrideValue(UnlockedOverrideValue);
        }

        public string GetNormalizedLockedOverrideValue()
        {
            return NormalizeOverrideValue(LockedOverrideValue);
        }

        public void PrepareManagedLocalOverride(AchievementIconVariant variant)
        {
            var targetPath = GetManagedTargetPath(variant);
            if (string.IsNullOrWhiteSpace(targetPath) ||
                !File.Exists(targetPath) ||
                !string.IsNullOrWhiteSpace(GetBackupPath(variant)))
            {
                return;
            }

            var backupPath = BuildBackupPath(targetPath);
            var backupDirectory = Path.GetDirectoryName(backupPath);
            if (!string.IsNullOrWhiteSpace(backupDirectory))
            {
                Directory.CreateDirectory(backupDirectory);
            }

            File.Copy(targetPath, backupPath, overwrite: true);
            SetBackupPath(variant, backupPath);
            SetRestoreBackupOnReset(
                variant,
                string.Equals(GetBaselineOverrideValue(variant), targetPath, StringComparison.OrdinalIgnoreCase) ||
                IsHttpUrl(GetBaselineOverrideValue(variant)));
        }

        public void RevertFailedManagedLocalOverride(AchievementIconVariant variant)
        {
            RestoreManagedTargetFromBackup(variant);
            ClearManagedLocalOverrideState(variant);
        }

        public void ApplyManagedLocalOverride(AchievementIconVariant variant, string managedPath)
        {
            SetCurrentOverrideValue(variant, managedPath, notifyIfUnchanged: true);
            SetHasContentChange(variant, true);
            NotifyOverrideStateChangedForVariant(variant);
        }

        public string GetManagedTargetPath(AchievementIconVariant variant)
        {
            return variant == AchievementIconVariant.Locked ? _lockedManagedTargetPath : _unlockedManagedTargetPath;
        }

        public void ResetToBaseline()
        {
            RestoreManagedTargetFromBackup(AchievementIconVariant.Unlocked);
            RestoreManagedTargetFromBackup(AchievementIconVariant.Locked);
            ClearManagedLocalOverrideState(AchievementIconVariant.Unlocked);
            ClearManagedLocalOverrideState(AchievementIconVariant.Locked);

            SuppressNotifications = true;
            _unlockedOverrideValue = _baselineUnlockedOverrideValue ?? string.Empty;
            _lockedOverrideValue = _baselineLockedOverrideValue ?? string.Empty;
            SuppressNotifications = false;

            NotifyOverrideStateChanged(
                nameof(UnlockedOverrideText),
                nameof(LockedOverrideText),
                nameof(UnlockedOverrideValue),
                nameof(LockedOverrideValue),
                nameof(UnlockedOverrideToolTip),
                nameof(LockedOverrideToolTip),
                nameof(HasUnlockedOverrideValidationError),
                nameof(HasLockedOverrideValidationError),
                nameof(HasAnyOverrideValue),
                nameof(UnlockedPreviewPath),
                nameof(LockedPreviewPath));
        }

        public void DiscardTransientManagedState()
        {
            if (!HasTransientManagedState)
            {
                return;
            }

            RestoreManagedTargetFromBackup(AchievementIconVariant.Unlocked);
            RestoreManagedTargetFromBackup(AchievementIconVariant.Locked);
            ClearManagedLocalOverrideState(AchievementIconVariant.Unlocked);
            ClearManagedLocalOverrideState(AchievementIconVariant.Locked);
        }

        public void ClearOverride(AchievementIconVariant variant)
        {
            SetOverrideValue(variant, string.Empty);
        }

        public void ClearAllOverrides()
        {
            ClearOverride(AchievementIconVariant.Unlocked);
            ClearOverride(AchievementIconVariant.Locked);
        }

        public void CommitCurrentOverridesAsBaseline()
        {
            CleanupCommittedManagedTarget(AchievementIconVariant.Unlocked);
            CleanupCommittedManagedTarget(AchievementIconVariant.Locked);

            _baselineUnlockedOverrideValue = GetNormalizedUnlockedOverrideValue();
            _baselineLockedOverrideValue = GetNormalizedLockedOverrideValue();
            ClearManagedLocalOverrideState(AchievementIconVariant.Unlocked);
            ClearManagedLocalOverrideState(AchievementIconVariant.Locked);
            OnPropertyChanged(nameof(HasChanges));
        }

        public static AchievementIconOverrideItem Create(
            AchievementDisplayItem projected,
            string unlockedOverride,
            string lockedOverride,
            string originalUnlockedPreviewPath,
            string originalLockedPreviewPath,
            string gameIdText,
            string fileStem,
            ManagedCustomIconService managedCustomIconService)
        {
            if (projected == null)
            {
                return null;
            }

            var row = new AchievementIconOverrideItem(gameIdText, fileStem, managedCustomIconService)
            {
                ProviderKey = projected.ProviderKey,
                GameName = projected.GameName,
                SortingName = projected.SortingName,
                PlayniteGameId = projected.PlayniteGameId,
                ApiName = projected.ApiName,
                DisplayName = projected.DisplayName,
                Description = projected.Description,
                UnlockedIconPath = projected.UnlockedIconPath,
                LockedIconPath = projected.LockedIconPath,
                UnlockTimeUtc = projected.UnlockTimeUtc,
                GlobalPercentUnlocked = projected.GlobalPercentUnlocked,
                PointsValue = projected.PointsValue,
                ProgressNum = projected.ProgressNum,
                ProgressDenom = projected.ProgressDenom,
                TrophyType = projected.TrophyType,
                Unlocked = projected.Unlocked,
                Hidden = projected.Hidden,
                ShowHiddenIcon = projected.ShowHiddenIcon,
                ShowHiddenTitle = projected.ShowHiddenTitle,
                ShowHiddenDescription = projected.ShowHiddenDescription,
                ShowRarityGlow = projected.ShowRarityGlow,
                ShowRarityBar = projected.ShowRarityBar,
                ShowHiddenSuffix = projected.ShowHiddenSuffix,
                ShowLockedIcon = projected.ShowLockedIcon,
                UseSeparateLockedIconsWhenAvailable = projected.UseSeparateLockedIconsWhenAvailable,
                IsRevealed = projected.IsRevealed,
                CategoryType = projected.CategoryType,
                CategoryLabel = projected.CategoryLabel,
                GameIconPath = projected.GameIconPath,
                GameCoverPath = projected.GameCoverPath,
                _originalUnlockedPreviewPath = NormalizePreviewSourceValue(originalUnlockedPreviewPath),
                _originalLockedPreviewPath = NormalizePreviewSourceValue(originalLockedPreviewPath)
            };

            var normalizedUnlockedOverride = NormalizeOverrideValue(unlockedOverride);
            var normalizedLockedOverride = NormalizeOverrideValue(lockedOverride);
            row._baselineUnlockedOverrideValue = NormalizeOverrideValue(
                managedCustomIconService.ResolveManagedDisplayPath(normalizedUnlockedOverride, row._gameIdText));
            row._baselineLockedOverrideValue = NormalizeOverrideValue(
                managedCustomIconService.ResolveManagedDisplayPath(normalizedLockedOverride, row._gameIdText));
            row._unlockedOverrideValue = row._baselineUnlockedOverrideValue ?? string.Empty;
            row._lockedOverrideValue = row._baselineLockedOverrideValue ?? string.Empty;
            return row;
        }

        private void SetOverrideValue(AchievementIconVariant variant, string value)
        {
            var nextValue = ResolveOverrideInputValue(value) ?? string.Empty;
            if (string.Equals(GetCurrentOverrideValue(variant), nextValue, StringComparison.Ordinal))
            {
                return;
            }

            var normalizedNext = NormalizeOverrideValue(nextValue);
            if (HasPendingManagedLocalOverride(variant) &&
                !string.Equals(normalizedNext, GetManagedTargetPath(variant), StringComparison.OrdinalIgnoreCase))
            {
                RestoreManagedTargetFromBackup(variant);
                ClearManagedLocalOverrideState(variant);
            }

            SetCurrentOverrideValue(variant, nextValue, notifyIfUnchanged: false);
            NotifyOverrideStateChangedForVariant(variant);
        }

        private void CleanupCommittedManagedTarget(AchievementIconVariant variant)
        {
            DeleteFileQuietly(GetBackupPath(variant));
            SetBackupPath(variant, null);
            SetRestoreBackupOnReset(variant, false);

            if (HasPendingManagedLocalOverride(variant) &&
                !string.Equals(GetNormalizedOverrideValue(variant), GetManagedTargetPath(variant), StringComparison.OrdinalIgnoreCase))
            {
                DeleteFileQuietly(GetManagedTargetPath(variant));
            }
        }

        private void RestoreManagedTargetFromBackup(AchievementIconVariant variant)
        {
            var backupPath = GetBackupPath(variant);
            if (!string.IsNullOrWhiteSpace(backupPath) && File.Exists(backupPath))
            {
                if (ShouldRestoreBackupOnReset(variant))
                {
                    SafeCopyFile(backupPath, GetManagedTargetPath(variant));
                }
                else
                {
                    DeleteFileQuietly(GetManagedTargetPath(variant));
                }
            }
            else if (HasPendingManagedLocalOverride(variant) &&
                     string.Equals(GetNormalizedOverrideValue(variant), GetManagedTargetPath(variant), StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(GetBaselineOverrideValue(variant), GetManagedTargetPath(variant), StringComparison.OrdinalIgnoreCase))
            {
                DeleteFileQuietly(GetManagedTargetPath(variant));
            }

            DeleteFileQuietly(backupPath);
        }

        private bool HasPendingManagedLocalOverride(AchievementIconVariant variant)
        {
            return GetHasContentChange(variant) || !string.IsNullOrWhiteSpace(GetBackupPath(variant));
        }

        private void AchievementIconOverrideItem_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            switch (e.PropertyName)
            {
                case nameof(IsIconHidden):
                case nameof(ImageUnlocked):
                    OnPropertyChanged(nameof(UnlockedPreviewPath));
                    if (e.PropertyName == nameof(IsIconHidden))
                    {
                        OnPropertyChanged(nameof(LockedPreviewPath));
                    }
                    break;
                case nameof(IsLockedIconHidden):
                case nameof(ImageLocked):
                    OnPropertyChanged(nameof(LockedPreviewPath));
                    break;
            }
        }

        private string ResolveUnlockedPreviewSource()
        {
            if (IsIconHidden)
            {
                return AchievementIconResolver.GetDefaultIcon();
            }

            var previewOverride = ResolvePreviewOverrideValue(
                AchievementIconVariant.Unlocked,
                GetNormalizedUnlockedOverrideValue());
            if (!string.IsNullOrWhiteSpace(previewOverride))
            {
                return previewOverride;
            }

            return ResolveDefaultPreviewSource(AchievementIconVariant.Unlocked);
        }

        private string ResolveLockedPreviewSource()
        {
            if (IsIconHidden || IsLockedIconHidden)
            {
                return AchievementIconResolver.GetDefaultIcon();
            }

            var previewOverride = ResolvePreviewOverrideValue(
                AchievementIconVariant.Locked,
                GetNormalizedLockedOverrideValue());
            if (!string.IsNullOrWhiteSpace(previewOverride))
            {
                return previewOverride;
            }

            // Without an explicit locked override, mirror the normal locked-image behavior,
            // except any valid staged unlocked override still drives the grayscale fallback.
            var unlockedPreviewOverride = ResolvePreviewOverrideValue(
                AchievementIconVariant.Unlocked,
                GetNormalizedUnlockedOverrideValue());
            if (!string.IsNullOrWhiteSpace(unlockedPreviewOverride))
            {
                var lockedFromOverride = AchievementIconResolver.GetLockedDisplayIcon(unlockedPreviewOverride, null);
                return !string.IsNullOrWhiteSpace(lockedFromOverride)
                    ? lockedFromOverride
                    : AchievementIconResolver.GetDefaultIcon();
            }

            return ResolveDefaultPreviewSource(AchievementIconVariant.Locked);
        }

        private string ResolveDefaultPreviewSource(AchievementIconVariant variant)
        {
            if (variant == AchievementIconVariant.Locked)
            {
                if (!string.IsNullOrWhiteSpace(_originalLockedPreviewPath))
                {
                    return _originalLockedPreviewPath;
                }

                if (!string.IsNullOrWhiteSpace(_originalUnlockedPreviewPath))
                {
                    var lockedFromUnlocked = AchievementIconResolver.GetLockedDisplayIcon(_originalUnlockedPreviewPath, null);
                    if (!string.IsNullOrWhiteSpace(lockedFromUnlocked))
                    {
                        return lockedFromUnlocked;
                    }
                }

                return AchievementIconResolver.GetDefaultIcon();
            }

            if (!string.IsNullOrWhiteSpace(_originalUnlockedPreviewPath))
            {
                return _originalUnlockedPreviewPath;
            }

            return AchievementIconResolver.GetDefaultIcon();
        }

        private string ResolvePreviewOverrideValue(AchievementIconVariant variant, string value)
        {
            var normalized = NormalizeOverrideValue(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            if (Path.IsPathRooted(normalized))
            {
                return normalized;
            }

            if (IsHttpUrl(normalized))
            {
                // Always preview the current URL input so edits are immediately reflected.
                return BuildPreviewHttpValue(normalized);
            }

            if (Uri.TryCreate(normalized, UriKind.Absolute, out _))
            {
                return normalized;
            }

            return IsManagedLocalOverride(normalized)
                ? normalized
                : normalized;
        }

        private bool IsValidOverrideValueOrBlank(string value)
        {
            var normalized = NormalizeOverrideValue(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return true;
            }

            if (IsHttpUrl(normalized))
            {
                return Uri.TryCreate(normalized, UriKind.Absolute, out _);
            }

            return IsManagedLocalOverride(normalized) ||
                   IsExistingRootedPath(normalized);
        }

        private static bool IsExistingRootedPath(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   Path.IsPathRooted(value) &&
                   File.Exists(value);
        }

        private bool IsManagedLocalOverride(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   Path.IsPathRooted(value) &&
                   File.Exists(value) &&
                   _managedCustomIconService.IsManagedCustomIconPath(value, _gameIdText);
        }

        private string GetCurrentOverrideValue(AchievementIconVariant variant)
        {
            return variant == AchievementIconVariant.Locked ? _lockedOverrideValue : _unlockedOverrideValue;
        }

        private void SetCurrentOverrideValue(AchievementIconVariant variant, string value, bool notifyIfUnchanged)
        {
            var nextValue = value ?? string.Empty;
            if (!notifyIfUnchanged && string.Equals(GetCurrentOverrideValue(variant), nextValue, StringComparison.Ordinal))
            {
                return;
            }

            if (variant == AchievementIconVariant.Locked)
            {
                _lockedOverrideValue = nextValue;
            }
            else
            {
                _unlockedOverrideValue = nextValue;
            }
        }

        private string GetNormalizedOverrideValue(AchievementIconVariant variant)
        {
            return NormalizeOverrideValue(GetCurrentOverrideValue(variant));
        }

        private string GetDisplayOverrideValue(AchievementIconVariant variant)
        {
            var currentValue = GetCurrentOverrideValue(variant);
            return _managedCustomIconService.GetManagedDisplayPath(currentValue, _gameIdText) ?? string.Empty;
        }

        private string ResolveOverrideInputValue(string value)
        {
            var normalized = NormalizeOverrideValue(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            return _managedCustomIconService.ResolveManagedDisplayPath(normalized, _gameIdText);
        }

        private string GetBaselineOverrideValue(AchievementIconVariant variant)
        {
            return variant == AchievementIconVariant.Locked ? _baselineLockedOverrideValue : _baselineUnlockedOverrideValue;
        }

        private string GetBackupPath(AchievementIconVariant variant)
        {
            return variant == AchievementIconVariant.Locked ? _lockedBackupPath : _unlockedBackupPath;
        }

        private void SetBackupPath(AchievementIconVariant variant, string value)
        {
            if (variant == AchievementIconVariant.Locked)
            {
                _lockedBackupPath = value;
            }
            else
            {
                _unlockedBackupPath = value;
            }
        }

        private bool ShouldRestoreBackupOnReset(AchievementIconVariant variant)
        {
            return variant == AchievementIconVariant.Locked ? _restoreLockedBackupOnReset : _restoreUnlockedBackupOnReset;
        }

        private void SetRestoreBackupOnReset(AchievementIconVariant variant, bool value)
        {
            if (variant == AchievementIconVariant.Locked)
            {
                _restoreLockedBackupOnReset = value;
            }
            else
            {
                _restoreUnlockedBackupOnReset = value;
            }
        }

        private bool GetHasContentChange(AchievementIconVariant variant)
        {
            return variant == AchievementIconVariant.Locked ? _hasLockedContentChange : _hasUnlockedContentChange;
        }

        private void SetHasContentChange(AchievementIconVariant variant, bool value)
        {
            if (variant == AchievementIconVariant.Locked)
            {
                _hasLockedContentChange = value;
            }
            else
            {
                _hasUnlockedContentChange = value;
            }
        }

        private void ClearManagedLocalOverrideState(AchievementIconVariant variant)
        {
            DeleteFileQuietly(GetBackupPath(variant));
            SetBackupPath(variant, null);
            SetRestoreBackupOnReset(variant, false);
            SetHasContentChange(variant, false);
        }

        private void NotifyOverrideStateChangedForVariant(AchievementIconVariant variant)
        {
            if (variant == AchievementIconVariant.Locked)
            {
                NotifyOverrideStateChanged(
                    nameof(LockedOverrideText),
                    nameof(LockedOverrideValue),
                    nameof(LockedOverrideToolTip),
                    nameof(HasLockedOverrideValidationError),
                    nameof(HasAnyOverrideValue),
                    nameof(LockedPreviewPath));
            }
            else
            {
                NotifyOverrideStateChanged(
                    nameof(UnlockedOverrideText),
                    nameof(UnlockedOverrideValue),
                    nameof(UnlockedOverrideToolTip),
                    nameof(HasUnlockedOverrideValidationError),
                    nameof(HasAnyOverrideValue),
                    nameof(UnlockedPreviewPath),
                    nameof(LockedPreviewPath));
            }
        }

        private void NotifyOverrideStateChanged(params string[] changedProperties)
        {
            for (var i = 0; i < changedProperties.Length; i++)
            {
                OnPropertyChanged(changedProperties[i]);
            }

            OnPropertyChanged(nameof(HasChanges));
            OnPropertyChanged(nameof(HasValidationErrors));
        }

        private static string NormalizeOverrideValue(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static string BuildPreviewPath(string value)
        {
            var normalized = NormalizePreviewSourceValue(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return AchievementIconResolver.GetDefaultIcon();
            }

            var cacheBustToken = TryGetPreviewCacheBustToken(normalized);
            return string.IsNullOrWhiteSpace(cacheBustToken)
                ? normalized
                : $"cachebust|{cacheBustToken}|{normalized}";
        }

        private static string NormalizePreviewSourceValue(string value)
        {
            var normalized = NormalizeOverrideValue(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }

            // Remove preview wrappers and normalize to a concrete source before rebasing.
            var hadGrayPrefix = false;
            while (normalized.StartsWith("cachebust|", StringComparison.OrdinalIgnoreCase))
            {
                var firstSeparator = normalized.IndexOf('|');
                if (firstSeparator < 0)
                {
                    break;
                }

                var secondSeparator = normalized.IndexOf('|', firstSeparator + 1);
                if (secondSeparator < 0 || secondSeparator + 1 >= normalized.Length)
                {
                    break;
                }

                normalized = normalized.Substring(secondSeparator + 1);
            }

            while (normalized.StartsWith("gray:", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring("gray:".Length);
                hadGrayPrefix = true;
            }

            if (!Path.IsPathRooted(normalized) &&
                normalized.StartsWith("icon_cache", StringComparison.OrdinalIgnoreCase))
            {
                var pluginDataPath = PlayniteAchievementsPlugin.Instance?.GetPluginUserDataPath();
                if (!string.IsNullOrWhiteSpace(pluginDataPath))
                {
                    try
                    {
                        normalized = Path.GetFullPath(Path.Combine(pluginDataPath, normalized));
                    }
                    catch
                    {
                    }
                }
            }

            return hadGrayPrefix
                ? "gray:" + normalized
                : normalized;
        }

        private static string BuildPreviewHttpValue(string url)
        {
            var normalized = NormalizeOverrideValue(url);
            if (string.IsNullOrWhiteSpace(normalized) ||
                normalized.StartsWith(PreviewHttpPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }

            return PreviewHttpPrefix + normalized;
        }

        private static string TryGetPreviewCacheBustToken(string value)
        {
            var normalized = NormalizeOverrideValue(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            if (normalized.StartsWith("gray:", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring("gray:".Length);
            }

            if (!Path.IsPathRooted(normalized) || !File.Exists(normalized))
            {
                return null;
            }

            try
            {
                return File.GetLastWriteTimeUtc(normalized).Ticks.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static string BuildBackupPath(string targetPath)
        {
            var backupDirectory = Path.Combine(Path.GetTempPath(), "PlayniteAchievements", "custom-icon-backups");
            var extension = Path.GetExtension(targetPath);
            var backupName = Guid.NewGuid().ToString("N") + (string.IsNullOrWhiteSpace(extension) ? ".bak" : extension + ".bak");
            return Path.Combine(backupDirectory, backupName);
        }

        private static void SafeCopyFile(string sourcePath, string destinationPath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) ||
                string.IsNullOrWhiteSpace(destinationPath) ||
                !File.Exists(sourcePath))
            {
                return;
            }

            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.Copy(sourcePath, destinationPath, overwrite: true);
        }

        private static void DeleteFileQuietly(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static bool IsHttpUrl(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }
    }
}
