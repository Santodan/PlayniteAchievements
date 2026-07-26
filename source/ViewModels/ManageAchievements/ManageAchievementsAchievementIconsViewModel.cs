using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.ViewModels.Items;
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

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    public sealed class ManageAchievementsAchievementIconsViewModel : ObservableObject
    {
        private readonly Guid _gameId;
        private readonly string _gameIdText;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly ManageAchievementsDataSnapshotProvider _gameDataSnapshotProvider;
        private readonly ManagedCustomIconService _managedCustomIconService;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;

        private bool _hasAchievements;
        private bool _hasValidationErrors;
        private bool _isPersisting;
        private string _saveStatusText;
        private bool _saveStatusIsError;

        public ManageAchievementsAchievementIconsViewModel(
            Guid gameId,
            AchievementOverridesService achievementOverridesService,
            ManageAchievementsDataSnapshotProvider gameDataSnapshotProvider,
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
            OpenIconsFolderCommand = new RelayCommand(_ => OpenIconsFolder());

            ForceReloadData();
        }

        public event EventHandler<IconOverridesSavedEventArgs> IconOverridesSaved;

        public ObservableCollection<AchievementIconOverrideItem> AchievementRows { get; }

        public RelayCommand OpenIconsFolderCommand { get; }

        public bool HasAchievements
        {
            get => _hasAchievements;
            private set => SetValue(ref _hasAchievements, value);
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
                }
            }
        }

        public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

        public string StatusText
        {
            get
            {
                if (HasValidationErrors)
                {
                    return L("LOCPlayAch_ManageAchievements_CustomIcons_ValidationError");
                }

                return _saveStatusText;
            }
        }

        public bool StatusIsError => HasValidationErrors || _saveStatusIsError;

        public void RefreshData()
        {
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
                    L("LOCPlayAch_ManageAchievements_CustomIcons_LocalFileMissing"),
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
                    string.Format(L("LOCPlayAch_Status_Failed"), ex.Message),
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

                // Per-game invariants hoisted out of the row loop: the appearance snapshot and
                // category art/order resolution are identical for every row in this pass.
                var appearanceSnapshot = AchievementDisplayItem.CreateAppearanceSettingsSnapshot(
                    _settings,
                    _gameId,
                    displayGameData?.UseSeparateLockedIconsWhenAvailable);
                var categoryMemo = new AchievementDisplayItem.CategoryPresentationMemo();

                var rows = new List<AchievementIconOverrideItem>();
                for (var i = 0; i < orderedAchievements.Count; i++)
                {
                    var achievement = orderedAchievements[i];
                    var projected = AchievementDisplayItem.Create(
                        displayGameData,
                        achievement,
                        _settings,
                        playniteGameIdOverride: _gameId,
                        appearanceSettings: appearanceSnapshot,
                        categoryMemo: categoryMemo);
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

                if (!TryMergeRowsInPlace(rows))
                {
                    RestoreRevealedState(rows);
                    ReplaceRows(rows);
                }

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
                    string.Format(L("LOCPlayAch_Status_Failed"), ex.Message),
                    isError: true);
                RefreshComputedState();
            }
        }

        private void PersistCurrentIconOverrides()
        {
            _isPersisting = true;
            try
            {
                var unlockedOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var lockedOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var changedApiNames = new List<string>();
                for (var i = 0; i < AchievementRows.Count; i++)
                {
                    var row = AchievementRows[i];
                    var apiName = NormalizeText(row?.ApiName);
                    if (string.IsNullOrWhiteSpace(apiName))
                    {
                        continue;
                    }

                    if (row.HasPersistableChanges)
                    {
                        changedApiNames.Add(apiName);
                    }

                    // An invalid pending edit keeps its last persisted value in the store;
                    // the invalid text stays in the row with its inline error.
                    var unlockedOverride = row.GetPersistableUnlockedOverrideValue();
                    var lockedOverride = row.GetPersistableLockedOverrideValue();
                    if (!string.IsNullOrWhiteSpace(unlockedOverride))
                    {
                        unlockedOverrides[apiName] = unlockedOverride;
                    }

                    if (!string.IsNullOrWhiteSpace(lockedOverride))
                    {
                        lockedOverrides[apiName] = lockedOverride;
                    }
                }

                _achievementOverridesService.SetAchievementIconOverrides(_gameId, unlockedOverrides, lockedOverrides);

                for (var i = 0; i < AchievementRows.Count; i++)
                {
                    AchievementRows[i]?.CommitPersistedOverridesAsBaseline();
                }

                RefreshComputedState();
                if (changedApiNames.Count > 0)
                {
                    IconOverridesSaved?.Invoke(this, new IconOverridesSavedEventArgs(changedApiNames));
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed saving custom icon overrides for gameId={_gameId}");
                SetSaveStatus(
                    string.Format(L("LOCPlayAch_Status_Failed"), ex.Message),
                    isError: true);
            }
            finally
            {
                _isPersisting = false;
            }
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
                            L("LOCPlayAch_Status_Failed"),
                            L("LOCPlayAch_ManageAchievements_CustomIcons_OpenFolderUnavailable")),
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
                    string.Format(L("LOCPlayAch_Status_Failed"), ex.Message),
                    isError: true);
            }
        }

        /// <summary>
        /// Reuses the live row instances when the reloaded achievement set is unchanged, so
        /// transient view state (reveal, scroll position, focus) survives the reload.
        /// </summary>
        private bool TryMergeRowsInPlace(IReadOnlyList<AchievementIconOverrideItem> newRows)
        {
            if (newRows.Count == 0 || newRows.Count != AchievementRows.Count)
            {
                return false;
            }

            for (var i = 0; i < newRows.Count; i++)
            {
                if (!string.Equals(
                    NormalizeText(AchievementRows[i]?.ApiName),
                    NormalizeText(newRows[i]?.ApiName),
                    StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            // Rows adopt already-persisted state here; Row_PropertyChanged must not
            // treat the resulting notifications as user edits and re-persist.
            _isPersisting = true;
            try
            {
                for (var i = 0; i < newRows.Count; i++)
                {
                    AchievementRows[i]?.UpdateFrom(newRows[i]);
                }
            }
            finally
            {
                _isPersisting = false;
            }

            return true;
        }

        private void RestoreRevealedState(IReadOnlyList<AchievementIconOverrideItem> newRows)
        {
            var revealedByApiName = AchievementRows
                .Where(row => row != null && !string.IsNullOrWhiteSpace(row.ApiName))
                .GroupBy(row => NormalizeText(row.ApiName), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().IsRevealed, StringComparer.OrdinalIgnoreCase);
            if (revealedByApiName.Count == 0)
            {
                return;
            }

            foreach (var row in newRows)
            {
                var apiName = NormalizeText(row?.ApiName);
                if (!string.IsNullOrWhiteSpace(apiName) &&
                    revealedByApiName.TryGetValue(apiName, out var isRevealed) &&
                    isRevealed)
                {
                    row.IsRevealed = true;
                }
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

            var unlockedChanged = e.PropertyName == nameof(AchievementIconOverrideItem.UnlockedOverrideValue);
            var lockedChanged = e.PropertyName == nameof(AchievementIconOverrideItem.LockedOverrideValue);
            if ((unlockedChanged || lockedChanged) && !_isPersisting)
            {
                SetSaveStatus(null, isError: false);
                // Values arrive on complete input (focus loss, Enter, picker, drop, clear).
                // Valid values persist immediately; an invalid value stays pending in the
                // row with its inline error and the store keeps the last good value.
                if (sender is AchievementIconOverrideItem row &&
                    ((unlockedChanged && !row.HasUnlockedOverrideValidationError) ||
                     (lockedChanged && !row.HasLockedOverrideValidationError)))
                {
                    PersistCurrentIconOverrides();
                }
            }

            RefreshComputedState();
        }

        private void RefreshComputedState()
        {
            var hasValidationErrors = false;

            for (var i = 0; i < AchievementRows.Count; i++)
            {
                var row = AchievementRows[i];
                if (row == null)
                {
                    continue;
                }

                hasValidationErrors |= row.HasValidationErrors;
            }

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

                var preferred = disk.GetAchievementIconCachePath(
                    _gameIdText,
                    fileStem,
                    variant);
                if (!string.IsNullOrWhiteSpace(preferred) && File.Exists(preferred))
                {
                    return preferred;
                }

                // Retired compressed 128px cache; still served for games not refreshed since the
                // compressed mode was removed. The folder is deleted on each game's next refresh.
                var fallback = disk.GetLegacyCompressedAchievementIconCachePath(
                    _gameIdText,
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

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }

    public sealed class IconOverridesSavedEventArgs : EventArgs
    {
        public IconOverridesSavedEventArgs(IReadOnlyCollection<string> changedApiNames)
        {
            ChangedApiNames = changedApiNames ?? Array.Empty<string>();
        }

        public IReadOnlyCollection<string> ChangedApiNames { get; }
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

        public bool HasChanges =>
            _hasUnlockedContentChange ||
            _hasLockedContentChange ||
            !string.Equals(GetNormalizedUnlockedOverrideValue(), _baselineUnlockedOverrideValue, StringComparison.Ordinal) ||
            !string.Equals(GetNormalizedLockedOverrideValue(), _baselineLockedOverrideValue, StringComparison.Ordinal);

        /// <summary>
        /// Like HasChanges, but only counts variants whose current value will actually be
        /// written to the store (invalid pending edits are excluded).
        /// </summary>
        internal bool HasPersistableChanges =>
            (!HasUnlockedOverrideValidationError &&
                (_hasUnlockedContentChange ||
                 !string.Equals(GetNormalizedUnlockedOverrideValue(), _baselineUnlockedOverrideValue, StringComparison.Ordinal))) ||
            (!HasLockedOverrideValidationError &&
                (_hasLockedContentChange ||
                 !string.Equals(GetNormalizedLockedOverrideValue(), _baselineLockedOverrideValue, StringComparison.Ordinal)));

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

        /// <summary>
        /// The values to write to the store: the current value when valid, otherwise the
        /// last persisted one, so an invalid pending edit never reaches the store.
        /// </summary>
        public string GetPersistableUnlockedOverrideValue()
        {
            return HasUnlockedOverrideValidationError
                ? NormalizeOverrideValue(_baselineUnlockedOverrideValue)
                : GetNormalizedUnlockedOverrideValue();
        }

        public string GetPersistableLockedOverrideValue()
        {
            return HasLockedOverrideValidationError
                ? NormalizeOverrideValue(_baselineLockedOverrideValue)
                : GetNormalizedLockedOverrideValue();
        }

        /// <summary>
        /// Commits each variant that was just persisted; a variant holding an invalid
        /// pending edit keeps its previous baseline so the inline error stays visible.
        /// </summary>
        public void CommitPersistedOverridesAsBaseline()
        {
            if (!HasUnlockedOverrideValidationError)
            {
                CleanupCommittedManagedTarget(AchievementIconVariant.Unlocked);
                _baselineUnlockedOverrideValue = GetNormalizedUnlockedOverrideValue();
                ClearManagedLocalOverrideState(AchievementIconVariant.Unlocked);
            }

            if (!HasLockedOverrideValidationError)
            {
                CleanupCommittedManagedTarget(AchievementIconVariant.Locked);
                _baselineLockedOverrideValue = GetNormalizedLockedOverrideValue();
                ClearManagedLocalOverrideState(AchievementIconVariant.Locked);
            }

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

        /// <summary>
        /// Adopts a freshly-built row's data onto this live instance so the bound row keeps
        /// its transient view state. IsRevealed is intentionally not copied, and a variant
        /// with a pending edit keeps its current text (only its baseline advances).
        /// </summary>
        internal void UpdateFrom(AchievementIconOverrideItem source)
        {
            if (source == null)
            {
                return;
            }

            ProviderKey = source.ProviderKey;
            GameName = source.GameName;
            SortingName = source.SortingName;
            PlayniteGameId = source.PlayniteGameId;
            DisplayName = source.DisplayName;
            Description = source.Description;
            UnlockedIconPath = source.UnlockedIconPath;
            LockedIconPath = source.LockedIconPath;
            UnlockTimeUtc = source.UnlockTimeUtc;
            GlobalPercentUnlocked = source.GlobalPercentUnlocked;
            PointsValue = source.PointsValue;
            ProgressNum = source.ProgressNum;
            ProgressDenom = source.ProgressDenom;
            TrophyType = source.TrophyType;
            Unlocked = source.Unlocked;
            Hidden = source.Hidden;
            ShowHiddenIcon = source.ShowHiddenIcon;
            ShowHiddenTitle = source.ShowHiddenTitle;
            ShowHiddenDescription = source.ShowHiddenDescription;
            ShowRarityBar = source.ShowRarityBar;
            ShowHiddenSuffix = source.ShowHiddenSuffix;
            ShowLockedIcon = source.ShowLockedIcon;
            UseSeparateLockedIconsWhenAvailable = source.UseSeparateLockedIconsWhenAvailable;
            CategoryType = source.CategoryType;
            CategoryLabel = source.CategoryLabel;
            GameIconPath = source.GameIconPath;
            GameCoverPath = source.GameCoverPath;

            _originalUnlockedPreviewPath = source._originalUnlockedPreviewPath;
            _originalLockedPreviewPath = source._originalLockedPreviewPath;

            var unlockedValueChanged = AdoptPersistedBaseline(
                AchievementIconVariant.Unlocked,
                source._baselineUnlockedOverrideValue);
            var lockedValueChanged = AdoptPersistedBaseline(
                AchievementIconVariant.Locked,
                source._baselineLockedOverrideValue);

            // Only a variant whose committed value actually changed re-raises its text
            // bindings, so in-progress typing in an unrelated field is never clobbered.
            if (unlockedValueChanged)
            {
                NotifyOverrideStateChangedForVariant(AchievementIconVariant.Unlocked);
            }

            if (lockedValueChanged)
            {
                NotifyOverrideStateChangedForVariant(AchievementIconVariant.Locked);
            }

            // Always re-raise the previews: a re-materialized managed file keeps its path,
            // and only a fresh evaluation picks up the new cache-bust token.
            OnPropertyChanged(nameof(UnlockedPreviewPath));
            OnPropertyChanged(nameof(LockedPreviewPath));
            OnPropertyChanged(nameof(HasChanges));
        }

        private bool AdoptPersistedBaseline(AchievementIconVariant variant, string newBaseline)
        {
            var normalizedNewBaseline = NormalizeOverrideValue(newBaseline);
            var hadPendingEdit = GetHasContentChange(variant) ||
                !string.Equals(
                    GetNormalizedOverrideValue(variant),
                    GetBaselineOverrideValue(variant),
                    StringComparison.Ordinal);

            if (variant == AchievementIconVariant.Locked)
            {
                _baselineLockedOverrideValue = normalizedNewBaseline;
            }
            else
            {
                _baselineUnlockedOverrideValue = normalizedNewBaseline;
            }

            if (hadPendingEdit)
            {
                return false;
            }

            var nextValue = normalizedNewBaseline ?? string.Empty;
            if (string.Equals(GetCurrentOverrideValue(variant), nextValue, StringComparison.Ordinal))
            {
                return false;
            }

            SetCurrentOverrideValue(variant, nextValue, notifyIfUnchanged: false);
            return true;
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
                    nameof(LockedPreviewPath));
            }
            else
            {
                NotifyOverrideStateChanged(
                    nameof(UnlockedOverrideText),
                    nameof(UnlockedOverrideValue),
                    nameof(UnlockedOverrideToolTip),
                    nameof(HasUnlockedOverrideValidationError),
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
            return string.IsNullOrWhiteSpace(normalized)
                ? AchievementIconResolver.GetDefaultIcon()
                : AchievementIconResolver.ApplyCacheBust(normalized);
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
