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
        private readonly ShadPS4DataProvider _provider;
        private readonly IPlayniteAPI _playniteApi;

        // PS4's RTC epoch is January 1, 2008 00:00:00 UTC
        private static readonly DateTime Ps4Epoch = new DateTime(2008, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // The consistent difference we need to subtract (ShadPS4-specific offset)
        private const int YearOffset = 2007;

        public ShadPS4Scanner(ILogger logger, PlayniteAchievementsSettings settings, ShadPS4DataProvider provider = null, IPlayniteAPI playniteApi = null, string pluginUserDataPath = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
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

            // Use the provider's cache if available, otherwise build our own
            Dictionary<string, string> titleCache;
            if (_provider != null)
            {
                titleCache = _provider.GetOrBuildTitleCache();
            }
            else
            {
                titleCache = await BuildTitleIdCacheAsync(cancel).ConfigureAwait(false);
            }

            if (titleCache == null || titleCache.Count == 0)
            {
                _logger?.Warn("[ShadPS4] No games found in ShadPS4 user/game_data folder.");
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            _logger?.Info($"[ShadPS4] Scanning {titleCache.Count} titles from game_data folder.");

            return await RefreshPipeline.RunProviderGamesAsync(
                gamesToRefresh,
                onGameStarting,
                async (game, token) =>
                {
                    var data = await FetchGameDataAsync(game, titleCache, token).ConfigureAwait(false);
                    return new RefreshPipeline.ProviderGameResult
                    {
                        Data = data
                    };
                },
                onGameCompleted,
                isAuthRequiredException: _ => false,
                onGameError: (game, ex, consecutiveErrors) =>
                {
                    _logger?.Error(ex, $"[ShadPS4] Error processing game '{game?.Name}'");
                },
                delayBetweenGamesAsync: null,
                delayAfterErrorAsync: null,
                cancel).ConfigureAwait(false);
        }

        /// <summary>
        /// Builds a cache of title ID to trophy data directory path.
        /// Cache structure: title_id (e.g., "CUSA00432") -> full path to game_data directory
        /// </summary>
        private async Task<Dictionary<string, string>> BuildTitleIdCacheAsync(CancellationToken cancel)
        {
            var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var gameDataPath = _settings?.Persisted?.ShadPS4GameDataPath;

            if (string.IsNullOrWhiteSpace(gameDataPath))
            {
                _logger?.Warn("[ShadPS4] No game_data path configured in settings");
                return cache;
            }

            if (!Directory.Exists(gameDataPath))
            {
                _logger?.Warn($"[ShadPS4] game_data folder not found at {gameDataPath}");
                return cache;
            }

            try
            {
                var titleDirectories = Directory.GetDirectories(gameDataPath);

                foreach (var titleDir in titleDirectories)
                {
                    cancel.ThrowIfCancellationRequested();

                    var titleId = Path.GetFileName(titleDir);
                    if (string.IsNullOrWhiteSpace(titleId))
                    {
                        continue;
                    }

                    // Verify trophy data exists
                    var xmlPath = Path.Combine(titleDir, "trophyfiles", "trophy00", "Xml", "TROP.XML");
                    if (File.Exists(xmlPath))
                    {
                        cache[titleId.ToUpperInvariant()] = titleDir;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[ShadPS4] Failed to enumerate title directories.");
            }

            return await Task.FromResult(cache).ConfigureAwait(false);
        }

        /// <summary>
        /// Extracts the PS4 title ID from the game's install directory path.
        /// PS4 title IDs follow pattern: AAAA12345 (e.g., CUSA00432)
        /// </summary>
        private string ExtractTitleIdFromGame(Game game)
        {
            var rawInstallDir = game?.InstallDirectory;
            if (string.IsNullOrWhiteSpace(rawInstallDir))
            {
                return null;
            }

            var installDir = ExpandGamePath(game, rawInstallDir);
            if (string.IsNullOrWhiteSpace(installDir))
            {
                return null;
            }

            // Search for title ID pattern in the path
            var match = TitleIdPattern.Match(installDir);
            if (match.Success)
            {
                return match.Groups[1].Value.ToUpperInvariant();
            }

            return null;
        }

        /// <summary>
        /// Expands path variables in game paths using Playnite's variable expansion.
        /// </summary>
        private string ExpandGamePath(Game game, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            // Use provider's expansion if available
            if (_provider != null)
            {
                return _provider.ExpandGamePath(game, path);
            }

            // Fallback: use Playnite API directly if available
            try
            {
                return _playniteApi?.ExpandGameVariables(game, path) ?? path;
            }
            catch
            {
                return path;
            }
        }

        // PS4 title ID patterns: CUSA (US), BCAS (Asia), PCAS (Asia digital), etc.
        private static readonly System.Text.RegularExpressions.Regex TitleIdPattern =
            new System.Text.RegularExpressions.Regex(@"\b([A-Z]{4}\d{5})\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private Task<GameAchievementData> FetchGameDataAsync(
            Game game,
            Dictionary<string, string> titleCache,
            CancellationToken cancel)
        {
            if (game == null)
            {
                return Task.FromResult<GameAchievementData>(null);
            }

            // Extract title ID from game's install directory
            var titleId = ExtractTitleIdFromGame(game);

            if (string.IsNullOrWhiteSpace(titleId))
            {
                return Task.FromResult(BuildNoAchievementsData(game));
            }

            // Look up trophy data directory in cache
            if (!titleCache.TryGetValue(titleId, out var trophyDataPath))
            {
                return Task.FromResult(BuildNoAchievementsData(game));
            }

            cancel.ThrowIfCancellationRequested();

            var xmlPath = Path.Combine(trophyDataPath, "trophyfiles", "trophy00", "Xml", "TROP.XML");

            if (!File.Exists(xmlPath))
            {
                return Task.FromResult(BuildNoAchievementsData(game));
            }

            var iconsFolder = Path.Combine(trophyDataPath, "trophyfiles", "trophy00", "Icons");

            try
            {
                var doc = XDocument.Load(xmlPath);
                var achievements = new List<AchievementDetail>();
                var unlockedCount = 0;
                var groupNamesById = BuildGroupNamesDictionary(doc);

                // Map global language to PS4 locale (same format as PS3)
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

                    // Check if trophy is unlocked
                    var isUnlocked = trophyElement.Attribute("unlockstate") != null;
                    DateTime? unlockTime = null;

                    if (isUnlocked)
                    {
                        unlockedCount++;
                        var timestamp = trophyElement.Attribute("timestamp")?.Value;
                        unlockTime = ConvertPs4Timestamp(timestamp);
                    }

                    string trophyTypeNormalized = NormalizeTrophyType(trophyType);

                    // Build icon path - caching handled by DiskImageService
                    var iconPath = GetTrophyIconPath(iconsFolder, trophyId);

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
                        TrophyType = trophyTypeNormalized,
                        IsCapstone = trophyType?.ToUpperInvariant() == "P",
                        CategoryType = MapGroupIdToCategoryType(groupId),
                        Category = string.IsNullOrWhiteSpace(groupName) ? null : groupName.Trim()
                    });
                }

                _logger?.Info($"[ShadPS4] Parsed {achievements.Count} trophies for '{game.Name}' ({unlockedCount} unlocked)");

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
                _logger?.Error(ex, $"[ShadPS4] Failed to parse TROP.XML for {game.Name}");
                return Task.FromResult(BuildNoAchievementsData(game));
            }
        }

        private static Dictionary<string, string> BuildGroupNamesDictionary(XDocument doc)
        {
            var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (doc == null)
            {
                return groups;
            }

            foreach (var groupElement in doc.Descendants("group"))
            {
                var id = groupElement.Attribute("id")?.Value?.Trim();
                var name = groupElement.Element("name")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                groups[id] = name;
            }

            return groups;
        }

        private static string MapGroupIdToCategoryType(string groupId)
        {
            var normalized = (groupId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized) ||
                string.Equals(normalized, "0", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "000", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "default", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "base", StringComparison.OrdinalIgnoreCase))
            {
                return "Base";
            }

            return "DLC";
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

        /// <summary>
        /// Converts a PS4 timestamp to UTC DateTime.
        /// PS4 epoch: 2008-01-01 00:00:00 UTC
        /// Format: Microseconds since epoch
        /// Correction: Subtract 2007 years from result (ShadPS4-specific offset)
        /// </summary>
        private DateTime? ConvertPs4Timestamp(string timestamp)
        {
            if (string.IsNullOrEmpty(timestamp))
            {
                return null;
            }

            try
            {
                var tickValue = ulong.Parse(timestamp);

                // Divide by 1000 to get milliseconds instead of microseconds
                var milliseconds = (long)(tickValue / 1000);

                // Add milliseconds to PS4 epoch
                var utcTime = Ps4Epoch.AddMilliseconds(milliseconds);

                // Adjust the year by subtracting the offset
                if (utcTime.Year > YearOffset)
                {
                    return new DateTime(
                        utcTime.Year - YearOffset,
                        utcTime.Month,
                        utcTime.Day,
                        utcTime.Hour,
                        utcTime.Minute,
                        utcTime.Second,
                        utcTime.Millisecond,
                        DateTimeKind.Utc);
                }

                return utcTime;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string NormalizeTrophyType(string trophyType)
        {
            if (string.IsNullOrWhiteSpace(trophyType))
            {
                return null;
            }

            return trophyType.ToUpperInvariant() switch
            {
                "P" => "platinum",
                "G" => "gold",
                "S" => "silver",
                "B" => "bronze",
                _ => null
            };
        }

        /// <summary>
        /// Gets the trophy icon path from the ShadPS4 installation.
        /// Icon caching is handled by DiskImageService via AchievementService.
        /// </summary>
        private string GetTrophyIconPath(string iconsFolder, string trophyId)
        {
            if (string.IsNullOrWhiteSpace(trophyId))
            {
                return null;
            }

            try
            {
                // Trophy icons follow TROP###.PNG format with zero-padded ID
                var iconFileName = $"TROP{trophyId.PadLeft(3, '0')}.PNG";
                var iconPath = Path.Combine(iconsFolder, iconFileName);

                if (File.Exists(iconPath))
                {
                    return iconPath;
                }

                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Gets a localized element value from a trophy element.
        /// Tries to find an element with matching lang attribute, falls back to element without lang.
        /// </summary>
        private static string GetLocalizedElement(XElement trophyElement, string elementName, string language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                // No language specified, return first element found
                return trophyElement.Element(elementName)?.Value;
            }

            // Try to find element with matching lang attribute
            var localizedElement = trophyElement.Elements(elementName)
                .FirstOrDefault(e => string.Equals(e.Attribute("lang")?.Value, language, StringComparison.OrdinalIgnoreCase));

            if (localizedElement != null)
            {
                return localizedElement.Value;
            }

            // Fall back to element without lang attribute (default language)
            var defaultElement = trophyElement.Elements(elementName)
                .FirstOrDefault(e => e.Attribute("lang") == null);

            return defaultElement?.Value ?? trophyElement.Element(elementName)?.Value;
        }

        /// <summary>
        /// Maps a global language setting to PS4 locale code.
        /// </summary>
        private static string MapGlobalLanguageToPs4Locale(string globalLanguage)
        {
            if (string.IsNullOrWhiteSpace(globalLanguage))
            {
                return null;
            }

            var normalized = globalLanguage.Trim().ToLowerInvariant();

            return normalized switch
            {
                "english" => "en",
                "french" => "fr",
                "spanish" => "es",
                "german" => "de",
                "italian" => "it",
                "japanese" => "ja",
                "dutch" => "nl",
                "portuguese" => "pt",
                "russian" => "ru",
                "korean" => "ko",
                "chinese" => "zh",
                "polish" => "pl",
                "danish" => "da",
                "finnish" => "fi",
                "norwegian" => "no",
                "swedish" => "sv",
                "turkish" => "tr",
                "czech" => "cs",
                "hungarian" => "hu",
                "greek" => "el",
                "brazilian" => "pt-br",
                "latam" => "es-419",
                _ => null
            };
        }
    }
}
