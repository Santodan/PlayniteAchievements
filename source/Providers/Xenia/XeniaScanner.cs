using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Xenia
{
    internal class XeniaScanner
    {
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly string _pluginUserDataPath;
        private readonly string _accountFolderPath;
        
        public XeniaScanner(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            string pluginUserDataPath,
            string accountFolderPath)
        {
            _logger = logger;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _accountFolderPath = accountFolderPath ?? throw new ArgumentNullException(nameof(accountFolderPath));
            _pluginUserDataPath = pluginUserDataPath ?? string.Empty;
        }

        public async Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(_settings.Persisted.XeniaAccountPath))
            {
                _logger?.Warn("[Xenia] Missing path to account - cannot scan achievements.");
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            if (gamesToRefresh is null || gamesToRefresh.Count == 0)
            {
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            var result = await RefreshPipeline.RunProviderGamesAsync(
                gamesToRefresh,
                onGameStarting,
                async (game, token) =>
                {
                    if (game == null)
                    {
                        return RefreshPipeline.ProviderGameResult.Skipped();
                    }
                    if(!game.IsInstalled)
                    {
                        _logger.Warn("[Xenia] Game isn't installed unable to resolve titleID!");
                        return RefreshPipeline.ProviderGameResult.Skipped();
                    }

                    if (!ResolveTitleID(game, out var titleID))
                    {
                        return RefreshPipeline.ProviderGameResult.Skipped();
                    }

                    GPDResolver resolver = new GPDResolver(_pluginUserDataPath);
                    var achievements = resolver.LoadGPD(_accountFolderPath, titleID);
                    int.TryParse(titleID, out var parsedId);

                    var data = new GameAchievementData
                    {
                        AppId = parsedId,
                        GameName = game?.Name,
                        ProviderKey = "Xenia",
                        LibrarySourceName = game?.Source?.Name,
                        LastUpdatedUtc = DateTime.UtcNow,
                        HasAchievements = achievements.Count > 0,
                        PlayniteGameId = game?.Id,
                        Achievements = achievements
                    };

                    return new RefreshPipeline.ProviderGameResult
                    {
                        Data = data
                    };
                },
                onGameCompleted,
                isAuthRequiredException: _ => false,
                onGameError: (game, ex, consecutiveErrors) =>
                {
                    _logger?.Warn(ex, $"[Xenia] Failed to scan game '{game?.Name}'");
                },
                delayBetweenGamesAsync: null,
                delayAfterErrorAsync: null,
                cancel).ConfigureAwait(false);

            return result;
        }

        bool ResolveTitleID(Game game, out string titleID)
        {
            foreach (var rom in game.Roms)
            {
                int exeAreaSize = 600;
                var path = rom.Path;
            
                if (path.EndsWith(".iso") || path.EndsWith(".xex"))
                {
                    using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open);
                    using var accessor = mmf.CreateViewStream(0, new FileInfo(path).Length, MemoryMappedFileAccess.Read);
            
                    var chunksize = 8 * 1024; // 8 KB buffer
                    var buffer = new byte[chunksize];
                    ReadOnlySpan<byte> spanbuffer;
                    ReadOnlySpan<byte> previousspanbuffer = new ReadOnlySpan<byte>();
                    var position = 0;
            
                    var bytesRead = 0;
                    byte[] combinedbuffer = new byte[chunksize * 2];
                    byte[] exeChunk = new byte[exeAreaSize];
            
                    while (accessor.Position < accessor.Length)
                    {
                        bytesRead = accessor.Read(buffer, 0, buffer.Length);
                        if (bytesRead == 0) break;
            
                        spanbuffer = new ReadOnlySpan<byte>(buffer);
                        var foundexe = spanbuffer.IndexOf(Encoding.UTF8.GetBytes(".exe").AsSpan());
                        var foundpe = spanbuffer.IndexOf(Encoding.UTF8.GetBytes(".pe").AsSpan());
            
                        if (foundexe != -1)
                        {
                            previousspanbuffer.CopyTo(combinedbuffer);
                            spanbuffer.CopyTo(combinedbuffer.AsSpan(chunksize));
            
                            // Pull the previous 600 characters and convert to char array (600 is arbitry just to account for possible lots of data between titleID and .exe entry)
                            Array.Copy(combinedbuffer, (foundexe + chunksize) - exeAreaSize, exeChunk, 0, exeAreaSize);
            
                            //Console.WriteLine($"\tFound .exe at {foundexe}");
                            var temptitleID = CheckChunk(ref exeChunk);
                            if (!string.IsNullOrEmpty(temptitleID))
                            {
                                titleID = temptitleID;
                                return true;
                                   
                            }
                        }
                        if (foundpe != -1)
                        {
                            previousspanbuffer.CopyTo(combinedbuffer);
                            spanbuffer.CopyTo(combinedbuffer.AsSpan(chunksize));
            
                            // Pull the previous 600 characters and convert to char array (600 is arbitry just to account for possible lots of data between titleID and .exe entry)
                            Array.Copy(combinedbuffer, (foundpe + chunksize) - exeAreaSize, exeChunk, 0, exeAreaSize);
            
                            //Console.WriteLine($"\tFound .pe at {foundpe}");
                            var temptitleID = CheckChunk(ref exeChunk);
                            if (!string.IsNullOrEmpty(temptitleID))
                            {
                                titleID = temptitleID;
                                return true;
                            }
                        }
            
                        position += bytesRead;
                        previousspanbuffer = spanbuffer;
                    }
                }
                else
                {
                    _logger.Error("[Xenia] Unsupported ROM only .xex or .iso are supported!");
                }
            }

            titleID = "";
            return false;
        }

        private string CheckChunk(ref byte[] chunk)
        {
            // Find this marker as it appears close to .exe and .pe files and its exactly 12 bytes after the titleID
            // From my testing this isn't 100% accurate out of 28 games tested 26 passed 
            byte[] marker = { 0, 1, 0, 1 };
            for (int i = 0; i < chunk.Length; i++)
            {
                if (chunk[i] != marker[0])
                    continue;

                int arrayToFindCounter = 0;
                bool wasFound = true;
                for (int j = i; j < i + marker.Length; j++)
                {
                    if (j > chunk.Length - 1)
                    {
                        wasFound = false;
                        break;
                    }

                    if (marker[arrayToFindCounter] == chunk[j])
                    {
                        arrayToFindCounter++;
                    }
                    else
                    {
                        wasFound = false;
                        break;
                    }
                }

                if (wasFound)
                {
                    // Once found the location of the marker go back 20 chars to start reading the titleID
                    // Check to see if found ID only contains letter and characters else its not the titleID
                    var titleID = System.Text.Encoding.UTF8.GetString(chunk, (i - 20), 8);
                    if (titleID.All(char.IsLetterOrDigit))
                    {
                        return System.Text.Encoding.UTF8.GetString(chunk, (i - 20), 8);
                    }
                }
            }

            return "";
        }
    }
}
