using PlayniteAchievements.Models;
using System;

namespace PlayniteAchievements.Services
{
    internal sealed class RefreshRunContext
    {
        public Guid? OperationId { get; set; }

        public RefreshModeType? Mode { get; set; }

        public Guid? SingleGameId { get; set; }
    }
}