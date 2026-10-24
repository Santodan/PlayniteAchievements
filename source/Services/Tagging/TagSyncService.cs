using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.Tagging;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Logging;
using PlayniteAchievements.Services.Rules;

namespace PlayniteAchievements.Services.Tagging
{
    /// <summary>
    /// Service for synchronizing achievement-based tags to Playnite games.
    /// Allows games to be tagged based on their achievement status for filtering and organization.
    /// </summary>
    public class TagSyncService
    {
        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        // Holds the settings wrapper rather than its PersistedSettings: CancelEdit
        // replaces the Persisted instance, so a captured instance would keep serving
        // the values the user reverted.
        private readonly PlayniteAchievementsSettings _settingsHost;
        private readonly Lazy<DefaultTagNameCatalog> _defaultNameCatalog;
        private TaggingSettings _subscribedTaggingSettings;

        // Cache of tag IDs by tag type to avoid repeated database lookups
        private readonly Dictionary<TagType, Guid> _tagIdCache = new Dictionary<TagType, Guid>();

        public TagSyncService(
            IPlayniteAPI api,
            ILogger logger,
            PlayniteAchievementsSettings settings,
            string localizationDirectory = null)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsHost = settings ?? throw new ArgumentNullException(nameof(settings));
            _defaultNameCatalog = new Lazy<DefaultTagNameCatalog>(
                () => new DefaultTagNameCatalog(localizationDirectory, _logger));
        }

        private PersistedSettings Settings => _settingsHost.Persisted;

        private TaggingSettings Tagging => Settings?.TaggingSettings;

        private bool TaggingEnabled => Tagging?.EnableTagging == true;

        /// <summary>
        /// Ensures tagging settings are initialized with defaults and subscribes
        /// to tagging property changes. Idempotent, so it can be re-run when the
        /// persisted settings instance is replaced.
        /// </summary>
        public void InitializeAndSubscribeTaggingSettings()
        {
            EnsureTaggingSettingsInitialized();
            SubscribeToTaggingSettingsChanges(Settings?.TaggingSettings);
        }

        /// <summary>
        /// Handles persisted settings changes that affect tagging wiring.
        /// </summary>
        public void HandlePersistedSettingsPropertyChanged(PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (e.PropertyName == nameof(PersistedSettings.TaggingSettings))
            {
                _logger.Debug("TaggingSettings property changed; reinitializing tagging bindings.");
                InitializeAndSubscribeTaggingSettings();
            }
        }

        /// <summary>
        /// Removes internal settings subscriptions.
        /// </summary>
        public void DetachTaggingSettingsSubscription()
        {
            if (_subscribedTaggingSettings != null)
            {
                _subscribedTaggingSettings.PropertyChanged -= TaggingSettings_PropertyChanged;
                _subscribedTaggingSettings = null;
            }
        }

        private void EnsureTaggingSettingsInitialized()
        {
            var settings = Settings;
            if (settings == null)
            {
                return;
            }

            if (settings.TaggingSettings == null)
            {
                settings.TaggingSettings = new TaggingSettings();
            }

            settings.TaggingSettings.InitializeDefaults(GetLocalizedDefaultName);
        }

        /// <summary>
        /// Resolves the default display name for a tag type in the current Playnite language.
        /// </summary>
        private static string GetLocalizedDefaultName(TagType tagType)
        {
            if (DefaultTagNameCatalog.StatusResourceKeys.TryGetValue(tagType, out var statusResourceKey))
            {
                return FormatTagName(statusResourceKey);
            }

            var areaKeys = DefaultTagNameCatalog.GetAreaResourceKeys(tagType);
            return areaKeys != null
                ? DefaultTagNameCatalog.ComposeAreaName(areaKeys.Select(ResourceProvider.GetString).ToList())
                : TaggingSettings.GetDefaultDisplayName(tagType);
        }

        private static string FormatTagName(string statusResourceKey)
        {
            return string.Format(
                ResourceProvider.GetString("LOCPlayAch_Tag_PrefixFormat"),
                ResourceProvider.GetString(statusResourceKey));
        }

        /// <summary>
        /// Updates tag display names that still match a known default (from any shipped
        /// language) to the current language's default, and renames the corresponding
        /// Playnite tags in place. Customized names are left untouched. Requires the
        /// Playnite database to be open, so call from OnApplicationStarted or later.
        /// </summary>
        /// <returns>True when any display name changed and settings should be persisted.</returns>
        public bool RelocalizeDefaultTagNames()
        {
            var tagConfigs = Tagging?.TagConfigs;
            if (tagConfigs == null)
            {
                return false;
            }

            var changed = false;
            foreach (var kvp in tagConfigs)
            {
                var config = kvp.Value;
                if (config == null)
                {
                    continue;
                }

                var newName = _defaultNameCatalog.Value.GetRelocalizedName(
                    kvp.Key,
                    config.DisplayName,
                    GetLocalizedDefaultName(kvp.Key));
                if (newName == null)
                {
                    continue;
                }

                var oldName = config.DisplayName;
                config.DisplayName = newName;
                changed = true;
                RenamePlayniteTag(config, oldName, newName);
            }

            return changed;
        }

