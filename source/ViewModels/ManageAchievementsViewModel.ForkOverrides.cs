
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using Playnite.SDK;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Providers.Steam;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    public sealed partial class ManageAchievementsViewModel
    {
        public sealed class GameOptionsProviderOption
        {
            public string ProviderKey { get; set; }
            public string DisplayName { get; set; }
        }

        public sealed class OverviewOverrideItem
        {
            public string Label { get; set; }
            public string Value { get; set; }
        }

        private const string LocalProviderKey = "Local";
        private GameOptionsOverrideTab _selectedOverridesTab = GameOptionsOverrideTab.Main;
        private GameOptionsLocalOverrideTab _selectedLocalOverrideTab = GameOptionsLocalOverrideTab.LocalSavesSchema;
        private bool _hasLocalFolderOverride;
        private string _localFolderOverrideValue = string.Empty;
        private string _localFolderOverrideInput = string.Empty;
        private string _localFolderAutoPath = string.Empty;
        private IReadOnlyList<string> _localFolderCandidates = Array.Empty<string>();
        private string _localFolderCandidatesSummary = string.Empty;
        private string _selectedAmbiguousFolder;
        private bool _hasAmbiguousLocalFolders;
        private bool _hasLocalSteamAppIdOverride;
        private string _localSteamAppIdOverrideValue = string.Empty;
        private string _localSteamAppIdOverrideInput = string.Empty;
        private bool _hasLocalSteamAppCacheUserOverride;
        private string _localSteamAppCacheUserOverrideValue = string.Empty;
        private string _localSteamAppCacheUserOverrideInput = string.Empty;
        private IReadOnlyList<LocalSteamAppCacheUserOption> _availableLocalSteamAppCacheUsers = Array.Empty<LocalSteamAppCacheUserOption>();
        private bool _hasLocalLumaPlayAppIdOverride;
        private string _localLumaPlayAppIdOverrideValue = string.Empty;
        private string _localLumaPlayAppIdOverrideInput = string.Empty;
        private bool _hasLocalLumaPlayIniPathOverride;
        private string _localLumaPlayIniPathOverrideValue = string.Empty;
        private string _localLumaPlayIniPathOverrideInput = string.Empty;
        private bool _hasLocalRefreshOnGameCloseOverride;
        private bool _localRefreshOnGameCloseOverrideValue;
        private bool _localRefreshOnGameCloseOverrideInput;
        private bool _localRefreshOnGameCloseGlobalValue;
        private string _raOverrideInput = string.Empty;
        private string _raStatusText = string.Empty;
        private string _xeniaTitleIdOverrideInput = string.Empty;
        private string _xeniaTitleIdStatusText = string.Empty;
        private string _shadPS4MatchIdOverrideInput = string.Empty;
        private string _shadPS4MatchIdStatusText = string.Empty;
        private string _exophaseSlugInput = string.Empty;
        private string _exophaseSlugOverrideValue = string.Empty;
        private string _exophaseAutoSlug = string.Empty;
        private bool _useExophaseForGame;
        private string _steamAccountOverrideValue = string.Empty;
        private string _steamAccountOverrideInput = string.Empty;
        private IReadOnlyList<SteamAccountOption> _availableSteamAccounts = Array.Empty<SteamAccountOption>();
        private IReadOnlyList<GameOptionsProviderOption> _availablePreferredProviders = Array.Empty<GameOptionsProviderOption>();
        private string _preferredProviderOverrideInput = string.Empty;
        private string _preferredProviderStatusText = string.Empty;
        private IReadOnlyList<OverviewOverrideItem> _overviewOverrides = Array.Empty<OverviewOverrideItem>();
        private RelayCommand _applyLocalFolderOverrideCommand, _clearLocalFolderOverrideCommand, _browseLocalFolderOverrideCommand, _browseLocalAchievementFileOverrideCommand;
        private RelayCommand _applyLocalSteamAppIdOverrideCommand, _clearLocalSteamAppIdOverrideCommand, _applyLocalSteamAppCacheUserOverrideCommand, _clearLocalSteamAppCacheUserOverrideCommand;
        private RelayCommand _applyLocalLumaPlayAppIdOverrideCommand, _clearLocalLumaPlayAppIdOverrideCommand, _applyLocalLumaPlayIniPathOverrideCommand, _clearLocalLumaPlayIniPathOverrideCommand, _browseLocalLumaPlayIniPathOverrideCommand;
        private RelayCommand _applyLocalRefreshOnGameCloseOverrideCommand, _clearLocalRefreshOnGameCloseOverrideCommand;
        private RelayCommand _applyRaOverrideCommand, _clearRaOverrideCommand, _applyXeniaTitleIdOverrideCommand, _clearXeniaTitleIdOverrideCommand, _applyShadPS4MatchIdOverrideCommand, _clearShadPS4MatchIdOverrideCommand;
        private RelayCommand _applyExophaseSlugOverrideCommand, _clearExophaseSlugOverrideCommand, _applySteamAccountOverrideCommand, _clearSteamAccountOverrideCommand, _applyPreferredProviderOverrideCommand, _clearPreferredProviderOverrideCommand;

        public GameOptionsOverrideTab SelectedOverridesTab { get => _selectedOverridesTab; set => SetValue(ref _selectedOverridesTab, value); }
        public GameOptionsLocalOverrideTab SelectedLocalOverrideTab { get => _selectedLocalOverrideTab; set => SetValue(ref _selectedLocalOverrideTab, value); }
        public bool IsRaCapable => true;
        public bool ShowXeniaTitleIdOverride => true;
        public bool ShowShadPS4MatchIdOverride => true;
        public bool ShowExophaseToggle => true;
        public bool HasExophaseSlugOverride => !string.IsNullOrWhiteSpace(_exophaseSlugOverrideValue);
        public string ExophaseSlugOverrideValue { get => _exophaseSlugOverrideValue; private set { if (SetValueAndReturn(ref _exophaseSlugOverrideValue, value ?? string.Empty)) OnPropertyChanged(nameof(HasExophaseSlugOverride)); } }
        public string ExophaseAutoSlug { get => _exophaseAutoSlug; private set => SetValue(ref _exophaseAutoSlug, value ?? string.Empty); }
        public bool UseExophaseForGame { get => _useExophaseForGame; set { if (SetValueAndReturn(ref _useExophaseForGame, value)) { ApplyExophaseForceFlag(value); RefreshOverviewOverrides(); } } }
        public bool HasLocalFolderOverride { get => _hasLocalFolderOverride; private set { if (SetValueAndReturn(ref _hasLocalFolderOverride, value)) { OnPropertyChanged(nameof(LocalFolderStatusText)); RaiseForkOverrideCommandStates(); } } }
        public string LocalFolderOverrideValue { get => _localFolderOverrideValue; private set { if (SetValueAndReturn(ref _localFolderOverrideValue, value ?? string.Empty)) OnPropertyChanged(nameof(LocalFolderStatusText)); } }
        public string LocalFolderOverrideInput { get => _localFolderOverrideInput; set { if (SetValueAndReturn(ref _localFolderOverrideInput, value ?? string.Empty)) RaiseForkOverrideCommandStates(); } }
        public string LocalFolderAutoPath { get => _localFolderAutoPath; private set { if (SetValueAndReturn(ref _localFolderAutoPath, value ?? string.Empty)) OnPropertyChanged(nameof(LocalFolderStatusText)); } }
        public IReadOnlyList<string> LocalFolderCandidates { get => _localFolderCandidates; private set => SetValue(ref _localFolderCandidates, value ?? Array.Empty<string>()); }
        public string LocalFolderCandidatesSummary { get => _localFolderCandidatesSummary; private set => SetValue(ref _localFolderCandidatesSummary, value ?? string.Empty); }
        public string SelectedAmbiguousFolder { get => _selectedAmbiguousFolder; set { if (SetValueAndReturn(ref _selectedAmbiguousFolder, value) && !string.IsNullOrWhiteSpace(value)) { LocalFolderOverrideInput = value; if (Directory.Exists(value)) TrySetLocalFolderOverride(value); } } }
        public bool HasAmbiguousLocalFolders { get => _hasAmbiguousLocalFolders; private set { if (SetValueAndReturn(ref _hasAmbiguousLocalFolders, value)) OnPropertyChanged(nameof(LocalFolderStatusText)); } }
        public string LocalFolderStatusText
        {
            get
            {
                if (HasLocalFolderOverride) return string.Format(L("LOCPlayAch_Common_Status_OverrideSetValue", "Override set: {0}"), LocalFolderOverrideValue);
                if (HasAmbiguousLocalFolders) return string.Format(L("LOCPlayAch_GameOptions_Status_LocalFolderAmbiguous", "Multiple local folders found. Using: {0}"), LocalFolderAutoPath);
                if (!string.IsNullOrWhiteSpace(LocalFolderAutoPath)) return string.Format(L("LOCPlayAch_GameOptions_Status_LocalFolderAuto", "Detected folder: {0}"), LocalFolderAutoPath);
                return L(
                    "LOCPlayAch_GameOptions_Status_LocalFolderNotInUse",
                    "Not using a local save folder");
            }
        }

        public bool HasLocalSteamAppIdOverride { get => _hasLocalSteamAppIdOverride; private set { if (SetValueAndReturn(ref _hasLocalSteamAppIdOverride, value)) { OnPropertyChanged(nameof(LocalSteamAppIdStatusText)); RaiseForkOverrideCommandStates(); } } }
        public string LocalSteamAppIdOverrideValue { get => _localSteamAppIdOverrideValue; private set => SetValue(ref _localSteamAppIdOverrideValue, value ?? string.Empty); }
        public string LocalSteamAppIdOverrideInput { get => _localSteamAppIdOverrideInput; set { if (SetValueAndReturn(ref _localSteamAppIdOverrideInput, value ?? string.Empty)) RaiseForkOverrideCommandStates(); } }
        public string LocalSteamAppIdStatusText => HasLocalSteamAppIdOverride ? string.Format(L("LOCPlayAch_Common_Status_OverrideSetValue", "Override set: {0}"), LocalSteamAppIdOverrideValue) : L("LOCPlayAch_Common_Status_NoOverrideSet", "No override set");
        public IReadOnlyList<LocalSteamAppCacheUserOption> AvailableLocalSteamAppCacheUsers { get => _availableLocalSteamAppCacheUsers; private set => SetValue(ref _availableLocalSteamAppCacheUsers, value ?? Array.Empty<LocalSteamAppCacheUserOption>()); }
        public bool HasLocalSteamAppCacheUserOverride { get => _hasLocalSteamAppCacheUserOverride; private set { if (SetValueAndReturn(ref _hasLocalSteamAppCacheUserOverride, value)) { OnPropertyChanged(nameof(LocalSteamAppCacheUserStatusText)); RaiseForkOverrideCommandStates(); } } }
        public string LocalSteamAppCacheUserOverrideValue { get => _localSteamAppCacheUserOverrideValue; private set => SetValue(ref _localSteamAppCacheUserOverrideValue, value ?? string.Empty); }
        public string LocalSteamAppCacheUserOverrideInput { get => _localSteamAppCacheUserOverrideInput; set { if (SetValueAndReturn(ref _localSteamAppCacheUserOverrideInput, value ?? string.Empty)) { OnPropertyChanged(nameof(LocalSteamAppCacheUserStatusText)); RaiseForkOverrideCommandStates(); } } }
        public string LocalSteamAppCacheUserStatusText => HasLocalSteamAppCacheUserOverride ? string.Format(L("LOCPlayAch_GameOptions_Status_LocalSteamUserOverrideValue", "Forced Steam user: {0}"), GetSteamUserDisplayName(LocalSteamAppCacheUserOverrideValue)) : L("LOCPlayAch_GameOptions_Status_LocalSteamUserOverrideNone", "Automatic Steam user detection");
        public bool HasLocalLumaPlayAppIdOverride { get => _hasLocalLumaPlayAppIdOverride; private set { if (SetValueAndReturn(ref _hasLocalLumaPlayAppIdOverride, value)) { OnPropertyChanged(nameof(LocalLumaPlayAppIdStatusText)); RaiseForkOverrideCommandStates(); } } }
        public string LocalLumaPlayAppIdOverrideValue { get => _localLumaPlayAppIdOverrideValue; private set => SetValue(ref _localLumaPlayAppIdOverrideValue, value ?? string.Empty); }
        public string LocalLumaPlayAppIdOverrideInput { get => _localLumaPlayAppIdOverrideInput; set { if (SetValueAndReturn(ref _localLumaPlayAppIdOverrideInput, value ?? string.Empty)) RaiseForkOverrideCommandStates(); } }
        public string LocalLumaPlayAppIdStatusText => HasLocalLumaPlayAppIdOverride ? string.Format(L("LOCPlayAch_GameOptions_Status_LocalLumaPlayAppIdOverrideValue", "Forced LumaPlay Uplay App ID: {0}"), LocalLumaPlayAppIdOverrideValue) : L("LOCPlayAch_GameOptions_Status_LocalLumaPlayAppIdOverrideNone", "No forced LumaPlay Uplay App ID set");
        public bool HasLocalLumaPlayIniPathOverride { get => _hasLocalLumaPlayIniPathOverride; private set { if (SetValueAndReturn(ref _hasLocalLumaPlayIniPathOverride, value)) { OnPropertyChanged(nameof(LocalLumaPlayIniPathStatusText)); RaiseForkOverrideCommandStates(); } } }
        public string LocalLumaPlayIniPathOverrideValue { get => _localLumaPlayIniPathOverrideValue; private set => SetValue(ref _localLumaPlayIniPathOverrideValue, value ?? string.Empty); }
        public string LocalLumaPlayIniPathOverrideInput { get => _localLumaPlayIniPathOverrideInput; set { if (SetValueAndReturn(ref _localLumaPlayIniPathOverrideInput, value ?? string.Empty)) RaiseForkOverrideCommandStates(); } }
        public string LocalLumaPlayIniPathStatusText => HasLocalLumaPlayIniPathOverride ? string.Format(L("LOCPlayAch_GameOptions_Status_LocalLumaPlayIniPathOverrideValue", "LumaPlay.ini override: {0}"), LocalLumaPlayIniPathOverrideValue) : L("LOCPlayAch_GameOptions_Status_LocalLumaPlayIniPathOverrideNone", "No LumaPlay.ini override set");
        public bool HasLocalRefreshOnGameCloseOverride { get => _hasLocalRefreshOnGameCloseOverride; private set { if (SetValueAndReturn(ref _hasLocalRefreshOnGameCloseOverride, value)) { OnPropertyChanged(nameof(LocalRefreshOnGameCloseStatusText)); RaiseForkOverrideCommandStates(); } } }
        public bool LocalRefreshOnGameCloseOverrideValue { get => _localRefreshOnGameCloseOverrideValue; private set { if (SetValueAndReturn(ref _localRefreshOnGameCloseOverrideValue, value)) OnPropertyChanged(nameof(LocalRefreshOnGameCloseStatusText)); } }
        public bool LocalRefreshOnGameCloseOverrideInput { get => _localRefreshOnGameCloseOverrideInput; set { if (SetValueAndReturn(ref _localRefreshOnGameCloseOverrideInput, value)) RaiseForkOverrideCommandStates(); } }
        public bool LocalRefreshOnGameCloseGlobalValue { get => _localRefreshOnGameCloseGlobalValue; private set { if (SetValueAndReturn(ref _localRefreshOnGameCloseGlobalValue, value)) OnPropertyChanged(nameof(LocalRefreshOnGameCloseStatusText)); } }
        public string LocalRefreshOnGameCloseStatusText => HasLocalRefreshOnGameCloseOverride ? string.Format(L("LOCPlayAch_Common_Status_OverrideSetValue", "Override set: {0}"), LocalRefreshOnGameCloseOverrideValue) : string.Format(L("LOCPlayAch_GameOptions_Status_LocalRefreshOnCloseInherited", "Inherited from Local settings: {0}"), LocalRefreshOnGameCloseGlobalValue);
        public string RaOverrideInput { get => _raOverrideInput; set => SetValue(ref _raOverrideInput, value ?? string.Empty); }
        public string RaStatusText { get => _raStatusText; private set => SetValue(ref _raStatusText, value ?? string.Empty); }
        public string XeniaTitleIdOverrideInput { get => _xeniaTitleIdOverrideInput; set => SetValue(ref _xeniaTitleIdOverrideInput, value ?? string.Empty); }
        public string XeniaTitleIdStatusText { get => _xeniaTitleIdStatusText; private set => SetValue(ref _xeniaTitleIdStatusText, value ?? string.Empty); }
        public string ShadPS4MatchIdOverrideInput { get => _shadPS4MatchIdOverrideInput; set => SetValue(ref _shadPS4MatchIdOverrideInput, value ?? string.Empty); }
        public string ShadPS4MatchIdStatusText { get => _shadPS4MatchIdStatusText; private set => SetValue(ref _shadPS4MatchIdStatusText, value ?? string.Empty); }
        public string ExophaseSlugInput { get => _exophaseSlugInput; set => SetValue(ref _exophaseSlugInput, value ?? string.Empty); }
        public string SteamAccountOverrideInput { get => _steamAccountOverrideInput; set => SetValue(ref _steamAccountOverrideInput, value ?? string.Empty); }
        public string SteamAccountStatusText => string.IsNullOrWhiteSpace(_steamAccountOverrideValue) ? L("LOCPlayAch_GameOptions_SteamAccount_Default", "Automatic (default Steam account)") : string.Format(L("LOCPlayAch_Common_Status_OverrideSetValue", "Override set: {0}"), GetSteamAccountDisplayName(_steamAccountOverrideValue));
        public bool HasSteamAccountOverride => !string.IsNullOrWhiteSpace(_steamAccountOverrideValue);
        public IReadOnlyList<SteamAccountOption> AvailableSteamAccounts { get => _availableSteamAccounts; private set => SetValue(ref _availableSteamAccounts, value ?? Array.Empty<SteamAccountOption>()); }
        public IReadOnlyList<GameOptionsProviderOption> AvailablePreferredProviders { get => _availablePreferredProviders; private set => SetValue(ref _availablePreferredProviders, value ?? Array.Empty<GameOptionsProviderOption>()); }
        public string PreferredProviderOverrideInput
        {
            get => _preferredProviderOverrideInput;
            set
            {
                if (SetValueAndReturn(ref _preferredProviderOverrideInput, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(HasPreferredProviderOverride));
                }
            }
        }
        public string PreferredProviderStatusText { get => _preferredProviderStatusText; private set => SetValue(ref _preferredProviderStatusText, value ?? string.Empty); }
        public bool HasPreferredProviderOverride => !string.IsNullOrWhiteSpace(PreferredProviderOverrideInput);
        public IReadOnlyList<OverviewOverrideItem> OverviewOverrides { get => _overviewOverrides; private set { if (SetValueAndReturn(ref _overviewOverrides, value ?? Array.Empty<OverviewOverrideItem>())) OnPropertyChanged(nameof(HasOverviewOverrides)); } }
        public bool HasOverviewOverrides => OverviewOverrides.Count > 0;

        public RelayCommand ApplyLocalFolderOverrideCommand => _applyLocalFolderOverrideCommand ??= new RelayCommand(_ => ApplyLocalFolderOverride(), _ => HasGame);
        public RelayCommand ClearLocalFolderOverrideCommand => _clearLocalFolderOverrideCommand ??= new RelayCommand(_ => ClearLocalFolderOverride(), _ => HasGame && HasLocalFolderOverride);
        public RelayCommand BrowseLocalFolderOverrideCommand => _browseLocalFolderOverrideCommand ??= new RelayCommand(_ => BrowseLocalFolderOverride(), _ => HasGame);
        public RelayCommand BrowseLocalAchievementFileOverrideCommand => _browseLocalAchievementFileOverrideCommand ??= new RelayCommand(_ => BrowseLocalAchievementFileOverride(), _ => HasGame);
        public RelayCommand ApplyLocalSteamAppIdOverrideCommand => _applyLocalSteamAppIdOverrideCommand ??= new RelayCommand(_ => ApplyLocalSteamAppIdOverride(), _ => HasGame);
        public RelayCommand ClearLocalSteamAppIdOverrideCommand => _clearLocalSteamAppIdOverrideCommand ??= new RelayCommand(_ => ClearLocalSteamAppIdOverride(), _ => HasGame && HasLocalSteamAppIdOverride);
        public RelayCommand ApplyLocalSteamAppCacheUserOverrideCommand => _applyLocalSteamAppCacheUserOverrideCommand ??= new RelayCommand(_ => ApplyLocalSteamAppCacheUserOverride(), _ => HasGame);
        public RelayCommand ClearLocalSteamAppCacheUserOverrideCommand => _clearLocalSteamAppCacheUserOverrideCommand ??= new RelayCommand(_ => ClearLocalSteamAppCacheUserOverride(), _ => HasGame && HasLocalSteamAppCacheUserOverride);
        public RelayCommand ApplyLocalLumaPlayAppIdOverrideCommand => _applyLocalLumaPlayAppIdOverrideCommand ??= new RelayCommand(_ => ApplyLocalLumaPlayAppIdOverride(), _ => HasGame);
        public RelayCommand ClearLocalLumaPlayAppIdOverrideCommand => _clearLocalLumaPlayAppIdOverrideCommand ??= new RelayCommand(_ => ClearLocalLumaPlayAppIdOverride(), _ => HasGame && HasLocalLumaPlayAppIdOverride);
        public RelayCommand ApplyLocalLumaPlayIniPathOverrideCommand => _applyLocalLumaPlayIniPathOverrideCommand ??= new RelayCommand(_ => ApplyLocalLumaPlayIniPathOverride(), _ => HasGame);
        public RelayCommand ClearLocalLumaPlayIniPathOverrideCommand => _clearLocalLumaPlayIniPathOverrideCommand ??= new RelayCommand(_ => ClearLocalLumaPlayIniPathOverride(), _ => HasGame && HasLocalLumaPlayIniPathOverride);
        public RelayCommand BrowseLocalLumaPlayIniPathOverrideCommand => _browseLocalLumaPlayIniPathOverrideCommand ??= new RelayCommand(_ => BrowseLocalLumaPlayIniPathOverride(), _ => HasGame);
        public RelayCommand ApplyLocalRefreshOnGameCloseOverrideCommand => _applyLocalRefreshOnGameCloseOverrideCommand ??= new RelayCommand(_ => ApplyLocalRefreshOnGameCloseOverride(), _ => HasGame);
        public RelayCommand ClearLocalRefreshOnGameCloseOverrideCommand => _clearLocalRefreshOnGameCloseOverrideCommand ??= new RelayCommand(_ => ClearLocalRefreshOnGameCloseOverride(), _ => HasGame && HasLocalRefreshOnGameCloseOverride);
        public RelayCommand ApplyRaOverrideCommand => _applyRaOverrideCommand ??= new RelayCommand(_ => ApplyProviderOverrideCompat("RetroAchievements", RaOverrideInput), _ => HasGame);
        public RelayCommand ClearRaOverrideCommand => _clearRaOverrideCommand ??= new RelayCommand(_ => ClearProviderOverrideCompat("RetroAchievements"), _ => HasGame);
        public RelayCommand ApplyXeniaTitleIdOverrideCommand => _applyXeniaTitleIdOverrideCommand ??= new RelayCommand(_ => ApplyProviderOverrideCompat("Xenia", XeniaTitleIdOverrideInput), _ => HasGame);
        public RelayCommand ClearXeniaTitleIdOverrideCommand => _clearXeniaTitleIdOverrideCommand ??= new RelayCommand(_ => ClearProviderOverrideCompat("Xenia"), _ => HasGame);
        public RelayCommand ApplyShadPS4MatchIdOverrideCommand => _applyShadPS4MatchIdOverrideCommand ??= new RelayCommand(_ => ApplyProviderOverrideCompat("ShadPS4", ShadPS4MatchIdOverrideInput), _ => HasGame);
        public RelayCommand ClearShadPS4MatchIdOverrideCommand => _clearShadPS4MatchIdOverrideCommand ??= new RelayCommand(_ => ClearProviderOverrideCompat("ShadPS4"), _ => HasGame);
        public RelayCommand ApplyExophaseSlugOverrideCommand => _applyExophaseSlugOverrideCommand ??= new RelayCommand(_ => ApplyProviderOverrideCompat("Exophase", ExophaseSlugInput), _ => HasGame);
        public RelayCommand ClearExophaseSlugOverrideCommand => _clearExophaseSlugOverrideCommand ??= new RelayCommand(_ => ClearProviderOverrideCompat("Exophase"), _ => HasGame && HasExophaseSlugOverride);
        public RelayCommand ApplySteamAccountOverrideCommand => _applySteamAccountOverrideCommand ??= new RelayCommand(_ => ApplySteamAccountOverride(), _ => HasGame);
        public RelayCommand ClearSteamAccountOverrideCommand => _clearSteamAccountOverrideCommand ??= new RelayCommand(_ => ClearSteamAccountOverride(), _ => HasGame && HasSteamAccountOverride);
        public RelayCommand ApplyPreferredProviderOverrideCommand => _applyPreferredProviderOverrideCommand ??= new RelayCommand(_ => ApplyPreferredProviderOverride(), _ => HasGame);
        public RelayCommand ClearPreferredProviderOverrideCommand => _clearPreferredProviderOverrideCommand ??= new RelayCommand(_ => ClearPreferredProviderOverride(), _ => HasGame && HasPreferredProviderOverride);

        private void RefreshForkOverrideState()
        {
            RefreshCustomSchemaState();
            var data = TryLoadStoredCustomData(_plugin?.GameCustomDataStore);
            var game = _playniteApi?.Database?.Games?.Get(_gameId);
            RefreshSteamAccounts();
            RefreshLocalOverrides(game);
            SteamAccountOverrideInput = SteamDataProvider.TryGetSteamAccountOverride(_gameId, out var steamAccount) ? steamAccount : string.Empty;
            _steamAccountOverrideValue = SteamAccountOverrideInput;
            OnPropertyChanged(nameof(SteamAccountStatusText)); OnPropertyChanged(nameof(HasSteamAccountOverride));
            RaOverrideInput = GetProviderOverrideValue(data, "RetroAchievements", data?.RetroAchievementsGameIdOverride?.ToString());
            RaStatusText = string.IsNullOrWhiteSpace(RaOverrideInput) ? L("LOCPlayAch_Common_Status_NoOverrideSet", "No override set") : string.Format(L("LOCPlayAch_Common_Status_OverrideSetValue", "Override set: {0}"), RaOverrideInput);
            XeniaTitleIdOverrideInput = GetProviderOverrideValue(data, "Xenia", data?.XeniaTitleIdOverride);
            XeniaTitleIdStatusText = string.IsNullOrWhiteSpace(XeniaTitleIdOverrideInput) ? L("LOCPlayAch_Common_Status_NoOverrideSet", "No override set") : string.Format(L("LOCPlayAch_Common_Status_OverrideSetValue", "Override set: {0}"), XeniaTitleIdOverrideInput);
            ShadPS4MatchIdOverrideInput = GetProviderOverrideValue(data, "ShadPS4", data?.ShadPS4MatchIdOverride);
            ShadPS4MatchIdStatusText = string.IsNullOrWhiteSpace(ShadPS4MatchIdOverrideInput) ? L("LOCPlayAch_Common_Status_NoOverrideSet", "No override set") : string.Format(L("LOCPlayAch_Common_Status_OverrideSetValue", "Override set: {0}"), ShadPS4MatchIdOverrideInput);
            ExophaseSlugOverrideValue = GetProviderOverrideValue(data, "Exophase", data?.ExophaseSlugOverride);
            ExophaseSlugInput = ExophaseSlugOverrideValue;
            SetValue(ref _useExophaseForGame, data?.ForceUseExophase == true);
            OnPropertyChanged(nameof(UseExophaseForGame));
            ExophaseAutoSlug = string.Empty;
            AvailablePreferredProviders = BuildPreferredProviders();
            PreferredProviderOverrideInput = _achievementOverridesService?.GetPreferredProviderOverride(_gameId) ?? string.Empty;
            PreferredProviderStatusText = string.IsNullOrWhiteSpace(PreferredProviderOverrideInput) ? L("LOCPlayAch_Common_Status_NoOverrideSet", "No override set") : string.Format(L("LOCPlayAch_Common_Status_OverrideSetValue", "Override set: {0}"), ProviderRegistry.GetLocalizedName(PreferredProviderOverrideInput));
            RefreshOverviewOverrides();
        }

        private void RefreshOverviewOverrides()
        {
            var items = new List<OverviewOverrideItem>();

            void Add(string label, string value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    items.Add(new OverviewOverrideItem { Label = label, Value = value });
                }
            }

            if (IsExcludedFromSummaries)
            {
                Add(L("LOCPlayAch_ManageAchievements_Overrides_SummaryExclusionHeader", "Summary Exclusion"), SummaryExclusionStatusText);
            }

            if (UseSeparateLockedIconsOverride)
            {
                Add(L("LOCPlayAch_ManageAchievements_Overrides_LockedIconsHeader", "Separate Locked Icons"), SeparateLockedIconsStatusText);
            }

            if (HasPreferredProviderOverride)
            {
                Add(
                    L("LOCPlayAch_Menu_LocalProvider_Change", "Preferred Achievement Provider"),
                    ProviderRegistry.GetLocalizedName(PreferredProviderOverrideInput));
            }

            Add(L("LOCPlayAch_ManageAchievements_Overview_RaOverride", "RA GameID Override"), RaOverrideInput);
            Add(L("LOCPlayAch_GameOptions_Overrides_XeniaHeader", "Xenia TitleID Override"), XeniaTitleIdOverrideInput);
            Add(L("LOCPlayAch_GameOptions_Overrides_ShadPS4Header", "ShadPS4 Match ID Override"), ShadPS4MatchIdOverrideInput);
            Add(L("LOCPlayAch_GameOptions_ExophaseSlugLabel", "Exophase Game ID Override"), ExophaseSlugOverrideValue);

            if (UseExophaseForGame)
            {
                Add(
                    L("LOCPlayAch_ManageAchievements_UseExophase", "Use Exophase for this game"),
                    L("LOCPlayAch_Common_Enabled", "Enabled"));
            }

            if (HasSteamAccountOverride)
            {
                Add(
                    L("LOCPlayAch_GameOptions_Overrides_SteamAccountHeader", "Steam Account Override"),
                    GetSteamAccountDisplayName(_steamAccountOverrideValue));
            }

            if (HasLocalFolderOverride)
            {
                Add(
                    L("LOCPlayAch_GameOptions_Overrides_LocalFolderHeader", "Local save folder override"),
                    LocalFolderOverrideValue);
            }

            if (HasLocalSteamAppIdOverride)
            {
                Add(
                    L("LOCPlayAch_GameOptions_Overrides_LocalSteamAppIdHeader", "Local forced Steam App ID"),
                    LocalSteamAppIdOverrideValue);
            }

            if (HasLocalSteamAppCacheUserOverride)
            {
                Add(
                    L("LOCPlayAch_GameOptions_Overrides_LocalSteamUserHeader", "Local forced Steam user"),
                    GetSteamUserDisplayName(LocalSteamAppCacheUserOverrideValue));
            }

            if (HasLocalLumaPlayAppIdOverride)
            {
                Add(
                    L("LOCPlayAch_GameOptions_Overrides_LocalLumaPlayAppIdHeader", "Local forced LumaPlay Uplay App ID"),
                    LocalLumaPlayAppIdOverrideValue);
            }

            if (HasLocalLumaPlayIniPathOverride)
            {
                Add(
                    L("LOCPlayAch_GameOptions_Overrides_LocalLumaPlayIniPathHeader", "LumaPlay.ini override"),
                    LocalLumaPlayIniPathOverrideValue);
            }

            if (HasLocalRefreshOnGameCloseOverride)
            {
                Add(
                    L("LOCPlayAch_GameOptions_Overrides_LocalRefreshOnCloseHeader", "Refresh when game closes"),
                    LocalRefreshOnGameCloseOverrideValue
                        ? L("LOCPlayAch_Common_Enabled", "Enabled")
                        : L("LOCPlayAch_Common_Status_Disabled", "Disabled"));
            }

            OverviewOverrides = items;
        }

        private static string GetProviderOverrideValue(
            PlayniteAchievements.Models.Settings.GameCustomDataFile data,
            string providerKey,
            string legacyValue)
        {
            var providerOverride = data?.ProviderOverride;
            if (providerOverride != null)
            {
                return string.Equals(providerOverride.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase)
                    ? providerOverride.Value ?? string.Empty
                    : string.Empty;
            }

            return legacyValue ?? string.Empty;
        }

        private void RefreshSteamAccounts()
        {
            var steamSettings = ProviderRegistry.Settings<SteamSettings>();
            AvailableSteamAccounts = steamSettings?.GetSelectableAccountOptions(includeAutomatic: true) ?? new[] { new SteamAccountOption(string.Empty, L("LOCPlayAch_GameOptions_SteamAccount_Default", "Automatic (default Steam account)")) };
        }

        private void RefreshLocalOverrides(Playnite.SDK.Models.Game game)
        {
            if (LocalSavesProvider.TryGetAppIdOverride(_gameId, out var appId)) { HasLocalSteamAppIdOverride = true; LocalSteamAppIdOverrideValue = appId.ToString(); LocalSteamAppIdOverrideInput = LocalSteamAppIdOverrideValue; }
            else { HasLocalSteamAppIdOverride = false; LocalSteamAppIdOverrideValue = string.Empty; LocalSteamAppIdOverrideInput = string.Empty; }
            if (LocalSavesProvider.TryGetLumaPlayAppIdOverride(_gameId, out var lumaId)) { HasLocalLumaPlayAppIdOverride = true; LocalLumaPlayAppIdOverrideValue = lumaId.ToString(); LocalLumaPlayAppIdOverrideInput = LocalLumaPlayAppIdOverrideValue; }
            else { HasLocalLumaPlayAppIdOverride = false; LocalLumaPlayAppIdOverrideValue = string.Empty; LocalLumaPlayAppIdOverrideInput = string.Empty; }
            if (LocalSavesProvider.TryGetLumaPlayIniPathOverride(_gameId, out var iniPath)) { HasLocalLumaPlayIniPathOverride = true; LocalLumaPlayIniPathOverrideValue = iniPath; LocalLumaPlayIniPathOverrideInput = iniPath; }
            else { HasLocalLumaPlayIniPathOverride = false; LocalLumaPlayIniPathOverrideValue = string.Empty; LocalLumaPlayIniPathOverrideInput = string.Empty; }
            var localProvider = _refreshService?.Providers?.OfType<LocalSavesProvider>().FirstOrDefault();
            var users = new List<LocalSteamAppCacheUserOption> { new LocalSteamAppCacheUserOption(string.Empty, L("LOCPlayAch_GameOptions_LocalSteamUser_Automatic", "Automatic (all detected users)")) };
            if (localProvider != null) users.AddRange(localProvider.GetAvailableSteamAppCacheUsers());
            AvailableLocalSteamAppCacheUsers = users;
            if (LocalSavesProvider.TryGetSteamAppCacheUserOverride(_gameId, out var cacheUser))
            {
                if (!AvailableLocalSteamAppCacheUsers.Any(option => string.Equals(option.UserId, cacheUser, StringComparison.OrdinalIgnoreCase)))
                    AvailableLocalSteamAppCacheUsers = AvailableLocalSteamAppCacheUsers.Concat(new[] { new LocalSteamAppCacheUserOption(cacheUser, cacheUser) }).ToList();
                HasLocalSteamAppCacheUserOverride = true; LocalSteamAppCacheUserOverrideValue = cacheUser; LocalSteamAppCacheUserOverrideInput = cacheUser;
            }
            else { HasLocalSteamAppCacheUserOverride = false; LocalSteamAppCacheUserOverrideValue = string.Empty; LocalSteamAppCacheUserOverrideInput = string.Empty; }
            if (localProvider != null && game != null && localProvider.TryGetResolvedFolderInfo(game, out var selectedFolder, out var candidates, out var isFolderOverridden, out var isAmbiguousFolder))
            {
                HasLocalFolderOverride = isFolderOverridden; LocalFolderOverrideValue = isFolderOverridden ? selectedFolder : string.Empty; LocalFolderOverrideInput = isFolderOverridden ? selectedFolder : string.Empty; LocalFolderAutoPath = selectedFolder;
                HasAmbiguousLocalFolders = isAmbiguousFolder; LocalFolderCandidates = candidates ?? Array.Empty<string>(); LocalFolderCandidatesSummary = candidates != null && candidates.Count > 1 ? string.Join(Environment.NewLine, candidates) : string.Empty; SelectedAmbiguousFolder = null;
            }
            else if (LocalSavesProvider.TryGetFolderOverride(_gameId, out var folder))
            {
                HasLocalFolderOverride = true; LocalFolderOverrideValue = folder; LocalFolderOverrideInput = folder; LocalFolderAutoPath = folder; HasAmbiguousLocalFolders = false; LocalFolderCandidates = Array.Empty<string>(); LocalFolderCandidatesSummary = string.Empty; SelectedAmbiguousFolder = null;
            }
            else
            {
                HasLocalFolderOverride = false; LocalFolderOverrideValue = string.Empty; LocalFolderOverrideInput = string.Empty; LocalFolderAutoPath = string.Empty; HasAmbiguousLocalFolders = false; LocalFolderCandidates = Array.Empty<string>(); LocalFolderCandidatesSummary = string.Empty; SelectedAmbiguousFolder = null;
            }
            var localSettings = ProviderRegistry.Settings<LocalSettings>();
            LocalRefreshOnGameCloseGlobalValue = localSettings?.RefreshAchievementsOnGameClose == true;
            if (LocalSavesProvider.TryGetRefreshOnGameCloseOverride(_gameId, out var refresh)) { HasLocalRefreshOnGameCloseOverride = true; LocalRefreshOnGameCloseOverrideValue = refresh; LocalRefreshOnGameCloseOverrideInput = refresh; }
            else { HasLocalRefreshOnGameCloseOverride = false; LocalRefreshOnGameCloseOverrideValue = false; LocalRefreshOnGameCloseOverrideInput = LocalRefreshOnGameCloseGlobalValue; }
        }

        private IReadOnlyList<GameOptionsProviderOption> BuildPreferredProviders()
        {
            var options = (_refreshService?.Providers ?? Enumerable.Empty<IDataProvider>())
                .Where(provider => provider != null && !string.IsNullOrWhiteSpace(provider.ProviderKey))
                .Select(provider => provider.ProviderKey.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(ProviderRegistry.GetLocalizedName, StringComparer.CurrentCultureIgnoreCase)
                .Select(key => new GameOptionsProviderOption
                {
                    ProviderKey = key,
                    DisplayName = ProviderRegistry.GetLocalizedName(key)
                })
                .ToList();

            options.Insert(0, new GameOptionsProviderOption
            {
                ProviderKey = string.Empty,
                DisplayName = L("LOCPlayAch_Menu_LocalProvider_Automatic", "Automatic")
            });

            return options;
        }

        private void RaiseForkOverrideCommandStates()
        {
            RaiseCustomSchemaCommandStates();
            _applyLocalFolderOverrideCommand?.RaiseCanExecuteChanged(); _clearLocalFolderOverrideCommand?.RaiseCanExecuteChanged(); _browseLocalFolderOverrideCommand?.RaiseCanExecuteChanged(); _browseLocalAchievementFileOverrideCommand?.RaiseCanExecuteChanged();
            _applyLocalSteamAppIdOverrideCommand?.RaiseCanExecuteChanged(); _clearLocalSteamAppIdOverrideCommand?.RaiseCanExecuteChanged(); _applyLocalSteamAppCacheUserOverrideCommand?.RaiseCanExecuteChanged(); _clearLocalSteamAppCacheUserOverrideCommand?.RaiseCanExecuteChanged();
            _applyLocalLumaPlayAppIdOverrideCommand?.RaiseCanExecuteChanged(); _clearLocalLumaPlayAppIdOverrideCommand?.RaiseCanExecuteChanged(); _applyLocalLumaPlayIniPathOverrideCommand?.RaiseCanExecuteChanged(); _clearLocalLumaPlayIniPathOverrideCommand?.RaiseCanExecuteChanged(); _browseLocalLumaPlayIniPathOverrideCommand?.RaiseCanExecuteChanged();
            _applyLocalRefreshOnGameCloseOverrideCommand?.RaiseCanExecuteChanged(); _clearLocalRefreshOnGameCloseOverrideCommand?.RaiseCanExecuteChanged(); _applySteamAccountOverrideCommand?.RaiseCanExecuteChanged(); _clearSteamAccountOverrideCommand?.RaiseCanExecuteChanged(); _applyPreferredProviderOverrideCommand?.RaiseCanExecuteChanged(); _clearPreferredProviderOverrideCommand?.RaiseCanExecuteChanged();
        }

        private string CurrentGameName => _playniteApi?.Database?.Games?.Get(_gameId)?.Name ?? GameName;
        private void EnsurePreferredProviderIsLocal() => _achievementOverridesService?.SetPreferredProviderOverride(_gameId, LocalProviderKey);
        private void TriggerRefreshForProvider(string providerKey, bool forceIconRefresh = false) => TriggerRefresh(forceIconRefresh);
        private void ApplyLocalFolderOverride() { var p = (LocalFolderOverrideInput ?? string.Empty).Trim(); if (string.IsNullOrWhiteSpace(p)) { ShowWarning("LOCPlayAch_GameOptions_LocalFolder_Invalid", "Please enter a valid local save folder or achievement file path."); return; } if (!Directory.Exists(p) && !File.Exists(p)) { ShowWarning("LOCPlayAch_GameOptions_LocalFolder_NotFound", "The selected local save folder or achievement file does not exist."); return; } if (TrySetLocalFolderOverride(p)) Reload(); }
        private void ClearLocalFolderOverride() { if (TryClearLocalFolderOverride()) Reload(); }
        private void BrowseLocalFolderOverride() { var p = _playniteApi?.Dialogs?.SelectFolder(); if (!string.IsNullOrWhiteSpace(p)) LocalFolderOverrideInput = p; }
        private void BrowseLocalAchievementFileOverride() { var d = new OpenFileDialog { Filter = "Achievement files (achievements.json;achievements.ini;user_stats.ini;tenoke.ini)|achievements.json;achievements.ini;user_stats.ini;tenoke.ini|JSON files (*.json)|*.json|INI files (*.ini)|*.ini|All files (*.*)|*.*", CheckFileExists = true, Multiselect = false }; if (d.ShowDialog() == true) LocalFolderOverrideInput = d.FileName; }
        private bool TrySetLocalFolderOverride(string p) { if (!LocalSavesProvider.TrySetFolderOverride(_gameId, p, CurrentGameName, _persistSettingsForUi, _logger)) return false; EnsurePreferredProviderIsLocal(); _achievementOverridesService?.ClearGameData(_gameId, CurrentGameName); TriggerRefreshForProvider(LocalProviderKey); return true; }
        private bool TryClearLocalFolderOverride() { if (!LocalSavesProvider.TryClearFolderOverride(_gameId, CurrentGameName, _persistSettingsForUi, _logger)) return false; _achievementOverridesService?.ClearGameData(_gameId, CurrentGameName); TriggerRefreshForProvider(LocalProviderKey); return true; }
        private void ApplyLocalSteamAppIdOverride() { if (int.TryParse(LocalSteamAppIdOverrideInput, out var id) && id > 0) { LocalSavesProvider.TrySetAppIdOverride(_gameId, id, CurrentGameName, _persistSettingsForUi, _logger); _achievementOverridesService?.ClearGameData(_gameId, CurrentGameName); TriggerRefreshForProvider(LocalProviderKey); Reload(); } else ShowWarning("LOCPlayAch_Menu_LocalAppId_InvalidId", "Please enter a valid positive Steam App ID."); }
        private void ClearLocalSteamAppIdOverride() { LocalSavesProvider.TryClearAppIdOverride(_gameId, CurrentGameName, _persistSettingsForUi, _logger); _achievementOverridesService?.ClearGameData(_gameId, CurrentGameName); TriggerRefreshForProvider(LocalProviderKey); Reload(); }
        private void ApplyLocalSteamAppCacheUserOverride() { var user = (LocalSteamAppCacheUserOverrideInput ?? string.Empty).Trim(); if (string.IsNullOrWhiteSpace(user)) { ClearLocalSteamAppCacheUserOverride(); return; } LocalSavesProvider.TrySetSteamAppCacheUserOverride(_gameId, user, CurrentGameName, _persistSettingsForUi, _logger); EnsurePreferredProviderIsLocal(); _achievementOverridesService?.ClearGameData(_gameId, CurrentGameName); TriggerRefreshForProvider(LocalProviderKey); Reload(); }
        private void ClearLocalSteamAppCacheUserOverride() { LocalSavesProvider.TryClearSteamAppCacheUserOverride(_gameId, CurrentGameName, _persistSettingsForUi, _logger); _achievementOverridesService?.ClearGameData(_gameId, CurrentGameName); TriggerRefreshForProvider(LocalProviderKey); Reload(); }
        private void ApplyLocalLumaPlayAppIdOverride() { if (int.TryParse(LocalLumaPlayAppIdOverrideInput, out var id) && id > 0) { LocalSavesProvider.TrySetLumaPlayAppIdOverride(_gameId, id, CurrentGameName, _persistSettingsForUi, _logger); _achievementOverridesService?.ClearGameData(_gameId, CurrentGameName); TriggerRefreshForProvider(LocalProviderKey); Reload(); } else ShowWarning("LOCPlayAch_Menu_LocalLumaPlayAppId_InvalidId", "Please enter a valid positive LumaPlay Uplay App ID."); }
        private void ClearLocalLumaPlayAppIdOverride() { LocalSavesProvider.TryClearLumaPlayAppIdOverride(_gameId, CurrentGameName, _persistSettingsForUi, _logger); _achievementOverridesService?.ClearGameData(_gameId, CurrentGameName); TriggerRefreshForProvider(LocalProviderKey); Reload(); }
        private void ApplyLocalLumaPlayIniPathOverride() { var p = (LocalLumaPlayIniPathOverrideInput ?? string.Empty).Trim(); if (string.IsNullOrWhiteSpace(p)) { ShowWarning("LOCPlayAch_GameOptions_LocalLumaPlayIniPath_Invalid", "Please enter a valid LumaPlay.ini path."); return; } if (!File.Exists(p)) { ShowWarning("LOCPlayAch_GameOptions_LocalLumaPlayIniPath_NotFound", "The selected LumaPlay.ini file does not exist."); return; } LocalSavesProvider.TrySetLumaPlayIniPathOverride(_gameId, p, CurrentGameName, _persistSettingsForUi, _logger); _achievementOverridesService?.ClearGameData(_gameId, CurrentGameName); TriggerRefreshForProvider(LocalProviderKey); Reload(); }
        private void ClearLocalLumaPlayIniPathOverride() { LocalSavesProvider.TryClearLumaPlayIniPathOverride(_gameId, CurrentGameName, _persistSettingsForUi, _logger); _achievementOverridesService?.ClearGameData(_gameId, CurrentGameName); TriggerRefreshForProvider(LocalProviderKey); Reload(); }
        private void BrowseLocalLumaPlayIniPathOverride() { var d = new OpenFileDialog { Filter = "LumaPlay.ini|LumaPlay.ini|INI Files (*.ini)|*.ini|All Files (*.*)|*.*", CheckFileExists = true, Multiselect = false }; if (d.ShowDialog() == true) LocalLumaPlayIniPathOverrideInput = d.FileName; }
        private void ApplyLocalRefreshOnGameCloseOverride() { LocalSavesProvider.TrySetRefreshOnGameCloseOverride(_gameId, LocalRefreshOnGameCloseOverrideInput, CurrentGameName, _persistSettingsForUi, _logger); Reload(); }
        private void ClearLocalRefreshOnGameCloseOverride() { LocalSavesProvider.TryClearRefreshOnGameCloseOverride(_gameId, CurrentGameName, _persistSettingsForUi, _logger); Reload(); }
        private void ApplySteamAccountOverride() { var account = (SteamAccountOverrideInput ?? string.Empty).Trim(); if (string.IsNullOrWhiteSpace(account)) { ClearSteamAccountOverride(); return; } SteamDataProvider.TrySetSteamAccountOverride(_gameId, account, CurrentGameName, _persistSettingsForUi, _logger); Reload(); }
        private void ClearSteamAccountOverride() { SteamDataProvider.TryClearSteamAccountOverride(_gameId, CurrentGameName, _persistSettingsForUi, _logger); Reload(); }
        private void ApplyPreferredProviderOverride()
        {
            var key = (PreferredProviderOverrideInput ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                ClearPreferredProviderOverride();
                return;
            }

            var result = _achievementOverridesService?.SetPreferredProviderOverride(_gameId, key);
            if (result?.Success != true)
            {
                ShowWarning("LOCPlayAch_Menu_LocalProvider_SetFailed", "The preferred provider override could not be saved.");
                return;
            }

            if (IsManualProviderKey(key))
            {
                PrepareManualProviderOverride();
                return;
            }

            TriggerRefreshForProvider(key);
            Reload();
        }
        private void ClearPreferredProviderOverride() { _achievementOverridesService?.ClearPreferredProviderOverride(_gameId); Reload(); }
        private void ApplyProviderOverrideCompat(string providerKey, string value) { if (TryCreateProviderOverride(providerKey, value, out var data, out _, out _)) { TrySetProviderOverride(data); Reload(); } }
        private void ClearProviderOverrideCompat(string providerKey) { TryClearProviderOverride(); Reload(); }
        private static bool IsManualProviderKey(string providerKey) => string.Equals((providerKey ?? string.Empty).Trim(), "Manual", StringComparison.OrdinalIgnoreCase);
        private void PrepareManualProviderOverride()
        {
            _achievementOverridesService?.ClearGameData(_gameId, CurrentGameName);
            _gameDataSnapshotProvider?.Invalidate();
            _refreshService?.Cache?.NotifyCacheInvalidated();
            ShowManualTrackingTab = true;
            _manualTrackingWarningAcceptedForProvider = _cachedProviderKey;
            SelectedTab = ManageAchievementsTab.ManualTracking;
        }
        private void ApplyExophaseForceFlag(bool value) { var data = TryLoadStoredCustomData(_plugin?.GameCustomDataStore) ?? new PlayniteAchievements.Models.Settings.GameCustomDataFile(); data.ForceUseExophase = value; _plugin?.GameCustomDataStore?.Save(_gameId, data); _persistSettingsForUi?.Invoke(); }
        private string GetSteamUserDisplayName(string userId) => string.IsNullOrWhiteSpace(userId) ? L("LOCPlayAch_GameOptions_LocalSteamUser_Automatic", "Automatic (all detected users)") : AvailableLocalSteamAppCacheUsers?.FirstOrDefault(o => string.Equals(o.UserId, userId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? userId;
        private string GetSteamAccountDisplayName(string accountId) => string.IsNullOrWhiteSpace(accountId) ? L("LOCPlayAch_GameOptions_SteamAccount_Default", "Automatic (default Steam account)") : AvailableSteamAccounts?.FirstOrDefault(o => string.Equals(o.AccountId, accountId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? accountId;
        private void ShowWarning(string key, string fallback) => _playniteApi?.Dialogs?.ShowMessage(L(key, fallback), L("LOCPlayAch_Title_PluginName", "Playnite Achievements"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
    }
}

