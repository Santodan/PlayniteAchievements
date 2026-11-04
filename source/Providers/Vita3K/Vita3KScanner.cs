using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.PlayStationTrophies;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Refresh;
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
    /// <summary>
    /// A game's resolved Vita3K trophy set: the installation it lives in, its NP
    /// communication id, and the title id of the installed app when known.
    /// </summary>
    internal sealed class Vita3KTrophySource
    {
        public Vita3KInstallationContext Installation { get; set; }
        public string NpCommId { get; set; }
        public string TitleId { get; set; }
        public string ResolvedBy { get; set; }
    }

    /// <summary>
    /// Refreshes PS Vita trophies from Vita3K: definitions from the trophy conf folder Vita3K
    /// copies out of the game's TROPHY.TRP (or the TRP itself before first boot), progress
    /// from Vita3K's TROPUSR.DAT.
    /// </summary>
    internal sealed class Vita3KScanner
    {
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly Vita3KSettings _providerSettings;
        private readonly Vita3KDataProvider _provider;
        private readonly IPlayniteAPI _playniteApi;
        private readonly string _pluginUserDataPath;

        public Vita3KScanner(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            Vita3KSettings providerSettings,
            Vita3KDataProvider provider,
            IPlayniteAPI playniteApi,
            string pluginUserDataPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _providerSettings = providerSettings ?? throw new ArgumentNullException(nameof(providerSettings));
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _playniteApi = playniteApi;
            _pluginUserDataPath = pluginUserDataPath ?? string.Empty;
        }

        public async Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            if (gamesToRefresh == null || gamesToRefresh.Count == 0)
            {
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            return await ProviderRefreshExecutor.RunProviderGamesAsync(
                gamesToRefresh,
                onGameStarting,
                (game, token) => Task.FromResult(new ProviderRefreshExecutor.ProviderGameResult { Data = FetchGameData(game, token) }),
                onGameCompleted,
                isAuthRequiredException: _ => false,
                onGameError: (game, ex, consecutiveErrors) =>
                    _logger?.Error(ex, $"[Vita3K] Failed to scan '{game?.Name}'"),
                delayBetweenGamesAsync: null,
                delayAfterErrorAsync: null,
                cancel).ConfigureAwait(false) ?? new RebuildPayload { Summary = new RebuildSummary() };
        }

        private GameAchievementData FetchGameData(
            Game game,
            CancellationToken cancel)
        {
            if (game == null)
            {
                return null;
            }

            // A null result means "trophy data not located"; the refresh pipeline skips
            // persistence for null results so previously cached achievements are preserved.
            var source = _provider.ResolveTrophySource(game);
            if (source == null)
            {
                _logger?.Info($"[Vita3K] '{game.Name}': no trophy set resolved; cached achievements preserved.");
                return null;
            }

            cancel.ThrowIfCancellationRequested();

            var data = BuildGameData(game, source, cancel);
            if (data == null)
            {
                return null;
            }

            // The trophy set's title, searched before the Playnite name, and the region its title
            // id belongs to, for the metadata sources that find the game's page.
            data.MetadataHints = new MetadataHints
            {
                SearchName = ReadSetTitle(source),
                RegionHint = ExophaseGameNameMatcher.MapVitaTitleIdToRegionHint(source.TitleId)
            };
            return data;
        }

        internal GameAchievementData BuildGameData(Game game, Vita3KTrophySource source, CancellationToken cancel)
        {
            var locale = TrophyConfParser.MapGlobalLanguageToTrophyLocale(_settings?.Persisted?.GlobalLanguage);
            var confFolder = source.Installation.GetConfFolder(source.NpCommId);
            var tropconfPath = Path.Combine(confFolder, "TROPCONF.SFM");

            List<NpTrophy> trophies;
            string iconFolder;
            var fromTrp = false;
            if (File.Exists(tropconfPath))
            {
                trophies = TrophyConfParser.ParseTrophyDefinitions(tropconfPath, locale, _logger);
                iconFolder = confFolder;
            }
            else
            {
                var trpPath = ResolveAppTrpPath(source);
                if (trpPath == null)
                {
                    _logger?.Info($"[Vita3K] '{game.Name}': no trophy definitions for '{source.NpCommId}' (boot the game once in Vita3K).");
                    return null;
                }

                trophies = TrophyConfParser.ParseTrophyDefinitionsFromTrp(trpPath, locale, _logger);
                iconFolder = ExtractTrpIcons(trpPath, source.NpCommId);
                fromTrp = true;
            }

            if (trophies.Count == 0)
            {
                return null;
            }

            cancel.ThrowIfCancellationRequested();

            if (!fromTrp && !ApplyProgress(game, source, trophies))
            {
                return null;
            }

            var groupArt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var achievements = new List<AchievementDetail>();
            foreach (var trophy in trophies)
            {
                var iconPath = GetTrophyIconPath(iconFolder, trophy.Id);
                var trophyType = NormalizeTrophyType(trophy.TrophyType);
                var category = CategoryPathHelper.SanitizeSegment(trophy.GroupName);
                var categoryKind = MapGroupIdToCategoryKind(trophy.GroupId);
                if (!string.IsNullOrWhiteSpace(category) &&
                    categoryKind == "DLC" &&
                    !groupArt.ContainsKey(category))
                {
                    var artPath = GetGroupIconPath(iconFolder, trophy.GroupId);
                    if (artPath != null)
                    {
                        groupArt[category] = artPath;
                    }
                }

                achievements.Add(new AchievementDetail
                {
                    ApiName = trophy.Id.ToString(),
                    DisplayName = trophy.Name,
                    Description = trophy.Description,
                    UnlockedIconPath = iconPath,
                    LockedIconPath = iconPath,
                    Hidden = trophy.Hidden,
                    Unlocked = trophy.Unlocked,
                    UnlockTimeUtc = trophy.Unlocked ? trophy.UnlockTimeUtc : null,
                    GlobalPercentUnlocked = null,
                    Rarity = GetRarityFromTrophyType(trophyType),
                    TrophyType = trophyType,
                    IsCapstone = trophyType == "platinum",
                    CategoryKind = categoryKind,
                    Category = category
                });
            }

            ApplyDefaultCategoryArt(game, groupArt);

            var unlockedCount = achievements.Count(achievement => achievement.Unlocked);
            _logger?.Info(
                $"[Vita3K] '{game.Name}': {achievements.Count} trophies ({unlockedCount} unlocked) from '{source.NpCommId}' " +
                $"(resolved by {source.ResolvedBy}; user '{source.Installation.UserId}' via {source.Installation.UserIdSource}; " +
                $"data root '{source.Installation.DataRoot}'{(fromTrp ? "; definitions from TROPHY.TRP" : string.Empty)}).");

            return new GameAchievementData
            {
                ProviderKey = "Vita3K",
                ProviderGameKey = source.NpCommId,
                RaritySource = RaritySource.Tier,
                LibrarySourceName = game.Source?.Name,
                GameName = game.Name,
                PlayniteGameId = game.Id,
                HasAchievements = achievements.Count > 0,
                Achievements = achievements,
                LastUpdatedUtc = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Overlays TROPUSR.DAT progress. A missing file means no unlock was recorded yet;
        /// an unreadable or foreign-format file returns false so cached progress is kept.
        /// </summary>
        private bool ApplyProgress(Game game, Vita3KTrophySource source, List<NpTrophy> trophies)
        {
            var progressPath = source.Installation.GetProgressPath(source.NpCommId);
            if (!File.Exists(progressPath))
            {
                return true;
            }

            if (!Vita3KTropusrReader.TryRead(progressPath, out var unlockedById, out var error))
            {
                _logger?.Warn($"[Vita3K] '{game.Name}': ignoring TROPUSR.DAT at '{progressPath}' ({error}); cached achievements preserved.");
                return false;
            }

            foreach (var trophy in trophies)
            {
                trophy.Unlocked = unlockedById.TryGetValue(trophy.Id, out var unlockTimeUtc);
                trophy.UnlockTimeUtc = trophy.Unlocked ? unlockTimeUtc : null;
            }

            return true;
        }

        private static string ResolveAppTrpPath(Vita3KTrophySource source)
        {
            if (string.IsNullOrWhiteSpace(source.TitleId))
            {
                return null;
            }

            var trpPath = Path.Combine(source.Installation.GetAppTrophyFolder(source.TitleId), source.NpCommId, "TROPHY.TRP");
            return File.Exists(trpPath) ? trpPath : null;
        }

        private string ReadSetTitle(Vita3KTrophySource source)
        {
            var title = TrophyConfParser.ReadSetTitle(source.Installation.GetConfFolder(source.NpCommId), _logger);
            if (title != null)
            {
                return title;
            }

            var trpPath = ResolveAppTrpPath(source);
            if (trpPath == null)
            {
                return null;
            }

            try
            {
                return TrophyConfParser.TryReadTrpIdentity(File.ReadAllBytes(trpPath), out _, out var titleName, _logger)
                    ? titleName
                    : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// Extracts the PNG entries of a TROPHY.TRP into the plugin's icon cache, laid out like
        /// a trophy conf folder. Extraction runs once per trophy set.
        /// </summary>
        private string ExtractTrpIcons(string trpPath, string npCommId)
        {
            if (string.IsNullOrWhiteSpace(_pluginUserDataPath))
            {
                return null;
            }

            try
            {
                var iconFolder = Path.Combine(_pluginUserDataPath, "icon_cache", "vita3k", npCommId);
                if (Directory.Exists(iconFolder) && Directory.EnumerateFiles(iconFolder, "TROP*.PNG").Any())
                {
                    return iconFolder;
                }

                var trpBytes = File.ReadAllBytes(trpPath);
                var entries = TrpArchiveReader.ReadEntries(trpBytes, _logger);
                var pngEntries = entries?
                    .Where(entry => entry.Name.EndsWith(".PNG", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (pngEntries == null || pngEntries.Count == 0)
                {
                    return null;
                }

                Directory.CreateDirectory(iconFolder);
                foreach (var entry in pngEntries)
                {
                    var data = TrpArchiveReader.ExtractEntry(trpBytes, entries, entry.Name);
                    if (data != null)
                    {
                        File.WriteAllBytes(Path.Combine(iconFolder, Path.GetFileName(entry.Name).ToUpperInvariant()), data);
                    }
                }

                return iconFolder;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger?.Debug(ex, $"[Vita3K] Failed to extract icons from '{trpPath}'");
                return null;
            }
        }

        /// <summary>
        /// Publishes each DLC trophy group's GR###.PNG as the default category art for its
        /// category label. Existing art is kept, and user overrides always win over defaults.
        /// </summary>
        private void ApplyDefaultCategoryArt(Game game, IReadOnlyDictionary<string, string> groupArt)
        {
            if (groupArt.Count == 0 || game.Id == Guid.Empty)
            {
                return;
            }

            var diskImageService = PlayniteAchievementsPlugin.Instance?.DiskImageService;
            if (diskImageService == null)
            {
                return;
            }

            var gameIdText = game.Id.ToString("D");
            foreach (var entry in groupArt)
            {
                var label = AchievementCategoryHelper.NormalizeCategoryOrDefault(entry.Key);
                if (string.Equals(label, AchievementCategoryHelper.DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    diskImageService.SaveDefaultCategoryImageFromFile(gameIdText, label, entry.Value);
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"[Vita3K] Default category image copy failed for '{entry.Key}'.");
                }
            }
        }

        private static string GetTrophyIconPath(string iconFolder, int trophyId)
        {
            if (string.IsNullOrWhiteSpace(iconFolder))
            {
                return null;
            }

            var path = Path.Combine(iconFolder, $"TROP{trophyId:D3}.PNG");
            return File.Exists(path) ? path : null;
        }

        private static string GetGroupIconPath(string iconFolder, string groupId)
        {
            if (string.IsNullOrWhiteSpace(iconFolder) ||
                !int.TryParse(groupId?.Trim(), out var number) ||
                number <= 0)
            {
                return null;
            }

            var path = Path.Combine(iconFolder, $"GR{number:D3}.PNG");
            return File.Exists(path) ? path : null;
        }

        internal static string NormalizeTrophyType(string trophyType)
        {
            switch ((trophyType ?? string.Empty).Trim().ToUpperInvariant())
            {
                case "P": return "platinum";
                case "G": return "gold";
                case "S": return "silver";
                case "B": return "bronze";
                default: return null;
            }
        }

        private static RarityTier GetRarityFromTrophyType(string trophyType)
        {
            switch (trophyType)
            {
                case "platinum": return RarityTier.UltraRare;
                case "gold": return RarityTier.Rare;
                case "silver": return RarityTier.Uncommon;
                default: return RarityTier.Common;
            }
        }

        internal static string MapGroupIdToCategoryKind(string groupId)
        {
            var normalized = (groupId ?? string.Empty).Trim();
            return normalized.Length == 0 ||
                   normalized.Equals("default", StringComparison.OrdinalIgnoreCase) ||
                   (int.TryParse(normalized, out var number) && number == 0)
                ? "Base"
                : "DLC";
        }
    }
}
