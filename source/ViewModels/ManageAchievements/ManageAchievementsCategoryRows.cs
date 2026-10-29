using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
        public string ProviderCategory { get; set; }

        public string ProviderTags { get; set; }

        public string ProviderCategoryKind { get; set; }

        public string Category
        {
            get => CategoryLabel;
            set
            {
                CategoryLabel = value;
                OnPropertyChanged(nameof(CategoryDisplay));
            }
        }

        public string CategoryDisplay => AchievementCategoryHelper.ToCategoryLeafDisplayText(Category);

        /// <summary>Full path for the category cell's tooltip; the cell itself shows the leaf.</summary>
        public string CategoryPathDisplay => AchievementCategoryHelper.ToCategoryLabelDisplayText(Category);
    }

    public sealed class ManageAchievementsCategoryMetadataItem : ObservableObject
    {
        private readonly string _gameIdText;
        private readonly ManagedCustomIconService _managedCustomIconService;
        private string _baselineArtOverrideValue;
        private string _artOverrideValue;
        private string _renameText;
        private bool _baselineIsSummarySelected;
        private bool _isSummarySelected;
        private AchievementFilterScope _filterScope;
        private int _memberAchievementCount;

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

        public string CategoryDisplay => AchievementCategoryHelper.ToCategoryLabelDisplayText(CategoryLabel);

        /// <summary>
        /// Depth of this row's category path, 1 for a root. The grid is a flat list, so nesting is
        /// conveyed by the tree connectors the category cell draws rather than by a tree control -
        /// the single metadata writer rebuilds order and art from the rendered rows, which a
        /// virtualizing tree would break.
        /// </summary>
        public int CategoryDepth { get; internal set; } = 1;

        private CategoryTreeShape _treeShape;

        /// <summary>
        /// Connector geometry for this row, or null when the list has no nesting to draw. Stamped
        /// by the view model, which is the only place that knows the rendered order - a row cannot
        /// tell from its own path whether a lane continues past it.
        /// </summary>
        public CategoryTreeShape TreeShape
        {
            get => _treeShape;
            internal set => SetValue(ref _treeShape, value);
        }

        /// <summary>
        /// The last path segment, which is what the row shows and what renaming edits. Ancestry is
        /// conveyed by the connectors, and the full path by the row's tooltip.
        /// </summary>
        public string CategoryLeafDisplay => CategoryPathHelper.ToDisplayLeaf(CategoryLabel);

        /// <summary>Full path, shown on hover where the row itself only has room for the leaf.</summary>
        public string CategoryPathDisplay => CategoryPathHelper.ToDisplayPath(CategoryLabel);

        public bool IsNested => CategoryDepth > 1;

        private bool _canIndent;

        /// <summary>
        /// Whether this row has a preceding sibling to nest under. Stamped by the view model, which
        /// is the only place that knows the rendered order; false disables the indent button rather
        /// than letting the click resolve to nothing.
        /// </summary>
        public bool CanIndent
        {
            get => _canIndent;
            internal set => SetValue(ref _canIndent, value);
        }

        /// <summary>
        /// True for the Default bucket row. It cannot be renamed or merged away because it is
        /// the fallback bucket for achievements without an explicit category.
        /// </summary>
        public bool IsDefaultCategory => string.Equals(
            CategoryLabel,
            AchievementCategoryHelper.DefaultCategoryLabel,
            StringComparison.OrdinalIgnoreCase);

        public string ProviderCategoryLabel { get; private set; }

        public string ProviderCategoryDisplay => AchievementCategoryHelper.ToCategoryLabelDisplayText(ProviderCategoryLabel);

        /// <summary>The leaf the providers named this category, which the rename box reverts to.</summary>
        public string ProviderCategoryLeafDisplay => CategoryPathHelper.ToDisplayLeaf(ProviderCategoryLabel);

        /// <summary>
        /// The rename box's live text: the current leaf until the user edits it. Pushed on every
        /// keystroke so <see cref="IsRenameUntouched"/> tracks the box; nothing persists until the
        /// view commits it on focus loss or Enter.
        /// </summary>
        public string RenameText
        {
            get => _renameText;
            set
            {
                var nextValue = value ?? string.Empty;
                if (SetValueAndReturn(ref _renameText, nextValue))
                {
                    OnPropertyChanged(nameof(IsRenameUntouched));
                }
            }
        }

        /// <summary>
        /// True while the box holds exactly the provider's name, which the row draws dimmed; any
        /// edit, including a case-only one, draws it in full.
        /// </summary>
        public bool IsRenameUntouched => string.Equals(
            (_renameText ?? string.Empty).Trim(),
            ProviderCategoryLeafDisplay,
            StringComparison.Ordinal);

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

        public bool HasArtOverride => !string.IsNullOrWhiteSpace(GetNormalizedArtOverrideValue());

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

        /// <summary>
        /// What this category's own achievements currently agree on, or
        /// <see cref="AchievementFilterScope.Mixed"/> when they disagree. Derived from the members
        /// rather than stored: the column pushes a scope onto them and then reads back what they
        /// hold, so an edit made per achievement elsewhere shows up here as a mixed cell instead of
        /// being contradicted by a remembered category value. Subcategories do not count.
        /// </summary>
        public AchievementFilterScope FilterScope
        {
            get => _filterScope;
            internal set
            {
                if (SetValueAndReturn(ref _filterScope, value))
                {
                    OnPropertyChanged(nameof(FilterScopeDisplayText));
                }
            }
        }

        /// <summary>The scope's name for the Filters cell's button face; blank when mixed.</summary>
        public string FilterScopeDisplayText => AchievementFilterScopes.GetDisplayText(_filterScope);

        /// <summary>
        /// How many achievements a push from this row would reach: the ones carrying this exact
        /// label. Stamped alongside <see cref="FilterScope"/> from the same index the push uses.
        /// </summary>
        public int MemberAchievementCount
        {
            get => _memberAchievementCount;
            internal set
            {
                if (SetValueAndReturn(ref _memberAchievementCount, value))
                {
                    OnPropertyChanged(nameof(CanEditFilterScope));
                }
            }
        }

        /// <summary>False for a category holding no achievements of its own, so there is nothing to push to.</summary>
        public bool CanEditFilterScope => _memberAchievementCount > 0;

        private string _storedKind;
        private string _kind;

        /// <summary>
        /// The kind the user set on this category: a kind, <see cref="CategoryKindHelper.None"/>, or
        /// null when the providers decide.
        /// </summary>
        public string StoredKind => _storedKind;

        /// <summary>The category's kind: the stored one, else what its achievements all agree on.</summary>
        public string Kind => _kind;

        public bool IsKindStored => _storedKind != null;

        /// <summary>The Kind cell's face: the kind's name, None when set to none, blank otherwise.</summary>
        public string KindDisplayText =>
            string.Equals(_storedKind, CategoryKindHelper.None, StringComparison.OrdinalIgnoreCase)
                ? ResourceProvider.GetString("LOCNone")
                : CategoryKindHelper.ToDisplayText(_kind);

        internal void SetKind(string storedKind, string kind)
        {
            var stored = CategoryKindHelper.NormalizeStored(storedKind);
            var normalized = CategoryKindHelper.Normalize(kind);
            if (string.Equals(_storedKind, stored, StringComparison.Ordinal) &&
                string.Equals(_kind, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _storedKind = stored;
            _kind = normalized;
            OnPropertyChanged(nameof(StoredKind));
            OnPropertyChanged(nameof(Kind));
            OnPropertyChanged(nameof(IsKindStored));
            OnPropertyChanged(nameof(KindDisplayText));
        }

        /// <summary>
        /// Stamps the scope this row's own achievements agree on.
        /// <paramref name="summaryEffectiveCount"/> includes the fully filtered, matching how the
        /// two flags are read everywhere else.
        /// </summary>
        public void SetFilterScopeFromMembers(int memberTotal, int filteredCount, int summaryEffectiveCount)
        {
            MemberAchievementCount = memberTotal;
            FilterScope = AchievementFilterScopes.FromMemberCounts(
                memberTotal,
                filteredCount,
                summaryEffectiveCount);
        }

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
            var normalizedLabel = AchievementCategoryHelper.NormalizeCategoryOrDefault(categoryLabel);
            var normalizedProviderLabel = AchievementCategoryHelper.NormalizeCategoryOrDefault(providerCategoryLabel);
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

            // Leaf-scoped, the same as after an edit: seeding the whole path would put a separator
            // in a box that rejects one.
            row.ResetRenameTextFromCurrentCategory();
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
            var normalized = (RenameText ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        // Editing is leaf-scoped: the row's indentation already conveys where it sits, and typing a
        // whole path would be a second way to express nesting alongside indent/outdent.
        //
        // Which is why the box holds the leaf, never the path: committing the text a row was seeded
        // with is a no-op, so moving a row cannot read as renaming it.
        public void ResetRenameTextFromCurrentCategory()
        {
            RenameText = CategoryPathHelper.GetLeafName(CategoryLabel);
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
            OnPropertyChanged(nameof(HasArtOverride));
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

    public sealed class TagSelectionOption : ObservableObject
    {
        private bool _isSelected;

        private string _displayName;

        public TagSelectionOption(string value, string displayName)
        {
            Value = value;
            _displayName = displayName;
        }

        public string Value { get; }

        public string DisplayName
        {
            get => _displayName;
            private set => SetValue(ref _displayName, value);
        }

        /// <summary>
        /// Brings <paramref name="options"/> in line with <paramref name="keys"/> after the tag lists
        /// changed: options for kept keys stay (and keep their selection) under their current names,
        /// new keys get an option passed through <paramref name="attach"/>, removed keys go.
        /// </summary>
        public static void Sync(
            ObservableCollection<TagSelectionOption> options,
            IReadOnlyList<string> keys,
            Func<string, string> getName,
            Action<TagSelectionOption> attach)
        {
            if (options == null || keys == null)
            {
                return;
            }

            var existing = options.ToDictionary(option => option.Value, StringComparer.OrdinalIgnoreCase);
            var ordered = keys.Select(key =>
            {
                if (existing.TryGetValue(key, out var option))
                {
                    option.DisplayName = getName(key);
                    return option;
                }

                var created = new TagSelectionOption(key, getName(key));
                attach?.Invoke(created);
                return created;
            }).ToList();

            if (ordered.SequenceEqual(options))
            {
                return;
            }

            options.Clear();
            foreach (var option in ordered)
            {
                options.Add(option);
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetValue(ref _isSelected, value);
        }
    }
}
