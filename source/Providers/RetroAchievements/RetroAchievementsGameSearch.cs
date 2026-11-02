using PlayniteAchievements.Common;
using PlayniteAchievements.Providers.RetroAchievements.Models;
using PlayniteAchievements.Services.GameSearch;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements
{
    /// <summary>
    /// Searches one console's RetroAchievements game list by title, for a game's override. The
    /// list is loaded once and kept with the scanner's, so a search after the first is local.
    /// </summary>
    internal sealed class RetroAchievementsGameSearch : IGameSearchSource
    {
        private const int MinimumScore = 40;
        private const int MaxResults = 100;

        private static readonly Regex GameAddress = new Regex(
            @"retroachievements\.org/game/(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly int _consoleId;
        private readonly Func<int, CancellationToken, Task<List<RaGameListWithTitle>>> _gameList;
        private readonly Func<int, CancellationToken, Task<RaGameInfoUserProgress>> _game;

        public RetroAchievementsGameSearch(
            int consoleId,
            Func<int, CancellationToken, Task<List<RaGameListWithTitle>>> gameList,
            Func<int, CancellationToken, Task<RaGameInfoUserProgress>> game,
            string displayName)
        {
            _consoleId = consoleId;
            _gameList = gameList;
            _game = game;
            DisplayName = displayName;
        }

        public string DisplayName { get; }

        public bool AllowsMultiple => false;

        public async Task<IReadOnlyList<GameSearchResult>> SearchAsync(string query, CancellationToken ct)
        {
            var games = await _gameList(_consoleId, ct).ConfigureAwait(false);
            return Filter(query, games).Select(ToResult).ToList();
        }

        public string TryParseId(string text) => ParseGameId(text)?.ToString(CultureInfo.InvariantCulture);
        /// <summary>The game from the console's list when it is there, else from RetroAchievements; the id alone when neither answers.</summary>
        public async Task<GameSearchResult> DescribeAsync(string id, CancellationToken ct)
        {
            if (!(ParseGameId(id) is int gameId))
            {
                return new GameSearchResult(id, id);
            }

            var listed = (await _gameList(_consoleId, ct).ConfigureAwait(false))?.FirstOrDefault(game => game?.ID == gameId);
            if (listed != null)
            {
                return ToResult(listed);
            }

            var info = _game == null ? null : await _game(gameId, ct).ConfigureAwait(false);
            return info == null || string.IsNullOrWhiteSpace(info.GameTitle)
                ? new GameSearchResult(id, id)
                : new GameSearchResult(id, info.GameTitle, RetroAchievementsAchievementMapper.NormalizeImageUrl(info.ImageIcon), info.ConsoleName);
        }

        /// <summary>A positive game id, or the id a retroachievements.org game address names; null otherwise.</summary>
        internal static int? ParseGameId(string text)
        {
            var part = text?.Trim() ?? string.Empty;
            var address = GameAddress.Match(part);
            if (address.Success)
            {
                part = address.Groups[1].Value;
            }

            return int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var gameId) && gameId > 0
                ? gameId
                : (int?)null;
        }

        /// <summary>
        /// The console's games whose title, or any of its "|"-separated alternate titles, matches
        /// <paramref name="query"/>: best match first, base sets before subsets, at most a hundred.
        /// </summary>
        internal static IReadOnlyList<RaGameListWithTitle> Filter(string query, IEnumerable<RaGameListWithTitle> games)
        {
            var normalizedQuery = NormalizeTitle(query);
            if (normalizedQuery.Length == 0)
            {
                return Array.Empty<RaGameListWithTitle>();
            }

            return (games ?? Enumerable.Empty<RaGameListWithTitle>())
                .Where(game => game != null && game.ID > 0 && !string.IsNullOrWhiteSpace(game.Title))
                .Select(game => (
                    Game: game,
                    Score: game.Title
                        .Split('|')
                        .Select(title => GameNameNormalizer.ComputeMatchScore(normalizedQuery, NormalizeTitle(title)))
                        .Max()))
                .Where(entry => entry.Score >= MinimumScore)
                .OrderByDescending(entry => entry.Score)
                .ThenBy(entry => RetroAchievementsSubsetTitleResolver.IsSubsetLikeTitle(entry.Game.Title) ? 1 : 0)
                .ThenBy(entry => entry.Game.Title, StringComparer.CurrentCultureIgnoreCase)
                .Take(MaxResults)
                .Select(entry => entry.Game)
                .ToList();
        }

        /// <summary>A title without RetroAchievements' ~tags~, region parentheticals or dump brackets, compared as game search compares names.</summary>
        private static string NormalizeTitle(string title)
        {
            var text = GameNameNormalizer.StripTildeTags(title);
            text = GameNameNormalizer.StripParentheticals(text);
            text = GameNameNormalizer.StripBrackets(text);
            return GameSearchRanking.Normalize(text);
        }

        private static GameSearchResult ToResult(RaGameListWithTitle game) =>
            new GameSearchResult(
                game.ID.ToString(CultureInfo.InvariantCulture),
                game.Title,
                RetroAchievementsAchievementMapper.NormalizeImageUrl(game.ImageIcon),
                game.ConsoleName);
    }
}
