using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Globalization;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// The facet a row edit touched, so the editor can persist just that field instead of
    /// rewriting the game's whole customization on every keystroke commit.
    /// </summary>
    internal enum AchievementEditorField
    {
        DisplayName,
        Description,
        Points,
        TrophyType,
        UnlockTime,
        Category,
        CategoryType,
        Note,
        Filtered,
        SummaryFiltered,
        Goal,
        Capstone,
        Rarity
    }

    /// <summary>
    /// One editable achievement row in the merged Manage Achievements editor. Provider-backed and
    /// fully-custom achievements are the same row type, distinguished by <see cref="IsCustomRow"/>,
    /// so they can share one list and be interleaved in the user's order.
    /// </summary>
    /// <remarks>
    /// Unlock status is deliberately not editable. It stays provider-owned so an edit cannot move
    /// unlocked counts or completion, or look like a real unlock to the in-game monitor; only the
    /// timestamp of an already-unlocked achievement can be corrected. Rarity is editable only on a
    /// custom row, because a provider achievement's rarity is derived from percentages the provider
    /// supplies.
    /// </remarks>
    internal sealed class AchievementEditorRow : AchievementDisplayItem
    {
        private bool _isLoading;
        private string _editDisplayName;
        private string _editDescription;
        private string _editPointsText;
        private string _editTrophyType;
        private string _editUnlockTimeText;
        private string _editCategory;
        private string _editCategoryType;
        private string _editNote;
        private string _editRarity;
        private bool _editIsFiltered;
        private bool _editIsSummaryFiltered;
        private bool _editIsGoal;
        private bool _editIsCapstone;
        private string _validationMessage;

        public AchievementEditorRow(AchievementDetail source, bool isCustomRow)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            SetSource(source, notifyChanges: false);
            IsCustomRow = isCustomRow;
            LoadFromSource();
        }

        /// <summary>Raised once an edit is complete and should be persisted.</summary>
        public event EventHandler<AchievementEditorField> FieldEdited;

        public bool IsCustomRow { get; }

        /// <summary>
        /// Rarity is user input only for achievements the user authored; for provider achievements
        /// it is derived from the provider's unlock percentages.
        /// </summary>
        public bool CanEditRarity => AchievementEditorFieldRules.CanEditRarity(IsCustomRow);

        public string ApiName => Source?.ApiName;

        /// <summary>
        /// Whether a corrected unlock timestamp applies at all. A locked achievement has no unlock
        /// time to correct, and inventing one would read as unlocked downstream.
        /// </summary>
        public bool CanEditUnlockTime => AchievementEditorFieldRules.CanEditUnlockTime(Source?.Unlocked == true);

        public string ValidationMessage
        {
            get => _validationMessage;
            private set => SetValue(ref _validationMessage, value);
        }

        public bool HasValidationError => !string.IsNullOrWhiteSpace(ValidationMessage);

        public string EditDisplayName
        {
            get => _editDisplayName;
            set => SetEditable(ref _editDisplayName, value, AchievementEditorField.DisplayName);
        }

        public string EditDescription
        {
            get => _editDescription;
            set => SetEditable(ref _editDescription, value, AchievementEditorField.Description);
        }

        public string EditPointsText
        {
            get => _editPointsText;
            set => SetEditable(ref _editPointsText, value, AchievementEditorField.Points);
        }

        public string EditTrophyType
        {
            get => _editTrophyType;
            set => SetEditable(ref _editTrophyType, value, AchievementEditorField.TrophyType);
        }

        public string EditUnlockTimeText
        {
            get => _editUnlockTimeText;
            set => SetEditable(ref _editUnlockTimeText, value, AchievementEditorField.UnlockTime);
        }

        public string EditCategory
        {
            get => _editCategory;
            set => SetEditable(ref _editCategory, value, AchievementEditorField.Category);
        }

        public string EditCategoryType
        {
            get => _editCategoryType;
            set => SetEditable(ref _editCategoryType, value, AchievementEditorField.CategoryType);
        }

        public string EditNote
        {
            get => _editNote;
            set => SetEditable(ref _editNote, value, AchievementEditorField.Note);
        }

        public string EditRarity
        {
            get => _editRarity;
            set
            {
                if (!CanEditRarity)
                {
                    return;
                }

                SetEditable(ref _editRarity, value, AchievementEditorField.Rarity);
            }
        }

        public bool EditIsFiltered
        {
            get => _editIsFiltered;
            set => SetEditable(ref _editIsFiltered, value, AchievementEditorField.Filtered);
        }

        public bool EditIsSummaryFiltered
        {
            get => _editIsSummaryFiltered;
            set => SetEditable(ref _editIsSummaryFiltered, value, AchievementEditorField.SummaryFiltered);
        }

        public bool EditIsGoal
        {
            get => _editIsGoal;
            set => SetEditable(ref _editIsGoal, value, AchievementEditorField.Goal);
        }

        public bool EditIsCapstone
        {
            get => _editIsCapstone;
            set => SetEditable(ref _editIsCapstone, value, AchievementEditorField.Capstone);
        }

        /// <summary>
        /// Parsed points, or null when the field is blank. Returns false when the text is present
        /// but not a non-negative integer, so the caller can refuse to persist it.
        /// </summary>
        public bool TryGetPoints(out int? points) =>
            AchievementEditorFieldRules.TryParsePoints(EditPointsText, out points);

        /// <summary>
        /// Parsed unlock timestamp in UTC, or null when blank. Returns false when the text is
        /// present but unparsable.
        /// </summary>
        public bool TryGetUnlockTimeUtc(out DateTime? unlockTimeUtc) =>
            AchievementEditorFieldRules.TryParseUnlockTimeUtc(EditUnlockTimeText, out unlockTimeUtc);

        public void SetValidationMessage(string message)
        {
            ValidationMessage = string.IsNullOrWhiteSpace(message) ? null : message;
            OnPropertyChanged(nameof(HasValidationError));
        }

        /// <summary>
        /// Rereads every editable field from the underlying achievement without raising edit
        /// notifications, so a reload cannot be mistaken for user input and re-persisted.
        /// </summary>
        public void LoadFromSource()
        {
            _isLoading = true;
            try
            {
                var detail = Source;
                EditDisplayName = detail?.DisplayName;
                EditDescription = detail?.Description;
                EditPointsText = detail?.Points?.ToString(CultureInfo.CurrentCulture);
                EditTrophyType = detail?.TrophyType;
                EditUnlockTimeText = AchievementEditorFieldRules.FormatUnlockTimeForEditing(detail?.UnlockTimeUtc);
                EditCategory = detail?.Category;
                EditCategoryType = detail?.CategoryType;
                EditNote = detail?.AchievementNote;
                EditRarity = detail?.RarityText;
                EditIsFiltered = detail?.IsFiltered == true;
                EditIsSummaryFiltered = detail?.IsFilteredFromSummaries == true;
                EditIsGoal = detail?.IsGoal == true;
                EditIsCapstone = detail?.IsCapstone == true;
                ValidationMessage = null;
            }
            finally
            {
                _isLoading = false;
            }

            OnPropertyChanged(nameof(CanEditUnlockTime));
            OnPropertyChanged(nameof(HasValidationError));
        }

        private void SetEditable<T>(ref T field, T value, AchievementEditorField changed)
        {
            if (Equals(field, value))
            {
                return;
            }

            field = value;
            OnPropertyChanged(GetFieldPropertyName(changed));
            if (!_isLoading)
            {
                FieldEdited?.Invoke(this, changed);
            }
        }

        private static string GetFieldPropertyName(AchievementEditorField field)
        {
            switch (field)
            {
                case AchievementEditorField.DisplayName: return nameof(EditDisplayName);
                case AchievementEditorField.Description: return nameof(EditDescription);
                case AchievementEditorField.Points: return nameof(EditPointsText);
                case AchievementEditorField.TrophyType: return nameof(EditTrophyType);
                case AchievementEditorField.UnlockTime: return nameof(EditUnlockTimeText);
                case AchievementEditorField.Category: return nameof(EditCategory);
                case AchievementEditorField.CategoryType: return nameof(EditCategoryType);
                case AchievementEditorField.Note: return nameof(EditNote);
                case AchievementEditorField.Filtered: return nameof(EditIsFiltered);
                case AchievementEditorField.SummaryFiltered: return nameof(EditIsSummaryFiltered);
                case AchievementEditorField.Goal: return nameof(EditIsGoal);
                case AchievementEditorField.Capstone: return nameof(EditIsCapstone);
                case AchievementEditorField.Rarity: return nameof(EditRarity);
                default: return null;
            }
        }
    }
}
