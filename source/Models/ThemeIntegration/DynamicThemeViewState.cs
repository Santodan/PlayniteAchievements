using System;

namespace PlayniteAchievements.Models.ThemeIntegration
{
    internal static class DynamicThemeViewKeys
    {
        public const string All = "All";
        public const string Unlocked = "Unlocked";
        public const string Locked = "Locked";
        public const string Visible = "Visible";
        public const string Hidden = "Hidden";
        public const string InProgress = "InProgress";
        public const string NoProgress = "NoProgress";
        public const string HasNotes = "HasNotes";
        public const string NoNotes = "NoNotes";
        public const string Capstone = "Capstone";
        public const string Common = "Common";
        public const string Uncommon = "Uncommon";
        public const string Rare = "Rare";
        public const string UltraRare = "UltraRare";
        public const string Platinum = "Platinum";
        public const string Gold = "Gold";
        public const string Silver = "Silver";
        public const string Bronze = "Bronze";
        public const string Completed = "Completed";
        public const string Incomplete = "Incomplete";
        public const string Started = "Started";
        public const string NotStarted = "NotStarted";
        public const string Played = "Played";
        public const string Unplayed = "Unplayed";
        public const string HasLastUnlock = "HasLastUnlock";
        public const string NoLastUnlock = "NoLastUnlock";
        public const string Default = "Default";
        public const string Name = "Name";
        public const string Game = "Game";
        public const string Provider = "Provider";
        public const string Progress = "Progress";
        public const string AchievementCount = "AchievementCount";
        public const string SharedGamesCount = "SharedGamesCount";
        public const string Status = "Status";
        public const string RarityPercent = "RarityPercent";
        public const string Points = "Points";
        public const string CollectionScore = "CollectionScore";
        public const string PrestigeScore = "PrestigeScore";
        public const string TrophyType = "TrophyType";
        public const string CategoryType = "CategoryType";
        public const string CategoryLabel = "CategoryLabel";
        public const string Notes = "Notes";
        public const string UnlockTime = "UnlockTime";
        public const string Rarity = "Rarity";
        public const string LastUnlock = "LastUnlock";
        public const string LastPlayed = "LastPlayed";
        public const string UnlockedCount = "UnlockedCount";
        public const string Ascending = "Ascending";
        public const string Descending = "Descending";
    }

    internal sealed class DynamicThemeListSelection
    {
        public DynamicThemeListSelection(
            string providerKey,
            string filterKey,
            string sortKey,
            string sortDirectionKey)
        {
            ProviderKey = providerKey;
            FilterKey = filterKey;
            SortKey = sortKey;
            SortDirectionKey = sortDirectionKey;
        }

        public string ProviderKey { get; set; }

        public string FilterKey { get; set; }

        public string SortKey { get; set; }

        public string SortDirectionKey { get; set; }

        public bool Apply(
            string providerKey,
            string filterKey,
            string sortKey,
            string sortDirectionKey)
        {
            var changed = false;
            changed |= SetIfChanged(ProviderKey, providerKey, value => ProviderKey = value);
            changed |= SetIfChanged(FilterKey, filterKey, value => FilterKey = value);
            changed |= SetIfChanged(SortKey, sortKey, value => SortKey = value);
            changed |= SetIfChanged(SortDirectionKey, sortDirectionKey, value => SortDirectionKey = value);
            return changed;
        }

        public bool Apply(DynamicThemeListSelection selection)
        {
            if (selection == null)
            {
                return false;
            }

            return Apply(
                selection.ProviderKey,
                selection.FilterKey,
                selection.SortKey,
                selection.SortDirectionKey);
        }

        private static bool SetIfChanged(
            string currentValue,
            string nextValue,
            Action<string> setValue)
        {
            if (string.Equals(currentValue ?? string.Empty, nextValue ?? string.Empty, StringComparison.Ordinal))
            {
                return false;
            }

            setValue(nextValue);
            return true;
        }
    }

    internal abstract class DynamicThemeListViewState
    {
        protected DynamicThemeListViewState(
            string providerKey,
            string filterKey,
            string sortKey,
            string sortDirectionKey)
        {
            Current = new DynamicThemeListSelection(providerKey, filterKey, sortKey, sortDirectionKey);
            Default = new DynamicThemeListSelection(providerKey, filterKey, sortKey, sortDirectionKey);
        }

        private DynamicThemeListSelection Current { get; }

        private DynamicThemeListSelection Default { get; }

        public bool HasUserSelection { get; set; }

        public string ProviderKey
        {
            get => Current.ProviderKey;
            set => Current.ProviderKey = value;
        }

        public string FilterKey
        {
            get => Current.FilterKey;
            set => Current.FilterKey = value;
        }

        public string SortKey
        {
            get => Current.SortKey;
            set => Current.SortKey = value;
        }

        public string SortDirectionKey
        {
            get => Current.SortDirectionKey;
            set => Current.SortDirectionKey = value;
        }

        public string DefaultProviderKey => Default.ProviderKey;

        public string DefaultFilterKey => Default.FilterKey;

        public string DefaultSortKey => Default.SortKey;

        public string DefaultSortDirectionKey => Default.SortDirectionKey;

        public bool ApplyDefaults(
            string providerKey,
            string filterKey,
            string sortKey,
            string sortDirectionKey)
        {
            var changed = Default.Apply(providerKey, filterKey, sortKey, sortDirectionKey);
            if (!HasUserSelection)
            {
                changed |= Current.Apply(Default);
            }

            return changed;
        }

        public bool ResetToDefault()
        {
            HasUserSelection = false;
            return Current.Apply(Default);
        }
    }

