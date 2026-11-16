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

        private string _rememberedKey;
        private Action<string> _saveSelection;

        /// <summary>
        /// Remembers the entry picked in the dropdown: restores <paramref name="savedKey"/> (an
        /// option key, or null) whenever that entry is on offer, and hands <paramref name="save"/>
        /// the key each time the user picks or clears an entry. A game or friend change that
        /// drops the selection leaves the remembered entry in place.
        /// </summary>
        public void PersistSelection(string savedKey, Action<string> save)
        {
            _rememberedKey = string.IsNullOrWhiteSpace(savedKey) ? null : savedKey;
            _saveSelection = save;
            TryRestoreRememberedSelection();
        }

        /// <summary>The entry to select again once it is offered, or null.</summary>
        private protected string RememberedKey => _rememberedKey;

        /// <summary>Records the user's own pick; null records that they cleared it.</summary>
        private protected void Remember(string key)
        {
            if (_saveSelection == null ||
                string.Equals(_rememberedKey, key, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _rememberedKey = key;
            _saveSelection(key);
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
        /// best state. Rows without an ApiName are skipped.
        /// </summary>
        private protected static Dictionary<string, FriendAchievementDisplayItem> BuildBestRowsByApiName(
            IEnumerable<FriendAchievementDisplayItem> rows,
            Func<FriendAchievementDisplayItem, bool> isCompareSideRow)
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

                if (!compareRows.TryGetValue(row.ApiName, out var existing) ||
                    (row.Unlocked && !existing.Unlocked))
                {
                    compareRows[row.ApiName] = row;
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
