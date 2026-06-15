using System;
using Playnite.SDK;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views
{
    internal static class ScoreInfoDialogPresenter
    {
        public static void Show()
        {
            var dialog = new ScoreInfoDialog();
            var window = PlayniteUiProvider.CreateExtensionWindow(
                L("LOCPlayAch_Score_Info_WindowTitle", "Collection and Prestige Scores"),
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

        private static string L(string key, string fallback)
        {
            var value = ResourceProvider.GetString(key);
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            if (value.Length > 4 &&
                value.StartsWith("<!", StringComparison.Ordinal) &&
                value.EndsWith("!>", StringComparison.Ordinal))
            {
                return fallback;
            }

            return value;
        }
    }
}
