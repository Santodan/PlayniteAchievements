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

        /// <summary>Raised once an edit is complete and should be persisted.</summary>
        public event EventHandler<AchievementEditorField> FieldEdited;

        /// <summary>
        /// True for an achievement the user authored. Set during construction alongside the
        /// display fields, matching how the other manage rows are built.
        /// </summary>
        public bool IsCustomRow { get; set; }

        /// <summary>
        /// Rarity is user input only for achievements the user authored; for provider achievements
        /// it is derived from the provider's unlock percentages.
        /// </summary>
        public bool CanEditRarity => AchievementEditorFieldRules.CanEditRarity(IsCustomRow);

        /// <summary>
        /// Whether a corrected unlock timestamp applies at all. A locked achievement has no unlock
        /// time to correct, and inventing one would read as unlocked downstream.
        /// </summary>
        public bool CanEditUnlockTime => AchievementEditorFieldRules.CanEditUnlockTime(Unlocked);

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

        /// <summary>
        /// Applies a change without raising <see cref="FieldEdited"/>. Used when the editor itself
        /// adjusts a row to keep an invariant — clearing the previous capstone, say — so the
        /// correction does not come back as another edit to persist.
        /// </summary>
        public void SuppressFieldEdits(Action apply)
        {
            if (apply == null)
            {
                return;
            }

            var previous = _isLoading;
            _isLoading = true;
            try
            {
                apply();
            }
            finally
            {
                _isLoading = previous;
            }
        }

        public void SetValidationMessage(string message)
        {
            ValidationMessage = string.IsNullOrWhiteSpace(message) ? null : message;
            OnPropertyChanged(nameof(HasValidationError));
        }

        /// <summary>
        /// Seeds every editable field from the row's already-projected display values, without
        /// raising edit notifications, so a load or reload cannot be mistaken for user input and
        /// persisted straight back.
        /// </summary>
        /// <remarks>
        /// Call after the display properties have been set. The display values come from hydrated
        /// data, so any existing override is already applied and the editor shows the effective
        /// value rather than the provider's original.
        /// </remarks>
        /// <param name="isFiltered">Hidden from views and counts. Not carried on the display item.</param>
        /// <param name="isSummaryFiltered">Hidden from summaries only. Not carried on the display item.</param>
        public void LoadEditableFields(bool isFiltered, bool isSummaryFiltered)
        {
            _isLoading = true;
            try
            {
                EditDisplayName = DisplayName;
                EditDescription = Description;
                EditPointsText = PointsValue?.ToString(CultureInfo.CurrentCulture);
                EditTrophyType = TrophyType;
                EditUnlockTimeText = AchievementEditorFieldRules.FormatUnlockTimeForEditing(UnlockTimeUtc);
                EditCategory = CategoryLabel;
                EditCategoryType = CategoryType;
                EditNote = AchievementNote;
                // Rarity is shown as its tier name; only a custom row can change it.
                EditRarity = Rarity.ToString();
                EditIsFiltered = isFiltered;
                EditIsSummaryFiltered = isSummaryFiltered;
                EditIsGoal = IsGoal;
                EditIsCapstone = IsCapstone;
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
