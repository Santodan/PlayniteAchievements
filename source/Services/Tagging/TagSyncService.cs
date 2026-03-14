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

        // Prefix for all PA tags to identify them
        private const string TagPrefix = "[PA]";

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

            // Step 2: Clean up orphan PA tags from the database
            CleanupOrphanTags();

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
                    }
                }
            }
        }

        /// <summary>
        /// Removes orphan PA tags from the database.
        /// These are tags that start with [PA] but don't match any current TagConfig display names.
        /// </summary>
        private void CleanupOrphanTags()
        {
            if (_settings.TaggingSettings?.TagConfigs == null) return;

            // Get all current tag display names (case-insensitive lookup)
            var currentTagNames = new HashSet<string>(
                _settings.TaggingSettings.TagConfigs.Values
                    .Where(c => c != null && !string.IsNullOrWhiteSpace(c.DisplayName))
                    .Select(c => c.DisplayName),
                StringComparer.OrdinalIgnoreCase);

            // Find all PA tags in the database
            var paTags = _api.Database.Tags
                .Where(t => t.Name?.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase) ?? false)
                .ToList();

            var orphanTags = paTags.Where(t => !currentTagNames.Contains(t.Name)).ToList();

            foreach (var orphanTag in orphanTags)
            {
                try
                {
                    _logger.Debug($"Deleting orphan PA tag: {orphanTag.Name}");
                    _api.Database.Tags.Remove(orphanTag);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"Failed to delete orphan tag: {orphanTag.Name}");
                }
            }

            // Clear the cache since tags may have been deleted
            _tagIdCache.Clear();

            if (orphanTags.Any())
            {
                _logger.Info($"Cleaned up {orphanTags.Count} orphan PA tags");
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

            // Check exclusion states first (highest priority - exclusive)
            if (_settings.ExcludedGameIds.Contains(gameId))
            {
                types.Add(TagType.Excluded);
                return types;
            }

            if (_settings.ExcludedFromSummariesGameIds.Contains(gameId))
            {
                types.Add(TagType.ExcludedFromSummaries);
                return types;
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

            // Add status tag based on progress
            if (data.IsCompleted)
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
        /// Removes all PA tags from a single game.
        /// </summary>
        /// <returns>True if any tags were removed.</returns>
        private bool RemoveAllPATags(Game game)
        {
            if (game?.TagIds == null || game.TagIds.Count == 0)
            {
                return false;
            }

            var paTagIds = GetAllPATagIds();
            var originalCount = game.TagIds.Count;

            // Remove all PA tag IDs from the game
            game.TagIds.RemoveAll(id => paTagIds.Contains(id));

            return game.TagIds.Count != originalCount;
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
        /// Migrates a tag name across all games in the library.
        /// Removes the old tag and adds the new tag to games that had the old tag.
        /// </summary>
        /// <param name="oldName">The current tag name to migrate from.</param>
        /// <param name="newName">The new tag name to migrate to.</param>
        public void MigrateTagName(string oldName, string newName)
        {
            if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
            {
                return;
            }

            // If names are the same, nothing to do
            if (oldName == newName)
            {
                return;
            }

            _logger.Info($"Migrating tag '{oldName}' to '{newName}'");

            var oldTagId = FindTagByName(oldName);
            var newTagId = GetOrCreateTag(newName);

            if (!oldTagId.HasValue)
            {
                _logger.Debug($"Old tag '{oldName}' not found, nothing to migrate");
                return;
            }

            if (!newTagId.HasValue)
            {
                _logger.Error($"Failed to create new tag '{newName}'");
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

                var migratedCount = 0;

                foreach (var game in games)
                {
                    if (game?.TagIds == null) continue;

                    progress.Text = $"{ResourceProvider.GetString("LOCPlayAch_Tagging_SyncingProgress")}: {game.Name}";
                    progress.CurrentProgressValue++;

                    try
                    {
                        if (game.TagIds.Contains(oldTagId.Value))
                        {
                            game.TagIds.Remove(oldTagId.Value);
                            if (!game.TagIds.Contains(newTagId.Value))
                            {
                                game.TagIds.Add(newTagId.Value);
                            }
                            _api.Database.Games.Update(game);
                            migratedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"Failed to migrate tag for game {game.Name}");
                    }
                }

                _logger.Info($"Tag migration complete: {migratedCount} games migrated from '{oldName}' to '{newName}'");
            }, progressOptions);

            // Invalidate the cache for this tag type
            foreach (var kvp in _tagIdCache.ToList())
            {
                if (kvp.Value == oldTagId.Value)
                {
                    _tagIdCache[kvp.Key] = newTagId.Value;
                }
            }
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

        /// <summary>
        /// Gets all tag IDs that start with the PA prefix.
        /// </summary>
        private HashSet<Guid> GetAllPATagIds()
        {
            var paTagIds = new HashSet<Guid>();

            var paTags = _api.Database.Tags
                .Where(t => t.Name?.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase) ?? false);

            foreach (var tag in paTags)
            {
                paTagIds.Add(tag.Id);
            }

            return paTagIds;
        }
    }
}
