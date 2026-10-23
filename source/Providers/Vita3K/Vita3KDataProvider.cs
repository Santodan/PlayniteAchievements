using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.EmuLibrary;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.Overrides;
using PlayniteAchievements.Providers.PlayStationTrophies;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services.GameCustomData;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Vita3K
{
    internal sealed class Vita3KDataProvider : DataProviderBase<Vita3KSettings>, IDataProvider, IProviderOverride, IInGameProgressSource, IEnglishTitleSource
    {
        internal const string Key = "Vita3K";
        private const string VitaPlatformSpecificationId = "sony_vita";

        public ProviderOverrideDescriptor OverrideDescriptor { get; } = ProviderOverrideDescriptor.Text(
            "LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_Vita3K",
            raw => Vita3KMatchIdHelper.TryNormalize(raw, out var matchId)
                ? ProviderOverrideValidation.Valid(matchId)
                : ProviderOverrideValidation.InvalidId("PCSE00120"));

        private readonly Vita3KScanner _scanner;
        private readonly ILogger _logger;
        private readonly IPlayniteAPI _playniteApi;

        public Vita3KDataProvider(ILogger logger, PlayniteAchievementsSettings settings, IPlayniteAPI playniteApi)
            : this(logger, settings, playniteApi, string.Empty)
        {
        }

        public Vita3KDataProvider(ILogger logger, PlayniteAchievementsSettings settings, IPlayniteAPI playniteApi, string pluginUserDataPath)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _playniteApi = playniteApi;
            _scanner = new Vita3KScanner(_logger, settings, ProviderSettings, this, _playniteApi, pluginUserDataPath);
        }

        public string ProviderName
        {
            get
            {
                var value = ResourceProvider.GetString("LOCPlayAch_Provider_Vita3K");
                return string.IsNullOrWhiteSpace(value) ? "Vita3K" : value;
            }
        }

        public string ProviderKey => Key;

        public string ProviderIconKey => "ProviderIconVita3K";

        public string ProviderColorHex => "#E97F02";

        public ISessionManager AuthSession => null;

        public PlayniteAchievements.Models.Friends.IFriendsProvider Friends => null;

        public bool IsAuthenticated
        {
            get
            {
                var installation = Vita3KInstallationResolver.Resolve(null, ProviderSettings, _playniteApi, null);
                return installation != null && Directory.Exists(Path.GetDirectoryName(installation.TrophyConfRoot));
            }
        }

        public bool IsCapable(Game game)
        {
            if (game == null)
            {
                return false;
            }

            if (TryGetMatchIdOverride(game.Id, out _) ||
                Vita3KInstallationResolver.GetVita3KActions(game, _playniteApi).Count > 0)
            {
                return true;
            }

            if (game.Platforms?.Any(platform => string.Equals(
                    platform?.SpecificationId,
                    VitaPlatformSpecificationId,
                    StringComparison.OrdinalIgnoreCase)) != true)
            {
                return false;
            }

            var titleId = ResolveTitleId(game);
            if (titleId == null)
            {
                return false;
            }

            var installation = Vita3KInstallationResolver.Resolve(game, ProviderSettings, _playniteApi, null);
            return installation != null && Vita3KTitleIdResolver.GetAppNpCommIds(installation, titleId).Count > 0;
        }

        internal static bool TryGetMatchIdOverride(Guid gameId, out string matchId)
        {
            matchId = null;
            return GameCustomDataLookup.TryGetProviderOverrideValue(gameId, Key, out var raw) &&
                   Vita3KMatchIdHelper.TryNormalize(raw, out matchId);
        }

        /// <summary>
        /// Resolves a game's trophy set in order: the match-id override, the title id
        /// (launch arguments, .vpk param.sfo, then paths) mapped through the installed
        /// app's trophy folders, then a unique trophy-set title match against the game name.
        /// </summary>
        internal Vita3KTrophySource ResolveTrophySource(Game game)
        {
            var installation = Vita3KInstallationResolver.Resolve(game, ProviderSettings, _playniteApi, _logger);
            if (installation == null)
            {
                _logger?.Warn($"[Vita3K] '{game?.Name}': no Vita3K installation or user profile could be resolved.");
                return null;
            }

            if (TryGetMatchIdOverride(game.Id, out var matchId))
            {
                return Vita3KMatchIdHelper.GetKind(matchId) == Vita3KMatchIdKind.NpCommId
                    ? new Vita3KTrophySource
                    {
                        Installation = installation,
                        NpCommId = matchId,
                        TitleId = ResolveTitleId(game),
                        ResolvedBy = "override"
                    }
                    : FromTitleId(installation, matchId, "override");
            }

            var titleId = ResolveTitleId(game);
            if (titleId != null)
            {
                var source = FromTitleId(installation, titleId, "title id");
                if (source != null)
                {
                    return source;
                }
            }

            var nameMatch = MatchConfFolderByName(installation, game.Name);
            return nameMatch == null
                ? null
                : new Vita3KTrophySource
                {
                    Installation = installation,
                    NpCommId = nameMatch,
                    TitleId = titleId,
                    ResolvedBy = "trophy set title"
                };
        }

        private static Vita3KTrophySource FromTitleId(Vita3KInstallationContext installation, string titleId, string resolvedBy)
        {
            var npCommIds = Vita3KTitleIdResolver.GetAppNpCommIds(installation, titleId);
            if (npCommIds.Count == 0)
            {
                return null;
            }

            // An app normally ships one trophy set; prefer one Vita3K has already set up.
            var npCommId = npCommIds.FirstOrDefault(id => Directory.Exists(installation.GetConfFolder(id))) ?? npCommIds[0];
            return new Vita3KTrophySource
            {
                Installation = installation,
                NpCommId = npCommId,
                TitleId = titleId,
                ResolvedBy = $"{resolvedBy} {titleId}"
            };
        }

        /// <summary>
        /// The conf folder whose trophy-set title equals the game name after normalization,
        /// or null when none or several match.
        /// </summary>
        private string MatchConfFolderByName(Vita3KInstallationContext installation, string gameName)
        {
            var normalizedGameName = NormalizeNameForMatch(gameName);
            if (string.IsNullOrEmpty(normalizedGameName) || !Directory.Exists(installation.TrophyConfRoot))
            {
                return null;
            }

            List<string> matches;
            try
            {
                matches = Directory.GetDirectories(installation.TrophyConfRoot)
                    .Where(folder => Vita3KMatchIdHelper.NormalizeNpCommId(Path.GetFileName(folder)) != null)
                    .Where(folder => string.Equals(
                        NormalizeNameForMatch(TrophyConfParser.ReadSetTitle(folder, _logger)),
                        normalizedGameName,
                        StringComparison.Ordinal))
                    .Select(folder => Vita3KMatchIdHelper.NormalizeNpCommId(Path.GetFileName(folder)))
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }

            return matches.Count == 1 ? matches[0] : null;
        }

        private static string NormalizeNameForMatch(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var normalized = ExophaseGameNameMatcher.NormalizeGameName(System.Net.WebUtility.HtmlDecode(name));
            return new string((normalized ?? string.Empty)
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
        }

        /// <summary>
        /// The game's PS Vita title id from a Vita3K -r launch argument, a .vpk's param.sfo,
        /// or a title-id-shaped token in its ROM, action or install paths.
        /// </summary>
        internal string ResolveTitleId(Game game)
        {
            if (game == null)
            {
                return null;
            }

            foreach (var action in Vita3KInstallationResolver.GetVita3KActions(game, _playniteApi))
            {
                var fromArguments = Vita3KTitleIdResolver.FromArguments(action.Arguments) ??
                                    Vita3KTitleIdResolver.FromArguments(action.AdditionalArguments);
                if (fromArguments != null)
                {
                    return fromArguments;
                }
            }

            var paths = EnumerateGamePaths(game).ToList();
            foreach (var path in paths.Where(path => path.EndsWith(".vpk", StringComparison.OrdinalIgnoreCase)))
            {
                var fromVpk = Vita3KTitleIdResolver.FromVpk(path, _logger);
                if (fromVpk != null)
                {
                    return fromVpk;
                }
            }

            return paths
                .Select(Vita3KTitleIdResolver.FromPath)
                .FirstOrDefault(titleId => titleId != null);
        }

        private IEnumerable<string> EnumerateGamePaths(Game game)
        {
            var candidates = new List<string>();
            if (game.Roms != null)
            {
                candidates.AddRange(game.Roms.Select(rom => rom?.Path));
            }

            if (game.GameActions != null)
            {
                candidates.AddRange(game.GameActions.Select(action => action?.Path));
            }

            candidates.Add(game.InstallDirectory);

            if (EmuLibraryPathResolver.TryResolveSourcePath(_playniteApi, game, out var emuLibraryPath))
            {
                candidates.Add(emuLibraryPath);
            }

            return candidates
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => PathExpansion.ExpandGamePath(_playniteApi, game, path))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        public Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            return _scanner.RefreshAsync(gamesToRefresh, onGameStarting, onGameCompleted, cancel);
        }

        /// <summary>
        /// The game's trophy conf folder re-read with English text only; null when the set has
        /// not been booted yet or carries no English text.
        /// </summary>
        Task<IList<AchievementDetail>> IEnglishTitleSource.GetEnglishTitlesAsync(Game game, GameAchievementData data, CancellationToken ct)
        {
            var source = game == null ? null : ResolveTrophySource(game);
            var trophies = source == null
                ? null
                : TrophyConfParser.ParseEnglishTrophyDefinitions(
                    Path.Combine(source.Installation.GetConfFolder(source.NpCommId), "TROPCONF.SFM"),
                    _logger);

            return Task.FromResult<IList<AchievementDetail>>(trophies?
                .Select(trophy => new AchievementDetail
                {
                    ApiName = trophy.Id.ToString(),
                    DisplayName = trophy.Name,
                    Description = trophy.Description
                })
                .ToList());
        }

        InGameProgressRegistration IInGameProgressSource.TryRegister(Game game, GameAchievementData cachedSchema)
        {
            if (game == null ||
                cachedSchema?.Achievements == null ||
                cachedSchema.Achievements.Count == 0 ||
                !string.Equals(cachedSchema.ProviderKey, ProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var source = ResolveTrophySource(game);
            if (source == null || !Directory.Exists(Path.GetDirectoryName(source.Installation.TrophyDataRoot)))
            {
                return null;
            }

            // The data folder for a set appears with its first unlock; until then the
            // safety poll picks the file up once Vita3K writes it.
            var progressPath = source.Installation.GetProgressPath(source.NpCommId);
            return new InGameProgressRegistration
            {
                ProviderKey = ProviderKey,
                WatchTargets = new[] { progressPath },
                PollInterval = InGameProgressRegistration.FileWatchSafetyPollInterval,
                State = progressPath
            };
        }

        Task<IReadOnlyList<InGameProgressQueryResult>> IInGameProgressSource.QueryAsync(
            IReadOnlyList<InGameTrackingContext> games,
            CancellationToken cancellationToken)
        {
            var results = new List<InGameProgressQueryResult>();
            foreach (var context in games ?? Array.Empty<InGameTrackingContext>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var gameId = context?.Game?.Id ?? Guid.Empty;
                var path = context?.Registration?.State as string;
                if (!Vita3KTropusrReader.TryRead(path, out var unlocked, out _))
                {
                    results.Add(InGameProgressQueryResult.Failed(gameId, "file_unstable"));
                    continue;
                }

                var observations = unlocked.Select(pair => new AchievementProgressObservation
                {
                    ApiName = pair.Key.ToString(),
                    Unlocked = true,
                    UnlockTimeUtc = pair.Value
                }).ToList();
                results.Add(InGameProgressQueryResult.Succeeded(gameId, observations));
            }

            return Task.FromResult<IReadOnlyList<InGameProgressQueryResult>>(results);
        }

        /// <inheritdoc />
        public ProviderSettingsViewBase CreateSettingsView() => new Vita3KSettingsView(_playniteApi);
    }
}
