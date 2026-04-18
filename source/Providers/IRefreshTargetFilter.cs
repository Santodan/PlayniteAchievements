using Playnite.SDK.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers
{
    public interface IRefreshTargetFilter
    {
        Task<IReadOnlyList<Game>> FilterRefreshTargetsAsync(
            IReadOnlyList<Game> gamesToRefresh,
            CancellationToken cancel);
    }
}