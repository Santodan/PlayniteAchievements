using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers
{
    public interface IAchievementPageLinkProvider
    {
        bool CanResolveAchievementPageUrl(AchievementPageLinkContext context);

        Task<string> GetAchievementPageUrlAsync(
            AchievementPageLinkContext context,
            CancellationToken cancel);
    }
}
