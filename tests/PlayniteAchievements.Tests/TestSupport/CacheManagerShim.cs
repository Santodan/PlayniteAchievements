using System;

namespace PlayniteAchievements.Services
{
    public sealed class GameCacheUpdatedEventArgs : EventArgs
    {
        public GameCacheUpdatedEventArgs(string gameId)
        {
            GameId = gameId;
        }

        public string GameId { get; }
    }
}
