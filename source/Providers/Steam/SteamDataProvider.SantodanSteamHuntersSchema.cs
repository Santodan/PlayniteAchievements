using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Metadata;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Steam
{
    internal sealed partial class SteamDataProvider
    {
        private SteamHuntersApiClient _santodanSchemaClient;

        internal Task<List<AchievementDetail>> GetSteamHuntersSchemaAsync(int appId, CancellationToken ct)
        {
            return _santodanSchemaClient.GetSchemaAsync(appId, ct);
        }
    }
}
