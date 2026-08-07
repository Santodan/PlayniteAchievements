using Playnite.SDK.Models;
using System;

namespace PlayniteAchievements.Services.Refresh
{
    internal static class NewGameAutoRefreshPolicy
    {
        private const string ManualGamePlaceholderName = "New Game";

        public static bool ShouldDefer(Game game)
        {
            return game != null &&
                   game.PluginId == Guid.Empty &&
                   (string.IsNullOrWhiteSpace(game.Name) ||
                    string.Equals(
                        game.Name.Trim(),
                        ManualGamePlaceholderName,
                        StringComparison.OrdinalIgnoreCase));
        }
    }
}
