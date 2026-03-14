using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.Achievements;
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
        private readonly AchievementService _achievementService;

        // Cache of tag IDs by tag type to avoid repeated database lookups
        private readonly Dictionary<TagType, Guid> _tagIdCache = new Dictionary<TagType, Guid>();

        public TagSyncService(
            IPlayniteAPI api,
            ILogger logger,
            PersistedSettings settings,
            AchievementService achievementService)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _achievementService = achievementService ?? throw new ArgumentNullException(nameof(achievementService));
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
            // Step 1: Ensure all configured tags exist in the database
            EnsureAllTagsExist();

            var games = _api.Database.Games.ToList();
            if (progress != null)
            {
                progress.ProgressMaxValue = games.Count;
                progress.CurrentProgressValue = 0;
            }

            var tagConfigs = _settings.TaggingSettings.TagConfigs;
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
                    if (SyncGameTags(game, tagConfigs))
                    {
                        updatedCount++;
                    }

                    // Also update completion status in the same pass
                    if (targetCompletionStatusId.HasValue)
                    {
                        var tagType = DetermineTagType(game);
                        if (tagType == TagType.Completed && game.CompletionStatusId != targetCompletionStatusId.Value)
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

            // Step 2: Clean up orphan PA tags from the database AFTER games are synced
            // This must come after syncing so we don't delete tags that games still reference
            CleanupOrphanTags();

            _logger.Info($"Tag sync complete: {updatedCount} games updated");
        }

        /// <summary>
        /// Syncs tags for specific games (called after refreshes).
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

                foreach (var gameId in gameIds)
                {
                    var game = _api.Database.Games.Get(gameId);
                    if (game == null) continue;

                    try
                    {
                        SyncGameTags(game, tagConfigs);

                        // Also update completion status if enabled
                        if (_settings.TaggingSettings?.SetCompletionStatus ?? false)
                        {
                            var targetStatusId = GetCompletionStatusId();
                            if (targetStatusId.HasValue)
                            {
                                var tagType = DetermineTagType(game);
                                if (tagType == TagType.Completed && game.CompletionStatusId != targetStatusId.Value)
                                {
                                    game.CompletionStatusId = targetStatusId.Value;
                                    _api.Database.Games.Update(game);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"Failed to sync tags for game {game.Name}");
                    }
                }

                _logger.Debug($"Auto-synced tags for {gameIds.Count} games");
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
            if (_settings.TaggingSettings?.TagConfigs == null) return;

            foreach (var kvp in _settings.TaggingSettings.TagConfigs)
            {
                if (kvp.Value != null && !string.IsNullOrWhiteSpace(kvp.Value.DisplayName))
                {
                    var tagId = GetOrCreateTag(kvp.Value.DisplayName);
                    if (tagId.HasValue)
                    {
                        _tagIdCache[kvp.Key] = tagId.Value;
                        // Track the tag ID in the config for future reference
                        kvp.Value.TagId = tagId.Value;
                    }
                }
            }
        }

        /// <summary>
        /// Removes orphan tags from the database.
        /// These are tags that were previously tracked but no longer match any current TagConfig.
        /// Uses tracked TagIds rather than prefix matching.
        /// </summary>
        private void CleanupOrphanTags()
        {
            if (_settings.TaggingSettings?.TagConfigs == null) return;

            // Get all current tag IDs (the ones we just synced)
            var currentTagIds = new HashSet<Guid>(
                _settings.TaggingSettings.TagConfigs.Values
                    .Where(c => c != null && c.TagId.HasValue)
                    .Select(c => c.TagId.Value));

            // Get all tracked tag IDs from configs (including ones that might have been renamed)
            var trackedTagIds = _settings.TaggingSettings.TagConfigs.Values
                .Where(c => c != null && c.TagId.HasValue)
                .Select(c => c.TagId.Value)
                .ToHashSet();

            // Find tags in the database that match our tracked IDs but aren't current
            // (These are old tags from renames that should be cleaned up)
            var tagsToDelete = new List<Tag>();
            foreach (var tag in _api.Database.Tags)
            {
                // Skip if this is a current tag
                if (currentTagIds.Contains(tag.Id)) continue;

                // If we have a tracked ID for this tag type but the tag doesn't match current name, it's an orphan
                // Also clean up any old [PA] prefix tags that are no longer in use
                if (trackedTagIds.Contains(tag.Id))
                {
                    tagsToDelete.Add(tag);
                }
            }

            foreach (var orphanTag in tagsToDelete)
            {
                try
                {
                    _logger.Debug($"Deleting orphan tag: {orphanTag.Name}");
                    _api.Database.Tags.Remove(orphanTag);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Failed to delete orphan tag: {orphanTag.Name}");
                }
            }

            // Clear the cache since tags may have been deleted
            _tagIdCache.Clear();

            if (tagsToDelete.Any())
            {
                _logger.Info($"Cleaned up {tagsToDelete.Count} orphan tags");
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
        /// <returns>True if any tags were changed.</returns>
        public bool SyncGameTags(Game game)
        {
            if (!_settings.TaggingSettings?.EnableTagging ?? true)
            {
                return false;
            }

            return SyncGameTags(game, _settings.TaggingSettings.TagConfigs);
        }

        /// <summary>
        /// Syncs tags for a single game based on its achievement status.
        /// </summary>
        private bool SyncGameTags(Game game, Dictionary<TagType, TagConfig> tagConfigs)
        {
            if (game == null) return false;

            var changed = false;

            // First, remove all PA tags from the game
            changed |= RemoveAllPATags(game);

            // Determine all applicable tag types (can be multiple)
            var tagTypes = DetermineTagTypes(game);

            // Add each applicable tag
            foreach (var tagType in tagTypes)
            {
                if (tagConfigs != null && tagConfigs.TryGetValue(tagType, out var config) && config.IsEnabled)
                {
                    var tagId = GetOrCreateTag(config.DisplayName);
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
            if (_settings.ExcludedGameIds.Contains(gameId))
            {
                types.Add(TagType.Excluded);
                return types;
            }

            // Check if excluded from summaries (can coexist with other tags)
            var excludedFromSummaries = _settings.ExcludedFromSummariesGameIds.Contains(gameId);
            if (excludedFromSummaries)
            {
                types.Add(TagType.ExcludedFromSummaries);
            }

            // Load the cached achievement data
            var data = _achievementService.Cache?.LoadGameData(gameId.ToString());
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
            if (!isCompleted && _settings.ManualCapstones.TryGetValue(gameId, out var capstoneApiName))
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
                        if (RemoveAllPATags(game))
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
        /// Removes all tracked achievement tags from a single game.
        /// Uses both current config tag IDs and previously tracked tag IDs.
        /// </summary>
        /// <returns>True if any tags were removed.</returns>
        private bool RemoveAllPATags(Game game)
        {
            if (game?.TagIds == null || game.TagIds.Count == 0)
            {
                return false;
            }

            var trackedTagIds = GetAllTrackedTagIds();
            var originalCount = game.TagIds.Count;

            // Remove all tracked tag IDs from the game
            game.TagIds.RemoveAll(id => trackedTagIds.Contains(id));

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
