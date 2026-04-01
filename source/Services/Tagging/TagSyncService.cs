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
using PlayniteAchievements.Services.Logging;

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
        private readonly PersistedSettings _settings;
        private readonly ICacheManager _cacheManager;
        private TaggingSettings _subscribedTaggingSettings;

        // Cache of tag IDs by tag type to avoid repeated database lookups
        private readonly Dictionary<TagType, Guid> _tagIdCache = new Dictionary<TagType, Guid>();

        public TagSyncService(
            IPlayniteAPI api,
            ILogger logger,
            PersistedSettings settings,
            ICacheManager cacheManager)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
        }

        /// <summary>
        /// Ensures tagging settings are initialized with defaults and subscribes
        /// to tagging property changes.
        /// </summary>
        public void InitializeAndSubscribeTaggingSettings()
        {
            EnsureTaggingSettingsInitialized();
            SubscribeToTaggingSettingsChanges(_settings.TaggingSettings);
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
                EnsureTaggingSettingsInitialized();
                SubscribeToTaggingSettingsChanges(_settings.TaggingSettings);
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
            if (_settings.TaggingSettings == null)
            {
                _settings.TaggingSettings = new TaggingSettings();
            }

            _settings.TaggingSettings.InitializeDefaults(tagType =>
            {
                return tagType switch
                {
                    TagType.HasAchievements => ResourceProvider.GetString("LOCPlayAch_Tag_HasAchievements"),
                    TagType.InProgress => ResourceProvider.GetString("LOCPlayAch_Tag_InProgress"),
                    TagType.Completed => ResourceProvider.GetString("LOCPlayAch_Tag_Completed"),
                    TagType.NoAchievements => ResourceProvider.GetString("LOCPlayAch_Tag_NoAchievements"),
                    TagType.Excluded => ResourceProvider.GetString("LOCPlayAch_Tag_Excluded"),
                    TagType.ExcludedFromSummaries => ResourceProvider.GetString("LOCPlayAch_Tag_ExcludedFromSummaries"),
                    _ => TaggingSettings.GetDefaultDisplayName(tagType)
                };
            });
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
                var taggingSettings = _settings.TaggingSettings;
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
                    if (taggingSettings.SetCompletionStatus && taggingSettings.EnableTagging)
                    {
                        SyncCompletionStatus();
                    }
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
            if (!_settings.TaggingSettings?.EnableTagging ?? true)
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
            // Capture old tag IDs BEFORE we update them, so we can remove old tags from games
            var oldTagIds = new HashSet<Guid>();
            if (_settings.TaggingSettings?.TagConfigs != null)
            {
                foreach (var config in _settings.TaggingSettings.TagConfigs.Values)
                {
                    if (config?.TagId.HasValue == true)
                    {
                        oldTagIds.Add(config.TagId.Value);
                    }
                }
            }

            // Step 1: Ensure all configured tags exist in the database
            // This updates TagConfig.TagId to the new tag IDs
            var tagConfigs = _settings.TaggingSettings.TagConfigs;
            EnsureConfiguredTagIds(tagConfigs);

            // Combine old and new tag IDs for removal (handles renames)
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
            var setCompletionStatus = _settings.TaggingSettings?.SetCompletionStatus ?? false;
            if (setCompletionStatus)
            {
                targetCompletionStatusId = GetCompletionStatusId();
            }

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
                    var tagTypes = DetermineTagTypes(game);
                    if (SyncGameTags(game, tagConfigs, tagTypes, allManagedTagIds))
                    {
                        updatedCount++;
                    }

                    // Also update completion status in the same pass
                    if (targetCompletionStatusId.HasValue)
                    {
                        if (tagTypes.Contains(TagType.Completed) &&
                            game.CompletionStatusId != targetCompletionStatusId.Value)
                        {
                            game.CompletionStatusId = targetCompletionStatusId.Value;
                            _api.Database.Games.Update(game);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Failed to sync tags for game {game.Name}");
                }
            }

            // Step 2: Clean up orphan tags from the database AFTER games are synced
            // This must come after syncing so we don't delete tags that games still reference
            // Pass only the NEW tag IDs as current - old tags not in new set should be deleted
            var newTagIds = new HashSet<Guid>(_tagIdCache.Values);
            CleanupOrphanTags(oldTagIds, newTagIds);

            _logger.Info($"Tag sync complete: {updatedCount} games updated");
        }
        /// Does not show a progress dialog - runs silently in background.
        /// </summary>
        public void SyncTagsForGames(List<Guid> gameIds)
        {
            if (!_settings.TaggingSettings?.EnableTagging ?? true || gameIds == null || gameIds.Count == 0)
            {
                return;
            }

            try
            {
                var tagConfigs = _settings.TaggingSettings.TagConfigs;
                EnsureConfiguredTagIds(tagConfigs);
                var targetStatusId = (_settings.TaggingSettings?.SetCompletionStatus ?? false)
                    ? GetCompletionStatusId()
                    : (Guid?)null;

                foreach (var gameId in gameIds)
                {
                    var game = _api.Database.Games.Get(gameId);
                    if (game == null) continue;

                    try
                    {
                        var tagTypes = DetermineTagTypes(game);
                        SyncGameTags(game, tagConfigs, tagTypes);

                        // Also update completion status if enabled
                        if (targetStatusId.HasValue)
                        {
                            if (tagTypes.Contains(TagType.Completed) &&
                                game.CompletionStatusId != targetStatusId.Value)
                            {
                                game.CompletionStatusId = targetStatusId.Value;
                                _api.Database.Games.Update(game);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"Failed to sync tags for game {game.Name}");
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
        /// Ensures all configured tags exist in the database.
        /// Called during sync to create tags even if no games currently have them.
        /// Also updates the TagConfig.TagId to track the actual tag ID.
        /// </summary>
        private void EnsureAllTagsExist()
        {
            EnsureConfiguredTagIds(_settings.TaggingSettings?.TagConfigs);
        }

        private void EnsureConfiguredTagIds(Dictionary<TagType, TagConfig> tagConfigs)
        {
            if (tagConfigs == null)
            {
                return;
            }

            foreach (var kvp in tagConfigs)
            {
                ResolveConfiguredTagId(kvp.Key, kvp.Value);
            }
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
        /// Removes orphan tags from the database.
        /// These are tags that were previously used but are no longer in the current config.
        /// </summary>
        /// <param name="oldTagIds">Tag IDs that were captured before the sync (for rename detection).</param>
        /// <param name="currentManagedTagIds">All tag IDs currently managed by this plugin.</param>
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

            if (deletedCount > 0)
            {
                _logger.Info($"Cleaned up {deletedCount} orphan tags");
            }
        }

        /// <summary>
        /// Gets the completion status ID to use for completed games.
        /// </summary>
        private Guid? GetCompletionStatusId()
        {
            var completionStatusId = _settings.TaggingSettings.CompletionStatusId;

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
            if (!_settings.TaggingSettings?.EnableTagging ?? true)
            {
                return false;
            }

            var tagConfigs = _settings.TaggingSettings.TagConfigs;
            EnsureConfiguredTagIds(tagConfigs);
            var tagTypes = DetermineTagTypes(game);
            return SyncGameTags(game, tagConfigs, tagTypes, tagIdsToRemove);
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
        /// Determines all applicable tag types for a game.
        /// A game can have multiple tags (e.g., HasAchievements + InProgress).
        /// Excluded is exclusive and removes other tags.
        /// ExcludedFromSummaries can coexist with achievement status tags.
        /// </summary>
        private List<TagType> DetermineTagTypes(Game game)
        {
            var types = new List<TagType>();

            if (game == null)
            {
                types.Add(TagType.NoAchievements);
                return types;
            }

            var gameId = game.Id;

            // Full exclusion is exclusive - no other tags
            if (GameCustomDataLookup.IsExcludedFromRefreshes(gameId, _settings))
            {
                types.Add(TagType.Excluded);
                return types;
            }

            // Check if excluded from summaries (can coexist with other tags)
            var excludedFromSummaries = GameCustomDataLookup.IsExcludedFromSummaries(gameId, _settings);
            if (excludedFromSummaries)
            {
                types.Add(TagType.ExcludedFromSummaries);
            }

            // Load the cached achievement data
            var data = _cacheManager?.LoadGameData(gameId.ToString());
            if (data == null || !data.HasAchievements)
            {
                types.Add(TagType.NoAchievements);
                return types;
            }

            // If the game is excluded by user
            if (data.ExcludedByUser)
            {
                types.Add(TagType.Excluded);
                return types;
            }

            // Game has achievements - add HasAchievements tag
            types.Add(TagType.HasAchievements);

            // Check if completed (all unlocked OR manual capstone unlocked)
            var isCompleted = data.IsCompleted;

            // Also check for manual capstone override from settings
            // (raw cached data doesn't have IsCapstone set - that's applied during hydration)
            var capstoneApiName = GameCustomDataLookup.GetManualCapstone(gameId, _settings);
            if (!isCompleted && !string.IsNullOrWhiteSpace(capstoneApiName))
            {
                var capstoneAchievement = data.Achievements?.FirstOrDefault(a =>
                    a?.ApiName?.Equals(capstoneApiName, StringComparison.OrdinalIgnoreCase) == true);
                if (capstoneAchievement?.Unlocked == true)
                {
                    isCompleted = true;
                }
            }

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

            return types;
        }

        /// <summary>
        /// Determines the primary tag type for a game based on its achievement status.
        /// Used for completion status syncing.
        /// </summary>
        public TagType DetermineTagType(Game game)
        {
            var types = DetermineTagTypes(game);
            // Return the most significant status tag
            if (types.Contains(TagType.Completed)) return TagType.Completed;
            if (types.Contains(TagType.InProgress)) return TagType.InProgress;
            return types.FirstOrDefault();
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

                // Delete only the exact tag names from config
                if (_settings.TaggingSettings?.TagConfigs != null)
                {
                    var tagNames = _settings.TaggingSettings.TagConfigs.Values
                        .Where(c => c != null && !string.IsNullOrWhiteSpace(c.DisplayName))
                        .Select(c => c.DisplayName)
                        .ToList();

                    foreach (var tagName in tagNames)
                    {
                        var tag = _api.Database.Tags
                            .FirstOrDefault(t => t.Name?.Equals(tagName, StringComparison.OrdinalIgnoreCase) ?? false);

                        if (tag != null)
                        {
                            try
                            {
                                _api.Database.Tags.Remove(tag);
                                _logger.Debug($"Deleted tag: {tag.Name}");
                            }
                            catch (Exception ex)
                            {
                                _logger.Error(ex, $"Failed to delete tag: {tag.Name}");
                            }
                        }
                    }
                }

                _logger.Info($"Tag removal complete: {removedCount} games updated");
            }, progressOptions);

            // Clear the tag ID cache
            _tagIdCache.Clear();
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

            // Add tracked tag IDs from config (for handling renames)
            if (_settings.TaggingSettings?.TagConfigs != null)
            {
                foreach (var config in _settings.TaggingSettings.TagConfigs.Values)
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
        /// Syncs completion status for completed games.
        /// </summary>
        public void SyncCompletionStatus()
        {
            if (!(_settings.TaggingSettings?.SetCompletionStatus ?? false))
            {
                return;
            }

            var completionStatusId = _settings.TaggingSettings.CompletionStatusId;
            Guid? targetStatusId;

            if (completionStatusId.HasValue && completionStatusId.Value != Guid.Empty)
            {
                targetStatusId = completionStatusId;
            }
            else
            {
                // Find the default "Completed" status
                var completedStatus = _api.Database.CompletionStatuses
                    .FirstOrDefault(s => s.Name?.Equals("Completed", StringComparison.OrdinalIgnoreCase) ?? false);

                if (completedStatus == null)
                {
                    _logger.Warn("Could not find 'Completed' status in database");
                    return;
                }

                targetStatusId = completedStatus.Id;
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

                foreach (var game in games)
                {
                    if (game == null) continue;

                    progress.Text = $"{ResourceProvider.GetString("LOCPlayAch_Tagging_SyncingProgress")}: {game.Name}";
                    progress.CurrentProgressValue++;

                    try
                    {
                        var tagType = DetermineTagType(game);
                        if (tagType == TagType.Completed && game.CompletionStatusId != targetStatusId)
                        {
                            game.CompletionStatusId = targetStatusId.Value;
                            _api.Database.Games.Update(game);
                            updatedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"Failed to update completion status for game {game.Name}");
                    }
                }

                _logger.Info($"Completion status sync complete: {updatedCount} games updated");
            }, progressOptions);
        }

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
