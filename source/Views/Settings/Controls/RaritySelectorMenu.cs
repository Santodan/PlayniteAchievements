using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        /// <summary>
        /// Tiers offered for the glow settings. Common is left out because no glow is ever drawn for
        /// it — offering it would be a toggle that visibly does nothing.
        /// </summary>
        public const RaritySelection GlowTiers =
            RaritySelection.Uncommon | RaritySelection.Rare | RaritySelection.UltraRare;

        public static void Open(
            Button button,
            Func<RaritySelection> get,
            Action<RaritySelection> set,
            Action onChanged = null,
            bool includeCommon = true)
        {
            var menu = button?.ContextMenu;
            if (menu == null || get == null || set == null)
            {
                return;
            }

            menu.Items.Clear();
            foreach (var option in Options)
            {
                if (!includeCommon && option.Tier == RarityTier.Common)
                {
                    continue;
                }

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

            OpenContextMenu(button, menu);
        }

        /// <summary>
        /// Summary text for a selection: All, None, or the selected tiers in order. When Common is
        /// excluded it is also ignored for the All check, so a glow selection covering every tier it
        /// can reads as All rather than listing three of four.
        /// </summary>
        public static string Format(RaritySelection selection, bool includeCommon = true)
        {
            var offered = includeCommon ? RaritySelection.All : GlowTiers;
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
                if (!includeCommon && option.Tier == RarityTier.Common)
                {
                    continue;
                }

                if (selection.Contains(option.Tier))
                {
                    labels.Add(Localize(option.LabelKey));
                }
            }

            return labels.Count > 0 ? string.Join(", ", labels) : Localize("LOCPlayAch_Common_None");
        }

        private static MenuItem CreateMenuItem(Button button, string header, bool isChecked, Action<bool> onToggle)
        {
            var item = new MenuItem
            {
                Header = header,
                IsCheckable = true,
                StaysOpenOnClick = true,
                IsChecked = isChecked
            };

            if (button?.TryFindResource("AchievementMultiSelectMenuItemStyle") is Style itemStyle)
            {
                item.Style = itemStyle;
            }

            item.Click += (_, __) => onToggle?.Invoke(item.IsChecked);
            return item;
        }

        private static void OpenContextMenu(Button button, ContextMenu menu)
        {
            if (button == null || menu == null || menu.Items.Count == 0)
            {
                return;
            }

            RoutedEventHandler onClosed = null;
            onClosed = (_, __) =>
            {
                menu.Closed -= onClosed;
                button.ReleaseMouseCapture();
            };

            menu.Closed += onClosed;
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
            menu.IsOpen = true;
        }

        private static string Localize(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }
}
