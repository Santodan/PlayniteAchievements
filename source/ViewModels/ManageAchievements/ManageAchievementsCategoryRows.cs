using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.Search;
using PlayniteAchievements.ViewModels.Items;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    public sealed class ManageAchievementsCategoryItem : AchievementDisplayItem
    {
        private bool _isSelected;

        public string ProviderCategory { get; set; }

        public string ProviderCategoryType { get; set; }

        public string Category
        {
            get => CategoryLabel;
            set
            {
                CategoryLabel = value;
                OnPropertyChanged(nameof(CategoryDisplay));
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetValue(ref _isSelected, value);
        }

        public string CategoryDisplay => AchievementCategoryTypeHelper.ToCategoryLabelDisplayText(Category);
    }

    public sealed class ManageAchievementsCategoryMetadataItem : ObservableObject
    {
        private readonly string _gameIdText;
        private readonly ManagedCustomIconService _managedCustomIconService;
        private string _baselineArtOverrideValue;
        private string _artOverrideValue;
        private string _renameOverrideText;
        private bool _baselineIsSummarySelected;
        private bool _isSummarySelected;

        private ManageAchievementsCategoryMetadataItem(
            string gameIdText,
            string fileStem,
            ManagedCustomIconService managedCustomIconService)
        {
            _gameIdText = gameIdText;
            FileStem = fileStem;
            _managedCustomIconService = managedCustomIconService;
        }

        public string CategoryLabel { get; private set; }

        public string CategoryDisplay => AchievementCategoryTypeHelper.ToCategoryLabelDisplayText(CategoryLabel);

        /// <summary>
        /// True for the Default bucket row. It cannot be renamed or merged away because it is
        /// the fallback bucket for achievements without an explicit category.
        /// </summary>
        public bool IsDefaultCategory => string.Equals(
            CategoryLabel,
            AchievementCategoryTypeHelper.DefaultCategoryLabel,
            StringComparison.OrdinalIgnoreCase);

        public string ProviderCategoryLabel { get; private set; }

        public string ProviderCategoryDisplay => AchievementCategoryTypeHelper.ToCategoryLabelDisplayText(ProviderCategoryLabel);

        public string RenameOverrideText
        {
            get => _renameOverrideText;
            set
            {
                var nextValue = value ?? string.Empty;
                if (SetValueAndReturn(ref _renameOverrideText, nextValue))
                {
                    OnPropertyChanged(nameof(HasRenameOverride));
                }
            }
        }

        public bool HasRenameOverride => !string.IsNullOrWhiteSpace(GetNormalizedRenameOverrideValue());

        public string FileStem { get; }

        public int TotalAchievements { get; private set; }

        public int UnlockedAchievements { get; private set; }

        public string ProgressText => $"{UnlockedAchievements:N0}/{TotalAchievements:N0}";

        public string DefaultArtPath { get; private set; }

        public string ArtOverrideValue
        {
            get => _artOverrideValue;
            set => SetOverrideValue(value);
        }

        public string ArtOverrideText
        {
            get => GetDisplayOverrideValue();
            set => SetOverrideValue(value);
        }

        public string ArtPreviewPath => BuildPreviewPath(
            ResolvePreviewOverrideValue(GetNormalizedArtOverrideValue()) ?? DefaultArtPath);

        public bool HasArtOverrideValidationError => !IsValidOverrideValueOrBlank(ArtOverrideValue);

        public bool HasValidationErrors => HasArtOverrideValidationError;

        public bool IsSummarySelected
        {
            get => _isSummarySelected;
            set
            {
                if (SetValueAndReturn(ref _isSummarySelected, value))
                {
                    OnPropertyChanged(nameof(HasChanges));
                }
            }
        }

        public bool HasChanges =>
            !string.Equals(GetNormalizedArtOverrideValue(), _baselineArtOverrideValue, StringComparison.Ordinal) ||
            _isSummarySelected != _baselineIsSummarySelected;

        public static ManageAchievementsCategoryMetadataItem Create(
            string categoryLabel,
            string providerCategoryLabel,
            IReadOnlyList<ManageAchievementsCategoryItem> achievements,
            CategoryImageOverrideData imageOverride,
            string gameIdText,
            string fileStem,
            ManagedCustomIconService managedCustomIconService,
            bool isSummarySelected = false,
            IReadOnlyList<ManageAchievementsCategoryItem> artFallbackSource = null)
        {
            var normalizedLabel = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(categoryLabel);
            var normalizedProviderLabel = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(providerCategoryLabel);
            var playniteGameId = Guid.TryParse(gameIdText, out var parsedGameId) ? parsedGameId : (Guid?)null;
            // An empty bucket (the always-listed Default row) still resolves game art
            // from the fallback source so its preview matches the other rows.
            var artSource = achievements != null && achievements.Count > 0 ? achievements : artFallbackSource;
            var row = new ManageAchievementsCategoryMetadataItem(gameIdText, fileStem, managedCustomIconService)
            {
                CategoryLabel = normalizedLabel,
                ProviderCategoryLabel = normalizedProviderLabel,
                TotalAchievements = achievements?.Count ?? 0,
                UnlockedAchievements = achievements?.Count(item => item?.Unlocked == true) ?? 0,
                // Provider-supplied defaults are the true revert target, ahead of game art.
                // They are keyed by the provider label so renamed rows still find them.
                DefaultArtPath = CategoryDefaultImageResolver.Resolve(playniteGameId, normalizedProviderLabel) ??
                                 ResolveSharedImage(artSource, item => item?.GameIconPath) ??
                                 ResolveSharedImage(artSource, item => item?.GameCoverPath)
            };

            row._renameOverrideText = string.Equals(row.CategoryLabel, row.ProviderCategoryLabel, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : row.CategoryLabel;
            row._baselineArtOverrideValue = row.ResolveOverrideInputValue(imageOverride?.Art);
            row._artOverrideValue = row._baselineArtOverrideValue ?? string.Empty;
            row._baselineIsSummarySelected = isSummarySelected;
            row._isSummarySelected = isSummarySelected;
            return row;
        }

        public void SetOverrideValue(string value)
        {
            var nextValue = ResolveOverrideInputValue(value) ?? string.Empty;
            if (string.Equals(_artOverrideValue, nextValue, StringComparison.Ordinal))
            {
                return;
            }

            _artOverrideValue = nextValue;
            NotifyOverrideStateChanged();
        }

        public void ClearOverride()
        {
            SetOverrideValue(null);
        }

        public void CommitCurrentOverridesAsBaseline()
        {
            _baselineArtOverrideValue = GetNormalizedArtOverrideValue();
            _baselineIsSummarySelected = _isSummarySelected;
            NotifyOverrideStateChanged();
        }

        /// <summary>
        /// The art value to write to the store: the current value when valid, otherwise
        /// the last persisted one, so an invalid pending edit never reaches the store.
        /// </summary>
        public string GetPersistableArtOverrideValue()
        {
            return HasArtOverrideValidationError
                ? NormalizeOverrideValue(_baselineArtOverrideValue)
                : GetNormalizedArtOverrideValue();
        }

        public string GetNormalizedArtOverrideValue()
        {
            return NormalizeOverrideValue(ArtOverrideValue);
        }

        public string GetNormalizedRenameOverrideValue()
        {
            var normalized = (RenameOverrideText ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        public void ResetRenameOverrideTextFromCurrentCategory()
        {
            RenameOverrideText = string.Equals(CategoryLabel, ProviderCategoryLabel, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : CategoryLabel;
        }

        private string GetDisplayOverrideValue()
        {
            return _managedCustomIconService.GetManagedDisplayPath(_artOverrideValue, _gameIdText) ?? string.Empty;
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

        private string ResolvePreviewOverrideValue(string value)
        {
            var normalized = NormalizeOverrideValue(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            if (Path.IsPathRooted(normalized) || IsHttpUrl(normalized) || Uri.TryCreate(normalized, UriKind.Absolute, out _))
            {
                return normalized;
            }

            return normalized;
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

            return IsManagedLocalOverride(normalized) || IsExistingRootedPath(normalized);
        }

        private bool IsManagedLocalOverride(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   Path.IsPathRooted(value) &&
                   File.Exists(value) &&
                   _managedCustomIconService.IsManagedCustomIconPath(value, _gameIdText);
        }

        private static bool IsExistingRootedPath(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   Path.IsPathRooted(value) &&
                   File.Exists(value);
        }

        private void NotifyOverrideStateChanged()
        {
            OnPropertyChanged(nameof(ArtOverrideValue));
            OnPropertyChanged(nameof(ArtOverrideText));
            OnPropertyChanged(nameof(HasArtOverrideValidationError));
            OnPropertyChanged(nameof(ArtPreviewPath));
            OnPropertyChanged(nameof(HasValidationErrors));
            OnPropertyChanged(nameof(HasChanges));
        }

        private static string ResolveSharedImage(
            IEnumerable<ManageAchievementsCategoryItem> source,
            Func<ManageAchievementsCategoryItem, string> selector)
        {
            string image = null;
            foreach (var item in source ?? Enumerable.Empty<ManageAchievementsCategoryItem>())
            {
                var candidate = selector?.Invoke(item);
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                if (image == null)
                {
                    image = candidate;
                }
                else if (!string.Equals(image, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            return image;
        }

        private static string BuildPreviewPath(string value)
        {
            var normalized = NormalizeOverrideValue(value);
            return string.IsNullOrWhiteSpace(normalized)
                ? AchievementIconResolver.GetDefaultIcon()
                : AchievementIconResolver.ApplyCacheBust(normalized);
        }

        private static string NormalizeOverrideValue(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static bool IsHttpUrl(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }
    }

    public sealed class CategoryTypeSelectionOption : ObservableObject
    {
        private bool _isSelected;

        public CategoryTypeSelectionOption(string value, string displayName)
        {
            Value = value;
            DisplayName = displayName;
        }

        public string Value { get; }

        public string DisplayName { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetValue(ref _isSelected, value);
        }
    }
}
