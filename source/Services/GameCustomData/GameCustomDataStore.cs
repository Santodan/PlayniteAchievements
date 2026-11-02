using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace PlayniteAchievements.Services.GameCustomData
{
    public sealed class PortableGameCustomDataImportResult
    {
        public GameCustomDataFile ImportedData { get; set; }

        public int IgnoredPackageImageCount { get; set; }

        public bool HasIgnoredPackageImages => IgnoredPackageImageCount > 0;
    }

    public sealed class GameCustomDataChangedEventArgs : EventArgs
    {
        public GameCustomDataChangedEventArgs(
            Guid playniteGameId,
            bool affectsSummaryData = true,
            bool affectsOverrideMirror = true,
            bool affectsTagTotals = false)
        {
            PlayniteGameId = playniteGameId;
            AffectsSummaryData = affectsSummaryData;
            AffectsOverrideMirror = affectsOverrideMirror;
            AffectsTagTotals = affectsTagTotals;
        }

        public Guid PlayniteGameId { get; }

        /// <summary>
        /// True when the write changed which tags the user put on achievements, or the store could
        /// not tell. A tag edit leaves every other summary count alone, so its writers report
        /// <see cref="AffectsSummaryData"/> false and subscribers recount only the game's tags.
        /// </summary>
        public bool AffectsTagTotals { get; }

        /// <summary>
        /// False when the change cannot have moved the per-achievement override mirror -- the
        /// filtered ApiNames and the user-editable points and trophy type. A capstone edit is the
        /// case that matters: it is summary-visible but touches nothing the mirror carries, and
        /// resyncing it costs a record clone and a write-connection query on the click.
        ///
        /// Defaults to true so every existing caller keeps resyncing, and only a writer that
        /// knows better opts out.
        /// </summary>
        public bool AffectsOverrideMirror { get; }

        /// <summary>
        /// False when the change only reorders or re-presents achievements the user already had,
        /// so nothing about counts, filters or library rollups can have moved. Subscribers that
        /// rebuild summary or projection data skip those changes; everything else must keep
        /// treating the change as significant, which is why this defaults to true.
        /// </summary>
        public bool AffectsSummaryData { get; }
    }

    /// <summary>
    /// A completed write, carrying the record as it stood before and after.
    /// </summary>
    /// <remarks>
    /// Raised alongside <see cref="GameCustomDataChangedEventArgs"/> but for a different purpose:
    /// that one says something changed, this one says what it was. Every writer already produces
    /// both images on its way through the save, so a subscriber that wants to reverse a write -
    /// the achievement editor's undo - can record them without loading anything itself.
    ///
    /// <see cref="Previous"/> is null when the write came through the overload that does not
    /// compute a pre-image, which means the state before it is unknown rather than empty.
    /// </remarks>
    public sealed class GameCustomDataWrittenEventArgs : EventArgs
    {
        public GameCustomDataWrittenEventArgs(
            Guid playniteGameId,
            GameCustomDataFile previous,
            GameCustomDataFile persisted,
            bool affectsSummaryData,
            bool affectsOverrideMirror)
        {
            PlayniteGameId = playniteGameId;
            Previous = previous;
            Persisted = persisted;
            AffectsSummaryData = affectsSummaryData;
            AffectsOverrideMirror = affectsOverrideMirror;
        }

        public Guid PlayniteGameId { get; }

        /// <summary>The normalized record before the write, or null when it was not computed.</summary>
        public GameCustomDataFile Previous { get; }

        /// <summary>The record as it was stored.</summary>
        public GameCustomDataFile Persisted { get; }

        /// <summary>
        /// The flags this write reported. A reversal has to repeat them, or the mirrors that key
        /// off them resync for the original change and not for the one that undid it.
        /// </summary>
        public bool AffectsSummaryData { get; }

        public bool AffectsOverrideMirror { get; }
    }

    /// <summary>
    /// Orchestrates per-game custom data persistence and migration.
    /// </summary>
    public sealed partial class GameCustomDataStore
    {
        private const string DatabaseFileName = "game_custom_data.db";

        // Portable files are always zip packages under the bare .pa extension (zip inside like
        // Playnite's .pext); the legacy .pa.zip spelling is still accepted on import.
        public const string PortableFileExtension = ".pa";
        public const string PortablePackageFileExtension = ".pa.zip";
        public const string PortablePackageManifestEntryName = "custom-data.pa";
        private const string PortablePackageImagesFolderName = "images";

        /// <summary>
        /// The .pa package entry stem for each notification image slot (path accessors come
        /// from <see cref="NotificationImageSlotMap"/>).
        /// </summary>
        private static readonly IReadOnlyDictionary<NotificationImageSlot, string> NotificationImageEntryStems =
            new Dictionary<NotificationImageSlot, string>
            {
                [NotificationImageSlot.Background] = "notification_background",
                [NotificationImageSlot.BadgeCommon] = "notification_badge_common",
                [NotificationImageSlot.BadgeUncommon] = "notification_badge_uncommon",
                [NotificationImageSlot.BadgeRare] = "notification_badge_rare",
                [NotificationImageSlot.BadgeUltraRare] = "notification_badge_ultrarare",
                [NotificationImageSlot.BadgeCompletion] = "notification_badge_completion",
                [NotificationImageSlot.FrameBadgeCommon] = "notification_frame_badge_common",
                [NotificationImageSlot.FrameBadgeUncommon] = "notification_frame_badge_uncommon",
                [NotificationImageSlot.FrameBadgeRare] = "notification_frame_badge_rare",
                [NotificationImageSlot.FrameBadgeUltraRare] = "notification_frame_badge_ultrarare",
                [NotificationImageSlot.FrameBadgeCompletion] = "notification_frame_badge_completion",
                [NotificationImageSlot.FrameBackground] = "notification_frame_background"
            };

        private readonly ILogger _logger;
        // Indented, for the portable .pa manifest a user can open and read.
        private readonly JsonSerializerSettings _writeSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore
        };

        // Compact, for the stored blob. The payload is a SQLite TEXT column that is only ever
        // round-tripped through JsonConvert -- never diffed as text, hashed, or shown to anyone --
        // so its indentation was whitespace written on every per-game save and carried into the
        // WAL. The serialize scales with the game's override count, which is exactly the profile
        // that made editing a heavily customized game slow. Anything that ever wants to compare
        // payload text must normalize first.
        private readonly JsonSerializerSettings _storeWriteSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Ignore
        };
        private readonly object _cacheSync = new object();

        private readonly GameCustomDataLegacyMigration _legacyMigration = new GameCustomDataLegacyMigration();
        private readonly GameCustomDataRepository _repository;
        private ManagedCustomIconService _managedCustomIconService;
        private NotificationImageStore _notificationImageStore;
        private AchievementDataService _achievementDataService;
        private Func<string, CustomProviderDefinition> _tryGetCustomProvider;
        private Func<CustomProviderDefinition, bool> _importCustomProviderIfMissing;
        private Func<Guid, IReadOnlyList<PortableGameKey>> _resolveGameKeys;
        private Dictionary<Guid, GameCustomDataFile> _cacheByGameId;
        private HashSet<Guid> _missingGameIds;

        public event EventHandler<GameCustomDataChangedEventArgs> CustomDataChanged;

        /// <summary>
        /// Raised for a write that reached the repository, with the record before and after it.
        /// Raised just before <see cref="CustomDataChanged"/>, so a subscriber has recorded the
        /// write before anything reacts to it.
        /// </summary>
        public event EventHandler<GameCustomDataWrittenEventArgs> CustomDataWritten;

        public GameCustomDataStore(string pluginUserDataPath, ILogger logger = null)
        {
            _logger = logger;
            var databasePath = Path.Combine(pluginUserDataPath ?? string.Empty, DatabaseFileName);
            _repository = new GameCustomDataRepository(databasePath, _storeWriteSettings, logger);
        }

        public string DatabasePath => _repository.DatabasePath;

        public void AttachManagedCustomIconService(ManagedCustomIconService managedCustomIconService)
        {
            _managedCustomIconService = managedCustomIconService;
        }

        public void AttachNotificationImageStore(NotificationImageStore notificationImageStore)
        {
            _notificationImageStore = notificationImageStore;
        }

        public void AttachAchievementDataService(AchievementDataService achievementDataService)
        {
            _achievementDataService = achievementDataService;
        }

        public void AttachRuntimeSettings(PlayniteAchievementsSettings settings)
        {
            _ = settings;
        }

        /// <summary>
        /// Connects the custom provider catalog so portable exports embed the assigned provider's
        /// definition and imports recreate a missing one. Delegates keep this store free of the
        /// catalog type.
        /// </summary>
        public void AttachCustomProviderCatalog(
            Func<string, CustomProviderDefinition> tryGetCustomProvider,
            Func<CustomProviderDefinition, bool> importCustomProviderIfMissing)
        {
            _tryGetCustomProvider = tryGetCustomProvider;
            _importCustomProviderIfMissing = importCustomProviderIfMissing;
        }

        /// <summary>
        /// Connects the lookup that names a game in machine-independent terms (provider ids,
        /// name and platform), so an exported .pa file can be matched to a library game elsewhere.
        /// </summary>
        public void AttachGameKeyResolver(Func<Guid, IReadOnlyList<PortableGameKey>> resolveGameKeys)
        {
            _resolveGameKeys = resolveGameKeys;
        }

        public bool TryLoad(Guid playniteGameId, out GameCustomDataFile data)
        {
            data = null;
            if (playniteGameId == Guid.Empty)
            {
                return false;
            }

            lock (_cacheSync)
            {
                if (_cacheByGameId != null)
                {
                    if (_cacheByGameId.TryGetValue(playniteGameId, out var cached))
                    {
                        data = cached?.Clone();
                        return data != null;
                    }

                    // A complete cache answers a miss on its own. Otherwise every game without
                    // custom data - most of a library - cost a repository read to learn that.
                    if (_cacheHoldsEveryStoredRow ||
                        (_missingGameIds != null && _missingGameIds.Contains(playniteGameId)))
                    {
                        return false;
                    }
                }
            }

            var found = _repository.TryLoad(playniteGameId, out var loaded);
            lock (_cacheSync)
            {
                EnsureCacheCollections();
                if (found && loaded != null)
                {
                    _cacheByGameId[playniteGameId] = loaded.Clone();
                    _missingGameIds.Remove(playniteGameId);
                    data = loaded.Clone();
                    return true;
                }

                _cacheByGameId.Remove(playniteGameId);
                _missingGameIds.Add(playniteGameId);
                return false;
            }
        }

        /// <summary>
        /// Reads through the same cache TryLoad uses, rather than going straight to the
        /// repository: every write path either seeds the cache (Save), removes the entry (Delete)
        /// or invalidates it wholesale (the legacy migration's SaveMany), so the cache is
        /// authoritative and the extra SELECT plus full-blob deserialize bought nothing.
        /// </summary>
        public GameCustomDataFile LoadOrDefault(Guid playniteGameId)
        {
            return TryLoad(playniteGameId, out var data)
                ? data
                : GameCustomDataNormalizer.CreateDefault(playniteGameId);
        }

        /// <param name="affectsSummaryData">
        /// Pass false when the mutation cannot change achievement counts, filters or library
        /// rollups, so summary and projection subscribers can skip the rebuild.
        /// </param>
        public void Update(
            Guid playniteGameId,
            Action<GameCustomDataFile> mutate,
            bool affectsSummaryData = true,
            bool affectsOverrideMirror = true)
        {
            if (playniteGameId == Guid.Empty)
            {
                throw new ArgumentException("Game ID is required.", nameof(playniteGameId));
            }

            if (mutate == null)
            {
                throw new ArgumentNullException(nameof(mutate));
            }

            // The mutation runs on the normalized record, not the record as stored.
            //
            // Normalization is what folds the legacy per-field maps into AchievementOverrides,
            // and writers treat that record as authoritative: AchievementOverridesService rebuilds
            // it and drops the mirrors, on the grounds that the fold already happened. Mutating
            // the stored shape instead left that only true for a record whose mirrors were
            // already projections of it -- for one carrying a value the record did not, from an
            // older schema or an import, the write read past it and then dropped it.
            GameCustomDataFile data;
            GameCustomDataFile previous;
            using (PerfScope.Start(_logger, "GameCustomData.Update.NormalizePrevious", thresholdMs: 10))
            {
                data = GameCustomDataNormalizer.NormalizeInternal(LoadOrDefault(playniteGameId), playniteGameId);
                previous = data.Clone();
            }

            mutate(data);
            Save(playniteGameId, data, previous, affectsSummaryData, affectsOverrideMirror);
        }

        // Rewrites every ApiName-keyed field after achievement definitions were renamed in place
        // (stable-key migrations / renamed achievements), so notes, ordering, filters, category and
        // icon overrides, and the manual capstone follow the definition to its new key. No-op when
        // the game has no stored custom data or none of the old keys appear in it.
        public bool RenameAchievementApiNames(Guid playniteGameId, IReadOnlyDictionary<string, string> renamedApiNames)
        {
            if (playniteGameId == Guid.Empty || renamedApiNames == null || renamedApiNames.Count == 0)
            {
                return false;
            }

            if (!TryLoad(playniteGameId, out var probe) || probe == null)
            {
                return false;
            }

            if (!ApplyAchievementApiNameRenames(probe, renamedApiNames))
            {
                return false;
            }

            Update(playniteGameId, data => ApplyAchievementApiNameRenames(data, renamedApiNames));
            _logger?.Info($"Rewrote achievement custom-data keys for game {playniteGameId} after {renamedApiNames.Count} definition renames.");
            return true;
        }

        private static bool ApplyAchievementApiNameRenames(
            GameCustomDataFile data,
            IReadOnlyDictionary<string, string> renamedApiNames)
        {
            if (data == null)
            {
                return false;
            }

            var changed = false;

            if (TryResolveRenamedApiName(renamedApiNames, data.ManualCapstoneApiName, out var renamedCapstone))
            {
                data.ManualCapstoneApiName = renamedCapstone;
                changed = true;
            }

            foreach (var capstone in data.Capstones ?? Enumerable.Empty<CapstoneAssignment>())
            {
                if (TryResolveRenamedApiName(renamedApiNames, capstone?.ApiName, out var renamedEntry))
                {
                    capstone.ApiName = renamedEntry;
                    changed = true;
                }
            }

            changed |= RenameListEntries(data.AchievementOrder, renamedApiNames);
            changed |= RenameListEntries(data.FilteredAchievementApiNames, renamedApiNames);
            changed |= RenameListEntries(data.SummaryFilteredAchievementApiNames, renamedApiNames);
            changed |= RenameListEntries(data.GoalAchievementApiNames, renamedApiNames);
            changed |= RenameDictionaryKeys(data.AchievementCategoryOverrides, renamedApiNames);
            changed |= RenameDictionaryKeys(data.AchievementCategoryTypeOverrides, renamedApiNames);
            changed |= RenameDictionaryKeys(data.AchievementUnlockedIconOverrides, renamedApiNames);
            changed |= RenameDictionaryKeys(data.AchievementLockedIconOverrides, renamedApiNames);
            changed |= RenameDictionaryKeys(data.AchievementNotes, renamedApiNames);
            changed |= RenameDictionaryKeys(data.AchievementOverrides, renamedApiNames);

            return changed;
        }

        private static bool TryResolveRenamedApiName(
            IReadOnlyDictionary<string, string> renamedApiNames,
            string apiName,
            out string renamed)
        {
            renamed = null;
            var normalized = apiName?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            foreach (var pair in renamedApiNames)
            {
                if (string.Equals(pair.Key, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    renamed = pair.Value;
                    return true;
                }
            }

            return false;
        }

        private static bool RenameListEntries(IList<string> entries, IReadOnlyDictionary<string, string> renamedApiNames)
        {
            if (entries == null || entries.Count == 0)
            {
                return false;
            }

            var changed = false;
            for (var i = 0; i < entries.Count; i++)
            {
                if (TryResolveRenamedApiName(renamedApiNames, entries[i], out var renamed))
                {
                    entries[i] = renamed;
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>
        /// Republishes the icon paths from the legacy mirror maps onto the per-achievement record.
        /// Existing record paths are cleared first, so an icon dropped from the package (its source
        /// file was missing) does not survive as a stale absolute path.
        /// </summary>
        private static void SyncPortableOverrideIconsFromLegacyMaps(GameCustomDataPortableFile portable)
        {
            if (portable.AchievementOverrides != null)
            {
                foreach (var entry in portable.AchievementOverrides.Values)
                {
                    if (entry != null)
                    {
                        entry.UnlockedIconPath = null;
                        entry.LockedIconPath = null;
                    }
                }
            }

            if (portable.AchievementUnlockedIconOverrides != null)
            {
                foreach (var pair in portable.AchievementUnlockedIconOverrides)
                {
                    ResolvePortableOverride(portable, pair.Key).UnlockedIconPath = pair.Value;
                }
            }

            if (portable.AchievementLockedIconOverrides != null)
            {
                foreach (var pair in portable.AchievementLockedIconOverrides)
                {
                    ResolvePortableOverride(portable, pair.Key).LockedIconPath = pair.Value;
                }
            }
        }

        /// <summary>
        /// Gets or creates the per-achievement override record on a portable package. The record is
        /// the authoritative shape, so a writer that only updated the legacy mirror map would have
        /// its value overwritten when normalization re-projects the record.
        /// </summary>
        private static AchievementOverride ResolvePortableOverride(
            GameCustomDataPortableFile portable,
            string apiName)
        {
            if (portable.AchievementOverrides == null)
            {
                portable.AchievementOverrides =
                    new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase);
            }

            if (!portable.AchievementOverrides.TryGetValue(apiName, out var entry) || entry == null)
            {
                entry = new AchievementOverride();
                portable.AchievementOverrides[apiName] = entry;
            }

            return entry;
        }

        private static bool RenameDictionaryKeys<TValue>(
            IDictionary<string, TValue> map,
            IReadOnlyDictionary<string, string> renamedApiNames)
        {
            if (map == null || map.Count == 0)
            {
                return false;
            }

            var changed = false;
            foreach (var oldKey in map.Keys.ToList())
            {
                if (!TryResolveRenamedApiName(renamedApiNames, oldKey, out var newKey))
                {
                    continue;
                }

                var value = map[oldKey];
                map.Remove(oldKey);
                if (!map.ContainsKey(newKey))
                {
                    map[newKey] = value;
                }

                changed = true;
            }

            return changed;
        }

        public void Save(Guid playniteGameId, GameCustomDataFile data)
        {
            Save(playniteGameId, data, previousData: null);
        }

        private void Save(
            Guid playniteGameId,
            GameCustomDataFile data,
            GameCustomDataFile previousData,
            bool affectsSummaryData = true,
            bool affectsOverrideMirror = true)
        {
            // Names the caller. A bulk rename across 641 rows produced 215 store writes over 42
            // seconds with a 5.8s UI freeze inside it, and the log could not say which path
            // emitted them -- every candidate batches correctly when read on its own.
            using (var saveScope = PerfScope.Start(_logger, "GameCustomData.Save", thresholdMs: 10))
            {
                saveScope?.SetContext(DescribeSaveCaller());

                GameCustomDataFile normalized;
                using (PerfScope.Start(_logger, "GameCustomData.Save.Normalize", thresholdMs: 10))
                {
                    normalized = GameCustomDataNormalizer.NormalizeInternal(data, playniteGameId);
                }

                GameCustomDataFile persisted;
                using (PerfScope.Start(_logger, "GameCustomData.Save.Repository", thresholdMs: 10))
                {
                    // Normalized immediately above and untouched since, so the repository does
                    // not repeat it.
                    persisted = _repository.Save(playniteGameId, normalized, alreadyNormalized: true);
                }

                SetCachedEntry(playniteGameId, persisted);
                if (ShouldSyncManagedCustomIconCache(previousData, normalized))
                {
                    SyncManagedCustomIconCache(playniteGameId, normalized);
                }

                _notificationImageStore?.PruneGameImages(
                    playniteGameId,
                    normalized.NotificationAppearanceOverride?.Style);
                // Before the change event, so a recorder has the write in hand before the
                // subscribers that rebuild from it run. In its own guard because this is
                // bookkeeping: a listener that throws must not fail a write that already landed.
                try
                {
                    CustomDataWritten?.Invoke(
                        this,
                        new GameCustomDataWrittenEventArgs(
                            playniteGameId,
                            previousData,
                            persisted,
                            affectsSummaryData,
                            affectsOverrideMirror));
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"A custom-data write subscriber failed for game {playniteGameId}.");
                }

                // Decided here rather than by each writer: the store holds both records, so every
                // path that edits tags (the editor, combine-by-name, undo) is covered alike.
                var affectsTagTotals = previousData == null ||
                    !string.Equals(
                        TagEditSignature(previousData),
                        TagEditSignature(persisted),
                        StringComparison.Ordinal);
                using (PerfScope.Start(_logger, "GameCustomData.Save.RaiseChanged", thresholdMs: 10))
                {
                    RaiseCustomDataChanged(playniteGameId, affectsSummaryData, affectsOverrideMirror, affectsTagTotals);
                }
            }
        }

        public void Delete(Guid playniteGameId)
        {
            _repository.Delete(playniteGameId);
            _repository.DeleteCapstoneArtRecords(playniteGameId);
            lock (_cacheSync)
            {
                EnsureCacheCollections();
                _cacheByGameId.Remove(playniteGameId);
                _missingGameIds.Add(playniteGameId);
            }

            _managedCustomIconService?.ClearGameCustomCache(playniteGameId.ToString("D"));
            _notificationImageStore?.DeleteGameImages(playniteGameId);
            RaiseCustomDataChanged(playniteGameId);
        }

        /// <summary>
        /// Loads every capstone art record. These rows live beside the per-game payload, are not
        /// cached by this store, and are not part of portable packages.
        /// </summary>
        internal IReadOnlyList<CapstoneArtRecordRow> LoadAllCapstoneArtRecords()
        {
            return _repository.LoadAllCapstoneArtRecords();
        }

        /// <summary>
        /// Upserts one game's capstone art record for <paramref name="apiName"/>, or deletes it
        /// when <paramref name="record"/> is null. Raises no change event.
        /// </summary>
        internal void SetCapstoneArtRecord(Guid playniteGameId, string apiName, CapstoneArtRecordRow record)
        {
            _repository.SetCapstoneArtRecord(playniteGameId, apiName, record);
        }

        /// <summary>
        /// Upserts every capstone art record in one transaction. Raises no change event.
        /// </summary>
        internal void ImportCapstoneArtRecords(IReadOnlyList<CapstoneArtRecordRow> rows)
        {
            _repository.ImportCapstoneArtRecords(rows);
        }

        /// <summary>
        /// Persists the schema upgrade that reads have been performing and discarding. Run once at
        /// startup, before the cache is warmed, so the warm reads the upgraded rows.
        /// </summary>
        /// <remarks>
        /// Raises no change event. Every record this rewrites already presented itself to callers
        /// in exactly this shape, so nothing downstream has anything to recompute.
        /// </remarks>
        public int UpgradeStoredRecordsToCurrentSchema()
        {
            var upgraded = _repository.UpgradeStoredRecordsToCurrentSchema();
            if (upgraded > 0)
            {
                InvalidateCache();
            }

            return upgraded;
        }

        /// <summary>
        /// Every stored record, deep-cloned so a caller cannot mutate the cache through what it
        /// is handed. Prefer <see cref="QueryAll{TResult}"/> when the answer is a projection:
        /// the copies here cost a library's worth of overrides, notes and maps.
        /// </summary>
        public IReadOnlyList<GameCustomDataFile> LoadAll()
        {
            EnsureCacheLoaded();
            lock (_cacheSync)
            {
                return _cacheByGameId.Values
                    .Select(data => data?.Clone())
                    .Where(data => data != null)
                    .ToList();
            }
        }

        /// <summary>
        /// Answers a read-only question over every stored record without copying any of them.
        /// </summary>
        /// <remarks>
        /// <see cref="LoadAll"/> deep-clones every record it returns, because a caller that
        /// holds one must not be able to mutate the cache through it. For a caller that only
        /// reads - counting the games on a custom provider, asking whether any game has an
        /// authored achievement - that copied a library's worth of overrides, notes, category
        /// maps and icon maps to produce a number or a bool, and the cost grew with how many
        /// games the user has customized.
        ///
        /// The records handed to <paramref name="query"/> are the live cached instances and are
        /// valid only for the duration of the call: read them, do not store them, and do not
        /// mutate them. The cache lock is held throughout, so <paramref name="query"/> must not
        /// call back into the store.
        /// </remarks>
        /// <summary>
        /// Reads a projection of one game's record without copying it. Returns
        /// <paramref name="missing"/> when the game has no stored custom data.
        /// </summary>
        /// <remarks>
        /// <see cref="TryLoad"/> hands back a deep clone, which is right for a caller that keeps
        /// the record but wrong for one that reads a field or two off it. The whole-library
        /// overview build did the latter once per game, so it deep-copied every customized
        /// game's overrides, notes, category maps and icon maps to resolve summary art.
        ///
        /// What <paramref name="query"/> receives is the live cached instance, valid only for
        /// the duration of the call: read it, do not store it, do not mutate it, and return a
        /// copy of anything that outlives the call. The cache lock is held throughout, so
        /// <paramref name="query"/> must not call back into the store.
        /// </remarks>
        public TResult QueryGame<TResult>(
            Guid playniteGameId,
            Func<GameCustomDataFile, TResult> query,
            TResult missing = default(TResult))
        {
            if (query == null)
            {
                throw new ArgumentNullException(nameof(query));
            }

            if (playniteGameId == Guid.Empty)
            {
                return missing;
            }

            lock (_cacheSync)
            {
                if (_cacheByGameId != null)
                {
                    if (_cacheByGameId.TryGetValue(playniteGameId, out var cached))
                    {
                        return cached != null ? query(cached) : missing;
                    }

                    if (_cacheHoldsEveryStoredRow ||
                        (_missingGameIds != null && _missingGameIds.Contains(playniteGameId)))
                    {
                        return missing;
                    }
                }
            }

            // Not cached yet: fall back to the cloning load, which also populates the cache, so
            // the next read of this game takes the path above.
            return TryLoad(playniteGameId, out var loaded) && loaded != null
                ? query(loaded)
                : missing;
        }

        public TResult QueryAll<TResult>(Func<IEnumerable<GameCustomDataFile>, TResult> query)
        {
            if (query == null)
            {
                throw new ArgumentNullException(nameof(query));
            }

            EnsureCacheLoaded();
            lock (_cacheSync)
            {
                return query(_cacheByGameId.Values);
            }
        }

        public HashSet<Guid> GetExcludedRefreshGameIds(ISet<Guid> fallbackIds = null)
        {
            return GetExcludedGameIds(fallbackIds, data => data?.ExcludedFromRefreshes == true);
        }

        public HashSet<Guid> GetExcludedSummaryGameIds(ISet<Guid> fallbackIds = null)
        {
            return GetExcludedGameIds(fallbackIds, data => data?.ExcludedFromSummaries == true);
        }

        public void ExportPortablePackage(Guid playniteGameId, string destinationPath)
        {
            EnsurePortablePackageExtension(destinationPath);

            var portable = LoadNormalizedPortableOrThrow(playniteGameId);
            ScopePortableForExport(playniteGameId, portable);
            var fileStems = AchievementIconCachePathBuilder.BuildFileStems(
                EnumeratePortableIconApiNames(portable));
            var categoryFileStems = AchievementIconCachePathBuilder.BuildCategoryFileStems(
                EnumeratePortableCategoryLabels(portable));
            var imageSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            RewritePortableIconsForPackage(
                playniteGameId,
                portable.AchievementUnlockedIconOverrides,
                fileStems,
                AchievementIconVariant.Unlocked,
                imageSources);
            RewritePortableIconsForPackage(
                playniteGameId,
                portable.AchievementLockedIconOverrides,
                fileStems,
                AchievementIconVariant.Locked,
                imageSources);
            RewritePortableCustomAchievementIconsForPackage(
                playniteGameId,
                portable.CustomAchievements,
                fileStems,
                imageSources);
            RewritePortableCategoryImagesForPackage(
                playniteGameId,
                portable.AchievementCategoryImageOverrides,
                categoryFileStems,
                imageSources);
            RewritePortableNotificationImagesForPackage(
                playniteGameId,
                portable.NotificationAppearanceOverride?.Style,
                imageSources);
            // The rewrites above retarget the legacy mirror maps at package-relative paths. Mirror
            // them back onto the record, or the manifest ships two disagreeing copies and the
            // record's absolute local paths win when the package is normalized on import.
            SyncPortableOverrideIconsFromLegacyMaps(portable);

            EnsureDestinationDirectory(destinationPath);
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            using (var archive = ZipFile.Open(destinationPath, ZipArchiveMode.Create))
            {
                var manifestEntry = archive.CreateEntry(PortablePackageManifestEntryName, CompressionLevel.Optimal);
                using (var writer = new StreamWriter(manifestEntry.Open()))
                {
                    writer.Write(JsonConvert.SerializeObject(portable, _writeSettings));
                }

                foreach (var pair in imageSources.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(pair.Value) || !File.Exists(pair.Value))
                    {
                        throw new InvalidOperationException($"Missing bundled icon file: {pair.Value ?? pair.Key}");
                    }

                    // Stored icons keep their source bytes; anything not portable goes in as PNG.
                    var imageEntry = archive.CreateEntry(pair.Key, CompressionLevel.Optimal);
                    using (var destination = imageEntry.Open())
                    {
                        PackageImageFormat.CopyPortable(pair.Value, destination);
                    }
                }
            }
        }

        /// <summary>
        /// The game keys a .pa package names (provider identity, name and platform of the game it
        /// was exported from), without importing anything. Empty for a package written before
        /// game keys existed or for a custom-achievements package, which has no manifest.
        /// </summary>
        public IReadOnlyList<PortableGameKey> ReadPortableGameKeys(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath) || !IsPortablePackagePath(sourcePath))
            {
                return Array.Empty<PortableGameKey>();
            }

            using (var archive = ZipFile.OpenRead(sourcePath))
            {
                var portable = ReadPortableManifestOrNull(IndexPackageEntries(archive));
                return portable?.GameKeys?.Where(key => key != null).ToList() ?? (IReadOnlyList<PortableGameKey>)Array.Empty<PortableGameKey>();
            }
        }

        public PortableGameCustomDataImportResult ImportReplacePortable(Guid playniteGameId, string sourcePath)
        {
            if (playniteGameId == Guid.Empty)
            {
                throw new ArgumentException("Game ID is required.", nameof(playniteGameId));
            }

            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException("Source path is required.", nameof(sourcePath));
            }

            if (!IsPortablePackagePath(sourcePath))
            {
                throw new InvalidOperationException("Only .PA files are supported.");
            }

            return ImportReplacePortablePackage(playniteGameId, sourcePath);
        }

        public GameCustomDataFile ImportReplace(Guid playniteGameId, string sourcePath)
        {
            if (playniteGameId == Guid.Empty)
            {
                throw new ArgumentException("Game ID is required.", nameof(playniteGameId));
            }

            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException("Source path is required.", nameof(sourcePath));
            }

            var json = File.ReadAllText(sourcePath);
            var portable = JsonConvert.DeserializeObject<GameCustomDataPortableFile>(json);
            return ImportPortableReplace(playniteGameId, portable, "Imported JSON does not contain any portable custom data.");
        }

        public string MigrateLegacyConfig(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return rawJson;
            }

            try
            {
                var migration = _legacyMigration.Parse(rawJson);
                var rowsToSave = new System.Collections.Generic.List<GameCustomDataFile>();
                foreach (var pair in migration.LegacyByGame)
                {
                    var legacy = GameCustomDataNormalizer.NormalizeInternal(pair.Value, pair.Key);
                    var existing = _repository.TryLoad(pair.Key, out var existingData) ? existingData : null;
                    var merged = GameCustomDataNormalizer.MergePreferExisting(existing, legacy);
                    if (GameCustomDataNormalizer.HasInternalData(merged))
                    {
                        rowsToSave.Add(merged);
                    }
                }

                _repository.SaveMany(rowsToSave);
                InvalidateCache();
                return migration.CleanedJson;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed migrating legacy per-game custom data. Using original settings JSON.");
                return rawJson;
            }
        }

        public bool HasPortableData(Guid playniteGameId)
        {
            return GameCustomDataNormalizer.HasPortableData(LoadOrDefault(playniteGameId));
        }

        public void SyncRuntimeCaches()
        {
            // Per-game custom data is read directly from the database at runtime.
        }

        private GameCustomDataPortableFile LoadNormalizedPortableOrThrow(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty)
            {
                throw new ArgumentException("Game ID is required.", nameof(playniteGameId));
            }

            var internalData = LoadOrDefault(playniteGameId);
            var normalized = GameCustomDataNormalizer.NormalizeInternal(internalData, playniteGameId);
            if (!GameCustomDataNormalizer.HasPortableData(normalized))
            {
                throw new InvalidOperationException("No exportable custom data exists for this game.");
            }

            var portable = GameCustomDataNormalizer.NormalizePortable(normalized.ToPortable(), playniteGameId);
            PortablePersonalState.Strip(portable);
            if (!GameCustomDataNormalizer.HasPortableData(portable))
            {
                throw new InvalidOperationException("No exportable custom data exists for this game.");
            }

            portable.CustomProvider = string.IsNullOrWhiteSpace(portable.CustomProviderId)
                ? null
                : _tryGetCustomProvider?.Invoke(portable.CustomProviderId)?.Clone();
            if (portable.CustomProvider != null && !IsHttpUrl(portable.CustomProvider.IconSource))
            {
                // A local SVG path names the sharer's folders; the icon itself is IconPathData.
                portable.CustomProvider.IconSource = null;
            }

            portable.Kind = GameCustomDataPortableFile.GameCustomDataKind;
            var keys = SafeResolveGameKeys(playniteGameId);
            portable.GameKeys = keys.Count == 0 ? null : keys;
            return portable;
        }

        private List<PortableGameKey> SafeResolveGameKeys(Guid playniteGameId)
        {
            try
            {
                return (_resolveGameKeys?.Invoke(playniteGameId) ?? Array.Empty<PortableGameKey>())
                    .Where(key => key != null)
                    .Select(key => key.Clone())
                    .ToList();
            }
            catch (Exception ex)
            {
                // A missing key only costs the workshop its automatic match; the export still works.
                _logger?.Debug(ex, $"Failed resolving portable game keys for {playniteGameId}.");
                return new List<PortableGameKey>();
            }
        }

        /// <summary>
        /// Keeps an imported assignment only when the id resolves locally, or when the package's
        /// embedded definition could be recreated under that id. A local definition wins.
        /// </summary>
        private string ResolveImportedCustomProviderId(GameCustomDataPortableFile portable)
        {
            var id = portable?.CustomProviderId;
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            if (_tryGetCustomProvider?.Invoke(id) != null)
            {
                return id;
            }

            var snapshot = portable.CustomProvider;
            if (snapshot == null || _importCustomProviderIfMissing == null)
            {
                return null;
            }

            var definition = snapshot.Clone();
            definition.Id = id;
            return _importCustomProviderIfMissing(definition) ? id : null;
        }

        private PortableGameCustomDataImportResult ImportReplacePortablePackage(Guid playniteGameId, string sourcePath)
        {
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("Package file not found.", sourcePath);
            }

            using (var archive = ZipFile.OpenRead(sourcePath))
            {
                var entriesByName = IndexPackageEntries(archive);

                if (entriesByName.ContainsKey(PortablePackageManifestEntryName))
                {
                    var portable = ReadPortableManifestOrNull(entriesByName);
                    ScopePortableForGame(portable, LoadAchievementDataOrNull(playniteGameId));
                    RewritePackageManifestImages(
                        new ManagedPackageImageSink(this, playniteGameId),
                        entriesByName,
                        portable);

                    return new PortableGameCustomDataImportResult
                    {
                        ImportedData = ImportPortableReplace(
                            playniteGameId,
                            portable,
                            "Imported .PA.ZIP does not contain any portable custom data.")
                    };
                }

                // A custom-achievements package is merged by ID (ImportCustomAchievementsPackage);
                // replacing the game's custom data with it would drop everything else the game has.
                if (IsCustomAchievementsPackage(entriesByName.Keys))
                {
                    throw new InvalidOperationException(
                        "This .PA file contains custom achievements only and cannot replace the game's custom data.");
                }

                return ImportReplacePortableImageOnlyPackage(playniteGameId, entriesByName);
            }
        }

        /// <summary>
        /// The package's file entries keyed by normalized entry name; the first entry wins when
        /// two normalize to the same name. Folder entries are left out.
        /// </summary>
        private static Dictionary<string, ZipArchiveEntry> IndexPackageEntries(ZipArchive archive)
        {
            return archive.Entries
                .Select(entry => new { Entry = entry, Name = NormalizeArchiveEntryName(entry.FullName) })
                .Where(item => item.Entry != null &&
                               !string.IsNullOrWhiteSpace(item.Entry.Name) &&
                               !string.IsNullOrWhiteSpace(item.Name))
                .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Entry, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The deserialized <see cref="PortablePackageManifestEntryName"/> entry, or null when the
        /// package has none or it deserializes to nothing.
        /// </summary>
        private static GameCustomDataPortableFile ReadPortableManifestOrNull(
            IReadOnlyDictionary<string, ZipArchiveEntry> entriesByName)
        {
            if (!entriesByName.TryGetValue(PortablePackageManifestEntryName, out var manifestEntry))
            {
                return null;
            }

            using (var reader = new StreamReader(manifestEntry.Open()))
            {
                return JsonConvert.DeserializeObject<GameCustomDataPortableFile>(reader.ReadToEnd());
            }
        }

        /// <summary>
        /// Points every image a manifest references at the file the sink wrote for its package
        /// entry: icon overrides, custom achievement icons, category images and notification
        /// images, in that order. Shared by import and preview so both validate the same way.
        /// </summary>
        private void RewritePackageManifestImages(
            IPackageImageSink sink,
            IReadOnlyDictionary<string, ZipArchiveEntry> entriesByName,
            GameCustomDataPortableFile portable)
        {
            RewritePackageImageOverrides(sink, entriesByName, portable?.AchievementUnlockedIconOverrides, AchievementIconVariant.Unlocked);
            RewritePackageImageOverrides(sink, entriesByName, portable?.AchievementLockedIconOverrides, AchievementIconVariant.Locked);
            if (portable != null)
            {
                // The rewrites above only see the legacy mirror maps. Republish them onto
                // the record, which normalization treats as authoritative, so the imported
                // icons point at the extracted files rather than the exporter's paths.
                SyncPortableOverrideIconsFromLegacyMaps(portable);
            }

            RewritePackageCustomAchievementImages(sink, entriesByName, portable?.CustomAchievements);
            RewritePackageCategoryImageOverrides(sink, entriesByName, portable?.AchievementCategoryImageOverrides);
            RewritePackageNotificationImages(
                sink,
                entriesByName,
                portable?.NotificationAppearanceOverride?.Style);
        }

        private PortableGameCustomDataImportResult ImportReplacePortableImageOnlyPackage(
            Guid playniteGameId,
            IReadOnlyDictionary<string, ZipArchiveEntry> entriesByName)
        {
            var achievementApiNames = LoadAchievementApiNamesForImageOnlyPackageOrThrow(playniteGameId);
            var achievementApiNameSet = new HashSet<string>(achievementApiNames, StringComparer.OrdinalIgnoreCase);
            var fileStems = AchievementIconCachePathBuilder.BuildFileStems(achievementApiNames);
            var portable = new GameCustomDataPortableFile();
            var ignoredPackageImages = 0;

            foreach (var pair in entriesByName.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!TryParseImageOnlyPackageEntry(pair.Key, out var apiName, out var variant))
                {
                    continue;
                }

                if (!achievementApiNameSet.Contains(apiName))
                {
                    ignoredPackageImages++;
                    continue;
                }

                if (!fileStems.TryGetValue(apiName, out var fileStem) || string.IsNullOrWhiteSpace(fileStem))
                {
                    ignoredPackageImages++;
                    continue;
                }

                var managedPath = ImportPackageImageToManagedPath(
                    playniteGameId,
                    pair.Value,
                    fileStem,
                    variant);
                if (variant == AchievementIconVariant.Locked)
                {
                    if (portable.AchievementLockedIconOverrides == null)
                    {
                        portable.AchievementLockedIconOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }

                    portable.AchievementLockedIconOverrides[apiName] = managedPath;
                    ResolvePortableOverride(portable, apiName).LockedIconPath = managedPath;
                }
                else
                {
                    if (portable.AchievementUnlockedIconOverrides == null)
                    {
                        portable.AchievementUnlockedIconOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }

                    portable.AchievementUnlockedIconOverrides[apiName] = managedPath;
                    ResolvePortableOverride(portable, apiName).UnlockedIconPath = managedPath;
                }
            }

            if (!GameCustomDataNormalizer.HasPortableData(portable))
            {
                throw new InvalidOperationException("Image-only .PA.ZIP did not contain any images matching this game's achievement API names.");
            }

            return new PortableGameCustomDataImportResult
            {
                ImportedData = ImportPortableReplace(
                    playniteGameId,
                    portable,
                    "Imported .PA.ZIP does not contain any portable custom data."),
                IgnoredPackageImageCount = ignoredPackageImages
            };
        }

        private void RewritePortableIconsForPackage(
            Guid playniteGameId,
            Dictionary<string, string> overrides,
            IReadOnlyDictionary<string, string> fileStems,
            AchievementIconVariant variant,
            IDictionary<string, string> imageSources)
        {
            if (overrides == null || overrides.Count == 0)
            {
                return;
            }

            foreach (var pair in overrides.ToList())
            {
                var apiName = NormalizeText(pair.Key);
                var overrideValue = NormalizeText(pair.Value);
                if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(overrideValue))
                {
                    overrides.Remove(pair.Key);
                    continue;
                }

                if (!fileStems.TryGetValue(apiName, out var fileStem) || string.IsNullOrWhiteSpace(fileStem))
                {
                    throw new InvalidOperationException($"Could not determine a bundled icon name for '{apiName}'.");
                }

                var bundledSource = ResolveBundledIconSourcePath(playniteGameId, overrideValue, fileStem, variant);
                var relativeEntryName = BuildPackageImageEntryName(fileStem, variant);
                overrides[apiName] = relativeEntryName;
                imageSources[relativeEntryName] = bundledSource;
            }
        }

        private static void RewritePackageImageOverrides(
            IPackageImageSink sink,
            IReadOnlyDictionary<string, ZipArchiveEntry> entriesByName,
            Dictionary<string, string> overrides,
            AchievementIconVariant variant)
        {
            if (overrides == null || overrides.Count == 0)
            {
                return;
            }

            sink.EnsureIconTargetAvailable();
            var fileStems = AchievementIconCachePathBuilder.BuildFileStems(overrides.Keys);

            foreach (var pair in overrides.ToList())
            {
                var apiName = NormalizeText(pair.Key);
                var overrideValue = NormalizeText(pair.Value);
                if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(overrideValue))
                {
                    overrides.Remove(pair.Key);
                    continue;
                }

                if (IsHttpUrl(overrideValue))
                {
                    overrides[apiName] = overrideValue;
                    continue;
                }

                var normalizedEntryName = NormalizePackageImagePathOrThrow(overrideValue);
                if (!entriesByName.TryGetValue(normalizedEntryName, out var imageEntry))
                {
                    throw new InvalidOperationException($"Package is missing bundled icon entry '{overrideValue}'.");
                }

                if (!fileStems.TryGetValue(apiName, out var fileStem) || string.IsNullOrWhiteSpace(fileStem))
                {
                    throw new InvalidOperationException($"Could not determine a managed custom icon path for '{apiName}'.");
                }

                overrides[apiName] = sink.WriteAchievementIcon(imageEntry, fileStem, variant);
            }
        }

        private void RewritePortableCustomAchievementIconsForPackage(
            Guid playniteGameId,
            IReadOnlyList<CustomAchievementDefinition> customAchievements,
            IReadOnlyDictionary<string, string> fileStems,
            IDictionary<string, string> imageSources)
        {
            if (customAchievements == null || customAchievements.Count == 0)
            {
                return;
            }

            foreach (var definition in customAchievements)
            {
                var apiName = CustomAchievementProjectionService.BuildApiName(definition?.Id);
                if (definition == null || string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                definition.UnlockedIconPath = RewritePortableCustomAchievementIconForPackage(
                    playniteGameId,
                    apiName,
                    definition.UnlockedIconPath,
                    fileStems,
                    AchievementIconVariant.Unlocked,
                    imageSources);
                definition.LockedIconPath = RewritePortableCustomAchievementIconForPackage(
                    playniteGameId,
                    apiName,
                    definition.LockedIconPath,
                    fileStems,
                    AchievementIconVariant.Locked,
                    imageSources);
            }
        }

        private string RewritePortableCustomAchievementIconForPackage(
            Guid playniteGameId,
            string apiName,
            string value,
            IReadOnlyDictionary<string, string> fileStems,
            AchievementIconVariant variant,
            IDictionary<string, string> imageSources)
        {
            var normalizedValue = NormalizeText(value);
            if (string.IsNullOrWhiteSpace(normalizedValue))
            {
                return null;
            }

            if (!fileStems.TryGetValue(apiName, out var fileStem) || string.IsNullOrWhiteSpace(fileStem))
            {
                throw new InvalidOperationException($"Could not determine a bundled icon name for '{apiName}'.");
            }

            var bundledSource = ResolveBundledIconSourcePath(playniteGameId, normalizedValue, fileStem, variant);
            var relativeEntryName = BuildPackageImageEntryName(fileStem, variant);
            imageSources[relativeEntryName] = bundledSource;
            return relativeEntryName;
        }

        private static void RewritePackageCustomAchievementImages(
            IPackageImageSink sink,
            IReadOnlyDictionary<string, ZipArchiveEntry> entriesByName,
            IReadOnlyList<CustomAchievementDefinition> customAchievements)
        {
            if (customAchievements == null || customAchievements.Count == 0)
            {
                return;
            }

            var apiNames = customAchievements
                .Select(definition => CustomAchievementProjectionService.BuildApiName(definition?.Id))
                .Where(apiName => !string.IsNullOrWhiteSpace(apiName))
                .ToList();
            var fileStems = AchievementIconCachePathBuilder.BuildFileStems(apiNames);

            foreach (var definition in customAchievements)
            {
                var apiName = CustomAchievementProjectionService.BuildApiName(definition?.Id);
                if (definition == null || string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                definition.UnlockedIconPath = RewritePackageCustomAchievementImage(
                    sink,
                    entriesByName,
                    fileStems,
                    apiName,
                    definition.UnlockedIconPath,
                    AchievementIconVariant.Unlocked);
                definition.LockedIconPath = RewritePackageCustomAchievementImage(
                    sink,
                    entriesByName,
                    fileStems,
                    apiName,
                    definition.LockedIconPath,
                    AchievementIconVariant.Locked);
            }
        }

        private static string RewritePackageCustomAchievementImage(
            IPackageImageSink sink,
            IReadOnlyDictionary<string, ZipArchiveEntry> entriesByName,
            IReadOnlyDictionary<string, string> fileStems,
            string apiName,
            string value,
            AchievementIconVariant variant)
        {
            var normalizedValue = NormalizeText(value);
            if (string.IsNullOrWhiteSpace(normalizedValue) || IsHttpUrl(normalizedValue))
            {
                return normalizedValue;
            }

            var normalizedEntryName = NormalizePackageImagePathOrThrow(normalizedValue);
            if (!entriesByName.TryGetValue(normalizedEntryName, out var imageEntry))
            {
                throw new InvalidOperationException($"Package is missing bundled icon entry '{normalizedValue}'.");
            }

            if (!fileStems.TryGetValue(apiName, out var fileStem) || string.IsNullOrWhiteSpace(fileStem))
            {
                throw new InvalidOperationException($"Could not determine a managed custom icon path for '{apiName}'.");
            }

            return sink.WriteCustomAchievementIcon(imageEntry, fileStem, variant);
        }

        private void RewritePortableCategoryImagesForPackage(
            Guid playniteGameId,
            Dictionary<string, CategoryImageOverrideData> overrides,
            IReadOnlyDictionary<string, string> fileStems,
            IDictionary<string, string> imageSources)
        {
            if (overrides == null || overrides.Count == 0)
            {
                return;
            }

            foreach (var pair in overrides.ToList())
            {
                var category = AchievementCategoryHelper.NormalizeCategoryOrDefault(pair.Key);
                if (string.IsNullOrWhiteSpace(category) || pair.Value == null)
                {
                    overrides.Remove(pair.Key);
                    continue;
                }

                if (!fileStems.TryGetValue(category, out var fileStem) || string.IsNullOrWhiteSpace(fileStem))
                {
                    throw new InvalidOperationException($"Could not determine a bundled category image name for '{category}'.");
                }

                RewritePortableCategoryImageForPackage(
                    playniteGameId,
                    pair.Value,
                    fileStem,
                    imageSources);

                if (string.IsNullOrWhiteSpace(pair.Value.Art))
                {
                    overrides.Remove(pair.Key);
                }
            }
        }

        private void RewritePortableCategoryImageForPackage(
            Guid playniteGameId,
            CategoryImageOverrideData overrideData,
            string fileStem,
            IDictionary<string, string> imageSources)
        {
            if (overrideData == null)
            {
                return;
            }

            var value = NormalizeText(overrideData.Art);
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var bundledSource = ResolveBundledCategoryImageSourcePath(
                playniteGameId,
                value,
                fileStem);
            var relativeEntryName = BuildPackageCategoryImageEntryName(fileStem);
            overrideData.Art = relativeEntryName;

            imageSources[relativeEntryName] = bundledSource;
        }

        private static void RewritePackageCategoryImageOverrides(
            IPackageImageSink sink,
            IReadOnlyDictionary<string, ZipArchiveEntry> entriesByName,
            Dictionary<string, CategoryImageOverrideData> overrides)
        {
            if (overrides == null || overrides.Count == 0)
            {
                return;
            }

            sink.EnsureIconTargetAvailable();
            var fileStems = AchievementIconCachePathBuilder.BuildCategoryFileStems(overrides.Keys);

            foreach (var pair in overrides.ToList())
            {
                var category = AchievementCategoryHelper.NormalizeCategoryOrDefault(pair.Key);
                if (string.IsNullOrWhiteSpace(category) || pair.Value == null)
                {
                    overrides.Remove(pair.Key);
                    continue;
                }

                if (!fileStems.TryGetValue(category, out var fileStem) || string.IsNullOrWhiteSpace(fileStem))
                {
                    throw new InvalidOperationException($"Could not determine a managed category image path for '{category}'.");
                }

                pair.Value.Art = RewritePackageCategoryImageOverride(
                    sink,
                    entriesByName,
                    fileStem,
                    pair.Value.Art);

                if (string.IsNullOrWhiteSpace(pair.Value.Art))
                {
                    overrides.Remove(pair.Key);
                }
            }
        }

        private static string RewritePackageCategoryImageOverride(
            IPackageImageSink sink,
            IReadOnlyDictionary<string, ZipArchiveEntry> entriesByName,
            string fileStem,
            string overrideValue)
        {
            var normalizedValue = NormalizeText(overrideValue);
            if (string.IsNullOrWhiteSpace(normalizedValue) || IsHttpUrl(normalizedValue))
            {
                return normalizedValue;
            }

            var normalizedEntryName = NormalizePackageImagePathOrThrow(normalizedValue);
            if (!entriesByName.TryGetValue(normalizedEntryName, out var imageEntry))
            {
                throw new InvalidOperationException($"Package is missing bundled category image entry '{overrideValue}'.");
            }

            return sink.WriteCategoryImage(imageEntry, fileStem);
        }

        private void RewritePortableNotificationImagesForPackage(
            Guid playniteGameId,
            NotificationStyleSettings style,
            IDictionary<string, string> imageSources)
        {
            if (style == null)
            {
                return;
            }

            foreach (var slot in NotificationImageSlotMap.Slots)
            {
                var sourceValue = NormalizeText(NotificationImageSlotMap.GetPath(style, slot));
                if (string.IsNullOrWhiteSpace(sourceValue))
                {
                    NotificationImageSlotMap.SetPath(style, slot, null);
                    continue;
                }

                var sourcePath = sourceValue;
                if (!File.Exists(sourcePath))
                {
                    if (_notificationImageStore == null)
                    {
                        throw new InvalidOperationException("Notification image store is not available.");
                    }

                    sourcePath = _notificationImageStore
                        .MaterializeAsync(
                            sourceValue,
                            NotificationImageOwner.ForGame(playniteGameId),
                            slot,
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                }

                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                {
                    NotificationImageSlotMap.SetPath(style, slot, null);
                    continue;
                }

                var extension = Path.GetExtension(sourcePath);
                if (!IsSupportedPackageImageExtension(extension))
                {
                    extension = ".png";
                }

                var entryName = PortablePackageImagesFolderName + "/" +
                    NotificationImageEntryStems[slot] + extension.ToLowerInvariant();
                NotificationImageSlotMap.SetPath(style, slot, entryName);
                imageSources[entryName] = sourcePath;
            }
        }

        private static void RewritePackageNotificationImages(
            IPackageImageSink sink,
            IReadOnlyDictionary<string, ZipArchiveEntry> entriesByName,
            NotificationStyleSettings style)
        {
            if (style == null)
            {
                return;
            }

            foreach (var slot in NotificationImageSlotMap.Slots)
            {
                var prefix = PortablePackageImagesFolderName + "/" + NotificationImageEntryStems[slot] + ".";
                var entryPair = entriesByName.FirstOrDefault(pair =>
                    pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                    IsSupportedPackageImageExtension(Path.GetExtension(pair.Key)));
                if (string.IsNullOrWhiteSpace(entryPair.Key) || entryPair.Value == null)
                {
                    // The package entries are authoritative; never retain a manifest path.
                    NotificationImageSlotMap.SetPath(style, slot, null);
                    continue;
                }

                sink.EnsureNotificationTargetAvailable();

                var normalizedEntryName = NormalizePackageImagePathOrThrow(entryPair.Key);
                var entry = entriesByName[normalizedEntryName];
                var extension = Path.GetExtension(entry.Name);
                if (!IsSupportedPackageImageExtension(extension))
                {
                    throw new InvalidOperationException(
                        $"Unsupported bundled notification image '{entry.FullName}'.");
                }

                EnsureBundledImageDecodableOrThrow(entry.FullName);

                NotificationImageSlotMap.SetPath(style, slot, sink.WriteNotificationImage(entry, slot, extension));
            }
        }

        private string ResolveBundledIconSourcePath(
            Guid playniteGameId,
            string overrideValue,
            string fileStem,
            AchievementIconVariant variant)
        {
            var normalizedValue = NormalizeText(overrideValue);
            if (string.IsNullOrWhiteSpace(normalizedValue))
            {
                throw new InvalidOperationException("Cannot bundle an empty custom icon override.");
            }

            var managedIcons = GetManagedCustomIconServiceOrThrow();
            var gameIdText = playniteGameId.ToString("D");
            if ((managedIcons.IsManagedCustomIconPath(normalizedValue, gameIdText) ||
                 AchievementIconCachePathBuilder.IsMetadataSourceIconPath(normalizedValue, gameIdText)) &&
                File.Exists(normalizedValue))
            {
                return normalizedValue;
            }

            var bundledSource = managedIcons
                .MaterializeCustomIconAsync(
                    normalizedValue,
                    gameIdText,
                    fileStem,
                    variant,
                    CancellationToken.None,
                    overwriteExistingTarget: false)
                .GetAwaiter()
                .GetResult();

            if (string.IsNullOrWhiteSpace(bundledSource) || !File.Exists(bundledSource))
            {
                throw new InvalidOperationException($"Failed to bundle custom icon override '{normalizedValue}'.");
            }

            return bundledSource;
        }

        private string ResolveBundledCategoryImageSourcePath(
            Guid playniteGameId,
            string overrideValue,
            string fileStem)
        {
            var normalizedValue = NormalizeText(overrideValue);
            if (string.IsNullOrWhiteSpace(normalizedValue))
            {
                throw new InvalidOperationException("Cannot bundle an empty category image override.");
            }

            var managedIcons = GetManagedCustomIconServiceOrThrow();
            var gameIdText = playniteGameId.ToString("D");
            if (managedIcons.IsManagedCustomIconPath(normalizedValue, gameIdText) && File.Exists(normalizedValue))
            {
                return normalizedValue;
            }

            var bundledSource = managedIcons
                .MaterializeCategoryImageAsync(
                    normalizedValue,
                    gameIdText,
                    fileStem,
                    CancellationToken.None,
                    overwriteExistingTarget: false)
                .GetAwaiter()
                .GetResult();

            if (string.IsNullOrWhiteSpace(bundledSource) || !File.Exists(bundledSource))
            {
                throw new InvalidOperationException($"Failed to bundle category image override '{normalizedValue}'.");
            }

            return bundledSource;
        }

        /// <summary>The game's cached achievements, or null when there are none or no data service is attached.</summary>
        private GameAchievementData LoadAchievementDataOrNull(Guid playniteGameId)
        {
            return _achievementDataService?.GetGameAchievementData(playniteGameId);
        }

        /// <summary>
        /// Writes portable keys in place of a scoped provider's per-scope keys
        /// (<see cref="GameCustomDataPortableScoping.ToPortable"/>). Icons count as the same when
        /// their files have the same content.
        /// </summary>
        private void ScopePortableForExport(Guid playniteGameId, GameCustomDataPortableFile portable)
        {
            var data = LoadAchievementDataOrNull(playniteGameId);
            var scope = AchievementKeyScopes.For(data?.ProviderKey);
            if (scope == null)
            {
                return;
            }

            var gameIdText = playniteGameId.ToString("D");
            GameCustomDataPortableScoping.ToPortable(
                portable,
                scope,
                data.Achievements,
                value => IconContentIdentity(value, gameIdText));
        }

        /// <summary>
        /// Spreads a package's portable keys over the target game's scopes
        /// (<see cref="GameCustomDataPortableScoping.ToGame"/>). A no-op without cached rows or
        /// for an unscoped provider.
        /// </summary>
        private static void ScopePortableForGame(GameCustomDataPortableFile portable, GameAchievementData target)
        {
            GameCustomDataPortableScoping.ToGame(
                portable,
                AchievementKeyScopes.For(target?.ProviderKey),
                target?.Achievements);
        }

        private string IconContentIdentity(string value, string gameIdText)
        {
            var path = _managedCustomIconService?.ResolveManagedDisplayPath(value, gameIdText) ?? value;
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    using (var sha = SHA256.Create())
                    using (var stream = File.OpenRead(path))
                    {
                        return BitConverter.ToString(sha.ComputeHash(stream));
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return value;
        }

        private IReadOnlyList<string> LoadAchievementApiNamesForImageOnlyPackageOrThrow(Guid playniteGameId)
        {
            if (_achievementDataService == null)
            {
                throw new InvalidOperationException("Achievement data service is not available.");
            }

            var apiNames = LoadAchievementDataOrNull(playniteGameId)?
                .Achievements?
                .Where(achievement => achievement != null && !string.IsNullOrWhiteSpace(achievement.ApiName))
                .Select(achievement => achievement.ApiName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (apiNames == null || apiNames.Count == 0)
            {
                throw new InvalidOperationException("Image-only .PA.ZIP imports require cached achievements for the target game.");
            }

            return apiNames;
        }

        private string ImportPackageImageToManagedPath(
            Guid playniteGameId,
            ZipArchiveEntry imageEntry,
            string fileStem,
            AchievementIconVariant variant)
        {
            if (imageEntry == null)
            {
                throw new InvalidOperationException("Package image entry is missing.");
            }

            var managedIcons = GetManagedCustomIconServiceOrThrow();
            var extension = Path.GetExtension(imageEntry.Name);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".png";
            }

            var tempDirectory = Path.Combine(Path.GetTempPath(), "PlayniteAchievements", "PortableImports");
            Directory.CreateDirectory(tempDirectory);

            var tempPath = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + extension);
            try
            {
                using (var source = imageEntry.Open())
                using (var destination = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    source.CopyTo(destination);
                }

                var managedPath = managedIcons
                    .MaterializeCustomIconAsync(
                        tempPath,
                        playniteGameId.ToString("D"),
                        fileStem,
                        variant,
                        CancellationToken.None,
                        overwriteExistingTarget: true)
                    .GetAwaiter()
                    .GetResult();

                if (string.IsNullOrWhiteSpace(managedPath) || !File.Exists(managedPath))
                {
                    throw new InvalidOperationException($"Failed to import packaged image '{imageEntry.FullName}'.");
                }

                return managedPath;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                }
            }
        }

        private GameCustomDataFile ImportPortableReplace(
            Guid playniteGameId,
            GameCustomDataPortableFile portable,
            string invalidDataMessage)
        {
            var normalizedPortable = GameCustomDataNormalizer.NormalizePortable(portable, playniteGameId);
            PortablePersonalState.Strip(normalizedPortable);
            if (!GameCustomDataNormalizer.HasPortableData(normalizedPortable))
            {
                throw new InvalidOperationException(invalidDataMessage);
            }

            normalizedPortable.CustomProviderId = ResolveImportedCustomProviderId(normalizedPortable);

            var current = LoadOrDefault(playniteGameId);
            var merged = GameCustomDataFile.FromPortable(
                normalizedPortable,
                playniteGameId,
                current.ExcludedFromRefreshes,
                current.ExcludedFromSummaries);
            PortablePersonalState.CarryLocal(current, merged);

            Save(playniteGameId, merged);
            return LoadOrDefault(playniteGameId);
        }

        private void SyncManagedCustomIconCache(Guid playniteGameId, GameCustomDataFile normalizedData)
        {
            if (_managedCustomIconService == null)
            {
                return;
            }

            var gameIdText = playniteGameId.ToString("D");
            if (!GameCustomDataNormalizer.HasInternalData(normalizedData))
            {
                _managedCustomIconService.ClearGameCustomCache(gameIdText);
                return;
            }

            _managedCustomIconService.PruneGameCustomCache(
                gameIdText,
                EnumerateManagedCustomIconPaths(playniteGameId, normalizedData));
        }

        private static bool ShouldSyncManagedCustomIconCache(
            GameCustomDataFile previousData,
            GameCustomDataFile currentData)
        {
            if (previousData == null)
            {
                return true;
            }

            return !StringMapEquals(
                       previousData.AchievementUnlockedIconOverrides,
                       currentData?.AchievementUnlockedIconOverrides) ||
                   !StringMapEquals(
                       previousData.AchievementLockedIconOverrides,
                       currentData?.AchievementLockedIconOverrides) ||
                   !CategoryImageMapEquals(
                       previousData.AchievementCategoryImageOverrides,
                       currentData?.AchievementCategoryImageOverrides);
        }

        private static bool StringMapEquals(
            IReadOnlyDictionary<string, string> left,
            IReadOnlyDictionary<string, string> right)
        {
            var leftCount = left?.Count ?? 0;
            var rightCount = right?.Count ?? 0;
            if (leftCount != rightCount)
            {
                return false;
            }

            if (leftCount == 0)
            {
                return true;
            }

            foreach (var pair in left)
            {
                var key = NormalizeText(pair.Key);
                if (string.IsNullOrWhiteSpace(key) ||
                    right == null ||
                    !right.TryGetValue(key, out var rightValue) ||
                    !string.Equals(NormalizeText(pair.Value), NormalizeText(rightValue), StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool CategoryImageMapEquals(
            IReadOnlyDictionary<string, CategoryImageOverrideData> left,
            IReadOnlyDictionary<string, CategoryImageOverrideData> right)
        {
            var leftCount = left?.Count ?? 0;
            var rightCount = right?.Count ?? 0;
            if (leftCount != rightCount)
            {
                return false;
            }

            if (leftCount == 0)
            {
                return true;
            }

            foreach (var pair in left)
            {
                var key = AchievementCategoryHelper.NormalizeCategoryOrDefault(pair.Key);
                if (string.IsNullOrWhiteSpace(key) ||
                    right == null ||
                    !right.TryGetValue(key, out var rightValue))
                {
                    return false;
                }

                if (!string.Equals(NormalizeText(pair.Value?.Art), NormalizeText(rightValue?.Art), StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        private IEnumerable<string> EnumerateManagedCustomIconPaths(Guid playniteGameId, GameCustomDataFile data)
        {
            if (_managedCustomIconService == null || data == null)
            {
                yield break;
            }

            var gameIdText = playniteGameId.ToString("D");
            var fileStems = AchievementIconCachePathBuilder.BuildFileStems(
                EnumeratePortableIconApiNames(data.ToPortable()));

            foreach (var retainedPath in EnumerateManagedCustomIconPaths(
                gameIdText,
                data.AchievementUnlockedIconOverrides,
                fileStems,
                AchievementIconVariant.Unlocked))
            {
                yield return retainedPath;
            }

            foreach (var retainedPath in EnumerateManagedCustomIconPaths(
                gameIdText,
                data.AchievementLockedIconOverrides,
                fileStems,
                AchievementIconVariant.Locked))
            {
                yield return retainedPath;
            }

            foreach (var retainedPath in EnumerateManagedCustomAchievementIconPaths(
                gameIdText,
                data.CustomAchievements,
                fileStems))
            {
                yield return retainedPath;
            }

            var categoryFileStems = AchievementIconCachePathBuilder.BuildCategoryFileStems(
                EnumeratePortableCategoryLabels(data.ToPortable()));
            foreach (var retainedPath in EnumerateManagedCategoryImagePaths(
                gameIdText,
                data.AchievementCategoryImageOverrides,
                categoryFileStems))
            {
                yield return retainedPath;
            }
        }

        private static IEnumerable<string> EnumerateIconOverrideValues(IReadOnlyDictionary<string, string> overrides)
        {
            return overrides == null
                ? Enumerable.Empty<string>()
                : overrides.Values.Where(value => !string.IsNullOrWhiteSpace(value));
        }

        private IEnumerable<string> EnumerateManagedCustomIconPaths(
            string gameIdText,
            IReadOnlyDictionary<string, string> overrides,
            IReadOnlyDictionary<string, string> fileStems,
            AchievementIconVariant variant)
        {
            if (overrides == null || overrides.Count == 0)
            {
                yield break;
            }

            foreach (var pair in overrides)
            {
                var apiName = NormalizeText(pair.Key);
                var value = NormalizeText(pair.Value);
                if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (_managedCustomIconService.IsManagedCustomIconPath(value, gameIdText))
                {
                    yield return value;
                    continue;
                }

                if (!IsHttpUrl(value))
                {
                    continue;
                }

                if (!fileStems.TryGetValue(apiName, out var fileStem) || string.IsNullOrWhiteSpace(fileStem))
                {
                    continue;
                }

                yield return _managedCustomIconService.GetAchievementCustomIconPath(
                    gameIdText,
                    fileStem,
                    variant);
            }
        }

        private static IEnumerable<string> EnumeratePortableIconApiNames(GameCustomDataPortableFile portable)
        {
            return (portable?.AchievementUnlockedIconOverrides?.Keys ?? Enumerable.Empty<string>())
                .Concat(portable?.AchievementLockedIconOverrides?.Keys ?? Enumerable.Empty<string>())
                .Concat(EnumerateCustomAchievementIconApiNames(portable?.CustomAchievements))
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private IEnumerable<string> EnumerateManagedCustomAchievementIconPaths(
            string gameIdText,
            IReadOnlyList<CustomAchievementDefinition> customAchievements,
            IReadOnlyDictionary<string, string> fileStems)
        {
            if (customAchievements == null || customAchievements.Count == 0)
            {
                yield break;
            }

            foreach (var definition in customAchievements)
            {
                var apiName = CustomAchievementProjectionService.BuildApiName(definition?.Id);
                if (string.IsNullOrWhiteSpace(apiName) ||
                    !fileStems.TryGetValue(apiName, out var fileStem) ||
                    string.IsNullOrWhiteSpace(fileStem))
                {
                    continue;
                }

                foreach (var retainedPath in EnumerateManagedCustomIconPath(
                    gameIdText,
                    fileStem,
                    definition.UnlockedIconPath,
                    AchievementIconVariant.Unlocked))
                {
                    yield return retainedPath;
                }

                foreach (var retainedPath in EnumerateManagedCustomIconPath(
                    gameIdText,
                    fileStem,
                    definition.LockedIconPath,
                    AchievementIconVariant.Locked))
                {
                    yield return retainedPath;
                }
            }
        }

        private IEnumerable<string> EnumerateManagedCustomIconPath(
            string gameIdText,
            string fileStem,
            string value,
            AchievementIconVariant variant)
        {
            var normalized = NormalizeText(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                yield break;
            }

            if (_managedCustomIconService.IsManagedCustomIconPath(normalized, gameIdText))
            {
                yield return normalized;
                yield break;
            }

            if (IsHttpUrl(normalized))
            {
                yield return _managedCustomIconService.GetAchievementCustomIconPath(gameIdText, fileStem, variant);
            }
        }

        private static IEnumerable<string> EnumerateCustomAchievementIconApiNames(
            IReadOnlyList<CustomAchievementDefinition> customAchievements)
        {
            if (customAchievements == null)
            {
                yield break;
            }

            foreach (var definition in customAchievements)
            {
                if (definition == null ||
                    (string.IsNullOrWhiteSpace(definition.UnlockedIconPath) &&
                     string.IsNullOrWhiteSpace(definition.LockedIconPath)))
                {
                    continue;
                }

                var apiName = CustomAchievementProjectionService.BuildApiName(definition.Id);
                if (!string.IsNullOrWhiteSpace(apiName))
                {
                    yield return apiName;
                }
            }
        }

        private IEnumerable<string> EnumerateManagedCategoryImagePaths(
            string gameIdText,
            IReadOnlyDictionary<string, CategoryImageOverrideData> overrides,
            IReadOnlyDictionary<string, string> fileStems)
        {
            if (overrides == null || overrides.Count == 0)
            {
                yield break;
            }

            foreach (var pair in overrides)
            {
                var category = AchievementCategoryHelper.NormalizeCategoryOrDefault(pair.Key);
                if (string.IsNullOrWhiteSpace(category) || pair.Value == null)
                {
                    continue;
                }

                foreach (var retainedPath in EnumerateManagedCategoryImagePaths(
                    gameIdText,
                    category,
                    pair.Value.Art,
                    fileStems))
                {
                    yield return retainedPath;
                }
            }
        }

        private IEnumerable<string> EnumerateManagedCategoryImagePaths(
            string gameIdText,
            string category,
            string value,
            IReadOnlyDictionary<string, string> fileStems)
        {
            var normalizedValue = NormalizeText(value);
            if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(normalizedValue))
            {
                yield break;
            }

            if (_managedCustomIconService.IsManagedCustomIconPath(normalizedValue, gameIdText))
            {
                yield return normalizedValue;
                yield break;
            }

            if (!IsHttpUrl(normalizedValue))
            {
                yield break;
            }

            if (!fileStems.TryGetValue(category, out var fileStem) || string.IsNullOrWhiteSpace(fileStem))
            {
                yield break;
            }

            yield return _managedCustomIconService.GetCategoryCustomImagePath(
                gameIdText,
                fileStem);
        }

        private static IEnumerable<string> EnumeratePortableCategoryLabels(GameCustomDataPortableFile portable)
        {
            return (portable?.AchievementCategoryImageOverrides?.Keys ?? Enumerable.Empty<string>())
                .Concat(portable?.AchievementCategoryOrder ?? Enumerable.Empty<string>())
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(AchievementCategoryHelper.NormalizeCategoryOrDefault)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private ManagedCustomIconService GetManagedCustomIconServiceOrThrow()
        {
            if (_managedCustomIconService != null)
            {
                return _managedCustomIconService;
            }

            throw new InvalidOperationException("Managed custom icon service is not available.");
        }

        internal static string BuildPackageImageEntryName(string fileStem, AchievementIconVariant variant)
        {
            var entryStem = PackageEntryStem(fileStem);
            var fileName = variant == AchievementIconVariant.Locked
                ? entryStem + ".locked.png"
                : entryStem + ".png";
            return PortablePackageImagesFolderName + "/" + fileName;
        }

        internal static string BuildPackageCategoryImageEntryName(string fileStem)
        {
            return PortablePackageImagesFolderName + "/category_" + PackageEntryStem(fileStem) + ".png";
        }

        // Readers refuse an entry name containing "..", which an API name or category label can
        // carry ("Act I...Begin"), so each run of dots becomes one underscore.
        private static string PackageEntryStem(string fileStem)
        {
            return System.Text.RegularExpressions.Regex.Replace(fileStem ?? string.Empty, @"\.{2,}", "_");
        }

        private static bool TryParseImageOnlyPackageEntry(
            string normalizedEntryName,
            out string apiName,
            out AchievementIconVariant variant)
        {
            apiName = null;
            variant = AchievementIconVariant.Unlocked;

            var normalized = NormalizeArchiveEntryName(normalizedEntryName);
            if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains(".."))
            {
                return false;
            }

            var fileName = normalized;
            var slashIndex = fileName.LastIndexOf('/');
            if (slashIndex >= 0)
            {
                fileName = fileName.Substring(slashIndex + 1);
            }

            if (string.IsNullOrWhiteSpace(fileName) || !IsSupportedPackageImageExtension(Path.GetExtension(fileName)))
            {
                return false;
            }

            var stem = NormalizeText(Path.GetFileNameWithoutExtension(fileName));
            if (string.IsNullOrWhiteSpace(stem))
            {
                return false;
            }

            if (stem.EndsWith(".locked", StringComparison.OrdinalIgnoreCase))
            {
                stem = NormalizeText(stem.Substring(0, stem.Length - ".locked".Length));
                variant = AchievementIconVariant.Locked;
            }

            apiName = string.IsNullOrWhiteSpace(stem) ? null : stem;
            return !string.IsNullOrWhiteSpace(apiName);
        }

        private static void EnsurePortablePackageExtension(string path)
        {
            if (!IsPortablePackagePath(path))
            {
                throw new InvalidOperationException("Destination path must end with .pa.");
            }
        }

        private static void EnsureDestinationDirectory(string destinationPath)
        {
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private static bool IsPortablePackagePath(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   (path.EndsWith(PortableFileExtension, StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(PortablePackageFileExtension, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeArchiveEntryName(string value)
        {
            var normalized = NormalizeText(value)?.Replace('\\', '/').TrimStart('/');
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static string NormalizePackageImagePathOrThrow(string value)
        {
            var normalized = NormalizeArchiveEntryName(value);
            if (string.IsNullOrWhiteSpace(normalized) ||
                normalized.Contains("..") ||
                !normalized.StartsWith(PortablePackageImagesFolderName + "/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Invalid bundled icon path '{value}'.");
            }

            var fileName = normalized.Substring((PortablePackageImagesFolderName + "/").Length);
            if (string.IsNullOrWhiteSpace(fileName) ||
                fileName.Contains("/") ||
                fileName.Contains("\\"))
            {
                throw new InvalidOperationException($"Invalid bundled icon path '{value}'.");
            }

            return normalized;
        }

        private static bool IsSupportedPackageImageExtension(string extension)
        {
            return ImageFormats.IsSupportedExtension(extension);
        }

        /// <summary>
        /// Rejects a bundled image this machine could not render, so the failure lands on import
        /// rather than while a notification surface is drawing.
        /// </summary>
        private static void EnsureBundledImageDecodableOrThrow(string entryName)
        {
            if (ImageFormats.IsWebpExtension(ImageFormats.GetExtension(entryName)) &&
                !WebpCodecProbe.IsSupported)
            {
                throw new InvalidOperationException(
                    $"The bundled image '{entryName}' is a WebP, which this system has no decoder for. " +
                    "Install the WebP Image Extension from the Microsoft Store, then import again.");
            }

            if (ImageFormats.IsWebmExtension(ImageFormats.GetExtension(entryName)) &&
                !WebmCodecProbe.IsSupported)
            {
                throw new InvalidOperationException(
                    $"The bundled image '{entryName}' is a WebM, which this system has no decoder for. " +
                    "Install the VP9 Video Extensions from the Microsoft Store, then import again.");
            }
        }

        private static bool IsHttpUrl(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeText(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        /// <summary>
        /// Seeds the cache with the instance the repository just persisted, so a write does not
        /// pay a second SELECT and full-blob deserialize to read back what it wrote. A null
        /// <paramref name="persisted"/> means the data normalized to nothing and the row was
        /// deleted, which is the same outcome a failed read-back produced.
        /// </summary>
        private void SetCachedEntry(Guid playniteGameId, GameCustomDataFile persisted)
        {
            if (playniteGameId == Guid.Empty)
            {
                return;
            }

            lock (_cacheSync)
            {
                EnsureCacheCollections();
                if (persisted != null)
                {
                    _cacheByGameId[playniteGameId] = persisted.Clone();
                    _missingGameIds.Remove(playniteGameId);
                    return;
                }

                _cacheByGameId.Remove(playniteGameId);
                _missingGameIds.Add(playniteGameId);
            }
        }

        private void InvalidateCache()
        {
            lock (_cacheSync)
            {
                _cacheByGameId = null;
                _missingGameIds = null;
                _cacheHoldsEveryStoredRow = false;
            }
        }

        private void EnsureCacheCollections()
        {
            if (_cacheByGameId == null)
            {
                _cacheByGameId = new Dictionary<Guid, GameCustomDataFile>();
            }

            if (_missingGameIds == null)
            {
                _missingGameIds = new HashSet<Guid>();
            }
        }

        /// <summary>
        /// True once <see cref="EnsureCacheLoaded"/> has read every stored row, so a game id the
        /// cache does not hold definitively has no custom data.
        /// </summary>
        /// <remarks>
        /// Without this, a miss fell through to a repository read - one SQLite query per game -
        /// and games with no custom data are most of a library. The whole-library overview build
        /// resolves summary art per game, so its first run after startup issued a query for
        /// every uncustomized game: measured at 1528ms of a 1719ms build for 500 games, against
        /// ~122ms for the second build once the lazy miss set had filled in.
        ///
        /// Writes keep the cache complete (a save adds its row, a delete removes one), so only
        /// <see cref="InvalidateCache"/> clears the flag.
        /// </remarks>
        private bool _cacheHoldsEveryStoredRow;

        private HashSet<Guid> GetExcludedGameIds(
            ISet<Guid> fallbackIds,
            Func<GameCustomDataFile, bool> selector)
        {
            if (selector == null)
            {
                throw new ArgumentNullException(nameof(selector));
            }

            EnsureCacheLoaded();

            lock (_cacheSync)
            {
                var result = fallbackIds != null
                    ? new HashSet<Guid>(fallbackIds)
                    : new HashSet<Guid>();

                foreach (var pair in _cacheByGameId)
                {
                    if (selector(pair.Value))
                    {
                        result.Add(pair.Key);
                    }
                    else
                    {
                        result.Remove(pair.Key);
                    }
                }

                return result;
            }
        }

        /// <summary>
        /// Populates the in-memory cache from the repository when it has not been read yet.
        /// </summary>
        /// <remarks>
        /// The repository read runs outside the cache lock, because it opens the database. A
        /// second caller that wins the race re-checks before installing, so the work is repeated
        /// at worst once and the cache is never replaced after it exists.
        ///
        /// This used to warm by calling <see cref="LoadAll"/> and discarding the result, which
        /// deep-cloned every stored record twice - once into the cache, once into the copy that
        /// was thrown away.
        /// </remarks>
        private void EnsureCacheLoaded()
        {
            lock (_cacheSync)
            {
                if (_cacheByGameId != null && _missingGameIds != null)
                {
                    return;
                }
            }

            var rows = _repository.EnumerateAllNormalized().ToList();
            lock (_cacheSync)
            {
                if (_cacheByGameId != null && _missingGameIds != null)
                {
                    return;
                }

                _cacheByGameId = rows
                    .Where(data => data?.PlayniteGameId != Guid.Empty)
                    .ToDictionary(
                        data => data.PlayniteGameId,
                        data => data);
                _missingGameIds = new HashSet<Guid>();
                _cacheHoldsEveryStoredRow = true;
            }
        }

        /// <summary>
        /// Raises <see cref="CustomDataChanged"/> without writing, for changes that alter how a
        /// game's stored custom data resolves (such as an edited custom provider definition).
        /// </summary>
        public void NotifyChanged(
            Guid playniteGameId,
            bool affectsSummaryData = true,
            bool affectsOverrideMirror = true)
        {
            RaiseCustomDataChanged(playniteGameId, affectsSummaryData, affectsOverrideMirror);
        }

        /// <summary>
        /// The plugin frames on the current stack, nearest first, so a burst of writes says which
        /// path produced it. Diagnostic only: walking the stack is far too expensive to do on a
        /// hot path, so it is gated on tracing and bounded to a handful of frames.
        /// </summary>
        private static string DescribeSaveCaller()
        {
            if (!PerfScope.PerfTracingEnabled)
            {
                return string.Empty;
            }

            try
            {
                var frames = new System.Diagnostics.StackTrace(2, false).GetFrames();
                if (frames == null)
                {
                    return string.Empty;
                }

                var names = new List<string>();
                foreach (var frame in frames)
                {
                    var method = frame?.GetMethod();
                    var type = method?.DeclaringType;
                    if (type == null || type.Namespace?.StartsWith("PlayniteAchievements", StringComparison.Ordinal) != true)
                    {
                        continue;
                    }

                    // The store's own frames say nothing about who asked.
                    if (type == typeof(GameCustomDataStore))
                    {
                        continue;
                    }

                    names.Add(type.Name + "." + method.Name);
                    if (names.Count >= 4)
                    {
                        break;
                    }
                }

                return names.Count == 0 ? string.Empty : "via=" + string.Join("<-", names);
            }
            catch
            {
                return string.Empty;
            }
        }

        private void RaiseCustomDataChanged(
            Guid playniteGameId,
            bool affectsSummaryData = true,
            bool affectsOverrideMirror = true,
            bool affectsTagTotals = true)
        {
            if (playniteGameId == Guid.Empty)
            {
                return;
            }

            CustomDataChanged?.Invoke(
                this,
                new GameCustomDataChangedEventArgs(
                    playniteGameId,
                    affectsSummaryData,
                    affectsOverrideMirror,
                    affectsTagTotals));
        }

        /// <summary>
        /// The tags the user put on the game's achievements, in one comparable string: each tag
        /// edit as its ApiName and stored value, the custom achievements' tags after them.
        /// </summary>
        internal static string TagEditSignature(GameCustomDataFile data)
        {
            var parts = new List<string>();
            foreach (var pair in data?.AchievementOverrides ?? new Dictionary<string, AchievementOverride>())
            {
                if (AchievementTallyBuilder.HasTagEdit(pair.Value))
                {
                    parts.Add(pair.Key + "=" + (pair.Value.ClearTags ? string.Empty : pair.Value.Tags));
                }
            }

            parts.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var custom in data?.CustomAchievements ?? new List<CustomAchievementDefinition>())
            {
                parts.Add("custom:" + custom?.Id + "=" + custom?.Tags);
            }

            return string.Join("\u001f", parts);
        }

    }
}
