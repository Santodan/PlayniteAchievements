using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Tagging;

namespace PlayniteAchievements.Services.Tagging
{
    /// <summary>
    /// The sync state of one managed Playnite field (tags or features): which kinds it carries,
    /// their configs, the resolved Playnite ids, and the bookkeeping that keeps a game's id list
    /// and the Playnite collection in step with the configs. Decides nothing about which kinds a
    /// game gets; <see cref="TagSyncService"/> evaluates games and hands the result here.
    /// </summary>
    internal sealed class LibraryFieldSyncTarget
    {
        private readonly ILogger _logger;
        // Funcs rather than captured values: CancelEdit replaces the persisted settings instance.
        private readonly Func<Dictionary<TagType, TagConfig>> _configs;
        private readonly Func<bool> _enabled;
        private readonly HashSet<TagType> _kinds;

        // Resolved ids by kind, so a sync pass does not look each one up again.
        private readonly Dictionary<TagType, Guid> _idCache = new Dictionary<TagType, Guid>();

        /// <param name="kinds">The kinds this field carries; null for every kind.</param>
        public LibraryFieldSyncTarget(
            IManagedLibraryField field,
            ILogger logger,
            Func<Dictionary<TagType, TagConfig>> configs,
            Func<bool> enabled,
            IReadOnlyCollection<TagType> kinds,
            string removingProgressKey)
        {
            Field = field ?? throw new ArgumentNullException(nameof(field));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configs = configs ?? throw new ArgumentNullException(nameof(configs));
            _enabled = enabled ?? throw new ArgumentNullException(nameof(enabled));
            _kinds = kinds == null ? null : new HashSet<TagType>(kinds);
            RemovingProgressKey = removingProgressKey ?? throw new ArgumentNullException(nameof(removingProgressKey));
        }

        public IManagedLibraryField Field { get; }

        public string Noun => Field.Noun;

        /// <summary>Resource key of the progress text shown while this field's items are removed.</summary>
        public string RemovingProgressKey { get; }

        public bool Enabled => _enabled();

        /// <summary>The configs by kind; null when the settings are not loaded.</summary>
        public Dictionary<TagType, TagConfig> Configs => _configs();

        public bool Manages(TagType kind) => _kinds == null || _kinds.Contains(kind);

        /// <summary>
        /// The ids the configs hold right now, taken before a sync resolves new ones so items of
        /// disabled kinds, or ones replaced after being deleted in Playnite, can still be
        /// stripped from games and deleted.
        /// </summary>
        public HashSet<Guid> CaptureConfiguredIds()
        {
            var ids = new HashSet<Guid>();
            var configs = Configs;
            if (configs == null)
            {
                return ids;
            }

            foreach (var config in configs.Values)
            {
                if (config?.TagId is Guid id && id != Guid.Empty)
                {
                    ids.Add(id);
                }
            }

            return ids;
        }

