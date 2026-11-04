using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Workshop;

namespace PlayniteAchievements.Services.Library
{
    /// <summary>
    /// Workshop game data as a property of each game. A game's <c>gamedata:&lt;id&gt;</c> link is
    /// its whole record: the Workshop item, its name, the version applied, the baseline the next
    /// update merges against, and a copy of the package as applied (in
    /// <c>UserData\library\gamedata</c>), so Reset works offline. Removing the record deletes the
    /// copy and the baseline with it. Game data is not a library item.
    /// </summary>
    public sealed class GameDataLinkService
    {
        public const string FolderName = "gamedata";

        private readonly GameLinkStore _links;
        private readonly string _packageDirectory;
        private readonly string _baselineDirectory;
        private readonly Func<Guid, GameCustomDataFile> _currentData;
        private readonly Func<Guid, string> _iconDirectory;
        private readonly Action<Exception, string> _warn;

        /// <param name="links">The per-game links.</param>
        /// <param name="libraryDirectory">The library folder (<c>UserData\library</c>); package copies go in its game data folder.</param>
        /// <param name="baselineDirectory">The folder game data baselines are written to; only files there are deleted with a record.</param>
        /// <param name="currentData">Reads a game's stored custom data, or null when it has none.</param>
        /// <param name="iconDirectory">A game's managed icon folder.</param>
        /// <param name="warn">Optional sink for file failures.</param>
        public GameDataLinkService(
            GameLinkStore links,
            string libraryDirectory,
            string baselineDirectory,
            Func<Guid, GameCustomDataFile> currentData = null,
            Func<Guid, string> iconDirectory = null,
            Action<Exception, string> warn = null)
        {
            if (string.IsNullOrWhiteSpace(libraryDirectory))
            {
                throw new ArgumentException("The library folder is required.", nameof(libraryDirectory));
            }

            _links = links ?? throw new ArgumentNullException(nameof(links));
            _packageDirectory = Path.Combine(libraryDirectory, FolderName);
            _baselineDirectory = baselineDirectory;
            _currentData = currentData;
            _iconDirectory = iconDirectory;
            _warn = warn;
        }

        public GameLinkStore Links => _links;

        public string PackageDirectory => _packageDirectory;

        /// <summary>The game's record, or null when no Workshop game data is applied to it.</summary>
        public LibraryLink Get(Guid gameId)
        {
            return gameId == Guid.Empty ? null : _links.Get(LibraryTargetKeys.GameData(gameId));
        }

        /// <summary>Every game with a record.</summary>
        public IReadOnlyDictionary<Guid, LibraryLink> All => _links.GameDataLinks;

        /// <summary>The kept package copy of a record, or null when it has none or the file is gone.</summary>
        public string PackagePathOf(LibraryLink link)
        {
            var name = link?.PackageFile;
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var path = Path.Combine(_packageDirectory, Path.GetFileName(name));
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// Records that the Workshop item went onto the game: keeps a copy of
        /// <paramref name="packagePath"/>, and replaces any earlier record, deleting what it kept
        /// that the new one no longer uses. Returns the stored record.
        /// </summary>
        public LibraryLink Record(Guid gameId, string workshopItemId, string name, string version, string baselineFile, string packagePath)
        {
            var key = LibraryTargetKeys.GameData(gameId);
            var previous = _links.Get(key);
            var packageFile = CopyPackage(gameId, packagePath);

            if (previous != null)
            {
                if (!SameName(previous.PackageFile, packageFile))
                {
                    TryDeletePackage(previous);
                }

                if (!SamePath(previous.BaselineFile, baselineFile))
                {
                    TryDeleteBaseline(previous.BaselineFile);
                }
            }

            var link = new LibraryLink
            {
                LibraryItemId = LibraryItem.WorkshopId(workshopItemId),
                Name = string.IsNullOrWhiteSpace(name) ? previous?.Name : name,
                AppliedVersion = version,
                BaselineFile = baselineFile,
                PackageFile = packageFile,
                AppliedUtc = DateTime.UtcNow
            };
            _links.Set(key, link);
            return link;
        }

        /// <summary>Ends the game's record and deletes its package copy and baseline; the game keeps its data. False when there was none.</summary>
        public bool Unlink(Guid gameId)
        {
            var link = Get(gameId);
            if (link == null)
            {
                return false;
            }

            _links.Remove(LibraryTargetKeys.GameData(gameId));
            TryDeletePackage(link);
            TryDeleteBaseline(link.BaselineFile);
            return true;
        }

        /// <summary>
        /// True when the game's stored data differs from what its record's apply left behind;
        /// false when it does not, or when that cannot be told. Reads the disk: keep it off the UI
        /// thread for many games.
        /// </summary>
        public bool IsEdited(Guid gameId, LibraryLink link)
        {
            GameCustomDataFile current;
            try
            {
                current = _currentData?.Invoke(gameId);
            }
            catch (Exception ex)
            {
                _warn?.Invoke(ex, $"Failed reading the custom data of {gameId} for its Workshop record.");
                return false;
            }

            return IsEdited(link, current, _iconDirectory?.Invoke(gameId)) == true;
        }

        /// <summary>
        /// True when the game's current data differs from what the record's apply left behind:
        /// any curated value (the user's own progress does not count), or any managed icon file
        /// when the baseline recorded them. Null when the baseline cannot be read.
        /// </summary>
        public bool? IsEdited(LibraryLink link, GameCustomDataFile current, string iconDirectory)
        {
            var baselineFile = link?.BaselineFile;
            if (string.IsNullOrWhiteSpace(baselineFile) || !File.Exists(baselineFile))
            {
                return null;
            }

            try
            {
                var baseline = WorkshopBaselineStore.Parse(File.ReadAllText(baselineFile));
                if (baseline == null)
                {
                    return null;
                }

                // Both sides at the current schema, as the baseline is.
                var normalizedCurrent = current == null
                    ? null
                    : GameCustomDataNormalizer.NormalizeInternal(current, current.PlayniteGameId);
                if (DataDiffers(baseline, normalizedCurrent))
                {
                    return true;
                }

                var iconsPath = WorkshopBaselineStore.IconHashesPath(baselineFile);
                if (!File.Exists(iconsPath))
                {
                    return false;
                }

                var recorded = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(iconsPath));
                return IconsDiffer(recorded, WorkshopBaselineStore.HashIcons(iconDirectory));
            }
            catch (Exception ex)
            {
                _warn?.Invoke(ex, $"Failed comparing a game's data with its Workshop baseline {baselineFile}.");
                return null;
            }
        }

