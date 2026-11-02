using Playnite.SDK;
using PlayniteAchievements.Services.GameSearch;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Dialogs;
using System.Collections.Generic;
using System.Windows;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Shows the <see cref="GameSearchDialog"/> for one source in a Playnite extension window.
    /// Returns the games chosen, or null when the user cancelled.
    /// </summary>
    internal static class GameSearchWindow
    {
        public static IReadOnlyList<GameSearchResult> Show(
            Window owner,
            IGameSearchSource source,
            string title,
            string query,
            IEnumerable<GameSearchResult> current)
        {
            if (source == null)
            {
                return null;
            }

            var viewModel = new GameSearchViewModel(source, query, current, LogManager.GetLogger());
            var dialog = new GameSearchDialog(viewModel);
            var window = PlayniteUiProvider.CreateExtensionWindow(
                title,
                dialog,
                new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 720,
                    Height = 560
                });

            WindowPlacementPersistenceService.Attach(window, "GameSearch");

            try
            {
                window.Owner = owner ?? API.Instance?.Dialogs?.GetCurrentAppWindow();
            }
            catch
            {
            }

            dialog.RequestClose += (sender, args) => window.Close();
            try
            {
                window.ShowDialog();
            }
            finally
            {
                viewModel.Cleanup();
            }

            return dialog.DialogResult == true ? dialog.Selection : null;
        }
    }
}
