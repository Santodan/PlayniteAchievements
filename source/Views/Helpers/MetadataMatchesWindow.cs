using Playnite.SDK;
using PlayniteAchievements.ViewModels.ManageAchievements;
using PlayniteAchievements.Views.Dialogs;
using System.Windows;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Shows the <see cref="MetadataMatchesDialog"/> for one metadata link in a Playnite extension
    /// window. Returns true when the user saved.
    /// </summary>
    internal static class MetadataMatchesWindow
    {
        public static bool Show(Window owner, MetadataMatchesViewModel viewModel, string title)
        {
            var dialog = new MetadataMatchesDialog(viewModel);
            var window = PlayniteUiProvider.CreateExtensionWindow(
                title,
                dialog,
                new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 960,
                    Height = 640
                });

            WindowPlacementPersistenceService.Attach(window, "MetadataMatches");

            try
            {
                window.Owner = owner ?? API.Instance?.Dialogs?.GetCurrentAppWindow();
            }
            catch
            {
            }

            dialog.RequestClose += (sender, args) => window.Close();
            window.ShowDialog();
            return dialog.DialogResult == true;
        }
    }
}
