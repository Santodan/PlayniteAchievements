using PlayniteAchievements.Providers;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services
{
    internal sealed class CustomRefreshResolution
    {
        public IReadOnlyList<IDataProvider> Providers { get; set; }

        public IReadOnlyList<Guid> TargetGameIds { get; set; }

        public bool RunProvidersInParallel { get; set; }
    }
}