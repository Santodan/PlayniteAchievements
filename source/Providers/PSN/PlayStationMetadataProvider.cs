using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Metadata;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.PSN
{
    /// <summary>
    /// PlayStation trophy icons (512px for PS5, 240px for PS4) and platinum art for games another
    /// provider services. A game links to a trophy set id (NPWR) and its trophies come from the PSN
    /// API in the user's language, so a PSN sign-in is required. Matching considers the set the user
    /// played and the sets behind the game's PSPrices results, preferring a PS5 set.
    /// </summary>
    internal sealed class PlayStationMetadataProvider : IMetadataProvider
    {
        public const string MetadataKey = "PlayStation";

        /// <summary>Separates the sets of a link that spans several (a game and its DLC lists).</summary>
        internal const char SetSeparator = ',';

        private static readonly HashSet<string> NativeProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "PSN", "RPCS3", "ShadPS4", "Vita3K"
        };

        private readonly PsnDataProvider _psn;
        private readonly PsPricesClient _psPrices;
        private readonly ILogger _logger;
        private readonly Dictionary<string, List<AchievementDetail>> _definitions =
            new Dictionary<string, List<AchievementDetail>>(StringComparer.OrdinalIgnoreCase);
        private IDisposable _psPricesRun;
        private volatile bool _runSignedIn;

        public PlayStationMetadataProvider(PsnDataProvider psn, IPlayniteAPI api, ILogger logger)
        {
            _psn = psn;
            _psPrices = new PsPricesClient(api, logger);
            _logger = logger;
        }

        public string Key => MetadataKey;

        public string DisplayNameKey => "LOCPlayAch_Metadata_Source_PlayStation";

        public MetadataField SupportedFields => MetadataField.Icons | MetadataField.CapstoneArt;

        // Set by this run's session probe, not the cached flag, which can outlive the token.
        private bool PsnSignedIn => _psn?.Scanner != null && _runSignedIn;

        public bool IsNativeFor(string servicingProviderKey) =>
            !string.IsNullOrWhiteSpace(servicingProviderKey) && NativeProviders.Contains(servicingProviderKey);

        /// <summary>
        /// Acquires the PSN access token once per run, as the PSN provider's own refresh does, so a
        /// lapsed token is renewed before any lookup and a missing sign-in skips the source.
        /// </summary>
        public async Task<bool> BeginRunAsync(CancellationToken ct)
        {
            if (_psn?.Scanner == null || !await _psn.Scanner.TryAcquireAccessTokenAsync(ct).ConfigureAwait(false))
            {
                return false;
            }

            _runSignedIn = true;
            _psPricesRun = _psPrices.BeginRun();
            return true;
        }

        public void EndRun()
        {
            _runSignedIn = false;
            Interlocked.Exchange(ref _psPricesRun, null)?.Dispose();
            lock (_definitions)
            {
                _definitions.Clear();
            }
        }

        public async Task<MetadataSourceLink> ResolveLinkAsync(MetadataLinkRequest request, CancellationToken ct)
        {
            var name = request?.Game?.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            if (!PsnSignedIn)
            {
                return null;
            }

            try
            {
                var main = await FindMainSetAsync(request, name, ct).ConfigureAwait(false);
                if (main == null)
                {
                    return null;
                }

                var extras = await FindExtraSetsAsync(request, main.Id, ct).ConfigureAwait(false);
                return extras.Count == 0
                    ? main
                    : new MetadataSourceLink(
                        string.Join(SetSeparator + " ", new[] { main.Id }.Concat(extras)),
                        $"{main.Label} (+{extras.Count})",
                        foundByName: true);
            }
            catch (PsnAuthRequiredException)
            {
                _logger?.Debug($"[PlayStationMetadata] PSN sign-in expired while matching '{name}'.");
                return null;
            }
        }

        /// <summary>
        /// The game's own trophy set: the set the user played under its name, else the sets behind
        /// its PSPrices results. PS5 sets carry 512px art, so the first PS5 one wins; otherwise the
        /// first that loads.
        /// </summary>
        private async Task<MetadataSourceLink> FindMainSetAsync(MetadataLinkRequest request, string name, CancellationToken ct)
        {
            MetadataSourceLink fallback = null;
            var played = await _psn.Scanner.FindPlayedTrophySetByNameAsync(name, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(played?.NpCommunicationId))
            {
                var link = new MetadataSourceLink(played.NpCommunicationId, played.Title, foundByName: true);
                if (await IsPs5SetAsync(link.Id, ct).ConfigureAwait(false))
                {
                    return link;
                }

                fallback = link;
            }

            foreach (var gamePage in await _psPrices.FindGamePagePathsAsync(request.SearchNames, ct).ConfigureAwait(false))
            {
                var page = await ReadTrophyPageAsync(gamePage, ct).ConfigureAwait(false);
                if (page == null || string.Equals(page.NpCommunicationId, fallback?.Id, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var link = new MetadataSourceLink(page.NpCommunicationId, page.Title, foundByName: true);
                if (await IsPs5SetAsync(link.Id, ct).ConfigureAwait(false))
                {
                    return link;
                }

                fallback = fallback ?? link;
            }

            return fallback;
        }

        /// <summary>
        /// Trophy sets sold apart from the main one (DLC lists such as The Binding of Isaac's
        /// Afterbirth+ and Repentance, or the parts of a collection), looked for whenever the main
        /// set leaves achievements unmatched. A related set is kept when at least half of its
        /// trophies match achievements nothing matched yet, which keeps out sequels that only share
        /// the name.
        /// </summary>
        private async Task<List<string>> FindExtraSetsAsync(MetadataLinkRequest request, string mainId, CancellationToken ct)
        {
            var extras = new List<string>();
            var targets = request.Data?.Achievements;
            var mainRows = await GetDefinitionsAsync(mainId, ct).ConfigureAwait(false);
            if (targets == null || targets.Count == 0 || mainRows == null)
            {
                return extras;
            }

            var matched = new HashSet<AchievementDetail>(
                Services.Metadata.CrossSourceAchievementMatcher.Match(targets, mainRows).Select(match => match.Target));
            var unmatched = targets.Where(target => !matched.Contains(target)).ToList();
            if (unmatched.Count == 0)
            {
                return extras;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mainId };
            foreach (var gamePage in await _psPrices.FindRelatedGamePagePathsAsync(BaseName(request.Game?.Name), ct).ConfigureAwait(false))
            {
                var page = await ReadTrophyPageAsync(gamePage, ct).ConfigureAwait(false);
                if (page == null || !seen.Add(page.NpCommunicationId))
                {
                    continue;
                }

                var rows = await GetDefinitionsAsync(page.NpCommunicationId, ct).ConfigureAwait(false);
                if (rows == null || rows.Count == 0)
                {
                    continue;
                }

                var matches = Services.Metadata.CrossSourceAchievementMatcher.Match(unmatched, rows);
                _logger?.Debug(
                    $"[PlayStationMetadata] Extra set {page.NpCommunicationId} ('{page.Title}') matched {matches.Count}/{rows.Count} " +
                    $"of the {unmatched.Count} achievements still unmatched for '{request.Game?.Name}'.");
                if (matches.Count == 0 || matches.Count < rows.Count * 0.5)
                {
                    continue;
                }

                extras.Add(page.NpCommunicationId);
                var newlyMatched = new HashSet<AchievementDetail>(matches.Select(match => match.Target));
                unmatched = unmatched.Where(target => !newlyMatched.Contains(target)).ToList();
                if (unmatched.Count == 0)
                {
                    break;
                }
            }

            return extras;
        }

        private async Task<PsPricesTrophyPage> ReadTrophyPageAsync(string gamePage, CancellationToken ct)
        {
            var trophyPath = await _psPrices.FindTrophyPagePathAsync(gamePage, ct).ConfigureAwait(false);
            return trophyPath == null ? null : await _psPrices.GetTrophyPageAsync(trophyPath, ct).ConfigureAwait(false);
        }

        /// <summary>The franchise part of a name: "The Binding of Isaac: Rebirth" gives "The Binding of Isaac".</summary>
        internal static string BaseName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            var cut = name.Length;
            foreach (var separator in new[] { ":", " - ", " – " })
            {
                var at = name.IndexOf(separator, StringComparison.Ordinal);
                if (at > 0 && at < cut)
                {
                    cut = at;
                }
            }

            return name.Substring(0, cut).Trim();
        }

        /// <summary>PS5 trophy art is served from psnobj; PS4 and older from the trophy/np image path.</summary>
        internal static bool IsPs5Icon(string url)
        {
            return !string.IsNullOrWhiteSpace(url) &&
                   url.IndexOf("psnobj.prod.dl.playstation.net", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async Task<bool> IsPs5SetAsync(string npCommunicationId, CancellationToken ct)
        {
            var rows = await GetDefinitionsAsync(npCommunicationId, ct).ConfigureAwait(false);
            var ps5 = rows?.Any(row => IsPs5Icon(row.UnlockedIconPath)) == true;
            _logger?.Debug($"[PlayStationMetadata] {npCommunicationId}: {(rows == null ? "unavailable" : ps5 ? "PS5" : "PS4 or older")}.");
            return ps5;
        }

        /// <summary>A set's definitions, fetched once per run so matching and the overlay share them.</summary>
        private async Task<List<AchievementDetail>> GetDefinitionsAsync(string npCommunicationId, CancellationToken ct)
        {
            lock (_definitions)
            {
                if (_definitions.TryGetValue(npCommunicationId, out var cached))
                {
                    return cached;
                }
            }

            var rows = await _psn.Scanner.FetchTrophyDefinitionsAsync(npCommunicationId, ct).ConfigureAwait(false);
            lock (_definitions)
            {
                _definitions[npCommunicationId] = rows;
            }

            return rows;
        }

        public async Task<AchievementSourceSet> FetchAsync(MetadataLinkRequest request, string linkId, CancellationToken ct)
        {
            if (!PsnSignedIn)
            {
                return null;
            }

            // One set, or the main set then its extra ones, comma-separated. Rows keep that order,
            // so the main set's platinum is the capstone art.
            var rows = new List<AchievementDetail>();
            foreach (var part in SplitSets(linkId))
            {
                var npCommunicationId = part;
                if (IsPsPricesPath(npCommunicationId))
                {
                    // A user may paste a PSPrices trophy page; it only supplies the NPWR.
                    var page = await _psPrices.GetTrophyPageAsync(npCommunicationId, ct).ConfigureAwait(false);
                    npCommunicationId = page?.NpCommunicationId;
                }

                if (string.IsNullOrWhiteSpace(npCommunicationId))
                {
                    continue;
                }

                try
                {
                    rows.AddRange(await GetDefinitionsAsync(npCommunicationId, ct).ConfigureAwait(false) ?? new List<AchievementDetail>());
                }
                catch (PsnAuthRequiredException)
                {
                    _logger?.Debug($"[PlayStationMetadata] PSN sign-in required to fetch {npCommunicationId}.");
                    return null;
                }
            }

            return rows.Count == 0 ? null : new AchievementSourceSet(Key, linkId, rows);
        }

        /// <summary>The sets in a link: one id, or several separated by commas.</summary>
        internal static IReadOnlyList<string> SplitSets(string linkId)
        {
            return (linkId ?? string.Empty)
                .Split(new[] { SetSeparator }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>A link entered as a PSPrices trophy page (path or full URL) rather than an NPWR id.</summary>
        internal static bool IsPsPricesPath(string linkId)
        {
            return !string.IsNullOrWhiteSpace(linkId) &&
                   linkId.IndexOf("/game-trophies/", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
