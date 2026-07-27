using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;

namespace PlayniteAchievements.Services.ThemeIntegration
{
    internal static class DynamicThemeOptionFactory
    {
        public static ObservableCollection<DynamicThemeOption> CreateOptions(
            IEnumerable<string> keys,
            string selectedKey,
            ICommand applyCommand = null,
            Func<string, object> commandParameterFactory = null)
        {
            var orderedKeys = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var key in keys ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                {
                    orderedKeys.Add(key);
                }
            }

            if (!string.IsNullOrWhiteSpace(selectedKey) && seen.Add(selectedKey))
            {
                orderedKeys.Add(selectedKey);
            }

            return new ObservableCollection<DynamicThemeOption>(orderedKeys
                .Select(key => new DynamicThemeOption(
                    key,
                    DynamicThemeLabels.GetLabel(key, key),
                    isSelected: IsSelected(key, selectedKey),
                    applyCommand: applyCommand,
                    commandParameter: commandParameterFactory?.Invoke(key)))
                .ToList());
        }

        public static ObservableCollection<DynamicThemeOption> CreateProviderOptions(
            IEnumerable<string> providerKeys,
            string selectedKey,
            ICommand applyCommand = null,
            Func<string, object> commandParameterFactory = null)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in providerKeys ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                var trimmed = key.Trim();
                counts.TryGetValue(trimmed, out var count);
                counts[trimmed] = count + 1;
            }

            if (!string.IsNullOrWhiteSpace(selectedKey) &&
                !string.Equals(selectedKey, DynamicThemeViewKeys.All, StringComparison.OrdinalIgnoreCase) &&
                !counts.ContainsKey(selectedKey))
            {
                counts[selectedKey.Trim()] = 0;
            }

            var options = new List<DynamicThemeOption>
            {
                new DynamicThemeOption(
                    DynamicThemeViewKeys.All,
                    DynamicThemeLabels.GetProviderLabel(DynamicThemeViewKeys.All),
                    counts.Values.Sum(),
                    IsSelected(DynamicThemeViewKeys.All, selectedKey),
                    applyCommand,
                    commandParameterFactory?.Invoke(DynamicThemeViewKeys.All))
            };

            options.AddRange(counts.Keys
                .OrderBy(DynamicThemeLabels.GetProviderLabel, StringComparer.CurrentCultureIgnoreCase)
                .Select(key => new DynamicThemeOption(
                    key,
                    DynamicThemeLabels.GetProviderLabel(key),
                    counts[key],
                    IsSelected(key, selectedKey),
                    applyCommand,
                    commandParameterFactory?.Invoke(key))));

            return new ObservableCollection<DynamicThemeOption>(options);
        }

        public static ObservableCollection<DynamicThemeOption> CreateCategoryLabelOptions(
            IEnumerable<AchievementDetail> achievements,
            string selectedKey,
            ICommand applyCommand = null)
        {
            var items = (achievements ?? Enumerable.Empty<AchievementDetail>())
                .Where(item => item != null)
                .ToList();

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var label = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(item.Category);
                counts.TryGetValue(label, out var count);
                counts[label] = count + 1;
            }

            // Labels follow the game's custom category order first (order indexes resolved
            // during presentation), then first-seen order for the remainder.
            var preferredOrder = items
                .Where(item => item.CategoryOrderIndex < int.MaxValue)
                .OrderBy(item => item.CategoryOrderIndex)
                .Select(item => AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(item.Category));
            var orderedLabels = AchievementCategoryFilterOrderHelper.BuildOrderedCategoryLabels(
                items,
                item => item.Category,
                preferredOrder);

            var options = new List<DynamicThemeOption>
            {
                new DynamicThemeOption(
                    DynamicThemeViewKeys.All,
                    DynamicThemeLabels.GetLabel(DynamicThemeViewKeys.All, DynamicThemeViewKeys.All),
                    counts.Values.Sum(),
                    IsSelected(DynamicThemeViewKeys.All, selectedKey),
                    applyCommand)
            };

            options.AddRange(orderedLabels.Select(label => new DynamicThemeOption(
                label,
                AchievementCategoryTypeHelper.ToCategoryLabelDisplayText(label),
                counts.TryGetValue(label, out var count) ? count : 0,
                IsSelected(label, selectedKey),
                applyCommand)));

            return new ObservableCollection<DynamicThemeOption>(options);
        }

        public static ObservableCollection<DynamicThemeOption> CreateGameOptions(
            IEnumerable<GameAchievementSummary> games,
            string selectedKey,
            ICommand applyCommand = null,
            Func<string, object> commandParameterFactory = null)
        {
            var options = (games ?? Enumerable.Empty<GameAchievementSummary>())
                .Where(game => game != null && game.GameId != Guid.Empty)
                .GroupBy(game => game.GameId)
                .Select(group =>
                {
                    var game = group.First();
                    var key = game.GameId.ToString("D");
                    return new
                    {
                        Option = new DynamicThemeOption(
                            key,
                            !string.IsNullOrWhiteSpace(game.Name) ? game.Name : key,
                            game.AchievementCount,
                            IsSelected(key, selectedKey),
                            applyCommand,
                            commandParameterFactory?.Invoke(key)),
                        SortName = string.IsNullOrWhiteSpace(game.SortingName) ? game.Name : game.SortingName
                    };
                })
                .OrderBy(item => item.SortName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.Option.Label, StringComparer.CurrentCultureIgnoreCase)
                .Select(item => item.Option)
                .ToList();

            if (!string.IsNullOrWhiteSpace(selectedKey) &&
                options.All(option => !string.Equals(option.Key, selectedKey, StringComparison.OrdinalIgnoreCase)))
            {
                options.Add(new DynamicThemeOption(
                    selectedKey,
                    selectedKey,
                    isSelected: true,
                    applyCommand: applyCommand,
                    commandParameter: commandParameterFactory?.Invoke(selectedKey)));
            }

            return new ObservableCollection<DynamicThemeOption>(options);
        }

        private static bool IsSelected(string key, string selectedKey)
        {
            return string.Equals(key ?? string.Empty, selectedKey ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
    }
}
