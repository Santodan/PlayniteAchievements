using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Views.Dialogs;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Asks for the name of a new category.
    ///
    /// One prompt for every surface that creates one - the picker's create row and the achievement
    /// row menus - so the rules a name has to satisfy are stated once. The separator is internal and
    /// rejected here rather than quietly rewritten, matching what the Categories tab does with a
    /// typed rename: nesting is a structural gesture there, not a second syntax.
    /// </summary>
    internal static class CategoryCreationPrompt
    {
        /// <summary>
        /// Returns false when the user cancelled or the name was unusable; the message explaining an
        /// unusable one has already been shown.
        /// </summary>
        public static bool TryPrompt(out string leafName)
        {
            leafName = null;

            var inputDialog = new TextInputDialog(
                ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Category_NewCategoryHint"));

            var window = PlayniteUiProvider.CreateExtensionWindow(
                ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Category_NewCategoryName"),
                inputDialog,
                new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    ShowCloseButton = true,
                    CanBeResizable = false,
                    Width = 460,
                    Height = 200
                });

            try
            {
                if (window.Owner == null)
                {
                    window.Owner = API.Instance?.Dialogs?.GetCurrentAppWindow();
                }
            }
            catch
            {
            }

            WindowPlacementPersistenceService.Attach(window, "NewCategory");
            inputDialog.RequestClose += (_, __) => window.Close();
            window.ShowDialog();

            if (inputDialog.DialogResult != true)
            {
                return false;
            }

            var typed = inputDialog.InputText?.Trim();
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
