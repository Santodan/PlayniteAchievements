using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Checks the name of a new category.
    ///
    /// One check for every surface that creates one - the picker's search and the achievement row
    /// menus' - so the rules a name has to satisfy are stated once. The separator is internal and
    /// rejected here rather than quietly rewritten, matching what the Categories tab does with a
    /// typed rename: nesting is a structural gesture there, not a second syntax.
    /// </summary>
    internal static class CategoryCreationPrompt
    {
        /// <summary>
        /// Returns false when the name is unusable; the message explaining why has already been
        /// shown.
        /// </summary>
        public static bool TryValidate(string name, out string leafName)
        {
            leafName = null;
            var typed = name?.Trim();
            if (string.IsNullOrWhiteSpace(typed))
            {
                return false;
            }

            if (CategoryPathHelper.ContainsSeparator(typed))
            {
                API.Instance?.Dialogs?.ShowMessage(
                    ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Category_PathSeparatorNotAllowed"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }

            leafName = typed;
            return true;
        }
    }
}
