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
        /// Game page paths of the PlayStation results whose title matches <paramref name="gameName"/>
        /// once normalized, PS5 cards first, then result order.
        /// </summary>
        public async Task<IReadOnlyList<string>> FindGamePagePathsAsync(string gameName, CancellationToken ct)
        {
            var normalized = GameNameNormalizer.StripDiacritics(ExophaseGameNameMatcher.NormalizeGameName(gameName));
            if (string.IsNullOrWhiteSpace(normalized) || _views == null)
            {
                return Array.Empty<string>();
            }

            var searchHtml = await GetPageSourceAsync(PsPricesPageParser.BuildSearchUrl(gameName), ct).ConfigureAwait(false);
            var paths = PsPricesPageParser.ParseSearchResults(searchHtml)
                .Where(result => result.IsPlayStation &&
                                 string.Equals(
                                     GameNameNormalizer.StripDiacritics(ExophaseGameNameMatcher.NormalizeGameName(result.Title)),
                                     normalized,
                                     StringComparison.OrdinalIgnoreCase))
                .OrderBy(result => result.IsPs5 ? 0 : 1)
                .Select(result => result.Path)
                .Take(MaxGamePagesPerSearch)
                .ToList();

            _logger?.Debug($"[PsPrices] '{gameName}': {paths.Count} matching PlayStation results.");
            return paths;
        }

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
