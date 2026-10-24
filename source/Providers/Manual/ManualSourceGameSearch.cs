using PlayniteAchievements.Services.GameSearch;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Manual
{
    /// <summary>
    /// Searches a manual source's catalog for the game picker: manual tracking itself, and any
    /// link or override that stores the same ids (a Steam appId, an Exophase slug).
    /// </summary>
    internal sealed class ManualSourceGameSearch : IGameSearchSource
    {
        private readonly IManualSource _source;
        private readonly string _language;

        public ManualSourceGameSearch(IManualSource source, string language, bool allowsMultiple = false, string displayName = null)
        {
            _source = source;
            _language = string.IsNullOrWhiteSpace(language) ? "english" : language;
            AllowsMultiple = allowsMultiple;
            DisplayName = displayName ?? source?.SourceName;
        }

        public string DisplayName { get; }

        public bool AllowsMultiple { get; }

        public async Task<IReadOnlyList<GameSearchResult>> SearchAsync(string query, CancellationToken ct)
        {
            var results = _source == null
                ? null
                : await _source.SearchGamesAsync(query, _language, ct).ConfigureAwait(false);
            return (results ?? new List<ManualGameSearchResult>())
                .Where(result => !string.IsNullOrWhiteSpace(result?.SourceGameId))
                .Select(ToResult)
                .ToList();
        }

        public string TryParseId(string text) => (_source as IManualSourceGameLookup)?.TryParseGameId(text);
        public async Task<GameSearchResult> DescribeAsync(string id, CancellationToken ct)
        {
            var described = _source is IManualSourceGameLookup lookup
                ? await lookup.DescribeGameAsync(id, _language, ct).ConfigureAwait(false)
                : null;
            return described == null ? new GameSearchResult(id, id) : ToResult(described);
        }

        internal static GameSearchResult ToResult(ManualGameSearchResult result) =>
            new GameSearchResult(result.SourceGameId, result.Name, result.IconUrl, result.Platforms);
    }
}
