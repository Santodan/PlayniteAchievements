using System;

namespace PlayniteAchievements.Providers.Local
{
    public class LocalSavesProvider
    {
        public static bool TryGetCustomSchemaEnabledOverride(Guid gameId, out bool enabled)
        {
            enabled = false;
            return false;
        }
    }
}
