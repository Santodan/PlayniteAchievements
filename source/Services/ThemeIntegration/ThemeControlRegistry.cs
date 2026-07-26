using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Controls;

namespace PlayniteAchievements.Services.ThemeIntegration
{
    internal sealed class ThemeControlRegistry
    {
        private readonly Dictionary<string, Func<Control>> _legacyFactories =
            new Dictionary<string, Func<Control>>(StringComparer.OrdinalIgnoreCase)
            {
                { "PluginButton", () => new Views.ThemeIntegration.Legacy.PluginButtonControl() },
                { "PluginProgressBar", () => new Views.ThemeIntegration.Legacy.PluginProgressBarControl() },
                { "PluginCompactList", () => new Views.ThemeIntegration.Legacy.PluginCompactListControl() },
                { "PluginCompactLocked", () => new Views.ThemeIntegration.Legacy.PluginCompactLockedControl() },
                { "PluginCompactUnlocked", () => new Views.ThemeIntegration.Legacy.PluginCompactUnlockedControl() },
                { "PluginChart", () => new Views.ThemeIntegration.Legacy.PluginChartControl() },
                { "PluginUserStats", () => new Views.ThemeIntegration.Legacy.PluginUserStatsControl() },
                { "PluginList", () => new Views.ThemeIntegration.Legacy.PluginListControl() },
                { "PluginViewItem", () => new Views.ThemeIntegration.Legacy.PluginViewItemControl() }
            };

        private readonly Dictionary<string, Func<Control>> _modernFactories =
            new Dictionary<string, Func<Control>>(StringComparer.OrdinalIgnoreCase)
            {
                { "AchievementButton", () => new Views.ThemeIntegration.Modern.AchievementButtonControl() },
                { "AchievementProgressBar", () => new Views.ThemeIntegration.Modern.AchievementProgressBarControl() },
                { "AchievementCompactList", () => new Views.ThemeIntegration.Modern.AchievementCompactListControl() },
                { "AchievementCompactLockedList", () => new Views.ThemeIntegration.Modern.AchievementCompactLockedListControl() },
                { "AchievementCompactUnlockedList", () => new Views.ThemeIntegration.Modern.AchievementCompactUnlockedListControl() },
                { "AchievementChart", () => new Views.ThemeIntegration.Modern.AchievementBarChartControl() },
                { "AchievementBarChart", () => new Views.ThemeIntegration.Modern.AchievementBarChartControl() },
                { "AchievementPieChart", () => new Views.ThemeIntegration.Modern.AchievementPieChartControl() },
                { "AchievementStats", () => new Views.ThemeIntegration.Modern.AchievementStatsControl() },
                { "AchievementDataGrid", () => new Views.ThemeIntegration.Modern.AchievementDataGridControl() },
                { "AchievementFriendSummariesGrid", () => new Views.ThemeIntegration.Modern.AchievementFriendSummariesGridControl() },
                { "AchievementFriendGameSummariesGrid", () => new Views.ThemeIntegration.Modern.AchievementFriendGameSummariesGridControl() },
                { "AchievementFriendAchievementsGrid", () => new Views.ThemeIntegration.Modern.AchievementFriendAchievementsGridControl() },
                { "AchievementToast", () => new Views.ThemeIntegration.Modern.AchievementToastControl() },
                { "AchievementViewItem", () => new Views.ThemeIntegration.Modern.AchievementViewItemControl() }
            };

        private static readonly string[] _supportedElements =
        {
            // SuccessStory-compatible controls (legacy naming; properties are also exposed via modern keys)
            "PluginButton",
            "PluginProgressBar",
            "PluginCompactList",
            "PluginCompactLocked",
            "PluginCompactUnlocked",
            "PluginChart",
            "PluginUserStats",
            "PluginList",
            "PluginViewItem",

            // Modern PlayniteAchievements controls (always available)
            "AchievementButton",
            "AchievementProgressBar",
            "AchievementCompactList",
            "AchievementCompactLockedList",
            "AchievementCompactUnlockedList",
            "AchievementBarChart",
            "AchievementPieChart",
            "AchievementStats",
            "AchievementDataGrid",
            "AchievementFriendSummariesGrid",
            "AchievementFriendGameSummariesGrid",
            "AchievementFriendAchievementsGrid",
            "AchievementToast",
            "AchievementViewItem"
        };

        public List<string> GetSupportedElementNames()
        {
            return _supportedElements.ToList();
        }

        public bool TryCreate(string controlName, out Control control)
        {
            control = null;

            if (string.IsNullOrWhiteSpace(controlName))
            {
                return false;
            }

            if (_legacyFactories.TryGetValue(controlName, out var legacyFactory))
            {
                control = legacyFactory();
                return true;
            }

            if (_modernFactories.TryGetValue(controlName, out var modernFactory))
            {
                control = modernFactory();
                return true;
            }

            return false;
        }
    }
}
