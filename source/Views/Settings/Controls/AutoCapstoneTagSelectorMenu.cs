using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Settings.Controls
{
    /// <summary>
    /// The checkable list of global tags an auto capstone kind carries, in the global list's
    /// order and with each tag's icon. Each toggle reads the stored value fresh, since the menu
    /// stays open across clicks. The definitions are the settings session's own list, so a tag
    /// added or renamed on the Tags page before saving already shows.
    /// </summary>
    internal static class AutoCapstoneTagSelectorMenu
    {
        public static void Open(
            Button button,
            IEnumerable<AchievementTagDefinition> definitions,
            Func<string> get,
            Action<string> set,
            Action onChanged)
        {
            var menu = button?.ContextMenu;
            if (menu == null || get == null || set == null)
            {
                return;
            }

            menu.Items.Clear();
            var offered = Offered(definitions);
            var iconKeys = SelectorContextMenuHelper.TagIconKeys(offered.Select(definition => TagControlBarFilter.IconKey(definition)));
            for (var i = 0; i < offered.Count; i++)
            {
                var key = offered[i].Key;
                var label = DisplayName(offered[i]);
                var item = MultiSelectMenu.CreateItem(
                    button,
                    label,
                    AchievementTagHelper.Contains(get(), key),
                    isChecked =>
                    {
                        var current = AchievementTagHelper.Normalize(get());
                        var updated = AchievementTagHelper.WithTag(current, key, isChecked);
                        if (!string.Equals(current, updated, StringComparison.OrdinalIgnoreCase))
                        {
                            set(updated);
                            onChanged?.Invoke();
                        }
                    });
                SelectorContextMenuHelper.SetTagHeader(item, iconKeys, i, label);
                menu.Items.Add(item);
            }

            if (menu.Items.Count == 0)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = ResourceProvider.GetString("LOCPlayAch_Common_None"),
                    IsEnabled = false
                });
            }

            MultiSelectMenu.Open(button, menu);
        }

        /// <summary>The selected tags' names in list order, or None. Tags no longer in the list are left out.</summary>
        public static string Format(IEnumerable<AchievementTagDefinition> definitions, string value)
        {
            var names = Offered(definitions)
                .Where(definition => AchievementTagHelper.Contains(value, definition.Key))
                .Select(DisplayName)
                .ToList();
            return names.Count > 0 ? string.Join(", ", names) : ResourceProvider.GetString("LOCPlayAch_Common_None");
        }

        /// <summary>Every tag the list defines except the derived unlock modes, hidden ones included.</summary>
        private static List<AchievementTagDefinition> Offered(IEnumerable<AchievementTagDefinition> definitions) =>
            (definitions ?? Enumerable.Empty<AchievementTagDefinition>())
                .Where(definition => !string.IsNullOrWhiteSpace(definition?.Key) &&
                                     !AchievementTagHelper.IsDerivedTag(definition.Key))
                .ToList();

        private static string DisplayName(AchievementTagDefinition definition) =>
            !string.IsNullOrWhiteSpace(definition.Name)
                ? definition.Name
                : AchievementTagHelper.GetDefaultName(definition.Key) ?? definition.Key;
    }
}
