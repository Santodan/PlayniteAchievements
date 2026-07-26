using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Search
{
    public static class SearchTextBuilder
    {
        public static string FromValues(params string[] values)
        {
            return FromValues((IEnumerable<string>)values);
        }

        public static string FromValues(IEnumerable<string> values)
        {
            return string.Join("\n", (values ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrEmpty(value)));
        }

        public static string ForAchievement(string displayName, string description)
        {
            return FromValues(displayName, description);
        }

        public static string ForAchievementWithGame(string gameName, string displayName, string description)
        {
            return FromValues(gameName, displayName, description);
        }

        public static string ForGameSummary(string gameName)
        {
            return FromValues(gameName);
        }

        public static string ForRecentAchievement(string gameName, string achievementName)
        {
            return FromValues(gameName, achievementName);
        }

        public static string ForManageCategory(
            string displayName,
            string description,
            string apiName,
            string category,
            string categoryTypeDisplay)
        {
            return FromValues(displayName, description, apiName, category, categoryTypeDisplay);
        }

        public static string ForManageFilter(
            string displayName,
            string description,
            string apiName,
            string categoryDisplay,
            string categoryTypeDisplay)
        {
            return FromValues(displayName, description, apiName, categoryDisplay, categoryTypeDisplay);
        }

        public static string ForManageNote(
            string displayName,
            string description,
            string apiName,
            string notePreview,
            string categoryDisplay,
            string categoryTypeDisplay)
        {
            return FromValues(displayName, description, apiName, notePreview, categoryDisplay, categoryTypeDisplay);
        }

        public static string ForCapstone(string displayName, string description)
        {
            return FromValues(displayName, description);
        }

        public static string ForManualEdit(string displayName, string description, string apiName)
        {
            return FromValues(displayName, description, apiName);
        }
    }
}