    internal sealed class SelectedGameAchievementViewState : DynamicThemeListViewState
    {
        private string _gameKey = string.Empty;
        private string _gameLabel = string.Empty;
        private string _categoryLabelKey = DynamicThemeViewKeys.All;

        public SelectedGameAchievementViewState()
            : base(
                DynamicThemeViewKeys.All,
                DynamicThemeViewKeys.All,
                DynamicThemeViewKeys.UnlockTime,
                DynamicThemeViewKeys.Descending)
        {
        }

        public string GameKey
        {
            get => _gameKey;
            set => _gameKey = value ?? string.Empty;
        }

        public string GameLabel
        {
            get => _gameLabel;
            set => _gameLabel = value ?? string.Empty;
        }

        // Category labels are per-game and user-renamable, so this scope lives outside the
        // Current/Default selection quartet (like GameKey) and resets when the selected
        // game no longer contains the label.
        public string CategoryLabelKey
        {
            get => _categoryLabelKey;
            set => _categoryLabelKey = string.IsNullOrWhiteSpace(value) ? DynamicThemeViewKeys.All : value;
        }

        public void ResetCategoryScope()
        {
            CategoryLabelKey = DynamicThemeViewKeys.All;
        }
    }

    internal sealed class LibraryAchievementViewState : DynamicThemeListViewState
    {
        private string _gameKey = DynamicThemeViewKeys.All;
        private string _gameLabel = DynamicThemeViewKeys.All;

        public LibraryAchievementViewState()
            : base(
                DynamicThemeViewKeys.All,
                DynamicThemeViewKeys.All,
                DynamicThemeViewKeys.UnlockTime,
                DynamicThemeViewKeys.Descending)
        {
        }

        public string GameKey
        {
            get => _gameKey;
            set => _gameKey = string.IsNullOrWhiteSpace(value) ? DynamicThemeViewKeys.All : value;
        }

        public string GameLabel
        {
            get => _gameLabel;
            set => _gameLabel = string.IsNullOrWhiteSpace(value) ? DynamicThemeViewKeys.All : value;
        }

        public void ResetGameScope()
        {
            GameKey = DynamicThemeViewKeys.All;
            GameLabel = DynamicThemeViewKeys.All;
        }
    }

    // Per-category rollup of the selected game's achievements. Default sort preserves the
    // builder's order, which already honors the game's custom category order.
    internal sealed class CategorySummaryViewState : DynamicThemeListViewState
    {
        public CategorySummaryViewState()
            : base(
                DynamicThemeViewKeys.All,
                DynamicThemeViewKeys.All,
                DynamicThemeViewKeys.Default,
                DynamicThemeViewKeys.Descending)
        {
        }
    }

    internal sealed class GameSummaryViewState : DynamicThemeListViewState
    {
        private string _gameKey = DynamicThemeViewKeys.All;
        private string _gameLabel = DynamicThemeViewKeys.All;

        public GameSummaryViewState()
            : base(
                DynamicThemeViewKeys.All,
                DynamicThemeViewKeys.All,
                DynamicThemeViewKeys.LastUnlock,
                DynamicThemeViewKeys.Descending)
        {
        }

        public string GameKey
        {
            get => _gameKey;
            set => _gameKey = string.IsNullOrWhiteSpace(value) ? DynamicThemeViewKeys.All : value;
        }

        public string GameLabel
        {
            get => _gameLabel;
            set => _gameLabel = string.IsNullOrWhiteSpace(value) ? DynamicThemeViewKeys.All : value;
        }

        public void ResetGameScope()
        {
            GameKey = DynamicThemeViewKeys.All;
            GameLabel = DynamicThemeViewKeys.All;
        }
    }

    internal sealed class FriendScopeViewState
    {
        private string _providerKey = DynamicThemeViewKeys.All;
        private string _userKey = DynamicThemeViewKeys.All;
        private string _gameKey = DynamicThemeViewKeys.All;

        public string ProviderKey
        {
            get => _providerKey;
            set => _providerKey = string.IsNullOrWhiteSpace(value) ? DynamicThemeViewKeys.All : value;
        }

        public string UserKey
        {
            get => _userKey;
            set => _userKey = string.IsNullOrWhiteSpace(value) ? DynamicThemeViewKeys.All : value;
        }

        public string GameKey
        {
            get => _gameKey;
            set => _gameKey = string.IsNullOrWhiteSpace(value) ? DynamicThemeViewKeys.All : value;
        }

        public void Reset()
        {
            ProviderKey = DynamicThemeViewKeys.All;
            UserKey = DynamicThemeViewKeys.All;
            GameKey = DynamicThemeViewKeys.All;
        }
    }

    internal sealed class FriendSummaryViewState : DynamicThemeListViewState
    {
        public FriendSummaryViewState()
            : base(
                DynamicThemeViewKeys.All,
                DynamicThemeViewKeys.All,
                DynamicThemeViewKeys.LastUnlock,
                DynamicThemeViewKeys.Descending)
        {
        }
    }

    internal sealed class FriendGameSummaryViewState : DynamicThemeListViewState
    {
        public FriendGameSummaryViewState()
            : base(
                DynamicThemeViewKeys.All,
                DynamicThemeViewKeys.All,
                DynamicThemeViewKeys.LastUnlock,
                DynamicThemeViewKeys.Descending)
        {
        }
    }

    internal sealed class FriendAchievementViewState : DynamicThemeListViewState
    {
        public FriendAchievementViewState()
            : base(
                DynamicThemeViewKeys.All,
                DynamicThemeViewKeys.All,
                DynamicThemeViewKeys.UnlockTime,
                DynamicThemeViewKeys.Descending)
        {
        }
    }
}
