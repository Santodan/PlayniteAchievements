using PlayniteAchievements.Models.ThemeIntegration;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PlayniteAchievements.Services.ThemeIntegration
{
    internal static class DynamicThemeOptionFactory
    {
        public static ObservableCollection<DynamicThemeOption> CreateOptions(
            IEnumerable<string> keys,
            string selectedKey)
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
                .Select(key => new DynamicThemeOption(key, DynamicThemeLabels.GetLabel(key, key)))
                .ToList());
        }

        public static ObservableCollection<DynamicThemeOption> CreateProviderOptions(
            IEnumerable<string> providerKeys,
            string selectedKey)
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
                    counts.Values.Sum())
            };

            options.AddRange(counts.Keys
                .OrderBy(DynamicThemeLabels.GetProviderLabel, StringComparer.CurrentCultureIgnoreCase)
                .Select(key => new DynamicThemeOption(key, DynamicThemeLabels.GetProviderLabel(key), counts[key])));

            return new ObservableCollection<DynamicThemeOption>(options);
        }

        public static ObservableCollection<DynamicThemeOption> CreateGameOptions(
            IEnumerable<GameAchievementSummary> games,
            string selectedKey)
        {
            var options = (games ?? Enumerable.Empty<GameAchievementSummary>())
                .Where(game => game != null && game.GameId != Guid.Empty)
                .GroupBy(game => game.GameId)
                .Select(group =>
                {
                    var game = group.First();
                    return new DynamicThemeOption(
                        game.GameId.ToString("D"),
                        !string.IsNullOrWhiteSpace(game.Name) ? game.Name : game.GameId.ToString("D"),
                        game.AchievementCount);
                })
                .OrderBy(option => option.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (!string.IsNullOrWhiteSpace(selectedKey) &&
                options.All(option => !string.Equals(option.Key, selectedKey, StringComparison.OrdinalIgnoreCase)))
            {
                options.Add(new DynamicThemeOption(selectedKey, selectedKey));
            }

            return new ObservableCollection<DynamicThemeOption>(options);
        }
    }
}
