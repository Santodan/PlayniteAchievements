using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Providers.RPCS3
{
    /// <summary>
    /// Per-resolution-run context for resolving PS3 serials (e.g. BLES01039) to NPWR
    /// trophy sets through RPCS3's own records: dev_hdd0/game/{serial} installs and
    /// games.yml registrations. Holds the emulator root, a lazily-built games.yml
    /// title-path map, and a per-serial trophy-source memo (empty lists record
    /// negative results). Memoized results depend on the caller's allowRawIsoScan
    /// flag, so an instance must not be shared across runs using different flag values.
    /// </summary>
    internal sealed class Rpcs3SerialNpwrBridge
    {
        // PS3 title ids are exactly four letters followed by five digits; deliberately
        // stricter than Ps3IdPattern (2-4 letters), which also matches tokens like SAVE12345.
        private static readonly Regex SerialPattern = new Regex(
            @"\b([A-Za-z]{4}\d{5})\b",
            RegexOptions.Compiled);

        private readonly ILogger _logger;
        private readonly object _sync = new object();
        private readonly Dictionary<string, IReadOnlyList<GameTrophySource>> _memo =
            new Dictionary<string, IReadOnlyList<GameTrophySource>>(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyDictionary<string, string> _gamesYmlMap;

        public Rpcs3SerialNpwrBridge(string rpcs3Root, ILogger logger)
        {
            Root = rpcs3Root;
            _logger = logger;
        }

        public string Root { get; }

        public IReadOnlyDictionary<string, string> GamesYmlMap
        {
            get
            {
                lock (_sync)
                {
                    if (_gamesYmlMap == null)
                    {
                        _gamesYmlMap = ReadTitlePathMap(Root, _logger);
                    }

                    return _gamesYmlMap;
                }
            }
        }

        public bool TryGetMemoizedSources(string serial, out IReadOnlyList<GameTrophySource> sources)
        {
            sources = null;
            if (string.IsNullOrWhiteSpace(serial))
            {
                return false;
            }

            lock (_sync)
            {
                return _memo.TryGetValue(serial, out sources);
            }
        }

        public void MemoizeSources(string serial, IReadOnlyList<GameTrophySource> sources)
        {
            if (string.IsNullOrWhiteSpace(serial))
            {
                return;
            }

            lock (_sync)
            {
                _memo[serial] = sources ?? Array.Empty<GameTrophySource>();
            }
        }

        /// <summary>
        /// Normalizes a candidate PS3 serial to uppercase; accepts only the
        /// four-letters-plus-five-digits title-id shape.
        /// </summary>
        public static bool TryNormalizeSerial(string raw, out string serial)
        {
            serial = null;

            var trimmed = raw?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length != 9)
            {
                return false;
            }

            for (var i = 0; i < 4; i++)
            {
                if (!char.IsLetter(trimmed[i]))
                {
                    return false;
                }
            }

            for (var i = 4; i < 9; i++)
            {
                if (!char.IsDigit(trimmed[i]))
                {
                    return false;
                }
            }

            serial = trimmed.ToUpperInvariant();
            return true;
        }

        /// <summary>
        /// Extracts every normalized PS3 serial token from a path or argument string.
        /// </summary>
        public static IEnumerable<string> ExtractSerials(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                yield break;
            }

            foreach (Match match in SerialPattern.Matches(text))
            {
                if (TryNormalizeSerial(match.Groups[1].Value, out var serial))
                {
                    yield return serial;
                }
            }
        }

        internal static IEnumerable<string> EnumerateGamesYmlPaths(string rpcs3Root)
        {
            if (string.IsNullOrWhiteSpace(rpcs3Root))
            {
                yield break;
            }

            yield return Path.Combine(rpcs3Root, "games.yml");
            yield return Path.Combine(rpcs3Root, "config", "games.yml");
        }

        /// <summary>
        /// Reads and merges the serial to game-path map from every games.yml location
        /// under the given RPCS3 root.
        /// </summary>
        internal static IReadOnlyDictionary<string, string> ReadTitlePathMap(string rpcs3Root, ILogger logger = null)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var gamesYmlPath in EnumerateGamesYmlPaths(rpcs3Root))
            {
                foreach (var kvp in Rpcs3GamesYmlReader.ReadTitlePathMap(gamesYmlPath, logger))
                {
                    map[kvp.Key] = kvp.Value;
                }
            }

            return map;
        }
    }
}
