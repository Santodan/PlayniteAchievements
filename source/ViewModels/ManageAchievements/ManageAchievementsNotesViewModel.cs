using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Search;
using PlayniteAchievements.ViewModels.Items;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    public enum AchievementNoteStateFilter
    {
        All,
        WithNotes,
        WithoutNotes
    }

    public sealed class ManageAchievementsNotesViewModel : ObservableObject
    {
        private readonly Guid _gameId;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly ManageAchievementsDataSnapshotProvider _gameDataSnapshotProvider;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;

        private List<ManageAchievementsNoteItem> _allRows = new List<ManageAchievementsNoteItem>();
        private readonly SearchTextIndex<ManageAchievementsNoteItem> _searchIndex =
            new SearchTextIndex<ManageAchievementsNoteItem>(item =>
                SearchTextBuilder.ForManageNote(
                    item?.DisplayName,
                    item?.Description,
                    item?.ApiName,
                    item?.NotePreview,
                    item?.CategoryDisplay,
                    item?.CategoryTypeDisplay));
        private bool _hasAchievements;
        private bool _hasCustomNotes;
        private string _searchText = string.Empty;
        private NoteStateOption _selectedNoteOption;

        public ManageAchievementsNotesViewModel(
            Guid gameId,
            AchievementOverridesService achievementOverridesService,
            ManageAchievementsDataSnapshotProvider gameDataSnapshotProvider,
            PlayniteAchievementsSettings settings,
            ILogger logger)
        {
            _gameId = gameId;
            _achievementOverridesService = achievementOverridesService ?? throw new ArgumentNullException(nameof(achievementOverridesService));
            _gameDataSnapshotProvider = gameDataSnapshotProvider ?? throw new ArgumentNullException(nameof(gameDataSnapshotProvider));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger;

            AchievementRows = new BulkObservableCollection<ManageAchievementsNoteItem>();
            NoteOptions = new ObservableCollection<NoteStateOption>(CreateNoteOptions());
            _selectedNoteOption = NoteOptions.FirstOrDefault();
            ClearSearchCommand = new RelayCommand(_ => SearchText = string.Empty);

            ReloadData();
        }

        public ObservableCollection<ManageAchievementsNoteItem> AchievementRows { get; }

        public ObservableCollection<NoteStateOption> NoteOptions { get; }

        public RelayCommand ClearSearchCommand { get; }

        public bool HasAchievements
        {
            get => _hasAchievements;
            private set => SetValue(ref _hasAchievements, value);
        }

        public bool HasCustomNotes
        {
            get => _hasCustomNotes;
            private set => SetValue(ref _hasCustomNotes, value);
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetValueAndReturn(ref _searchText, value ?? string.Empty))
                {
                    ApplyFilter();
                }
            }
        }

        public NoteStateOption SelectedNoteOption
        {
            get => _selectedNoteOption;
            set
            {
                if (SetValueAndReturn(ref _selectedNoteOption, value ?? NoteOptions.FirstOrDefault()))
                {
                    ApplyFilter();
                }
            }
        }

        public void ReloadData()
        {
            try
            {
                var revealedStateByApiName = AchievementRows
                    .Where(row => row != null && !string.IsNullOrWhiteSpace(row.ApiName))
                    .GroupBy(row => row.ApiName.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().IsRevealed, StringComparer.OrdinalIgnoreCase);

                var hydratedGameData = _gameDataSnapshotProvider.GetHydratedGameData();
                var rawGameData = _gameDataSnapshotProvider.GetRawGameData();
                var rawAchievements = rawGameData?.Achievements?
                    .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                    .ToList() ?? new List<AchievementDetail>();
                var projectionSource = hydratedGameData ?? rawGameData;
                var notes = GameCustomDataLookup.GetAchievementNotes(_gameId, _settings?.Persisted) ??
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var categoryOverrides = GetCurrentCategoryOverrideMap();
                var categoryTypeOverrides = GetCurrentCategoryTypeOverrideMap();

                List<AchievementDetail> orderedAchievements;
                if (hydratedGameData?.AchievementOrder != null && hydratedGameData.AchievementOrder.Count > 0)
                {
                    orderedAchievements = AchievementOrderHelper.ApplyOrder(
                        rawAchievements,
                        a => a.ApiName,
                        hydratedGameData.AchievementOrder);
                }
                else
                {
                    orderedAchievements = rawAchievements
                        .OrderBy(a => a.DisplayName ?? a.ApiName, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                // Per-game invariants hoisted out of the row loop: the appearance snapshot and
                // category art/order resolution are identical for every row in this pass.
                var appearanceSnapshot = AchievementDisplayItem.CreateAppearanceSettingsSnapshot(
                    _settings,
                    _gameId,
                    projectionSource?.UseSeparateLockedIconsWhenAvailable);
                var categoryMemo = new AchievementDisplayItem.CategoryPresentationMemo();

                _allRows = orderedAchievements.Select(a =>
                {
                    var apiName = (a.ApiName ?? string.Empty).Trim();
                    var projected = AchievementDisplayItem.Create(
                        projectionSource,
                        a,
                        _settings,
                        playniteGameIdOverride: _gameId,
                        appearanceSettings: appearanceSnapshot,
                        categoryMemo: categoryMemo);
                    if (projected == null)
                    {
                        return null;
                    }

                    notes.TryGetValue(apiName, out var note);
                    var item = new ManageAchievementsNoteItem
                    {
                        ProviderKey = projected.ProviderKey,
                        GameName = projected.GameName,
                        SortingName = projected.SortingName,
                        PlayniteGameId = projected.PlayniteGameId,
                        ApiName = apiName,
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
                        IsRevealed = revealedStateByApiName.TryGetValue(apiName, out var isRevealed)
                            ? isRevealed
                            : projected.IsRevealed,
                        CategoryLabel = ResolveEffectiveCategoryLabel(a, categoryOverrides),
                        CategoryType = ResolveEffectiveCategoryType(a, categoryTypeOverrides),
                        AchievementNote = note
                    };

                    return item;
                })
                .Where(a => a != null)
                .ToList();

                _searchIndex.Rebuild(_allRows);
                HasAchievements = _allRows.Count > 0;
                RefreshCustomNoteState();
                ApplyFilter();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed loading note rows for gameId={_gameId}");
                _allRows = new List<ManageAchievementsNoteItem>();
                _searchIndex.Clear();
                ReplaceAchievementRows(_allRows);
                HasAchievements = false;
                HasCustomNotes = false;
            }
        }

        public void SetNote(ManageAchievementsNoteItem item, string note)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.ApiName))
            {
                return;
            }

            _achievementOverridesService.SetAchievementNote(_gameId, item.ApiName, note);
            item.SetNoteText(note);
            _searchIndex.Invalidate(item);
            RefreshCustomNoteState();
            ApplyFilter();
        }

        private void RefreshCustomNoteState()
        {
            HasCustomNotes = _allRows.Any(row => row?.HasAchievementNote == true);
        }

        public void ToggleReveal(ManageAchievementsNoteItem item)
        {
            if (item == null || !item.CanReveal)
            {
                return;
            }

            item.ToggleReveal();
            _searchIndex.Invalidate(item);
        }

        private void ApplyFilter()
        {
            var filtered = _allRows.AsEnumerable();
            var searchQuery = SearchQuery.From(SearchText);

            if (searchQuery.HasValue)
            {
                filtered = filtered.Where(a => _searchIndex.Matches(a, searchQuery));
            }

            switch (SelectedNoteOption?.Value ?? AchievementNoteStateFilter.All)
            {
                case AchievementNoteStateFilter.WithNotes:
                    filtered = filtered.Where(a => a.HasAchievementNote);
                    break;
                case AchievementNoteStateFilter.WithoutNotes:
                    filtered = filtered.Where(a => !a.HasAchievementNote);
                    break;
            }

            ReplaceAchievementRows(filtered.ToList());
        }

        private void ReplaceAchievementRows(IEnumerable<ManageAchievementsNoteItem> rows)
        {
            CollectionHelper.Replace(AchievementRows, rows);
        }

        private Dictionary<string, string> GetCurrentCategoryOverrideMap()
        {
            var map = GameCustomDataLookup.GetAchievementCategoryOverrides(_gameId, _settings?.Persisted);
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in map)
            {
                var apiName = (pair.Key ?? string.Empty).Trim();
                var category = AchievementCategoryTypeHelper.NormalizeCategory(pair.Value);
                if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(category))
                {
                    continue;
                }

                normalized[apiName] = category;
            }

            return normalized;
        }

        private Dictionary<string, string> GetCurrentCategoryTypeOverrideMap()
        {
            var map = GameCustomDataLookup.GetAchievementCategoryTypeOverrides(_gameId, _settings?.Persisted);
            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in map)
            {
                var apiName = (pair.Key ?? string.Empty).Trim();
                var categoryType = AchievementCategoryTypeHelper.Normalize(pair.Value);
                if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(categoryType))
                {
                    continue;
                }

                normalized[apiName] = categoryType;
            }

            return normalized;
        }

        private static string ResolveEffectiveCategoryLabel(
            AchievementDetail achievement,
            IReadOnlyDictionary<string, string> categoryOverrides)
        {
            var apiName = (achievement?.ApiName ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(apiName) &&
                categoryOverrides != null &&
                categoryOverrides.TryGetValue(apiName, out var categoryOverride) &&
                !string.IsNullOrWhiteSpace(categoryOverride))
            {
                return AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(categoryOverride);
            }

            return AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(achievement?.Category);
        }

        private static string ResolveEffectiveCategoryType(
            AchievementDetail achievement,
            IReadOnlyDictionary<string, string> categoryTypeOverrides)
        {
            var apiName = (achievement?.ApiName ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(apiName) &&
                categoryTypeOverrides != null &&
                categoryTypeOverrides.TryGetValue(apiName, out var categoryTypeOverride) &&
                !string.IsNullOrWhiteSpace(categoryTypeOverride))
            {
                return AchievementCategoryTypeHelper.NormalizeOrDefault(categoryTypeOverride);
            }

            return AchievementCategoryTypeHelper.NormalizeOrDefault(achievement?.CategoryType);
        }

        private static IEnumerable<NoteStateOption> CreateNoteOptions()
        {
            return new[]
            {
                new NoteStateOption(AchievementNoteStateFilter.All, L("LOCPlayAch_Common_All")),
                new NoteStateOption(AchievementNoteStateFilter.WithNotes, L("LOCPlayAch_ManageAchievements_Notes_WithNotes")),
                new NoteStateOption(AchievementNoteStateFilter.WithoutNotes, L("LOCPlayAch_ManageAchievements_Notes_WithoutNotes"))
            };
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }

    public sealed class ManageAchievementsNoteItem : AchievementDisplayItem
    {
        public string NotePreview => AchievementNoteHelper.GetPreviewText(AchievementNote);

        public string NoteStatusText => HasAchievementNote
            ? L("LOCPlayAch_ManageAchievements_Notes_HasNote")
            : L("LOCPlayAch_ManageAchievements_Notes_NoNote");

        public string CategoryDisplay => AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(CategoryLabel);

        public void SetNoteText(string note)
        {
            AchievementNote = note;
            OnPropertyChanged(nameof(NotePreview));
            OnPropertyChanged(nameof(NoteStatusText));
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }

    public sealed class NoteStateOption
    {
        public NoteStateOption(AchievementNoteStateFilter value, string displayName)
        {
            Value = value;
            DisplayName = displayName;
        }

        public AchievementNoteStateFilter Value { get; }

        public string DisplayName { get; }
    }
}
