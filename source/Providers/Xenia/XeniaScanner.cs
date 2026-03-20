using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
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
        private readonly IPlayniteAPI _playniteApi;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly string _pluginUserDataPath;
        private readonly string _accountFolderPath;

        List<KeyValuePair<Guid, string>> _titleIDCache = new List<KeyValuePair<Guid, string>>();
        List<string> KnownPublishers = new List<string>() { "5444", "464F", "4143", "4156", "4158", "4142", "4144", "4150", "4151", "4157", "414B", "4148", "4153", "4159", "4154", "424D", "4241", "4257", "4253", "4242", "4248", "4246", "4245", "4247", "4254", "4244", "4252", "4256", "4255", "4343", "434D", "4356", "4354", "4458", "4445", "4443", "4546", "4553", "4541", "454D", "4543", "454C", "4556", "464C", "4649", "4653", "4746", "4745", "4756", "4857", "4850", "4845", "4855", "4946", "494F", "494D", "4947", "494C", "4950", "4958", "4A41", "4A57", "4B59", "4B4F", "4B4E", "4B41", "4B54", "4C41", "4D4A", "4D45", "4D44", "4D53", "4D57", "4D4D", "4E4D", "4E4B", "4E4C", "4F47", "4F58", "5058", "504C", "5043", "5241", "5341", "5343", "5345", "5353", "534E", "5350", "5351", "5354", "5355", "5357", "5441", "5454", "544B", "544D", "5443", "5451", "5453", "5553", "5647", "5656", "5643", "5655", "5745", "5752", "584B", "584C", "5841", "5849", "5850", "5942", "5A44" };

        public XeniaScanner(
            ILogger logger,
            IPlayniteAPI playniteApi,
            PlayniteAchievementsSettings settings,
            string pluginUserDataPath,
            string accountFolderPath)
        {
            _logger = logger;
            _playniteApi = playniteApi;
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _accountFolderPath = accountFolderPath ?? throw new ArgumentNullException(nameof(accountFolderPath));
            if(_accountFolderPath.EndsWith("\\"))
            {
                _accountFolderPath = _accountFolderPath.Remove(_accountFolderPath.Length - 1);
            }
            _pluginUserDataPath = pluginUserDataPath ?? string.Empty;

            if(File.Exists($"{_pluginUserDataPath}\\xenia\\titleID_cache.json"))
            {
                var jsonfile = File.ReadAllText($"{_pluginUserDataPath}\\xenia\\titleID_cache.json");
                try
                {
                    _titleIDCache = JsonConvert.DeserializeObject<List<KeyValuePair<Guid, string>>>(jsonfile);
                }
                catch
                {
                    logger.Error("Failed to load titleID cache!");
                }
            }
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

            return await RefreshPipeline.RunProviderGamesAsync(
                gamesToRefresh,
                onGameStarting,
                (game, token) => Task.FromResult(
                    new RefreshPipeline.ProviderGameResult
                    {
                        Data = GetAchievementDataAsync(game)
                    }),
                onGameCompleted,
                isAuthRequiredException: _ => false,
                onGameError: (game, ex, consecutiveErrors) =>
                {
                    _logger?.Warn(ex, $"[Xenia] Failed to scan game '{game?.Name}'");
                },
                delayBetweenGamesAsync: null,
                delayAfterErrorAsync: null,
                cancel).ConfigureAwait(false);
        }

        private GameAchievementData GetAchievementDataAsync(Game game)
        {
            if (!game.IsInstalled)
            {
                _logger.Warn("[Xenia] Game isn't installed unable to resolve titleID!");
                return null;
            }

            if (!ResolveTitleID(game, out var titleID))
            {
                return null;
            }

            GameAchievementData data = null;

            if (!File.Exists($"{_accountFolderPath}\\{titleID}.gpd"))
            {
                _logger.Warn($"[Xenia] {titleID}.gpd file not found for {game.Name}! Has the game been launched?");
                data = new GameAchievementData
                {
                    AppId = int.Parse(titleID, System.Globalization.NumberStyles.HexNumber),
                    GameName = game?.Name,
                    ProviderKey = "Xenia",
                    LibrarySourceName = game?.Source?.Name,
                    LastUpdatedUtc = DateTime.UtcNow,
                    HasAchievements = false,
                    PlayniteGameId = game?.Id,
                };
            }
            else
            {
                GPDResolver resolver = new GPDResolver(_pluginUserDataPath);
                var achievements = resolver.LoadGPD(game.Id, _accountFolderPath, titleID);
                data = new GameAchievementData
                {
                    AppId = int.Parse(titleID, System.Globalization.NumberStyles.HexNumber),
                    GameName = game?.Name,
                    ProviderKey = "Xenia",
                    LibrarySourceName = game?.Source?.Name,
                    LastUpdatedUtc = DateTime.UtcNow,
                    HasAchievements = achievements.Count > 0,
                    PlayniteGameId = game?.Id,
                    Achievements = achievements
                };
            }

            var jsondata = JsonConvert.SerializeObject(_titleIDCache);
            if (!Directory.Exists($"{_pluginUserDataPath}\\xenia\\"))
                Directory.CreateDirectory($"{_pluginUserDataPath}\\xenia\\");
            File.WriteAllText($"{_pluginUserDataPath}\\xenia\\titleID_cache.json", jsondata);

            return data;
        }

        bool ResolveTitleID(Game game, out string titleID)
        {
#if false
            if (_settings.Persisted.XeniaGameIdOverrides.ContainsKey(game.Id))
            {
                titleID = _settings.Persisted.XeniaGameIdOverrides[game.Id];
                return true;
            }
#endif

            // Skip titleID search if it has been cached
            if (_titleIDCache.Any(x => x.Key == game.Id))
            {
                titleID = _titleIDCache.Find(x => x.Key == game.Id).Value;
                return true;
            }

            int exeAreaSize = 300;
            foreach (var rom in game.Roms)
            {
                var path = PathExpansion.ExpandGamePath(_playniteApi, game, rom?.Path);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                path = path.Replace("\\\\", "\\").Trim('"');

                if (path.EndsWith(".iso") || path.EndsWith(".xex") || string.IsNullOrEmpty(Path.GetExtension(path)))
                {
                    using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open);

                    Int64 accessoroffset = 0;
                    var filesize = new FileInfo(path).Length;
                    bool filecomplete = false;

                    // Place this outside of loop to combat potential edge-case where .exe/.pe is found at the start of
                    // the 1.5gb chunk but titleID is in the previous 1.5GB chunk that has been wiped
                    var chunksize = 8 * 1024; // 8 KB buffer
                    var buffer = new byte[chunksize];
                    var previousbuffer = new byte[chunksize];

                    // Accessor needs to be split into sub 2GB chunks due to virtual address max size on 32bit
                    // This didn't need chunking in my testing on .NET8 but we are on .NET4
                    do
                    {
                        Int64 filechunk = 1500000000;
                        if(filechunk + accessoroffset > filesize)
                        {
                            filechunk = filesize - accessoroffset;
                            filecomplete = true;
                        }

                        using var accessor = mmf.CreateViewStream(accessoroffset, filechunk, MemoryMappedFileAccess.Read);
                        accessoroffset += filechunk;
                        var position = 0;
                        var bytesRead = 0;
                        byte[] combinedbuffer = new byte[chunksize * 2];
                        byte[] exeChunk = new byte[exeAreaSize];

                        while (accessor.Position < accessor.Length)
                        {
                            bytesRead = accessor.Read(buffer, 0, buffer.Length);
                            if (bytesRead == 0) break;

                            Array.Copy(previousbuffer, combinedbuffer, previousbuffer.Length);
                            Array.Copy(buffer, 0, combinedbuffer, chunksize, bytesRead);

                            var combinedLength = previousbuffer.Length + bytesRead;
                            var foundexe = IndexOf(combinedbuffer, combinedLength, Encoding.UTF8.GetBytes(".exe"));
                            var foundpe = IndexOf(combinedbuffer, combinedLength, Encoding.UTF8.GetBytes(".pe"));

                            if (foundexe >= exeAreaSize)
                            {
                                // Pull the previous 300 characters and convert to char array (300 is arbitry just to account for possible lots of data between titleID and .exe entry)
                                Array.Copy(combinedbuffer, foundexe - exeAreaSize, exeChunk, 0, exeAreaSize);

                                var temptitleID = CheckChunk(ref exeChunk);
                                if (!string.IsNullOrEmpty(temptitleID))
                                {
                                    titleID = temptitleID;
                                    _titleIDCache.Add(new KeyValuePair<Guid, string>(game.Id, temptitleID));
                                    return true;

                                }
                            }
                            if (foundpe >= exeAreaSize)
                            {
                                Array.Copy(combinedbuffer, foundpe - exeAreaSize, exeChunk, 0, exeAreaSize);

                                var temptitleID = CheckChunk(ref exeChunk);
                                if (!string.IsNullOrEmpty(temptitleID))
                                {
                                    titleID = temptitleID;
                                    _titleIDCache.Add(new KeyValuePair<Guid, string>(game.Id, temptitleID));
                                    return true;
                                }
                            }

                            position += bytesRead;
                            Array.Clear(previousbuffer, 0, previousbuffer.Length);
                            var tailCount = Math.Min(previousbuffer.Length, bytesRead);
                            Array.Copy(buffer, bytesRead - tailCount, previousbuffer, previousbuffer.Length - tailCount, tailCount);
                        }



                    } while (!filecomplete);
                    

                }
                else
                {
                    _logger.Error("[Xenia] Unsupported ROM only .xex, .iso, or extensionless package files are supported!");
                }
            }

            titleID = "";
            return false;
        }

        private string CheckChunk(ref byte[] chunk)
        {
            byte[] publisherCheck = new byte[4];
            // Im not sure if this is 100% accurate but out of 28/28 ROMs tested passed taking on average 100ms to find! (.iso)
            // This will take longer with larger files and if the title ID is at the end of the file (Longest i've seen is 11s, maybe it could be multi-threaded?)
            for (int i = 0; i < chunk.Length; i++)
            {
                if (i + 8 > chunk.Length - 1)
                {
                    break;
                }

                // Check for publisher code
                publisherCheck[0] = chunk[i];
                publisherCheck[1] = chunk[i + 1];
                publisherCheck[2] = chunk[i + 2];
                publisherCheck[3] = chunk[i + 3];
                bool passedcheck = KnownPublishers.Any(x => x == System.Text.Encoding.UTF8.GetString(publisherCheck, 0, 4));
                if (!passedcheck)
                    continue;       

                passedcheck &= char.IsDigit((char)chunk[i + 4]) || char.IsUpper((char)chunk[i + 4]);
                passedcheck &= char.IsDigit((char)chunk[i + 5]) || char.IsUpper((char)chunk[i + 5]);
                passedcheck &= char.IsDigit((char)chunk[i + 6]) || char.IsUpper((char)chunk[i + 6]);
                passedcheck &= char.IsDigit((char)chunk[i + 7]) || char.IsUpper((char)chunk[i + 7]);

                if (!passedcheck)
                {
                    continue;
                }
                else
                {
                    return System.Text.Encoding.UTF8.GetString(chunk, i, 8);
                }
            }

            return "";
        }

        private static int IndexOf(byte[] buffer, int bytesRead, byte[] pattern)
        {
            if (buffer == null || pattern == null || pattern.Length == 0 || bytesRead < pattern.Length)
            {
                return -1;
            }

            for (var i = 0; i <= bytesRead - pattern.Length; i++)
            {
                var match = true;
                for (var j = 0; j < pattern.Length; j++)
                {
                    if (buffer[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
