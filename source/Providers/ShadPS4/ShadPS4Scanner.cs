using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace PlayniteAchievements.Providers.ShadPS4
{
    internal sealed class ShadPS4Scanner
    {
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ShadPS4Settings _providerSettings;
        private readonly ShadPS4DataProvider _provider;
        private readonly IPlayniteAPI _playniteApi;

        // PS4's RTC epoch is January 1, 2008 00:00:00 UTC
        private static readonly DateTime Ps4Epoch = new DateTime(2008, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // ShadPS4-specific year offset correction
        private const int YearOffset = 2007;

        private static readonly System.Text.RegularExpressions.Regex TitleIdPattern =
            new System.Text.RegularExpressions.Regex(@"\b([A-Z]{4}\d{5})\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private enum TrophyFormat { Old, New }

        public ShadPS4Scanner(ILogger logger, PlayniteAchievementsSettings settings, ShadPS4Settings providerSettings, ShadPS4DataProvider provider = null, IPlayniteAPI playniteApi = null, string pluginUserDataPath = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _providerSettings = providerSettings ?? throw new ArgumentNullException(nameof(providerSettings));
            _provider = provider;
            _playniteApi = playniteApi;
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

            var titleCache = _provider != null
                ? _provider.GetOrBuildTitleCache()
                : await BuildTitleIdCacheAsync(cancel).ConfigureAwait(false);

            var npCommIdCache = _provider != null
                ? _provider.GetOrBuildNpCommIdCache()
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var hasOldData = titleCache?.Count > 0;
            var hasNewData = npCommIdCache?.Count > 0;
            var hasOverrideGames = gamesToRefresh.Any(game =>
                game != null &&
                GameCustomDataLookup.TryGetShadPS4MatchIdOverride(game.Id, out _));

            if (!hasOldData && !hasNewData && !hasOverrideGames)
            {
                _logger?.Warn("[ShadPS4] No games found in any ShadPS4 trophy location.");
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            if (hasOldData)
                _logger?.Info($"[ShadPS4] Found {titleCache.Count} titles in old game_data format.");
            if (hasNewData)
                _logger?.Info($"[ShadPS4] Found {npCommIdCache.Count} titles in new AppData format.");

            return await ProviderRefreshExecutor.RunProviderGamesAsync(
                gamesToRefresh,
                onGameStarting,
                async (game, token) =>
                {
                    var data = await FetchGameDataAsync(game, titleCache, npCommIdCache, token).ConfigureAwait(false);
                    return new ProviderRefreshExecutor.ProviderGameResult { Data = data };
                },
                onGameCompleted,
                isAuthRequiredException: _ => false,
                onGameError: (game, ex, consecutiveErrors) =>
                    _logger?.Error(ex, $"[ShadPS4] Error processing game '{game?.Name}'"),
                delayBetweenGamesAsync: null,
                delayAfterErrorAsync: null,
                cancel).ConfigureAwait(false);
        }

        private async Task<Dictionary<string, string>> BuildTitleIdCacheAsync(CancellationToken cancel)
        {
            var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var gameDataPath = ShadPS4PathResolver.ResolveConfiguredLegacyGameDataPath(_providerSettings?.GameDataPath);

            if (string.IsNullOrWhiteSpace(gameDataPath))
            {
                _logger?.Warn("[ShadPS4] No valid legacy game_data path configured in settings");
                return cache;
            }

            if (!Directory.Exists(gameDataPath))
            {
                _logger?.Warn($"[ShadPS4] game_data folder not found at {gameDataPath}");
                return cache;
            }

            try
            {
                foreach (var titleDir in Directory.GetDirectories(gameDataPath))
                {
                    cancel.ThrowIfCancellationRequested();
                    var titleId = Path.GetFileName(titleDir);
                    if (string.IsNullOrWhiteSpace(titleId)) continue;

                    var xmlPath = Path.Combine(titleDir, "trophyfiles", "trophy00", "Xml", "TROP.XML");
                    if (File.Exists(xmlPath))
                        cache[titleId.ToUpperInvariant()] = titleDir;
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[ShadPS4] Failed to enumerate title directories.");
            }

            return await Task.FromResult(cache).ConfigureAwait(false);
        }

        private Task<GameAchievementData> FetchGameDataAsync(
            Game game,
            Dictionary<string, string> titleCache,
            Dictionary<string, string> npCommIdCache,
            CancellationToken cancel)
        {
            if (game == null)
                return Task.FromResult<GameAchievementData>(null);

            if (GameCustomDataLookup.TryGetShadPS4MatchIdOverride(game.Id, out var overrideMatchId))
            {
                return ResolveOverrideGameData(game, overrideMatchId, titleCache, npCommIdCache, cancel);
            }

            if ((titleCache?.Count ?? 0) <= 0 && (npCommIdCache?.Count ?? 0) <= 0)
            {
                return Task.FromResult<GameAchievementData>(null);
            }

            // Try new format: resolve npcommid from npbind.dat
            if (npCommIdCache?.Count > 0)
            {
                var npcommid = _provider?.ResolveNpCommIdForGame(game);
                if (!string.IsNullOrWhiteSpace(npcommid) &&
                    npCommIdCache.TryGetValue(npcommid, out var perUserXmlPath))
                {
                    return ParseTrophyXml(game, perUserXmlPath, TrophyFormat.New, npcommid, cancel);
                }
            }

            // Fall back to old format: title ID lookup
            var titleId = ExtractTitleIdFromGame(game);
            if (string.IsNullOrWhiteSpace(titleId) || titleCache == null ||
                !titleCache.TryGetValue(titleId, out var trophyDataPath))
            {
                return Task.FromResult(BuildNoAchievementsData(game));
            }

            var xmlPath = Path.Combine(trophyDataPath, "trophyfiles", "trophy00", "Xml", "TROP.XML");
            if (!File.Exists(xmlPath))
                return Task.FromResult(BuildNoAchievementsData(game));

            return ParseTrophyXml(game, xmlPath, TrophyFormat.Old, null, cancel);
        }

        private Task<GameAchievementData> ResolveOverrideGameData(
            Game game,
            string overrideMatchId,
            Dictionary<string, string> titleCache,
            Dictionary<string, string> npCommIdCache,
            CancellationToken cancel)
        {
            switch (ShadPS4MatchIdHelper.GetKind(overrideMatchId))
            {
                case ShadPS4MatchIdKind.NpCommId:
                    if (npCommIdCache != null &&
                        npCommIdCache.TryGetValue(overrideMatchId, out var perUserXmlPath))
                    {
                        return ParseTrophyXml(game, perUserXmlPath, TrophyFormat.New, overrideMatchId, cancel);
                    }

                    return Task.FromResult(BuildNoAchievementsData(game));

                case ShadPS4MatchIdKind.TitleId:
                    if (titleCache != null &&
                        titleCache.TryGetValue(overrideMatchId, out var trophyDataPath))
                    {
                        var xmlPath = Path.Combine(trophyDataPath, "trophyfiles", "trophy00", "Xml", "TROP.XML");
                        if (File.Exists(xmlPath))
                        {
                            return ParseTrophyXml(game, xmlPath, TrophyFormat.Old, null, cancel);
                        }
                    }

                    return Task.FromResult(BuildNoAchievementsData(game));

                default:
                    return Task.FromResult(BuildNoAchievementsData(game));
            }
        }

        /// <summary>
        /// Shared trophy XML parser for both old and new formats.
        /// Differences are resolved via the format parameter and provider icon path helpers.
        /// </summary>
        private Task<GameAchievementData> ParseTrophyXml(
            Game game,
            string xmlPath,
            TrophyFormat format,
            string npcommid,
            CancellationToken cancel)
        {
            if (!File.Exists(xmlPath))
                return Task.FromResult(BuildNoAchievementsData(game));

            cancel.ThrowIfCancellationRequested();

            var iconsFolder = ResolveIconsFolder(format, npcommid, xmlPath);

            try
            {
                var doc = XDocument.Load(xmlPath);
                var achievements = new List<AchievementDetail>();
                var unlockedCount = 0;
                var groupNamesById = BuildGroupNamesDictionary(doc);
                var ps4Locale = MapGlobalLanguageToPs4Locale(_settings?.Persisted?.GlobalLanguage);

                foreach (var trophyElement in doc.Descendants("trophy"))
                {
                    cancel.ThrowIfCancellationRequested();

                    var trophyId = trophyElement.Attribute("id")?.Value;
                    var trophyType = trophyElement.Attribute("ttype")?.Value;
                    var isHidden = trophyElement.Attribute("hidden")?.Value == "yes";
                    var name = GetLocalizedElement(trophyElement, "name", ps4Locale)?.Trim();
                    var description = GetLocalizedElement(trophyElement, "detail", ps4Locale)?.Trim();
                    var groupId = trophyElement.Attribute("gid")?.Value?.Trim();
                    groupId = string.IsNullOrWhiteSpace(groupId) ? "0" : groupId;
                    groupNamesById.TryGetValue(groupId, out var groupName);

                    bool isUnlocked;
                    DateTime? unlockTime = null;

                    if (format == TrophyFormat.New)
                    {
                        isUnlocked = string.Equals(
                            trophyElement.Attribute("unlockstate")?.Value, "true",
                            StringComparison.OrdinalIgnoreCase);
                        if (isUnlocked)
                        {
                            unlockedCount++;
                            unlockTime = ConvertUnixTimestamp(trophyElement.Attribute("timestamp")?.Value);
                        }
                    }
                    else
                    {
                        isUnlocked = trophyElement.Attribute("unlockstate") != null;
                        if (isUnlocked)
                        {
                            unlockedCount++;
                            unlockTime = ConvertPs4Timestamp(trophyElement.Attribute("timestamp")?.Value);
                        }
                    }

                    var trophyTypeNormalized = NormalizeTrophyType(trophyType);
                    var iconPath = iconsFolder != null
                        ? GetTrophyIconPath(iconsFolder, trophyId)
                        : null;

                    achievements.Add(new AchievementDetail
                    {
                        ApiName = trophyId,
                        DisplayName = name,
                        Description = description,
                        UnlockedIconPath = iconPath,
                        LockedIconPath = iconPath,
                        Hidden = isHidden,
                        Unlocked = isUnlocked,
                        UnlockTimeUtc = unlockTime,
                        GlobalPercentUnlocked = null,
                        Rarity = GetRarityFromTrophyType(trophyTypeNormalized),
                        TrophyType = trophyTypeNormalized,
                        IsCapstone = trophyType?.ToUpperInvariant() == "P",
                        CategoryType = MapGroupIdToCategoryType(groupId),
                        Category = string.IsNullOrWhiteSpace(groupName) ? null : groupName.Trim()
                    });
                }

                var formatLabel = format == TrophyFormat.New ? "new format" : "old format";
                _logger?.Info($"[ShadPS4] Parsed {achievements.Count} trophies ({formatLabel}) for '{game.Name}' ({unlockedCount} unlocked)");

                return Task.FromResult(new GameAchievementData
                {
                    ProviderKey = "ShadPS4",
                    LibrarySourceName = game?.Source?.Name,
                    GameName = game?.Name,
                    PlayniteGameId = game?.Id,
                    HasAchievements = achievements.Count > 0,
                    Achievements = achievements,
                    LastUpdatedUtc = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"[ShadPS4] Failed to parse trophy XML for {game.Name}");
                return Task.FromResult(BuildNoAchievementsData(game));
            }
        }

        private string ResolveIconsFolder(TrophyFormat format, string npcommid, string xmlPath)
        {
            if (format == TrophyFormat.New && !string.IsNullOrWhiteSpace(npcommid))
            {
                var appDataPath = _provider?.GetAppDataPath();
                return !string.IsNullOrWhiteSpace(appDataPath)
                    ? Path.Combine(_provider.GetTrophyBasePath(appDataPath), npcommid, "Icons")
                    : null;
            }

            // Old format: icons are alongside the XML in ../Icons relative to Xml/
            var xmlDir = Path.GetDirectoryName(xmlPath);
            return xmlDir != null
                ? Path.Combine(xmlDir, "..", "Icons")
                : null;
        }

        #region Timestamp conversion

        /// <summary>
        /// Converts a standard Unix timestamp (seconds since 1970-01-01) to UTC DateTime.
        /// </summary>
        private static DateTime? ConvertUnixTimestamp(string timestamp)
        {
            if (string.IsNullOrEmpty(timestamp)) return null;
            try
            {
                var seconds = long.Parse(timestamp);
                return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime : (DateTime?)null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Converts a PS4 RTC timestamp (microseconds since 2008-01-01 epoch)
        /// with ShadPS4-specific year offset correction.
        /// </summary>
        private DateTime? ConvertPs4Timestamp(string timestamp)
        {
            if (string.IsNullOrEmpty(timestamp)) return null;
            try
            {
                var milliseconds = (long)(ulong.Parse(timestamp) / 1000);
                var utcTime = Ps4Epoch.AddMilliseconds(milliseconds);

                if (utcTime.Year > YearOffset)
                {
                    return new DateTime(
                        utcTime.Year - YearOffset, utcTime.Month, utcTime.Day,
                        utcTime.Hour, utcTime.Minute, utcTime.Second, utcTime.Millisecond,
                        DateTimeKind.Utc);
                }

                return utcTime;
            }
            catch { return null; }
        }

        #endregion

        #region XML helpers

        private static Dictionary<string, string> BuildGroupNamesDictionary(XDocument doc)
        {
            var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (doc == null) return groups;

            foreach (var groupElement in doc.Descendants("group"))
            {
                var id = groupElement.Attribute("id")?.Value?.Trim();
                var name = groupElement.Element("name")?.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                    groups[id] = name;
            }

            return groups;
        }

        private static string GetLocalizedElement(XElement trophyElement, string elementName, string language)
        {
            if (string.IsNullOrWhiteSpace(language))
                return trophyElement.Element(elementName)?.Value;

            var localized = trophyElement.Elements(elementName)
                .FirstOrDefault(e => string.Equals(e.Attribute("lang")?.Value, language, StringComparison.OrdinalIgnoreCase));
            if (localized != null) return localized.Value;

            return trophyElement.Elements(elementName)
                .FirstOrDefault(e => e.Attribute("lang") == null)?.Value
                ?? trophyElement.Element(elementName)?.Value;
        }

        private static string MapGlobalLanguageToPs4Locale(string globalLanguage)
        {
            if (string.IsNullOrWhiteSpace(globalLanguage)) return null;
            return globalLanguage.Trim().ToLowerInvariant() switch
            {
                "english" => "en", "french" => "fr", "spanish" => "es",
                "german" => "de", "italian" => "it", "japanese" => "ja",
                "dutch" => "nl", "portuguese" => "pt", "russian" => "ru",
                "korean" => "ko", "chinese" => "zh", "polish" => "pl",
                "danish" => "da", "finnish" => "fi", "norwegian" => "no",
                "swedish" => "sv", "turkish" => "tr", "czech" => "cs",
                "hungarian" => "hu", "greek" => "el",
                "brazilian" => "pt-br", "latam" => "es-419",
                _ => null
            };
        }

        #endregion

        #region Trophy metadata helpers

        private string GetTrophyIconPath(string iconsFolder, string trophyId)
        {
            if (string.IsNullOrWhiteSpace(trophyId)) return null;
            try
            {
                var iconPath = Path.Combine(iconsFolder, $"TROP{trophyId.PadLeft(3, '0')}.PNG");
                return File.Exists(iconPath) ? iconPath : null;
            }
            catch { return null; }
        }

        private static string NormalizeTrophyType(string trophyType)
        {
            if (string.IsNullOrWhiteSpace(trophyType)) return null;
            return trophyType.ToUpperInvariant() switch
            {
                "P" => "platinum", "G" => "gold", "S" => "silver", "B" => "bronze", _ => null
            };
        }

        private static RarityTier GetRarityFromTrophyType(string trophyType)
        {
            switch ((trophyType ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "platinum": case "p": return RarityTier.UltraRare;
                case "gold": case "g": return RarityTier.Rare;
                case "silver": case "s": return RarityTier.Uncommon;
                default: return RarityTier.Common;
            }
        }

        private static string MapGroupIdToCategoryType(string groupId)
        {
            var n = (groupId ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(n) ||
                n.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("000", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("default", StringComparison.OrdinalIgnoreCase) ||
                n.Equals("base", StringComparison.OrdinalIgnoreCase)
                ? "Base" : "DLC";
        }

        private static GameAchievementData BuildNoAchievementsData(Game game)
        {
            return new GameAchievementData
            {
                ProviderKey = "ShadPS4",
                LibrarySourceName = game?.Source?.Name,
                GameName = game?.Name,
                PlayniteGameId = game?.Id,
                HasAchievements = false,
                LastUpdatedUtc = DateTime.UtcNow
            };
        }

        #endregion

        #region Game path helpers

        private string ExtractTitleIdFromGame(Game game)
        {
            var rawInstallDir = game?.InstallDirectory;
            if (string.IsNullOrWhiteSpace(rawInstallDir)) return null;

            var installDir = ExpandGamePath(game, rawInstallDir);
            if (string.IsNullOrWhiteSpace(installDir)) return null;

            var match = TitleIdPattern.Match(installDir);
            return match.Success ? ShadPS4MatchIdHelper.Normalize(match.Groups[1].Value) : null;
        }

        private string ExpandGamePath(Game game, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            if (_provider != null) return _provider.ExpandGamePath(game, path);
            try { return _playniteApi?.ExpandGameVariables(game, path) ?? path; }
            catch { return path; }
        }

        #endregion
    }
}
