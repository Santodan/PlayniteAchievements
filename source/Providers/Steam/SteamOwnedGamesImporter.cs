using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Steam
{
    internal sealed class SteamOwnedGamesImporter
    {
        internal sealed class ImportResult
        {
            public bool IsAuthenticated { get; set; }

            public bool HasSteamLibraryPlugin { get; set; }

            public int OwnedCount { get; set; }

            public int ExistingCount { get; set; }

            public int ImportedCount { get; set; }

            public int FailedCount { get; set; }

            public List<Guid> ImportedGameIds { get; } = new List<Guid>();
        }

        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly SteamSessionManager _sessionManager;

        public SteamOwnedGamesImporter(
            IPlayniteAPI api,
            ILogger logger,
            SteamSessionManager sessionManager)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        }

        public async Task<ImportResult> ImportOwnedGamesAsync(CancellationToken ct)
        {
            var result = new ImportResult();

            var probe = await _sessionManager.ProbeAuthStateAsync(ct).ConfigureAwait(false);
            var steamUserId = probe?.UserId?.Trim();
            if (!probe.IsSuccess || string.IsNullOrWhiteSpace(steamUserId))
            {
                _logger?.Warn("[SteamAch] Owned-games import skipped because Steam web auth is not available.");
                return result;
            }

            result.IsAuthenticated = true;

            var steamLibraryPlugin = ResolveSteamLibraryPlugin();
            if (steamLibraryPlugin == null)
            {
                _logger?.Warn("[SteamAch] Owned-games import skipped because the Steam library plugin could not be resolved.");
                return result;
            }

            result.HasSteamLibraryPlugin = true;

            using (var metadataDownloader = steamLibraryPlugin.GetMetadataDownloader())
            using (var steamClient = new SteamHttpClient(_api, _logger, _sessionManager, pluginUserDataPath: null))
            {
                var ownedGames = await steamClient.GetOwnedGamesFromSessionAsync(ct).ConfigureAwait(false);
                result.OwnedCount = ownedGames.Count;
                if (ownedGames.Count == 0)
                {
                    return result;
                }

                var existingSteamAppIds = new HashSet<int>(
                    _api.Database.Games
                        .Where(game => game != null && game.PluginId == SteamDataProvider.SteamPluginId)
                        .Select(TryParseSteamAppId)
                        .Where(appId => appId > 0));

                var missingGames = ownedGames
                    .Where(game => game?.AppId > 0)
                    .GroupBy(game => game.AppId.Value)
                    .Select(group => group.First())
                    .Where(game => !existingSteamAppIds.Contains(game.AppId.Value))
                    .ToList();

                result.ExistingCount = Math.Max(0, result.OwnedCount - missingGames.Count);
                if (missingGames.Count == 0)
                {
                    return result;
                }

                _api.Database.Games.BeginBufferUpdate();
                try
                {
                    foreach (var ownedGame in missingGames)
                    {
                        ct.ThrowIfCancellationRequested();

                        try
                        {
                            var imported = _api.Database.ImportGame(BuildMetadata(ownedGame), steamLibraryPlugin);
                            if (imported == null)
                            {
                                result.FailedCount++;
                                continue;
                            }

                            ApplyDownloadedMetadata(imported, metadataDownloader);

                            result.ImportedCount++;
                            result.ImportedGameIds.Add(imported.Id);
                            existingSteamAppIds.Add(ownedGame.AppId.Value);
                        }
                        catch (Exception ex)
                        {
                            result.FailedCount++;
                            _logger?.Warn(ex, $"[SteamAch] Failed importing owned Steam game appId={ownedGame?.AppId} name='{ownedGame?.Name}'.");
                        }
                    }
                }
                finally
                {
                    _api.Database.Games.EndBufferUpdate();
                }
            }

            _logger?.Info($"[SteamAch] Owned-games import finished. owned={result.OwnedCount} existing={result.ExistingCount} imported={result.ImportedCount} failed={result.FailedCount}");
            return result;
        }

        private static int TryParseSteamAppId(Game game)
        {
            if (game == null || string.IsNullOrWhiteSpace(game.GameId))
            {
                return 0;
            }

            return int.TryParse(game.GameId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appId)
                ? appId
                : 0;
        }

        private void ApplyDownloadedMetadata(Game importedGame, LibraryMetadataProvider metadataDownloader)
        {
            if (importedGame == null || metadataDownloader == null)
            {
                return;
            }

            try
            {
                var metadata = metadataDownloader.GetMetadata(importedGame);
                if (metadata == null)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(metadata.Name))
                {
                    importedGame.Name = metadata.Name;
                }

                if (!string.IsNullOrWhiteSpace(metadata.SortingName))
                {
                    importedGame.SortingName = metadata.SortingName;
                }

                if (!string.IsNullOrWhiteSpace(metadata.Description))
                {
                    importedGame.Description = metadata.Description;
                }

                var iconId = PersistMetadataFile(importedGame.Id, metadata.Icon);
                if (!string.IsNullOrWhiteSpace(iconId))
                {
                    importedGame.Icon = iconId;
                }

                var coverId = PersistMetadataFile(importedGame.Id, metadata.CoverImage);
                if (!string.IsNullOrWhiteSpace(coverId))
                {
                    importedGame.CoverImage = coverId;
                }

                var backgroundId = PersistMetadataFile(importedGame.Id, metadata.BackgroundImage);
                if (!string.IsNullOrWhiteSpace(backgroundId))
                {
                    importedGame.BackgroundImage = backgroundId;
                }

                if (metadata.ReleaseDate != null)
                {
                    importedGame.ReleaseDate = metadata.ReleaseDate;
                }

                if (metadata.CriticScore.HasValue)
                {
                    importedGame.CriticScore = metadata.CriticScore;
                }

                if (metadata.CommunityScore.HasValue)
                {
                    importedGame.CommunityScore = metadata.CommunityScore;
                }

                if (metadata.Platforms?.Count > 0)
                {
                    ReplaceCollection(importedGame.Platforms, _api.Database.Platforms.Add(metadata.Platforms));
                }

                if (metadata.Genres?.Count > 0)
                {
                    ReplaceCollection(importedGame.Genres, _api.Database.Genres.Add(metadata.Genres));
                }

                if (metadata.Developers?.Count > 0)
                {
                    ReplaceCollection(importedGame.Developers, _api.Database.Companies.Add(metadata.Developers));
                }

                if (metadata.Publishers?.Count > 0)
                {
                    ReplaceCollection(importedGame.Publishers, _api.Database.Companies.Add(metadata.Publishers));
                }

                if (metadata.Tags?.Count > 0)
                {
                    ReplaceCollection(importedGame.Tags, _api.Database.Tags.Add(metadata.Tags));
                }

                if (metadata.Categories?.Count > 0)
                {
                    ReplaceCollection(importedGame.Categories, _api.Database.Categories.Add(metadata.Categories));
                }

                if (metadata.Features?.Count > 0)
                {
                    ReplaceCollection(importedGame.Features, _api.Database.Features.Add(metadata.Features));
                }

                if (metadata.AgeRatings?.Count > 0)
                {
                    ReplaceCollection(importedGame.AgeRatings, _api.Database.AgeRatings.Add(metadata.AgeRatings));
                }

                if (metadata.Regions?.Count > 0)
                {
                    ReplaceCollection(importedGame.Regions, _api.Database.Regions.Add(metadata.Regions));
                }

                if (metadata.Series?.Count > 0)
                {
                    ReplaceCollection(importedGame.Series, _api.Database.Series.Add(metadata.Series));
                }

                if (metadata.Links?.Count > 0)
                {
                    ReplaceCollection(importedGame.Links, metadata.Links);
                }

                _api.Database.Games.Update(importedGame);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[SteamAch] Failed applying downloaded Steam metadata for imported game '{importedGame?.Name}'.");
            }
        }

        private static void ReplaceCollection<T>(ICollection<T> target, IEnumerable<T> source)
        {
            if (target == null || source == null)
            {
                return;
            }

            target.Clear();
            foreach (var item in source)
            {
                target.Add(item);
            }
        }

        private string PersistMetadataFile(Guid gameId, MetadataFile metadataFile)
        {
            if (gameId == Guid.Empty || metadataFile == null || !metadataFile.HasImageData)
            {
                return null;
            }

            string tempFilePath = null;
            try
            {
                if (metadataFile.HasContent && metadataFile.Content?.Length > 0)
                {
                    var fileName = string.IsNullOrWhiteSpace(metadataFile.FileName)
                        ? $"steam_meta_{Guid.NewGuid():N}{GetExtensionFromPath(metadataFile.Path)}"
                        : metadataFile.FileName.Trim();
                    tempFilePath = Path.Combine(CreateMetadataTempDirectory(), SanitizeFileName(fileName));
                    File.WriteAllBytes(tempFilePath, metadataFile.Content);
                    return _api.Database.AddFile(tempFilePath, gameId);
                }

                if (!string.IsNullOrWhiteSpace(metadataFile.Path) && File.Exists(metadataFile.Path))
                {
                    return _api.Database.AddFile(metadataFile.Path, gameId);
                }

                if (!string.IsNullOrWhiteSpace(metadataFile.Path) && Uri.TryCreate(metadataFile.Path, UriKind.Absolute, out var uri))
                {
                    var targetExtension = GetExtensionFromPath(uri.AbsolutePath);
                    var fileName = string.IsNullOrWhiteSpace(metadataFile.FileName)
                        ? $"steam_meta_{Guid.NewGuid():N}{targetExtension}"
                        : metadataFile.FileName.Trim();
                    tempFilePath = Path.Combine(CreateMetadataTempDirectory(), SanitizeFileName(fileName));

                    using (var webClient = new System.Net.WebClient())
                    {
                        webClient.DownloadFile(uri, tempFilePath);
                    }

                    return _api.Database.AddFile(tempFilePath, gameId);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[SteamAch] Failed persisting metadata file for gameId={gameId} from '{metadataFile?.Path}'.");
            }
            finally
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(tempFilePath) && File.Exists(tempFilePath))
                    {
                        File.Delete(tempFilePath);
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static string CreateMetadataTempDirectory()
        {
            var directory = Path.Combine(Path.GetTempPath(), "PlayniteAchievements", "SteamMetadataImport");
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static string GetExtensionFromPath(string path)
        {
            try
            {
                var extension = Path.GetExtension(path ?? string.Empty);
                return string.IsNullOrWhiteSpace(extension) ? ".img" : extension;
            }
            catch
            {
                return ".img";
            }
        }

        private static string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return $"steam_meta_{Guid.NewGuid():N}.img";
            }

            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string(fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
            return string.IsNullOrWhiteSpace(sanitized) ? $"steam_meta_{Guid.NewGuid():N}.img" : sanitized;
        }

        private static GameMetadata BuildMetadata(Models.OwnedGame ownedGame)
        {
            var appId = ownedGame?.AppId ?? 0;
            var name = string.IsNullOrWhiteSpace(ownedGame?.Name)
                ? $"Steam App {appId}"
                : ownedGame.Name.Trim();

            return new GameMetadata
            {
                Name = name,
                SortingName = name,
                GameId = appId.ToString(CultureInfo.InvariantCulture),
                Source = new MetadataNameProperty("Steam"),
                IsInstalled = false,
                Playtime = (ulong)Math.Max(0, ownedGame?.PlaytimeForever ?? 0),
                LastActivity = ownedGame?.RTimeLastPlayed.HasValue == true && ownedGame.RTimeLastPlayed.Value > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(ownedGame.RTimeLastPlayed.Value).UtcDateTime
                    : (DateTime?)null
            };
        }

        private LibraryPlugin ResolveSteamLibraryPlugin()
        {
            try
            {
                return _api.Addons?.Plugins?
                    .OfType<LibraryPlugin>()
                    .FirstOrDefault(plugin => plugin != null && plugin.Id == SteamDataProvider.SteamPluginId);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[SteamAch] Failed resolving Steam library plugin instance.");
                return null;
            }
        }
    }
}