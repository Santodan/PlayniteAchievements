using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Services.GameSearch;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Metadata.Exophase
{
    /// <summary>
    /// Exophase search for one branch's link: the borrowed platform's pages, or the game's own
    /// platform's. A link names one page per game, several for a collection.
    /// </summary>
    internal sealed class ExophaseLinkSearch : IGameSearchSource
    {
        private readonly ExophasePageSource _pages;
        private readonly IReadOnlyList<string> _platforms;

        public ExophaseLinkSearch(ExophasePageSource pages, IReadOnlyList<string> platforms, string displayName)
        {
            _pages = pages;
            _platforms = platforms ?? new string[0];
            DisplayName = displayName;
        }

        public string DisplayName { get; }

        public bool AllowsMultiple => true;

        public async Task<IReadOnlyList<GameSearchResult>> SearchAsync(string query, CancellationToken ct)
        {
            var found = await _pages.SearchGamesAsync(query, _platforms, ct).ConfigureAwait(false);
            if (found == null)
            {
                throw new GameSearchUnavailableException(DisplayName);
            }

            return found
                .Select(entry => new GameSearchResult(
                    entry.Slug,
                    entry.Game.Title,
                    entry.Game.Images?.O ?? entry.Game.Images?.L ?? entry.Game.Images?.M,
                    string.Join(", ", (entry.Game.Platforms ?? new List<ExophasePlatform>())
                        .Select(platform => platform?.Name)
                        .Where(name => !string.IsNullOrWhiteSpace(name)))))
                .ToList();
        }

        public string TryParseId(string text) => ExophaseApiClient.TryParseGameSlug(text);
        /// <summary>Not looked up: reading a page costs a browser load, so the slug stands for the game.</summary>
        public Task<GameSearchResult> DescribeAsync(string id, CancellationToken ct) =>
            Task.FromResult(new GameSearchResult(id, id));
    }
}