        private void RenamePlayniteTag(TagConfig config, string oldName, string newName)
        {
            try
            {
                if (config.TagId is Guid tagId && tagId != Guid.Empty)
                {
                    var tag = _api.Database.Tags.Get(tagId);
                    if (tag != null && !string.Equals(tag.Name, newName, StringComparison.Ordinal))
                    {
                        tag.Name = newName;
                        _api.Database.Tags.Update(tag);
                        _logger.Info($"Renamed tag '{oldName}' to '{newName}' to match the current language.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to rename tag '{oldName}' to '{newName}'.");
            }
        }

        private void SubscribeToTaggingSettingsChanges(TaggingSettings taggingSettings)
        {
            if (_subscribedTaggingSettings != null)
            {
                _subscribedTaggingSettings.PropertyChanged -= TaggingSettings_PropertyChanged;
            }

            _subscribedTaggingSettings = taggingSettings;

            if (_subscribedTaggingSettings != null)
            {
                _subscribedTaggingSettings.PropertyChanged += TaggingSettings_PropertyChanged;
            }
        }

        private void TaggingSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            try
            {
                var taggingSettings = Tagging;
                if (taggingSettings == null)
                {
                    return;
                }

                if (e.PropertyName == nameof(TaggingSettings.EnableTagging))
                {
                    if (taggingSettings.EnableTagging)
                    {
                        SyncAllTags();
                    }
                    else
                    {
                        RemoveAllTags();
                    }
                }
                else if (e.PropertyName == nameof(TaggingSettings.SetCompletionStatus) ||
                         e.PropertyName == nameof(TaggingSettings.CompletionStatusId))
                {
                    SyncCompletionStatus();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error handling tagging settings change");
            }
        }

        /// <summary>
        /// Synchronizes tags for all games in the library based on their achievement status.
        /// Shows a progress dialog during the operation.
        /// Also cleans up orphan PA tags (tags that no longer match current config names).
        /// </summary>
        public void SyncAllTags()
        {
            SyncAllTags(silent: false);
        }

        /// <summary>
        /// Synchronizes tags for all games in the library based on their achievement status.
        /// Also cleans up orphan PA tags (tags that no longer match current config names).
        /// </summary>
        /// <param name="silent">If true, runs without showing a progress dialog.</param>
        public void SyncAllTags(bool silent)
        {
            if (!TaggingEnabled)
            {
                _logger.Info("Tag sync skipped: tagging is disabled");
                return;
            }

            if (silent)
            {
                ExecuteSyncAllTags(null);
            }
            else
            {
                var progressOptions = new GlobalProgressOptions(
                    ResourceProvider.GetString("LOCPlayAch_Tagging_SyncingProgress"))
                {
                    Cancelable = false,
                    IsIndeterminate = false
                };

                _api.Dialogs.ActivateGlobalProgress((progress) =>
                {
                    ExecuteSyncAllTags(progress);
                }, progressOptions);
            }
        }

        private void ExecuteSyncAllTags(GlobalProgressActionArgs progress)
        {
            var tagConfigs = Tagging?.TagConfigs;

            // Capture old tag IDs BEFORE we update them, so we can remove old tags from games
            var oldTagIds = new HashSet<Guid>();
            if (tagConfigs != null)
            {
                foreach (var config in tagConfigs.Values)
                {
                    if (config?.TagId.HasValue == true)
                    {
                        oldTagIds.Add(config.TagId.Value);
                    }
                }
            }

            // Step 1: Ensure the enabled tags exist in the database. This updates
            // TagConfig.TagId to the new tag IDs; disabled kinds are left alone.
            EnsureConfiguredTagIds(tagConfigs);

            // Combine old and new tag IDs for removal (handles renames, and strips the
            // tags of kinds the user has since disabled).
            var allManagedTagIds = new HashSet<Guid>(oldTagIds);
            foreach (var tagId in _tagIdCache.Values)
            {
                allManagedTagIds.Add(tagId);
            }

            var games = _api.Database.Games.ToList();
            if (progress != null)
            {
                progress.ProgressMaxValue = games.Count;
                progress.CurrentProgressValue = 0;
            }

            var updatedCount = 0;

            // Pre-compute completion status target if needed
            Guid? targetCompletionStatusId = null;
            var setCompletionStatus = Tagging?.SetCompletionStatus ?? false;
            if (setCompletionStatus)
            {
                targetCompletionStatusId = GetCompletionStatusId();
            }

            // Buffer database writes so all game updates in this pass raise a single
            // coalesced ItemUpdated event instead of one event per game.
            using (_api.Database.BufferedUpdate())
            {
                foreach (var game in games)
                {
                    if (game == null) continue;

                    if (progress != null)
                    {
                        progress.Text = $"{ResourceProvider.GetString("LOCPlayAch_Tagging_SyncingProgress")}: {game.Name}";
                        progress.CurrentProgressValue++;
                    }

                    try
                    {
                        var evaluation = EvaluateGame(game);
                        if (SyncGameTags(game, tagConfigs, evaluation.TagTypes, allManagedTagIds))
                        {
                            updatedCount++;
                        }

                        // Also reconcile completion status in the same pass. Keyed off the
                        // game's own completion, not the Completed tag, so disabling that
                        // tag does not disable completion status syncing.
                        if (ApplyCompletionStatus(game, evaluation.Completion, targetCompletionStatusId))
                        {
                            _api.Database.Games.Update(game);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"Failed to sync tags for game {game.Name}");
                    }
                }
            }

            // Step 2: Clean up orphan tags from the database AFTER games are synced.
            // This must come after syncing so we don't delete tags that games still
            // reference. Only the enabled kinds count as current, so a kind the user
            // disabled loses its Playnite tag here rather than lingering in the tag
            // list and filter panel.
            CleanupOrphanTags(oldTagIds, GetEnabledTagIds(tagConfigs));

            _logger.Info($"Tag sync complete: {updatedCount} games updated");
        }

        /// <summary>
        /// Synchronizes tags for specific games (e.g., after a refresh).
        /// Does not show a progress dialog - runs silently in background.
        /// </summary>
        public void SyncTagsForGames(List<Guid> gameIds)
        {
            if (!TaggingEnabled || gameIds == null || gameIds.Count == 0)
            {
                return;
            }

            try
            {
                var tagConfigs = Tagging?.TagConfigs;
                EnsureConfiguredTagIds(tagConfigs);
                var targetStatusId = (Tagging?.SetCompletionStatus ?? false)
                    ? GetCompletionStatusId()
                    : (Guid?)null;

                // Buffer database writes so all game updates in this batch raise a single
                // coalesced ItemUpdated event instead of one event per game.
                using (_api.Database.BufferedUpdate())
                {
                    foreach (var gameId in gameIds)
                    {
                        var game = _api.Database.Games.Get(gameId);
                        if (game == null) continue;

                        try
                        {
                            var evaluation = EvaluateGame(game);
                            SyncGameTags(game, tagConfigs, evaluation.TagTypes);

                            // Also reconcile completion status. Keyed off the game's own
                            // completion, not the Completed tag.
                            if (ApplyCompletionStatus(game, evaluation.Completion, targetStatusId))
                            {
                                _api.Database.Games.Update(game);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, $"Failed to sync tags for game {game.Name}");
                        }
                    }
                }

                // _logger.Debug($"Auto-synced tags for {gameIds.Count} games");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to sync tags for games");
            }
        }

        /// <summary>
        /// Resolves (creating when needed) the Playnite tag for every enabled kind.
        /// Disabled kinds are skipped so their tags are never created, but they keep any
        /// TagId they already have: the removal pass needs it to strip the tag from games
        /// before the tag itself is deleted.
        /// </summary>
        private void EnsureConfiguredTagIds(Dictionary<TagType, TagConfig> tagConfigs)
        {
            if (tagConfigs == null)
            {
                return;
            }

            foreach (var kvp in tagConfigs)
            {
                if (kvp.Value?.IsEnabled != true)
                {
                    continue;
                }

                ResolveConfiguredTagId(kvp.Key, kvp.Value);
            }
        }

        /// <summary>
        /// The tag IDs of the currently enabled kinds - the set that should survive a sync.
        /// </summary>
        private HashSet<Guid> GetEnabledTagIds(Dictionary<TagType, TagConfig> tagConfigs)
        {
            var tagIds = new HashSet<Guid>();
            if (tagConfigs == null)
            {
                return tagIds;
            }

            foreach (var kvp in tagConfigs)
            {
                var config = kvp.Value;
                if (config?.IsEnabled != true)
                {
                    continue;
                }

                if (_tagIdCache.TryGetValue(kvp.Key, out var cachedTagId))
                {
                    tagIds.Add(cachedTagId);
                }
                else if (config.TagId.HasValue && config.TagId.Value != Guid.Empty)
                {
                    tagIds.Add(config.TagId.Value);
                }
            }

            return tagIds;
        }

        private Guid? ResolveConfiguredTagId(TagType tagType, TagConfig config)
        {
            if (config == null || string.IsNullOrWhiteSpace(config.DisplayName))
            {
                return null;
            }

            if (_tagIdCache.TryGetValue(tagType, out var cachedTagId) &&
                _api.Database.Tags.Get(cachedTagId) != null)
            {
                return cachedTagId;
            }

            if (config.TagId.HasValue &&
                config.TagId.Value != Guid.Empty &&
                _api.Database.Tags.Get(config.TagId.Value) != null)
            {
                _tagIdCache[tagType] = config.TagId.Value;
                return config.TagId.Value;
            }

            var tagId = GetOrCreateTag(config.DisplayName);
            if (tagId.HasValue)
            {
                _tagIdCache[tagType] = tagId.Value;
                config.TagId = tagId.Value;
            }

            return tagId;
        }

        /// <summary>
        /// Removes orphan tags from the database: tags that were previously used but are
        /// no longer current, either because the kind was renamed or because the user
        /// disabled it.
        /// </summary>
        /// <param name="oldTagIds">Tag IDs that were captured before the sync (for rename detection).</param>
        /// <param name="currentManagedTagIds">Tag IDs of the currently enabled kinds.</param>
        private void CleanupOrphanTags(HashSet<Guid> oldTagIds, HashSet<Guid> currentManagedTagIds)
        {
            // Tags to delete: old tags that are no longer in current managed set
            var tagsToDelete = new HashSet<Guid>();

            foreach (var oldId in oldTagIds)
            {
                if (!currentManagedTagIds.Contains(oldId))
                {
                    tagsToDelete.Add(oldId);
                }
            }

            if (!tagsToDelete.Any())
            {
                return;
            }

            var deletedCount = 0;
            foreach (var tagId in tagsToDelete)
            {
                try
                {
                    var tag = _api.Database.Tags.Get(tagId);
                    if (tag != null)
                    {
                        _logger.Debug($"Deleting orphan tag: {tag.Name}");
                        _api.Database.Tags.Remove(tag);
                        deletedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Failed to delete orphan tag with ID {tagId}");
                }
            }

            // Clear the cache since tags may have been deleted
            _tagIdCache.Clear();

            // Drop the deleted IDs from the configs so a re-enabled kind creates a fresh
            // tag instead of carrying a dangling ID in the saved settings.
            ForgetDeletedTagIds(tagsToDelete);

            if (deletedCount > 0)
            {
                _logger.Info($"Cleaned up {deletedCount} orphan tags");
            }
        }

        private void ForgetDeletedTagIds(HashSet<Guid> deletedTagIds)
        {
            var tagConfigs = Tagging?.TagConfigs;
            if (tagConfigs == null)
            {
                return;
            }

            foreach (var config in tagConfigs.Values)
            {
                if (config?.TagId.HasValue == true && deletedTagIds.Contains(config.TagId.Value))
                {
                    config.TagId = null;
                }
            }
        }

        /// <summary>
        /// Gets the completion status ID to use for completed games.
        /// </summary>
        private Guid? GetCompletionStatusId()
        {
            var completionStatusId = Tagging?.CompletionStatusId;

            if (completionStatusId.HasValue && completionStatusId.Value != Guid.Empty)
            {
                return completionStatusId;
            }

            // Find the default "Completed" status
            var completedStatus = _api.Database.CompletionStatuses
                .FirstOrDefault(s => s.Name?.Equals("Completed", StringComparison.OrdinalIgnoreCase) ?? false);

            return completedStatus?.Id;
        }

        /// <summary>
        /// Syncs tags for a single game based on its achievement status.
        /// </summary>
        /// <param name="game">The game to sync tags for.</param>
        /// <param name="tagIdsToRemove">Optional set of tag IDs to remove. If null, uses current tracked IDs.</param>
        /// <returns>True if any tags were changed.</returns>
        public bool SyncGameTags(Game game, HashSet<Guid> tagIdsToRemove = null)
        {
            if (!TaggingEnabled)
            {
                return false;
            }

            var tagConfigs = Tagging?.TagConfigs;
            EnsureConfiguredTagIds(tagConfigs);
            var tagTypes = DetermineTagTypes(game);
            return SyncGameTags(game, tagConfigs, tagTypes, tagIdsToRemove);
        }

        /// <summary>
        /// Reconciles only the customization kinds (the Customized pair and the area pairs), for a
        /// change that cannot have moved any other tag.
        /// </summary>
        /// <remarks>
        /// A full sync evaluates every tag, and that means loading the game's hydrated
        /// achievement data -- the whole cost of a sync. Renaming an achievement, writing a note
        /// or overriding an icon moves these tags and nothing else, so the load is skipped and
        /// the other managed tags are left exactly as they are.
        /// </remarks>
        public void SyncCustomizationTagsForGames(List<Guid> gameIds)
        {
            if (!TaggingEnabled || gameIds == null || gameIds.Count == 0)
            {
                return;
            }

            try
            {
                var tagConfigs = Tagging?.TagConfigs;
                EnsureConfiguredTagIds(tagConfigs);

                using (_api.Database.BufferedUpdate())
                {
                    foreach (var gameId in gameIds)
                    {
                        var game = _api.Database.Games.Get(gameId);
                        if (game == null)
                        {
                            continue;
                        }

                        try
                        {
                            // Platform and Metadata only move on a refresh, but an edit can turn a
                            // layered area's None on or off, which needs the game's data.
                            var applies = new HashSet<TagType>(DetermineCustomizationTagTypes(
                                gameId,
                                () => PlayniteAchievementsPlugin.Instance?.AchievementDataService?.GetGameAchievementData(gameId)));
                            var changed = false;
                            foreach (var tagType in CustomizationTagTypes)
                            {
                                changed |= SetManagedTag(game, tagConfigs, tagType, applies.Contains(tagType));
                            }

                            if (changed)
                            {
                                _api.Database.Games.Update(game);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, $"Failed to sync the customization tag for game {game.Name}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to sync customization tags for games");
            }
        }

        /// <summary>
        /// Every kind that tracks customization: the Customized pair and each area's kinds.
        /// </summary>
        private static readonly IReadOnlyList<TagType> CustomizationTagTypes =
            new[] { TagType.Customized, TagType.NotCustomized }
                .Concat(CustomizationTagAreas.All.SelectMany(area => area.TagTypes))
                .ToList();

        /// <summary>
        /// The customization kinds that apply to a game: one side of the Customized pair and the
        /// applicable kinds of each area. Areas are only classified when one of their kinds is
        /// enabled, and the game's data is only loaded when a Platform, Metadata or None kind is,
        /// so the default setup pays nothing for them.
        /// </summary>
        private IEnumerable<TagType> DetermineCustomizationTagTypes(Guid gameId, Func<GameAchievementData> loadData)
        {
            var types = new List<TagType>
            {
                GameCustomDataLookup.HasVisibleCustomization(gameId, Settings)
                    ? TagType.Customized
                    : TagType.NotCustomized
            };

            if (!AnyAreaTagEnabled(_ => true))
            {
                return types;
            }

            var layers = default(GameDataLayers);
            if (AnyAreaTagEnabled(layer => layer == TagLayer.Platform || layer == TagLayer.Metadata || layer == TagLayer.None))
            {
                var data = loadData?.Invoke();
                layers = new GameDataLayers
                {
                    Platform = data?.PlatformFields,
                    Metadata = data?.MetadataFields,
                    Present = Metadata.DataLayers.Present(data?.Achievements)
                };
            }

            types.AddRange(CustomizationTagAreas.TagsFor(GameCustomDataLookup.GetCustomizationAreas(gameId, Settings), layers));
            return types;
        }

        private bool AnyAreaTagEnabled(Func<TagLayer, bool> layerFilter)
        {
            var tagConfigs = Tagging?.TagConfigs;
            return tagConfigs != null &&
                   CustomizationTagAreas.All
                       .SelectMany(area => area.Tags)
                       .Where(pair => layerFilter(pair.Key))
                       .Any(pair => tagConfigs.TryGetValue(pair.Value, out var config) && config?.IsEnabled == true);
        }

        /// <summary>
        /// Adds or removes one managed tag, leaving every other tag on the game alone. Returns
        /// whether the game's tag list actually moved.
        /// </summary>
        private bool SetManagedTag(
            Game game,
            Dictionary<TagType, TagConfig> tagConfigs,
            TagType tagType,
            bool shouldApply)
        {
            if (tagConfigs == null || !tagConfigs.TryGetValue(tagType, out var config) || !config.IsEnabled)
            {
                return false;
            }

            var tagId = ResolveConfiguredTagId(tagType, config);
            if (!tagId.HasValue)
            {
                return false;
            }

            if (shouldApply)
            {
                if (game.TagIds == null)
                {
                    game.TagIds = new List<Guid> { tagId.Value };
                    return true;
                }

                if (game.TagIds.Contains(tagId.Value))
                {
                    return false;
                }

                game.TagIds.Add(tagId.Value);
                return true;
            }

            return game.TagIds != null && game.TagIds.Remove(tagId.Value);
        }

        /// <summary>
        /// Syncs tags for a single game based on its achievement status.
        /// </summary>
        private bool SyncGameTags(
            Game game,
            Dictionary<TagType, TagConfig> tagConfigs,
            IReadOnlyCollection<TagType> tagTypes,
            HashSet<Guid> tagIdsToRemove = null)
        {
            if (game == null) return false;

            var changed = false;

            // First, remove all managed tags from the game
            changed |= RemoveManagedTags(game, tagIdsToRemove);

            // Add each applicable tag
            foreach (var tagType in tagTypes)
            {
                if (tagConfigs != null && tagConfigs.TryGetValue(tagType, out var config) && config.IsEnabled)
                {
                    var tagId = ResolveConfiguredTagId(tagType, config);
                    if (tagId.HasValue)
                    {
                        if (game.TagIds == null)
                        {
                            game.TagIds = new List<Guid> { tagId.Value };
                            changed = true;
                        }
                        else if (!game.TagIds.Contains(tagId.Value))
                        {
                            game.TagIds.Add(tagId.Value);
                            changed = true;
                        }
                    }
                }
            }

            if (changed)
            {
                _api.Database.Games.Update(game);
            }

            return changed;
        }

        /// <summary>
        /// The applicable tag types for a game plus whether the game counts as completed.
        /// Both come out of one evaluation so a sync pass loads each game's achievement
        /// data once.
        /// </summary>
        private struct GameTagEvaluation
        {
            public List<TagType> TagTypes;
            public bool IsCompleted;

            /// <summary>
            /// True when the evaluation stopped before looking at completion, as it does for an
            /// excluded game, so <see cref="IsCompleted"/> says nothing about the game.
            /// </summary>
            public bool CompletionUnknown;

            /// <summary>Completion for the status rule: null when unknown.</summary>
            public bool? Completion => CompletionUnknown ? (bool?)null : IsCompleted;
        }

        /// <summary>
        /// Completion under a game's stored capstone set, for raw cached data whose IsCapstone
        /// flags are still the provider seed.
        /// </summary>
        private static bool IsCompletedUnderStoredCapstones(
            GameAchievementData data,
            CapstoneSet capstones)
        {
            var achievements = data?.Achievements;
            if (achievements == null || achievements.Count == 0)
            {
                return false;
            }

            if (achievements.All(a => a?.Unlocked == true))
            {
                return true;
            }

            var resolver = CapstoneResolver.Resolve(achievements, capstones.Assignments, true);
            return resolver.Count > 0 &&
                   resolver.EffectiveApiNames.All(apiName =>
                       achievements.Any(a =>
                           a != null &&
                           a.Unlocked &&
                           string.Equals((a.ApiName ?? string.Empty).Trim(), apiName, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// Determines all applicable tag types for a game.
        /// A game can have multiple tags (e.g., HasAchievements + InProgress).
        /// Excluded is exclusive and removes other tags.
        /// ExcludedFromSummaries can coexist with achievement status tags.
        /// </summary>
        private List<TagType> DetermineTagTypes(Game game)
        {
            return EvaluateGame(game).TagTypes;
        }

        /// <summary>
        /// Whether a game counts as completed for completion-status syncing: every
        /// achievement unlocked, or the manual capstone earned. Deliberately independent
        /// of the Completed tag, so disabling that tag does not stop completion status
        /// from being applied.
        /// </summary>
        private bool IsGameCompleted(Game game)
        {
            return EvaluateGame(game).IsCompleted;
        }

        private GameTagEvaluation EvaluateGame(Game game)
        {
            var types = new List<TagType>();
            var result = new GameTagEvaluation { TagTypes = types, IsCompleted = false };

            if (game == null)
            {
                types.Add(TagType.NoAchievements);
                return result;
            }

            var gameId = game.Id;
            // Loaded once, and only when something needs it: excluded games stop before the
            // status tags, but the layered customization tags may still read it.
            var achievementDataService = PlayniteAchievementsPlugin.Instance?.AchievementDataService;
            var loaded = new Lazy<GameAchievementData>(() => achievementDataService?.GetGameAchievementData(gameId));
            types.AddRange(DetermineCustomizationTagTypes(gameId, () => loaded.Value));

            // Full exclusion is exclusive - no other tags
            if (GameCustomDataLookup.IsExcludedFromRefreshes(gameId, Settings))
            {
                types.Add(TagType.Excluded);
                result.CompletionUnknown = true;
                return result;
            }

            // Check if excluded from summaries (can coexist with other tags)
            var excludedFromSummaries = GameCustomDataLookup.IsExcludedFromSummaries(gameId, Settings);
            if (excludedFromSummaries)
            {
                types.Add(TagType.ExcludedFromSummaries);
            }

            // Load achievement data through the central data service when available.
            var data = loaded.Value;

            // HasAchievements defaults to true on unscanned stubs (so bulk scans do not skip
            // them) and can survive as true with an empty list on rows written by the legacy
            // JSON import or old schema migrations. Tags must match what the UI reports, so
            // require actual achievements like SummaryCacheReader does, not just the flag.
            if (data == null || !data.HasAchievements ||
                data.Achievements == null || data.Achievements.Count == 0)
            {
                types.Add(TagType.NoAchievements);
                return result;
            }

            // If the game is excluded by user
            if (data.ExcludedByUser)
            {
                types.Add(TagType.Excluded);
                result.CompletionUnknown = true;
                return result;
            }

            // Platform achievements are every row but the projected custom ones; a manually
            // tracked list counts. Custom ones are the user's own, an auto capstone aside.
            if (data.Achievements.Any(a => a != null && !CustomAchievementProjectionService.IsCustomApiName(a.ApiName)))
            {
                types.Add(TagType.HasAchievements);
            }

            if (GameCustomDataLookup.GetCustomAchievements(gameId, Settings)?.Any(definition =>
                    definition != null && !definition.IsAutoCapstone && !string.IsNullOrWhiteSpace(definition.DisplayName)) == true)
            {
                types.Add(TagType.CustomAchievements);
            }

            // Completed means every achievement unlocked, or every capstone earned. Raw cached data
            // has not been through hydration, so its IsCapstone flags are the provider seed and the
            // stored set has to be applied here before the rule is applied.
            var capstones = GameCustomDataLookup.GetCapstoneSet(gameId, Settings);
            var isCompleted = capstones.Materialized
                ? IsCompletedUnderStoredCapstones(data, capstones)
                : data.IsCompleted;

            result.IsCompleted = isCompleted;

            // Add status tag based on progress
            if (isCompleted)
            {
                types.Add(TagType.Completed);
            }
            else
            {
                var unlockedCount = data.Achievements?.Count(a => a?.Unlocked == true) ?? 0;
                if (unlockedCount > 0)
                {
                    types.Add(TagType.InProgress);
                }
            }

            return result;
        }

        /// <summary>
        /// Removes all PA tags from all games and deletes them from the database.
        /// Called when tagging is disabled.
        /// </summary>
        public void RemoveAllTags()
        {
            var progressOptions = new GlobalProgressOptions(
                ResourceProvider.GetString("LOCPlayAch_Tagging_RemovingProgress"))
            {
                Cancelable = false,
                IsIndeterminate = false
            };

            _api.Dialogs.ActivateGlobalProgress((progress) =>
            {
                var games = _api.Database.Games.ToList();
                progress.ProgressMaxValue = games.Count;
                progress.CurrentProgressValue = 0;

                var removedCount = 0;

                // Buffer database writes so all game updates in this pass raise a single
                // coalesced ItemUpdated event instead of one event per game.
                using (_api.Database.BufferedUpdate())
                {
                    foreach (var game in games)
                    {
                        if (game == null) continue;

                        progress.Text = $"{ResourceProvider.GetString("LOCPlayAch_Tagging_RemovingProgress")}: {game.Name}";
                        progress.CurrentProgressValue++;

                        try
                        {
                            if (RemoveManagedTags(game))
                            {
                                _api.Database.Games.Update(game);
                                removedCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, $"Failed to remove tags for game {game.Name}");
                        }
                    }
                }

                // Delete only the tags this plugin tracks by id. Matching by name would also
                // delete a user's own tag that happens to share a configured name.
                var deletedTagIds = new HashSet<Guid>();
                foreach (var tagId in GetAllTrackedTagIds())
                {
                    var tag = _api.Database.Tags.Get(tagId);
                    if (tag == null)
                    {
                        deletedTagIds.Add(tagId);
                        continue;
                    }

                    try
                    {
                        _api.Database.Tags.Remove(tag);
                        deletedTagIds.Add(tagId);
                        _logger.Debug($"Deleted tag: {tag.Name}");
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"Failed to delete tag: {tag.Name}");
                    }
                }

                ForgetDeletedTagIds(deletedTagIds);
                _logger.Info($"Tag removal complete: {removedCount} games updated");
            }, progressOptions);

            // Clear the tag ID cache
            _tagIdCache.Clear();

            // Completion status is part of tag sync, so turning tagging off takes back the
            // statuses it set as well.
            ReleaseCompletionStatuses();
        }

        /// <summary>
        /// Removes all managed achievement tags from a single game.
        /// Uses provided tag IDs or falls back to currently tracked IDs.
        /// </summary>
        /// <param name="game">The game to remove tags from.</param>
        /// <param name="tagIdsToRemove">Specific tag IDs to remove. If null, uses GetAllTrackedTagIds().</param>
        /// <returns>True if any tags were removed.</returns>
        private bool RemoveManagedTags(Game game, HashSet<Guid> tagIdsToRemove = null)
        {
            if (game?.TagIds == null || game.TagIds.Count == 0)
            {
                return false;
            }

            var idsToRemove = tagIdsToRemove ?? GetAllTrackedTagIds();
            var originalCount = game.TagIds.Count;

            // Remove all managed tag IDs from the game
            game.TagIds.RemoveAll(id => idsToRemove.Contains(id));

            return game.TagIds.Count != originalCount;
        }

        /// <summary>
        /// Gets all tag IDs that are tracked by this plugin.
        /// Includes both current config tags and previously used tags from TagId history.
        /// </summary>
        private HashSet<Guid> GetAllTrackedTagIds()
        {
            var tagIds = new HashSet<Guid>();

            // Add current config tag IDs from cache
            foreach (var tagId in _tagIdCache.Values)
            {
                tagIds.Add(tagId);
            }

            // Add tracked tag IDs from config (for handling renames, and for kinds the
            // user disabled whose tags have not been cleaned up yet)
            var tagConfigs = Tagging?.TagConfigs;
            if (tagConfigs != null)
            {
                foreach (var config in tagConfigs.Values)
                {
                    if (config?.TagId.HasValue == true)
                    {
                        tagIds.Add(config.TagId.Value);
                    }
                }
            }

            return tagIds;
        }

        /// <summary>
        /// Reconciles every game's completion status with the current settings: completed games
        /// take the configured status, and statuses this plugin set are restored once they no
        /// longer apply.
        /// </summary>
        public void SyncCompletionStatus()
        {
            if (!(TaggingEnabled && (Tagging?.SetCompletionStatus ?? false)))
            {
                ReleaseCompletionStatuses();
                return;
            }

            var targetStatusId = GetCompletionStatusId();
            if (!targetStatusId.HasValue)
            {
                _logger.Warn("Could not find 'Completed' status in database");
                ReleaseCompletionStatuses();
                return;
            }

            var progressOptions = new GlobalProgressOptions(
                ResourceProvider.GetString("LOCPlayAch_Tagging_SyncingProgress"))
            {
                Cancelable = false,
                IsIndeterminate = false
            };

            _api.Dialogs.ActivateGlobalProgress((progress) =>
            {
                var games = _api.Database.Games.ToList();
                progress.ProgressMaxValue = games.Count;
                progress.CurrentProgressValue = 0;

                var updatedCount = 0;

                // Buffer database writes so all game updates in this pass raise a single
                // coalesced ItemUpdated event instead of one event per game.
                using (_api.Database.BufferedUpdate())
                {
                    foreach (var game in games)
                    {
                        if (game == null) continue;

                        progress.Text = $"{ResourceProvider.GetString("LOCPlayAch_Tagging_SyncingProgress")}: {game.Name}";
                        progress.CurrentProgressValue++;

                        try
                        {
                            if (ApplyCompletionStatus(game, EvaluateGame(game).Completion, targetStatusId))
                            {
                                _api.Database.Games.Update(game);
                                updatedCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, $"Failed to update completion status for game {game.Name}");
                        }
                    }
                }

                _logger.Info($"Completion status sync complete: {updatedCount} games updated");
            }, progressOptions);
        }

        /// <summary>
        /// Restores every completion status this plugin set, for when the feature or tagging is
        /// turned off. Only games holding a claim are visited, so this costs nothing per game.
        /// </summary>
        private void ReleaseCompletionStatuses()
        {
            var store = Store;
            if (store == null)
            {
                return;
            }

            var gameIds = store.QueryAll(records => records
                .Where(data => data != null &&
                               (RuleReconciler.FindClaim(data, RuleFields.CompletionStatus) != null ||
                                RuleReconciler.IsPinned(data, RuleFields.CompletionStatus)))
                .Select(data => data.PlayniteGameId)
                .ToList());
            if (gameIds.Count == 0)
            {
                return;
            }

            var restoredCount = 0;
            using (_api.Database.BufferedUpdate())
            {
                foreach (var gameId in gameIds)
                {
                    var game = _api.Database.Games.Get(gameId);
                    if (game == null)
                    {
                        continue;
                    }

                    try
                    {
                        if (ApplyCompletionStatus(game, isCompleted: false, targetStatusId: null))
                        {
                            _api.Database.Games.Update(game);
                            restoredCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"Failed to restore completion status for game {game.Name}");
                    }
                }
            }

            _logger.Info($"Completion status release complete: {restoredCount} games restored");
        }

        /// <summary>
        /// Applies the completion status rule to one game through <see cref="RuleReconciler"/>:
        /// a completed game takes the target status, and a status this plugin set is restored to
        /// what it was once the game is no longer completed or the feature is off. A status the
        /// user chose is never overwritten. Returns whether the game's status changed, in which
        /// case the caller must write the game.
        /// </summary>
        /// <param name="isCompleted">Null when the game's completion is unknown, which leaves its
        /// status alone while the feature is on.</param>
        private bool ApplyCompletionStatus(Game game, bool? isCompleted, Guid? targetStatusId)
        {
            var store = Store;
            if (game == null || store == null || (targetStatusId.HasValue && !isCompleted.HasValue))
            {
                return false;
            }

            const string field = RuleFields.CompletionStatus;
            var current = ToStatusText(game.CompletionStatusId);
            var desired = isCompleted == true && targetStatusId.HasValue ? ToStatusText(targetStatusId.Value) : null;
            var stored = store.TryLoad(game.Id, out var data) ? data : null;
            var decision = RuleReconciler.Decide(
                field,
                current,
                desired,
                RuleReconciler.FindClaim(stored, field),
                RuleReconciler.IsPinned(stored, field));
            if (decision.IsNoOp)
            {
                return false;
            }

            if (decision.StateChanged)
            {
                store.Update(
                    game.Id,
                    record => RuleReconciler.ApplyState(record, field, decision),
                    affectsSummaryData: false,
                    affectsOverrideMirror: false);
            }

            if (!decision.Write)
            {
                return false;
            }

            game.CompletionStatusId = ResolveExistingStatusId(decision.Value);
            return true;
        }

        private static string ToStatusText(Guid statusId)
        {
            return statusId == Guid.Empty ? null : statusId.ToString("D");
        }

        /// <summary>The stored status id, or no status when it was deleted since.</summary>
        private Guid ResolveExistingStatusId(string value)
        {
            return Guid.TryParse(value, out var statusId) && _api.Database.CompletionStatuses.Get(statusId) != null
                ? statusId
                : Guid.Empty;
        }

        private static GameCustomDataStore Store => PlayniteAchievementsPlugin.Instance?.GameCustomDataStore;

        /// <summary>
        /// Gets or creates a tag with the specified name and returns its ID.
        /// </summary>
        private Guid? GetOrCreateTag(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName))
            {
                return null;
            }

            // Check if we have it cached
            var existing = FindTagByName(tagName);
            if (existing.HasValue)
            {
                return existing.Value;
            }

            // Create the tag
            try
            {
                var tag = new Tag { Name = tagName };
                _api.Database.Tags.Add(tag);
                _logger.Debug($"Created tag '{tagName}' with ID {tag.Id}");
                return tag.Id;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to create tag '{tagName}'");
                return null;
            }
        }

        /// <summary>
        /// Finds a tag by name and returns its ID.
        /// </summary>
        private Guid? FindTagByName(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName))
            {
                return null;
            }

            var tag = _api.Database.Tags
                .FirstOrDefault(t => t.Name?.Equals(tagName, StringComparison.OrdinalIgnoreCase) ?? false);

            return tag?.Id;
        }
    }
}
