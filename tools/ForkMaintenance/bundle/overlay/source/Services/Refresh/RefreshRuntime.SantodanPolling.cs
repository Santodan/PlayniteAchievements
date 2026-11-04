using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.InGameMonitoring;
using PlayniteAchievements.Services.Logging;

namespace PlayniteAchievements.Services.Refresh
{
    public partial class RefreshRuntime
    {
        // Restrict the entire preflight, including its second-chance provider probes.
        // Never treat an unauthenticated remote provider as authenticated.
        internal static List<IDataProvider> RestrictSantodanPollingAuthProviders(
            RefreshRequest request, List<IDataProvider> providers, bool enabled)
        {
            var keys = request?.Options?.ProviderKeys;
            if (!enabled || !SantodanPollingScope.IsActive || keys == null || keys.Count == 0 ||
                keys.Any(key => !string.Equals(key?.Trim(), "Local", StringComparison.OrdinalIgnoreCase)))
                return providers;

            var local = providers.Where(provider => string.Equals(provider.ProviderKey, "Local",
                StringComparison.OrdinalIgnoreCase)).ToList();
            PollingDebugLog.Write("authScope=local-only excludedProviders=" + (providers.Count - local.Count));
            return local;
        }
    }
}
