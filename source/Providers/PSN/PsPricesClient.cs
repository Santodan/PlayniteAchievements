using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Providers.Exophase;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.PSN
{
    /// <summary>
    /// Finds a game's PSPrices trophy page by name and reads it. PSPrices sits behind a Cloudflare
    /// challenge, so pages load in an offscreen browser view; one shared view serves a whole run
    /// while <see cref="BeginRun"/>'s lease is held. Requests are spaced so a library refresh does
    /// not hammer the site.
    /// </summary>
    internal sealed class PsPricesClient
    {
        private const int ChallengeWaitMs = 15000;
        private const int ChallengePollMs = 500;
        private const int RequestGapMs = 1000;
        private const int MaxGamePagesPerSearch = 4;

        private readonly OffscreenViewLeaseSource _views;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, string> _trophyPathByGamePage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PsPricesTrophyPage> _pageByPath = new Dictionary<string, PsPricesTrophyPage>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<PsPricesSearchResult>> _searchByQuery =
            new Dictionary<string, IReadOnlyList<PsPricesSearchResult>>(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastRequestUtc = DateTime.MinValue;

        public PsPricesClient(IPlayniteAPI api, ILogger logger)
        {
            _views = api == null ? null : new OffscreenViewLeaseSource(api, logger);
            _logger = logger;
        }

        public bool IsAvailable => _views != null;

        /// <summary>Holds one shared view open for the run and clears the run's memo when disposed.</summary>
        public IDisposable BeginRun()
        {
            var lease = _views?.BeginLease();
            return new RunScope(this, lease);
        }

        /// <summary>
        /// Game page paths of the PlayStation results for the first of <paramref name="gameNames"/>
        /// that finds any, PS5 cards first. Exact titles are tried for every name before loose ones
        /// ("Deus Ex - Mankind Divided" for "Deus Ex: Mankind Divided"), and a loose name is also
        /// searched in its own right, since the site finds nothing for some punctuated titles.
        /// </summary>
        public async Task<IReadOnlyList<string>> FindGamePagePathsAsync(IEnumerable<string> gameNames, CancellationToken ct)
        {
            var names = (gameNames ?? Enumerable.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .GroupBy(GameNameNormalizer.ToLooseKey, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            if (names.Count == 0 || _views == null)
            {
                return Array.Empty<string>();
            }

            foreach (var name in names)
            {
                var normalized = Normalize(name);
                var exact = await FindAsync(name, title => string.Equals(Normalize(title), normalized, StringComparison.OrdinalIgnoreCase), ct)
                    .ConfigureAwait(false);
                if (exact.Count > 0)
                {
                    _logger?.Debug($"[PsPrices] '{name}': {exact.Count} matching PlayStation results.");
                    return exact;
                }
            }

            foreach (var name in names)
            {
                var loose = GameNameNormalizer.ToLooseKey(name);
                if (loose.Length == 0)
                {
                    continue;
                }

                bool Close(string title) => string.Equals(GameNameNormalizer.ToLooseKey(title), loose, StringComparison.Ordinal);
                var close = await FindAsync(name, Close, ct).ConfigureAwait(false);
                if (close.Count == 0)
                {
                    close = await FindAsync(loose, Close, ct).ConfigureAwait(false);
                }

                if (close.Count > 0)
                {
                    _logger?.Debug($"[PsPrices] '{name}': {close.Count} close PlayStation results.");
                    return close;
                }
            }

            _logger?.Debug($"[PsPrices] '{names[0]}': 0 matching PlayStation results.");
            return Array.Empty<string>();
        }

        private async Task<List<string>> FindAsync(string query, Func<string, bool> accepts, CancellationToken ct)
        {
            return (await SearchAsync(query, ct).ConfigureAwait(false))
                .Where(result => result.IsPlayStation && accepts(result.Title))
                .OrderBy(result => result.IsPs5 ? 0 : 1)
                .Select(result => result.Path)
                .Take(MaxGamePagesPerSearch)
                .ToList();
        }

        /// <summary>Search results for a query, fetched once per run.</summary>
        private async Task<IReadOnlyList<PsPricesSearchResult>> SearchAsync(string query, CancellationToken ct)
        {
            lock (_searchByQuery)
            {
                if (_searchByQuery.TryGetValue(query, out var cached))
                {
                    return cached;
                }
            }

            var url = PsPricesPageParser.BuildSearchUrl(query);
            var results = PsPricesPageParser.ParseSearchResults(await GetPageSourceAsync(url, ct).ConfigureAwait(false));
            if (results.Count == 0)
            {
                // The site at times answers with a page holding no results for a query it found
                // games for earlier, so a page without any game card is loaded once more.
                results = PsPricesPageParser.ParseSearchResults(await GetPageSourceAsync(url, ct).ConfigureAwait(false));
                _logger?.Debug($"[PsPrices] '{query}': no results; retry found {results.Count}.");
            }

            lock (_searchByQuery)
            {
                _searchByQuery[query] = results;
            }

            return results;
        }

        private static string Normalize(string name) =>
            GameNameNormalizer.StripDiacritics(ExophaseGameNameMatcher.NormalizeGameName(name));

        /// <summary>The trophy page path a game page links to, or null.</summary>
        public async Task<string> FindTrophyPagePathAsync(string gamePagePath, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(gamePagePath) || _views == null)
            {
                return null;
            }

            lock (_trophyPathByGamePage)
            {
                if (_trophyPathByGamePage.TryGetValue(gamePagePath, out var cached))
                {
                    return cached;
                }
            }

            var gameHtml = await GetPageSourceAsync(PsPricesPageParser.BaseUrl + gamePagePath, ct).ConfigureAwait(false);
            var path = PsPricesPageParser.FindTrophyPagePath(gameHtml);
            _logger?.Debug($"[PsPrices] {gamePagePath} -> {path ?? "no trophy page"}.");
            lock (_trophyPathByGamePage)
            {
                _trophyPathByGamePage[gamePagePath] = path;
            }

            return path;
        }

        /// <summary>The parsed trophy page at <paramref name="path"/>, or null when it cannot be read.</summary>
        public async Task<PsPricesTrophyPage> GetTrophyPageAsync(string path, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(path) || _views == null)
            {
                return null;
            }

            lock (_pageByPath)
            {
                if (_pageByPath.TryGetValue(path, out var cached))
                {
                    return cached;
                }
            }

            var url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : PsPricesPageParser.BaseUrl + path;
            var page = PsPricesPageParser.ParseTrophyPage(await GetPageSourceAsync(url, ct).ConfigureAwait(false));
            lock (_pageByPath)
            {
                _pageByPath[path] = page;
            }

            return page;
        }

        private async Task<string> GetPageSourceAsync(string url, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var wait = _lastRequestUtc.AddMilliseconds(RequestGapMs) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }

                try
                {
                    return await _views.WithNavigableViewAsync(async view =>
                    {
                        await view.NavigateAndWaitAsync(url, ChallengeWaitMs).ConfigureAwait(false);

                        // The challenge page reloads into the real page once it passes.
                        var html = await view.GetPageSourceAsync().ConfigureAwait(false);
                        var waited = 0;
                        while (PsPricesPageParser.IsChallengePage(html) && waited < ChallengeWaitMs)
                        {
                            await Task.Delay(ChallengePollMs, ct).ConfigureAwait(false);
                            waited += ChallengePollMs;
                            html = await view.GetPageSourceAsync().ConfigureAwait(false);
                        }

                        if (PsPricesPageParser.IsChallengePage(html))
                        {
                            _logger?.Warn($"[PsPrices] Cloudflare challenge did not clear for {url}.");
                            return null;
                        }

                        return html;
                    }, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"[PsPrices] Failed to load {url}.");
                    return null;
                }
            }
            finally
            {
                _lastRequestUtc = DateTime.UtcNow;
                _gate.Release();
            }
        }

        private void ClearRunMemo()
        {
            lock (_trophyPathByGamePage)
            {
                _trophyPathByGamePage.Clear();
            }

            lock (_pageByPath)
            {
                _pageByPath.Clear();
            }

            lock (_searchByQuery)
            {
                _searchByQuery.Clear();
            }
        }

        private sealed class RunScope : IDisposable
        {
            private PsPricesClient _owner;
            private readonly IDisposable _lease;

            public RunScope(PsPricesClient owner, IDisposable lease)
            {
                _owner = owner;
                _lease = lease;
            }

            public void Dispose()
            {
                _lease?.Dispose();
                Interlocked.Exchange(ref _owner, null)?.ClearRunMemo();
            }
        }
    }
}
