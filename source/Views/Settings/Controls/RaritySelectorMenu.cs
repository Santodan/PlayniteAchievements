using System;
using System.Collections.Generic;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Views.Settings.Controls
{
    /// <summary>
    /// The checkable rarity-tier multi-select shared by every setting that takes a
    /// <see cref="RaritySelection"/>: a summary button that drops a menu of tier toggles. Extracted so
    /// the capture settings and the glow settings present the same control rather than each carrying
    /// its own copy.
    ///
    /// Each toggle reads the current selection fresh instead of capturing it, because the menu stays
    /// open across clicks and a captured value would go stale after the first toggle.
    /// </summary>
    internal static class RaritySelectorMenu
    {
        /// <summary>
        /// Rarity tiers in ascending order, paired with their display-label keys. Drives both the
        /// menus and the summary text, so the two can never list tiers differently.
        /// </summary>
        internal static readonly (RarityTier Tier, string LabelKey)[] Options =
        {
            (RarityTier.Common, "LOCPlayAch_Rarity_Common"),
            (RarityTier.Uncommon, "LOCPlayAch_Rarity_Uncommon"),
            (RarityTier.Rare, "LOCPlayAch_Rarity_Rare"),
            (RarityTier.UltraRare, "LOCPlayAch_Rarity_UltraRare")
        };

        /// <summary>
        /// Builds and opens the tier menu under <paramref name="button"/>, using that button's own
        /// ContextMenu so the caller keeps control of its placement and styling.
        /// <paramref name="onChanged"/> runs after each toggle, for refreshing summary text.
        /// </summary>
        public static void Open(
            Button button,
            Func<RaritySelection> get,
            Action<RaritySelection> set,
            Action onChanged = null,
            bool includeCompleted = false)
        {
            var menu = button?.ContextMenu;
            if (menu == null || get == null || set == null)
            {
                return;
            }

            menu.Items.Clear();
            foreach (var option in Options)
            {
                var flag = option.Tier.ToFlag();
                menu.Items.Add(CreateMenuItem(
                    button,
                    Localize(option.LabelKey),
                    get().Contains(option.Tier),
                    isChecked =>
                    {
                        var current = get();
                        set(isChecked ? current | flag : current & ~flag);
                        onChanged?.Invoke();
                    }));
            }

            if (includeCompleted)
            {
                // Listed last, after the tiers, because it is not one of them.
                menu.Items.Add(CreateMenuItem(
                    button,
                    Localize("LOCPlayAch_Completed"),
                    get().IncludesCompleted(),
                    isChecked =>
                    {
                        var current = get();
                        set(isChecked
                            ? current | RaritySelection.Completed
                            : current & ~RaritySelection.Completed);
                        onChanged?.Invoke();
                    }));
            }

            OpenContextMenu(button, menu);
        }

        /// <summary>
        /// Summary text for a selection: All, None, or the selected tiers in order.
        /// </summary>
        public static string Format(
            RaritySelection selection,
            bool includeCompleted = false)
        {
            var offered = RaritySelection.All;
            if (includeCompleted)
            {
                offered |= RaritySelection.Completed;
            }

            if ((selection & offered) == offered)
            {
                return Localize("LOCPlayAch_Common_All");
            }

            if ((selection & offered) == RaritySelection.None)
            {
                return Localize("LOCPlayAch_Common_None");
            }

            var labels = new List<string>();
            foreach (var option in Options)
            {
                if (selection.Contains(option.Tier))
                {
                    labels.Add(Localize(option.LabelKey));
                }
            }

            if (includeCompleted && selection.IncludesCompleted())
            {
                labels.Add(Localize("LOCPlayAch_Completed"));
            }

            return labels.Count > 0 ? string.Join(", ", labels) : Localize("LOCPlayAch_Common_None");
        }

        private static MenuItem CreateMenuItem(Button button, string header, bool isChecked, Action<bool> onToggle)
        {
            return MultiSelectMenu.CreateItem(button, header, isChecked, onToggle);
        }

        private static void OpenContextMenu(Button button, ContextMenu menu)
        {
            MultiSelectMenu.Open(button, menu);
        }

        private static string Localize(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }
}
