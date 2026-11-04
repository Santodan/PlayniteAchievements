using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace PlayniteAchievements.Services.Workshop
{
    /// <summary>
    /// Reads and writes the per-install baselines a game-data update merges against: the data an
    /// install produced, plus a hash of every managed icon file of the game at that moment.
    /// </summary>
    internal sealed class WorkshopBaselineStore
    {
        /// <summary>The baselines folder inside the library's game data folder (<see cref="Library.LibraryStore.GameDataBaselinesFolder"/>).</summary>
        public const string FolderName = "baselines";

        private readonly string _directory;
        private readonly ILogger _logger;

        /// <param name="directory">The folder baselines are written to; created on first write.</param>
        /// <param name="logger">Optional logger for read and write failures.</param>
        public WorkshopBaselineStore(string directory, ILogger logger)
        {
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
            _logger = logger;
        }

        /// <summary>The folder baselines are written to.</summary>
        public string Directory => _directory;

        /// <summary>The icon-hashes file that sits next to a baseline file.</summary>
        public static string IconHashesPath(string baselineFile) => baselineFile + ".icons.json";

        /// <summary>
        /// A baseline from its JSON, upgraded to the current schema so it compares like for like
        /// with the game's data, which is always current. A baseline written by an older version
        /// would otherwise differ on every field the upgrade moves. Null for empty JSON.
        /// </summary>
        public static GameCustomDataFile Parse(string json)
        {
            var parsed = JsonConvert.DeserializeObject<GameCustomDataFile>(json);
            return parsed == null ? null : GameCustomDataNormalizer.NormalizeInternal(parsed, parsed.PlayniteGameId);
        }

        /// <summary>The baseline in <paramref name="baselineFile"/>, or null when there is none or it cannot be read.</summary>
        public GameCustomDataFile Load(string baselineFile)
        {
            if (string.IsNullOrEmpty(baselineFile) || !File.Exists(baselineFile))
            {
                return null;
            }

            try
            {
                return Parse(File.ReadAllText(baselineFile));
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Could not read the Workshop baseline {baselineFile}; the update replaces the data.");
                return null;
            }
        }

        /// <summary>
        /// Writes the data an install produced, plus the hashes of every managed icon file of the
        /// game at that moment, so the next update knows what the user changed afterwards.
        /// Returns the baseline path, or null when it could not be written.
        /// </summary>
        public string Write(string itemId, Guid gameId, GameCustomDataFile data, string iconDirectory)
        {
            try
            {
                System.IO.Directory.CreateDirectory(_directory);
                var safeId = new string(itemId.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '/' ? '_' : c).ToArray());
                var path = Path.Combine(_directory, safeId + "-" + gameId.ToString("N") + ".json");
                File.WriteAllText(path, JsonConvert.SerializeObject(data));
                File.WriteAllText(IconHashesPath(path), JsonConvert.SerializeObject(HashIcons(iconDirectory)));
                return path;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Could not write the Workshop baseline for {itemId}.");
                return null;
            }
        }

        /// <summary>
        /// The icon hashes recorded next to an install's baseline, keyed by path relative to the
        /// game's icon folder. Null when the record has no baseline, the file is missing, or it
        /// cannot be read.
        /// </summary>
        public Dictionary<string, string> LoadIconHashes(string baselineFile)
        {
            if (string.IsNullOrEmpty(baselineFile))
            {
                return null;
            }

            try
            {
                var hashesPath = IconHashesPath(baselineFile);
                return File.Exists(hashesPath)
                    ? JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(hashesPath))
                    : null;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Could not read the Workshop icon baseline; swapped icons are not preserved.");
                return null;
            }
        }

        /// <summary>SHA-256 of every file under an icon folder, keyed by path relative to it.</summary>
        public static Dictionary<string, string> HashIcons(string iconDirectory)
        {
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(iconDirectory) || !System.IO.Directory.Exists(iconDirectory))
            {
                return hashes;
            }

            foreach (var file in System.IO.Directory.EnumerateFiles(iconDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = file.Substring(iconDirectory.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                hashes[relative] = HashFile(file);
            }

            return hashes;
        }

        /// <summary>Lowercase hex SHA-256 of a file.</summary>
        internal static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }
}
