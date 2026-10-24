using Playnite.SDK.Models;
using PlayniteAchievements.Services.GameSearch;

namespace PlayniteAchievements.Providers.Overrides
{
    /// <summary>
    /// Implemented by an <see cref="IProviderOverride"/> provider whose catalog can be searched by
    /// name, so the override is picked as a game rather than typed as an id.
    /// </summary>
    internal interface IProviderOverrideSearch
    {
        /// <summary>The search for <paramref name="game"/>'s override, or null when this game cannot be searched for.</summary>
        IGameSearchSource CreateOverrideSearch(Game game);
    }
}
