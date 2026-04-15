using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Providers
{
    internal static class ProviderUiPolicies
    {
        private static readonly HashSet<string> HiddenFromSetupSurfaces =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "BattleNet",
                "GooglePlay",
                "Apple",
                "EA",
                "Ubisoft"
            };

        public static bool ShouldHideFromSetupSurfaces(string providerKey)
        {
            return !string.IsNullOrWhiteSpace(providerKey) &&
                HiddenFromSetupSurfaces.Contains(providerKey);
        }
    }
}