        /// <summary>
        /// Resolves (creating when needed) the Playnite item for every enabled kind, and renames
        /// a remembered item in place when its config's name was edited, so games keep the same
        /// id. Disabled kinds are skipped so their items are never created, but they keep any id
        /// they already have: the removal pass needs it to strip the item from games before it
        /// is deleted.
        /// </summary>
        public void EnsureConfiguredIds()
        {
            var configs = Configs;
            if (configs == null)
            {
                return;
            }

            foreach (var kvp in configs)
            {
                var config = kvp.Value;
                if (config?.IsEnabled != true || !Manages(kvp.Key))
                {
                    continue;
                }

                var id = ResolveConfiguredId(kvp.Key, config);
                if (!id.HasValue)
                {
                    continue;
                }

                try
                {
                    if (Field.Rename(id.Value, config.ResolvedName))
                    {
                        _logger.Info($"Renamed {Noun} {id.Value} to '{config.ResolvedName}' to match its configured name.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Failed to rename {Noun} {id.Value} to '{config.ResolvedName}'.");
                }
            }
        }

        /// <summary>The ids of the enabled kinds: the set that should survive a sync.</summary>
        public HashSet<Guid> GetEnabledIds()
        {
            var ids = new HashSet<Guid>();
            var configs = Configs;
            if (configs == null)
            {
                return ids;
            }

            foreach (var kvp in configs)
            {
                var config = kvp.Value;
                if (config?.IsEnabled != true || !Manages(kvp.Key))
                {
                    continue;
                }

                if (_idCache.TryGetValue(kvp.Key, out var cachedId))
                {
                    ids.Add(cachedId);
                }
                else if (config.TagId is Guid id && id != Guid.Empty)
                {
                    ids.Add(id);
                }
            }

            return ids;
        }

        /// <summary>
        /// Every id this field tracks: the resolved ones plus every id a config remembers, which
        /// covers kinds the user disabled whose items are not cleaned up yet.
        /// </summary>
        public HashSet<Guid> GetAllTrackedIds()
        {
            var ids = new HashSet<Guid>(_idCache.Values);
            foreach (var id in CaptureConfiguredIds())
            {
                ids.Add(id);
            }

            return ids;
        }

        /// <summary>
        /// The Playnite id for a kind: the cached one, then the config's remembered one, then the
        /// item called by the config's name, created when there is none. Writes the id back to
        /// the config.
        /// </summary>
        public Guid? ResolveConfiguredId(TagType kind, TagConfig config)
        {
            if (config == null || string.IsNullOrWhiteSpace(config.ResolvedName))
            {
                return null;
            }

            if (_idCache.TryGetValue(kind, out var cachedId) && Field.Exists(cachedId))
            {
                return cachedId;
            }

            if (config.TagId is Guid rememberedId && Field.Exists(rememberedId))
            {
                _idCache[kind] = rememberedId;
                return rememberedId;
            }

            var id = Field.FindByName(config.ResolvedName) ?? Field.Create(config.ResolvedName);
            if (id.HasValue)
            {
                _idCache[kind] = id.Value;
                config.TagId = id.Value;
            }

            return id;
        }

        /// <summary>
        /// Brings a game's id list for this field to the item of each applicable enabled kind:
        /// strips the ids in <paramref name="idsToRemove"/> (every tracked id when null) that are
        /// not wanted, then adds the wanted ones that are missing. Ids already in place are left
        /// where they are, so a game whose list is already right reports no change. Returns
        /// whether the list moved; the caller writes the game.
        /// </summary>
        public bool SyncGame(Game game, IReadOnlyCollection<TagType> applicable, HashSet<Guid> idsToRemove = null)
        {
            if (game == null)
            {
                return false;
            }

            var desired = new List<Guid>();
            var configs = Configs;
            if (configs != null && applicable != null)
            {
                foreach (var kind in applicable)
                {
                    if (!Manages(kind) || !configs.TryGetValue(kind, out var config) || config?.IsEnabled != true)
                    {
                        continue;
                    }

                    var id = ResolveConfiguredId(kind, config);
                    if (id.HasValue && !desired.Contains(id.Value))
                    {
                        desired.Add(id.Value);
                    }
                }
            }

            var changed = false;
            var ids = Field.GetIds(game);
            if (ids != null && ids.Count > 0)
            {
                var toRemove = idsToRemove ?? GetAllTrackedIds();
                changed = ids.RemoveAll(id => toRemove.Contains(id) && !desired.Contains(id)) > 0;
            }

            foreach (var id in desired)
            {
                changed |= AddId(game, id);
            }

            return changed;
        }

        /// <summary>
        /// Adds or removes one kind's item, leaving every other id on the game alone. Returns
        /// whether the game's id list moved.
        /// </summary>
        public bool SetManaged(Game game, TagType kind, bool shouldApply)
        {
            var configs = Configs;
            if (game == null || !Manages(kind) || configs == null ||
                !configs.TryGetValue(kind, out var config) || config?.IsEnabled != true)
            {
                return false;
            }

            var id = ResolveConfiguredId(kind, config);
            if (!id.HasValue)
            {
                return false;
            }

            if (shouldApply)
            {
                return AddId(game, id.Value);
            }

            var ids = Field.GetIds(game);
            return ids != null && ids.Remove(id.Value);
        }

        /// <summary>
        /// Strips <paramref name="idsToRemove"/> (every tracked id when null) from a game's id
        /// list. Returns whether anything was removed.
        /// </summary>
        public bool RemoveManaged(Game game, HashSet<Guid> idsToRemove = null)
        {
            var ids = Field.GetIds(game);
            if (ids == null || ids.Count == 0)
            {
                return false;
            }

            var toRemove = idsToRemove ?? GetAllTrackedIds();
            var originalCount = ids.Count;
            ids.RemoveAll(id => toRemove.Contains(id));
            return ids.Count != originalCount;
        }

        /// <summary>
        /// Deletes the items that were tracked before a sync but are no longer current, because
        /// the user disabled the kind. Runs after the games are synced so no
        /// game still references a deleted item.
        /// </summary>
        public void CleanupOrphans(HashSet<Guid> oldIds, HashSet<Guid> currentIds)
        {
            var orphans = new HashSet<Guid>(oldIds.Where(id => !currentIds.Contains(id)));
            if (orphans.Count == 0)
            {
                return;
            }

            var deletedCount = 0;
            foreach (var id in orphans)
            {
                try
                {
                    if (Field.Remove(id))
                    {
                        deletedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Failed to delete orphan {Noun} with ID {id}");
                }
            }

            // The deleted ids leave the configs too, so a re-enabled kind creates a fresh item
            // instead of carrying a dangling id in the saved settings.
            _idCache.Clear();
            ForgetDeletedIds(orphans);

            if (deletedCount > 0)
            {
                _logger.Info($"Cleaned up {deletedCount} orphan {Noun}s");
            }
        }

        /// <summary>
        /// Deletes every item this field tracks, by id only: matching by name would also delete a
        /// user's own item that happens to share a configured name. Returns how many were
        /// deleted.
        /// </summary>
        public int DeleteAllTracked()
        {
            var deletedIds = new HashSet<Guid>();
            var deletedCount = 0;
            foreach (var id in GetAllTrackedIds())
            {
                try
                {
                    if (Field.Remove(id))
                    {
                        deletedCount++;
                    }

                    deletedIds.Add(id);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Failed to delete {Noun} with ID {id}");
                }
            }

            ForgetDeletedIds(deletedIds);
            _idCache.Clear();
            return deletedCount;
        }

        public void ForgetDeletedIds(HashSet<Guid> deletedIds)
        {
            var configs = Configs;
            if (configs == null)
            {
                return;
            }

            foreach (var config in configs.Values)
            {
                if (config?.TagId is Guid id && deletedIds.Contains(id))
                {
                    config.TagId = null;
                }
            }
        }

        /// <summary>Renames the config's item in place. Returns whether the item changed.</summary>
        public bool RenameItem(TagConfig config, string oldName, string newName)
        {
            try
            {
                if (config?.TagId is Guid id && Field.Rename(id, newName))
                {
                    _logger.Info($"Renamed {Noun} '{oldName}' to '{newName}' to match the current language.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to rename {Noun} '{oldName}' to '{newName}'.");
            }

            return false;
        }

        private bool AddId(Game game, Guid id)
        {
            var ids = Field.GetIds(game);
            if (ids == null)
            {
                Field.SetIds(game, new List<Guid> { id });
                return true;
            }

            if (ids.Contains(id))
            {
                return false;
            }

            ids.Add(id);
            return true;
        }
    }
}