        // ---- pure rules ---------------------------------------------------------------------------

        /// <summary>The Workshop item id a game data record names.</summary>
        public static string WorkshopItemIdOf(LibraryLink link)
        {
            var id = link?.LibraryItemId;
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            return id.StartsWith(LibraryItem.WorkshopIdPrefix, StringComparison.OrdinalIgnoreCase)
                ? id.Substring(LibraryItem.WorkshopIdPrefix.Length)
                : id;
        }

        /// <summary>True when <paramref name="link"/> records <paramref name="workshopItemId"/>.</summary>
        public static bool Names(LibraryLink link, string workshopItemId)
        {
            return !string.IsNullOrWhiteSpace(workshopItemId)
                   && string.Equals(WorkshopItemIdOf(link), workshopItemId.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when the index entry is the record's item at a newer version than the game has.</summary>
        public static bool HasUpdate(LibraryLink link, WorkshopItem indexItem)
        {
            return link != null && indexItem != null && Names(link, indexItem.Id)
                   && WorkshopIdentityStore.IsNewer(indexItem.Version, link.AppliedVersion);
        }

        /// <summary>The index entry of a record's item, or null when the index does not list it.</summary>
        public static WorkshopItem IndexItemOf(WorkshopIndexFile index, LibraryLink link)
        {
            return index?.Items?.FirstOrDefault(item => item != null && Names(link, item.Id));
        }

        /// <summary>The index items that are newer than the version some game has applied, once per item.</summary>
        public static IReadOnlyList<WorkshopItem> FindUpdates(WorkshopIndexFile index, IEnumerable<LibraryLink> links)
        {
            var records = (links ?? Enumerable.Empty<LibraryLink>()).Where(link => link != null).ToList();
            if (index?.Items == null || records.Count == 0)
            {
                return Array.Empty<WorkshopItem>();
            }

            return index.Items
                .Where(item => item != null && records.Any(link => HasUpdate(link, item)))
                .ToList();
        }

        /// <summary>
        /// The records grouped by Workshop item, one group per item, ordered by name: each with the
        /// games that have it, the highest version any of them applied, and the name the record
        /// at that version (or else any record) carries. Records that name no item are left out.
        /// </summary>
        public static IReadOnlyList<GameDataItemGroup> GroupByItem(IEnumerable<KeyValuePair<Guid, LibraryLink>> records)
        {
            return (records ?? Enumerable.Empty<KeyValuePair<Guid, LibraryLink>>())
                .Where(pair => pair.Key != Guid.Empty && WorkshopItemIdOf(pair.Value) != null)
                .GroupBy(pair => WorkshopItemIdOf(pair.Value).Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var games = group.ToList();
                    var highest = games
                        .Select(pair => pair.Value)
                        .Aggregate((LibraryLink)null, (best, link) =>
                            best == null || WorkshopIdentityStore.IsNewer(link.AppliedVersion, best.AppliedVersion)
                                || (string.IsNullOrWhiteSpace(best.AppliedVersion) && !string.IsNullOrWhiteSpace(link.AppliedVersion))
                                ? link
                                : best);
                    var name = !string.IsNullOrWhiteSpace(highest?.Name)
                        ? highest.Name
                        : games.Select(pair => pair.Value.Name).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                    return new GameDataItemGroup(group.Key, name, highest?.AppliedVersion, games);
                })
                .OrderBy(group => group.Name ?? group.WorkshopItemId, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>The games whose record names <paramref name="indexItem"/> at an older version than the index lists.</summary>
        public static IReadOnlyList<Guid> GamesBehind(IEnumerable<KeyValuePair<Guid, LibraryLink>> records, WorkshopItem indexItem)
        {
            return (records ?? Enumerable.Empty<KeyValuePair<Guid, LibraryLink>>())
                .Where(pair => pair.Key != Guid.Empty && HasUpdate(pair.Value, indexItem))
                .Select(pair => pair.Key)
                .ToList();
        }

        /// <summary>
        /// True when a curated value differs between the two: everything a package can carry,
        /// with the user's own progress (unlocks, goals) left out.
        /// </summary>
        public static bool DataDiffers(GameCustomDataFile baseline, GameCustomDataFile current)
        {
            return !JToken.DeepEquals(Curated(baseline), Curated(current));
        }

        /// <summary>True when a managed icon file was added, removed or changed since the recorded hashes.</summary>
        public static bool IconsDiffer(IReadOnlyDictionary<string, string> recorded, IReadOnlyDictionary<string, string> current)
        {
            if (recorded == null)
            {
                return false;
            }

            current = current ?? new Dictionary<string, string>();
            if (recorded.Count != current.Count)
            {
                return true;
            }

            foreach (var pair in current)
            {
                var match = recorded.FirstOrDefault(entry => string.Equals(entry.Key, pair.Key, StringComparison.OrdinalIgnoreCase));
                if (match.Key == null || !string.Equals(match.Value, pair.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static JToken Curated(GameCustomDataFile data)
        {
            var portable = (data ?? new GameCustomDataFile()).ToPortable();
            PortablePersonalState.Strip(portable);
            portable.SchemaVersion = 0;
            portable.PlayniteGameId = Guid.Empty;
            return Prune(JToken.FromObject(portable));
        }

        /// <summary>Drops nulls and empty collections, which the resolvers read the same as a missing value.</summary>
        private static JToken Prune(JToken token)
        {
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties().ToList())
                {
                    var value = Prune(property.Value);
                    if (value == null)
                    {
                        property.Remove();
                    }
                    else
                    {
                        property.Value = value;
                    }
                }

                return obj.HasValues ? obj : null;
            }

            if (token is JArray array)
            {
                return array.HasValues ? array : null;
            }

            return token == null || token.Type == JTokenType.Null ? null : token;
        }

        // ---- files --------------------------------------------------------------------------------

        private string CopyPackage(Guid gameId, string packagePath)
        {
            if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
            {
                return null;
            }

            try
            {
                Directory.CreateDirectory(_packageDirectory);
                var extension = Path.GetExtension(packagePath);
                var name = gameId.ToString("N") + (string.IsNullOrEmpty(extension) ? GameCustomDataStore.PortableFileExtension : extension);
                var destination = Path.Combine(_packageDirectory, name);
                if (!SamePath(Path.GetFullPath(packagePath), Path.GetFullPath(destination)))
                {
                    File.Copy(packagePath, destination, overwrite: true);
                }

                return name;
            }
            catch (Exception ex)
            {
                _warn?.Invoke(ex, "Failed keeping a copy of the Workshop game data package; Reset downloads it again.");
                return null;
            }
        }

        private void TryDeletePackage(LibraryLink link)
        {
            var path = PackagePathOf(link);
            if (path == null)
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                _warn?.Invoke(ex, $"Failed deleting the game data package copy {path}.");
            }
        }

        private void TryDeleteBaseline(string baselineFile)
        {
            if (string.IsNullOrWhiteSpace(baselineFile) || string.IsNullOrWhiteSpace(_baselineDirectory) || !Path.IsPathRooted(baselineFile))
            {
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(baselineFile));
                if (!SamePath(directory, Path.GetFullPath(_baselineDirectory)))
                {
                    return;
                }

                foreach (var path in new[] { baselineFile, WorkshopBaselineStore.IconHashesPath(baselineFile) })
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
            }
            catch (Exception ex)
            {
                _warn?.Invoke(ex, $"Failed deleting the game data baseline {baselineFile}.");
            }
        }

        private static bool SameName(string left, string right)
        {
            return string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SamePath(string left, string right)
        {
            return !string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right)
                   && string.Equals(
                       left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                       right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>One Workshop item's game data across every game that has it.</summary>
    public sealed class GameDataItemGroup
    {
        public GameDataItemGroup(string workshopItemId, string name, string highestVersion, IReadOnlyList<KeyValuePair<Guid, LibraryLink>> games)
        {
            WorkshopItemId = workshopItemId;
            Name = name;
            HighestVersion = highestVersion;
            Games = games ?? Array.Empty<KeyValuePair<Guid, LibraryLink>>();
        }

        public string WorkshopItemId { get; }

        /// <summary>The item's name as the records carry it, or null when none does.</summary>
        public string Name { get; }

        /// <summary>The highest version any game applied, or null when none recorded one.</summary>
        public string HighestVersion { get; }

        /// <summary>Each game that has the item, with its record.</summary>
        public IReadOnlyList<KeyValuePair<Guid, LibraryLink>> Games { get; }
    }
}
