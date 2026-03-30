using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Xenia.Models;
using PlayniteAchievements.Services;
using SharpCompress.Common;
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
        private readonly XeniaSettings _providerSettings;
        private readonly string _pluginUserDataPath;

        List<KeyValuePair<Guid, string>> _titleIDCache = new List<KeyValuePair<Guid, string>>();
        List<string> KnownPublishers = new List<string>() { "5444", "464F", "4143", "4156", "4158", "4142", "4144", "4150", "4151", "4157", "414B", "4148", "4153", "4159", "4154", "424D", "4241", "4257", "4253", "4242", "4248", "4246", "4245", "4247", "4254", "4244", "4252", "4256", "4255", "4343", "434D", "4356", "4354", "4458", "4445", "4443", "4546", "4553", "4541", "454D", "4543", "454C", "4556", "464C", "4649", "4653", "4746", "4745", "4756", "4857", "4850", "4845", "4855", "4946", "494F", "494D", "4947", "494C", "4950", "4958", "4A41", "4A57", "4B59", "4B4F", "4B4E", "4B41", "4B54", "4C41", "4D4A", "4D45", "4D44", "4D53", "4D57", "4D4D", "4E4D", "4E4B", "4E4C", "4F47", "4F58", "5058", "504C", "5043", "5241", "5341", "5343", "5345", "5353", "534E", "5350", "5351", "5354", "5355", "5357", "5441", "5454", "544B", "544D", "5443", "5451", "5453", "5553", "5647", "5656", "5643", "5655", "5745", "5752", "584B", "584C", "5841", "5849", "5850", "5942", "5A44", "4450", "394F", "4C53", "4656", "3734", "4133", "545A", "435A", "4346", "4D4B", "434E", "4436", "5A45", "4645" };

        public XeniaScanner(
            ILogger logger,
            IPlayniteAPI playniteApi,
            XeniaSettings providerSettings,
            string pluginUserDataPath)
        {
            _logger = logger;
            _playniteApi = playniteApi;
            _providerSettings = providerSettings ?? throw new ArgumentNullException(nameof(providerSettings));
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
            if (string.IsNullOrWhiteSpace(_providerSettings.AccountPath))
            {
                _logger?.Warn("[Xenia] Missing path to account - cannot scan achievements.");
                return new RebuildPayload { Summary = new RebuildSummary(), AuthRequired = true };
            }

            if (gamesToRefresh is null || gamesToRefresh.Count == 0)
            {
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            return await ProviderRefreshExecutor.RunProviderGamesAsync(
                gamesToRefresh,
                onGameStarting,
                (game, token) => Task.FromResult(
                    new ProviderRefreshExecutor.ProviderGameResult
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
                _playniteApi.Notifications.Add(new NotificationMessage("PA_Xenia", $"[Xenia] Game isn't installed unable to resolve titleID!", NotificationType.Error));
                return null;
            }

            if (!ResolveTitleID(game, out var titleID))
            {
                _playniteApi.Notifications.Add(new NotificationMessage("PA_Xenia", $"[Xenia] TitleID not found for {game.Name}! Has the game been launched?", NotificationType.Error));
                return null;
            }

            GameAchievementData data = null;

            if (!File.Exists($"{_providerSettings.AccountPath}\\{titleID}.gpd"))
            {
                _playniteApi.Notifications.Add(new NotificationMessage("PA_Xenia", $"[Xenia] {titleID}.gpd file not found for {game.Name}! Has the game been launched?", NotificationType.Info));
                _logger.Warn($"[Xenia] {titleID}.gpd file in {_providerSettings.AccountPath} not found for {game.Name}!");
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
                GPDResolver resolver = new GPDResolver();
                var gpdpath = $"{_providerSettings.AccountPath}\\{titleID}.gpd";
                var gpdFile = resolver.LoadGPD(gpdpath);

                // Write icon data to icon cache
                var iconDirectory = $"{_pluginUserDataPath}\\icon_cache\\{game.Id}\\";
                Directory.CreateDirectory(iconDirectory);
                foreach (var icon in gpdFile.IconData)
                {
                    using (var fs = new FileStream($"{iconDirectory}{icon.Key}.png", FileMode.Create, FileAccess.Write))
                    {
                        fs.Write(icon.Value, 0, icon.Value.Length);
                    }
                }

                List<AchievementDetail> achievements = new List<AchievementDetail>();
                foreach (var achievement in gpdFile.Achievements)
                {
                    var iconPath = $"{iconDirectory}{achievement.icon_id}.png";
                    if (!File.Exists(iconPath))
                    {
                        iconPath = null;
                    }

                    achievements.Add(new AchievementDetail
                    {
                        ApiName = achievement.id.ToString(),
                        DisplayName = achievement.title,
                        Description = achievement.unlock_time == 0 ? achievement.description : achievement.unlockDescription,
                        UnlockedIconPath = iconPath,
                        LockedIconPath = iconPath,
                        Points = (int?)achievement.gamerscore,
                        Rarity = GetRarityFromXboxPoints((int?)achievement.gamerscore),
                        Unlocked = achievement.earned,
                        UnlockTimeUtc = achievement.unlock_time != 0
                            ? DateTime.FromFileTimeUtc((Int64)achievement.unlock_time)
                            : (DateTime?)null,
                    });
                }

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
            // Skip titleID search if it has been cached
            if (_titleIDCache.Any(x => x.Key == game.Id))
            {
                titleID = _titleIDCache.Find(x => x.Key == game.Id).Value;
                return true;
            }

            // Try to find game in recent.toml
            foreach (var rom in game.Roms)
            {
                var path = PathExpansion.ExpandGamePath(_playniteApi, game, rom?.Path);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                path = path.Replace("\\\\", "\\").Trim('"');

                var xeniapath = _providerSettings.AccountPath + "\\..\\..\\..\\..\\..\\";
                if (File.Exists($"{xeniapath}recent.toml"))
                {
                    bool foundROM = false;
                    string ROMTitle = "";

                    // Read all lines in toml file
                    foreach (string line in File.ReadLines($"{xeniapath}recent.toml"))
                    {
                        if (foundROM)
                        {
                            var quoteMarks = line.IndexOf('"');
                            if (quoteMarks == -1)
                            {
                                quoteMarks = line.IndexOf('\'');
                            }

                            if (quoteMarks >= 0)
                            {
                                quoteMarks++;

                                ROMTitle = line.Substring(quoteMarks, (line.Length - quoteMarks) - 1);
                                break;
                            }
                        }

                        if (line.StartsWith("path", StringComparison.OrdinalIgnoreCase))
                        {
                            var linepath = line.Replace("\\\\", "\\");
                            if (linepath.Contains(path))
                            {
                                foundROM = true;
                                continue;
                            }
                        }
                    }

                    if (foundROM)
                    {
                        // Read all gpd files
                        foreach (var gpdFilePath in Directory.EnumerateFiles(_providerSettings.AccountPath, "*.gpd"))
                        {
                            // Skip base account data
                            if (gpdFilePath.EndsWith("FFFE07D1.gpd"))
                                continue;

                            var gpdfile = new GPDResolver().LoadGPD(gpdFilePath);
                            var gameName = gpdfile.StringData.Replace("\0", "");

                            if (gameName.Contains(ROMTitle))
                            {
                                titleID = Path.GetFileNameWithoutExtension(gpdFilePath);
                                return true;
                            }
                        }
                    }
                }

            }

            // Try to find TitleID in file
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

        private static RarityTier GetRarityFromXboxPoints(int? points)
        {
            var value = Math.Max(0, points ?? 0);
            if (value >= 100)
            {
                return RarityTier.UltraRare;
            }

            if (value >= 50)
            {
                return RarityTier.Rare;
            }

            if (value >= 25)
            {
                return RarityTier.Uncommon;
            }

            return RarityTier.Common;
        }
    }
}
