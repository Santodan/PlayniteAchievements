using System;
using Playnite.SDK;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Dialogs
{
    internal static class ScoreInfoDialogPresenter
    {
        public static void Show()
        {
            var dialog = new ScoreInfoDialog();
            var window = PlayniteUiProvider.CreateExtensionWindow(
                L("LOCPlayAch_Score_Info_WindowTitle"),
                dialog,
                new WindowOptions
                {
                    Width = 700,
                    Height = 800,
                    CanBeResizable = true,
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    ShowCloseButton = true
                });

            dialog.RequestClose += (s, args) => window.Close();
            window.ShowDialog();
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }
}
