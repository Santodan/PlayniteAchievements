using System;
using System.Collections.Generic;
using Playnite.SDK;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.ViewModels.Items;

using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// State and helpers shared by the two Compare dropdown controllers: tracking the display
    /// items a comparison was applied to, raising the dropdown's bindings, picking the compare
    /// side's row per achievement, and mapping the signed-in accounts by provider.
    /// </summary>
    /// <typeparam name="TItem">The display item type the comparison is applied onto.</typeparam>
    public abstract class FriendCompareControllerBase<TItem> : ObservableObject
        where TItem : AchievementDisplayItem
    {
        private protected readonly List<TItem> AppliedItems = new List<TItem>();

        private protected FriendCompareControllerBase()
        {
        }

        // Separates the option key from the display name in a saved pick; neither holds a tab.
        private const char SavedNameSeparator = '\t';

        private string _rememberedKey;
        private string _rememberedName;
        private Action<string> _saveSelection;

        /// <summary>
        /// Remembers the entry picked in the dropdown: restores <paramref name="saved"/> (a state
        /// string from an earlier <paramref name="save"/>, or null) whenever that entry is on
        /// offer, and hands <paramref name="save"/> the new state each time the user picks or
        /// clears an entry. A game or friend change that drops the selection leaves the
        /// remembered entry in place, and while it is not on offer the dropdown still shows it
        /// as its selection, by the name saved with it.
        /// </summary>
        public void PersistSelection(string saved, Action<string> save)
        {
            _rememberedKey = null;
            _rememberedName = null;
            if (!string.IsNullOrWhiteSpace(saved))
            {
                var split = saved.IndexOf(SavedNameSeparator);
                _rememberedKey = split < 0 ? saved : saved.Substring(0, split);
                _rememberedName = split < 0 ? null : saved.Substring(split + 1);
                if (string.IsNullOrWhiteSpace(_rememberedKey))
                {
                    _rememberedKey = null;
                    _rememberedName = null;
                }
            }

            _saveSelection = save;
            if (!TryRestoreRememberedSelection())
            {
                NotifyCompareStateChanged();
            }
        }

        /// <summary>The entry to select again once it is offered, or null.</summary>
        private protected string RememberedKey => _rememberedKey;

        /// <summary>The name the remembered entry was saved with, or null.</summary>
        private protected string RememberedName =>
            string.IsNullOrWhiteSpace(_rememberedName) ? null : _rememberedName;

        private protected bool IsRememberedKey(string key)
        {
            return _rememberedKey != null &&
                   string.Equals(_rememberedKey, key, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Records the user's own pick with the name it shows under; a null key records that
        /// they cleared it.
        /// </summary>
        private protected void Remember(string key, string displayName)
        {
            if (_saveSelection == null ||
                (string.Equals(_rememberedKey, key, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(_rememberedName, displayName, StringComparison.Ordinal)))
            {
                return;
            }

            _rememberedKey = key;
            _rememberedName = key == null ? null : displayName;
            _saveSelection(key == null
                ? null
                : string.IsNullOrWhiteSpace(displayName)
                    ? key
                    : key + SavedNameSeparator + displayName.Replace(SavedNameSeparator, ' '));
        }

        /// <summary>
        /// Selects and applies the remembered entry when nothing is selected and it is on offer.
        /// Returns whether it did.
        /// </summary>
        private protected abstract bool TryRestoreRememberedSelection();

        private protected void ClearApplied()
        {
            if (AppliedItems.Count == 0)
            {
                return;
            }

            foreach (var item in AppliedItems)
            {
                item?.ClearComparison();
            }

            AppliedItems.Clear();
        }

        private protected void NotifyCompareStateChanged()
        {
            OnPropertyChanged(nameof(IGridCompareSource.CompareSelectionText));
            OnPropertyChanged(nameof(IGridCompareSource.IsCompareAvailable));
        }

        /// <summary>
        /// Indexes the compare side's rows by ApiName. A friend can carry more than one row per
        /// achievement (merged accounts); an unlocked row wins so the comparison reflects their
        /// best state. Rows without an ApiName are skipped. <paramref name="keyOf"/> replaces the
        /// ApiName as the index key for rows that span more than one game.
        /// </summary>
        private protected static Dictionary<string, FriendAchievementDisplayItem> BuildBestRowsByApiName(
            IEnumerable<FriendAchievementDisplayItem> rows,
            Func<FriendAchievementDisplayItem, bool> isCompareSideRow,
            Func<FriendAchievementDisplayItem, string> keyOf = null)
        {
            var compareRows = new Dictionary<string, FriendAchievementDisplayItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (row == null ||
                    string.IsNullOrWhiteSpace(row.ApiName) ||
                    !isCompareSideRow(row))
                {
                    continue;
                }

                var key = keyOf != null ? keyOf(row) : row.ApiName;
                if (!compareRows.TryGetValue(key, out var existing) ||
                    (row.Unlocked && !existing.Unlocked))
                {
                    compareRows[key] = row;
                }
            }

            return compareRows;
        }

        /// <summary>
        /// Loads the signed-in account per provider. A failed load is logged and yields the
        /// accounts mapped before the failure.
        /// </summary>
        private protected static Dictionary<string, FriendIdentity> LoadIdentitiesByProvider(
            Func<IEnumerable<FriendIdentity>> loadIdentities,
            ILogger logger)
        {
            var map = new Dictionary<string, FriendIdentity>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var identity in loadIdentities() ?? new List<FriendIdentity>())
                {
                    if (identity != null && !string.IsNullOrWhiteSpace(identity.ProviderKey))
                    {
                        map[identity.ProviderKey] = identity;
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "Failed to load current user identities for compare.");
            }

            return map;
        }
    }
}
