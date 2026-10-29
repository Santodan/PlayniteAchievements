using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Services.Rules;

namespace PlayniteAchievements.Views.Settings.Controls
{
    /// <summary>
    /// The checkable list of Playnite saved filters, in the order the user sorted them in Playnite.
    /// Each toggle reads the current selection fresh, since the menu stays open across clicks.
    /// </summary>
    internal static class FilterPresetSelectorMenu
    {
        public static void Open(
            Button button,
            IPlayniteAPI api,
            Func<HashSet<Guid>> get,
            Action<HashSet<Guid>> set,
            Action onChanged)
        {
            var menu = button?.ContextMenu;
            if (menu == null || get == null || set == null)
            {
                return;
            }

            menu.Items.Clear();
            foreach (var preset in GetPresets(api))
            {
                var presetId = preset.Id;
                var item = MultiSelectMenu.CreateItem(
                    button,
                    preset.Name,
                    get().Contains(presetId),
                    isChecked =>
                    {
                        var selection = new HashSet<Guid>(get());
                        if (isChecked ? selection.Add(presetId) : selection.Remove(presetId))
                        {
                            set(selection);
                            onChanged?.Invoke();
                        }
                    });

                // A filter that only sorts or groups would match the whole library.
                item.IsEnabled = FilterExclusionRuleService.HasConditions(preset.Settings) || item.IsChecked;
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

        /// <summary>The selected filters' names, or None. Deleted filters are left out.</summary>
        public static string Format(IPlayniteAPI api, IEnumerable<Guid> selection)
        {
            var selected = selection != null ? new HashSet<Guid>(selection) : new HashSet<Guid>();
            var names = GetPresets(api)
                .Where(preset => selected.Contains(preset.Id))
                .Select(preset => preset.Name)
                .ToList();
            return names.Count > 0 ? string.Join(", ", names) : ResourceProvider.GetString("LOCPlayAch_Common_None");
        }

        private static IEnumerable<FilterPreset> GetPresets(IPlayniteAPI api)
        {
            IEnumerable<FilterPreset> presets = null;
            try
            {
                presets = api?.MainView?.GetSortedFilterPresets();
            }
            catch (Exception)
            {
                // Fullscreen mode and early startup have no main view; fall back to the database.
            }

            presets = presets ?? api?.Database?.FilterPresets;
            return presets?.Where(preset => preset != null) ?? Enumerable.Empty<FilterPreset>();
        }
    }
}
