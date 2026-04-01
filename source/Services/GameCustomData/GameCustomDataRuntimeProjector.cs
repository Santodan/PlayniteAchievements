using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services
{
    internal sealed class GameCustomDataRuntimeProjector
    {
        public void AttachRuntimeSettings(PlayniteAchievementsSettings settings)
        {
            _ = settings;
        }

        public void SyncAll(IEnumerable<GameCustomDataFile> allData)
        {
            _ = allData;
        }

        public void RefreshGame(Guid playniteGameId, GameCustomDataFile data)
        {
            _ = playniteGameId;
            _ = data;
        }
    }
}
