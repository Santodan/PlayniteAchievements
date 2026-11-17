using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.Tagging;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Logging;
using PlayniteAchievements.Services.Rules;

namespace PlayniteAchievements.Services.Tagging
{
    /// <summary>
    /// Keeps the Playnite library fields the plugin maintains in step with achievement state:
    /// tags and features (each a <see cref="LibraryFieldSyncTarget"/>) and the completion
    /// status. Evaluates each game once per pass and hands the result to every enabled field.
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

        private readonly GameTagEvaluator _evaluator;
        private readonly LibraryFieldSyncTarget _tagsTarget;
        private readonly LibraryFieldSyncTarget _featuresTarget;
        private readonly IReadOnlyList<LibraryFieldSyncTarget> _targets;

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

            _evaluator = new GameTagEvaluator(() => Settings);
            _tagsTarget = new LibraryFieldSyncTarget(
                ItemCollectionField<Tag>.Tags(_api.Database, _logger),
                _logger,
                () => Tagging?.TagConfigs,
                () => TaggingEnabled,
                kinds: null,
                removingProgressKey: "LOCPlayAch_Tagging_RemovingProgress");
            _featuresTarget = new LibraryFieldSyncTarget(
                ItemCollectionField<GameFeature>.Features(_api.Database, _logger),
                _logger,
                () => Tagging?.FeatureConfigs,
                () => FeatureSyncEnabled,
                TaggingSettings.FeatureKinds,
                removingProgressKey: "LOCPlayAch_Tagging_RemovingFeaturesProgress");
            _targets = new[] { _tagsTarget, _featuresTarget };
        }

        private PersistedSettings Settings => _settingsHost.Persisted;

        private TaggingSettings Tagging => Settings?.TaggingSettings;

        private bool TaggingEnabled => Tagging?.EnableTagging == true;

        private bool FeatureSyncEnabled => Tagging?.EnableFeatureSync == true;

        private bool CompletionStatusEnabled => Tagging?.SetCompletionStatus == true;

        /// <summary>
        /// Whether any sync is on, so a game change needs a pass at all: tags, features or the
        /// completion status.
        /// </summary>
        public bool AnySyncEnabled => TaggingEnabled || FeatureSyncEnabled || CompletionStatusEnabled;

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

            var tagging = settings.TaggingSettings;
            tagging.InitializeDefaults(GetLocalizedDefaultName);

            // Runs before the subscription is attached, so no handler fires on the write. The
            // completion-status switch used to be hidden and inert while tagging was off, so a
            // file holding it on in that state showed the user "off"; keep that once, then the
            // switch stands on its own.
            if (!tagging.CompletionStatusSwitchSeeded)
            {
                if (tagging.SetCompletionStatus && !tagging.EnableTagging)
                {
                    tagging.SetCompletionStatus = false;
                }

                tagging.CompletionStatusSwitchSeeded = true;
            }
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
        /// Updates tag and feature display names that still match a known default (from any
        /// shipped language) to the current language's default, and renames the corresponding
        /// Playnite items in place. Customized names are left untouched. Requires the Playnite
        /// database to be open, so call from OnApplicationStarted or later.
        /// </summary>
        /// <returns>True when any display name changed and settings should be persisted.</returns>
        public bool RelocalizeDefaultTagNames()
        {
            var changed = false;
            foreach (var target in _targets)
            {
                var configs = target.Configs;
                if (configs == null)
                {
                    continue;
                }

                foreach (var kvp in configs)
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
                    target.RenameItem(config, oldName, newName);
                }
            }

            return changed;
        }

        /// <summary>
        /// Strips the Playnite item of each <see cref="TaggingSettings.RetiredKinds"/> kind from
        /// every game, deletes it by id (never by name, so a user's own item is untouched), and
        /// drops the kind's config. A retired config left in the settings is what marks the work
        /// as pending, so this runs once. Requires the Playnite database to be open.
        /// </summary>
        /// <returns>True when a config was dropped and settings should be persisted.</returns>
        public bool RemoveRetiredKinds()
        {
            var changed = false;
            foreach (var target in _targets)
            {
                var configs = target.Configs;
                if (configs == null)
                {
                    continue;
                }

                var retired = TaggingSettings.RetiredKinds.Where(configs.ContainsKey).ToList();
                if (retired.Count == 0)
                {
                    continue;
                }

                var ids = new HashSet<Guid>(retired
                    .Select(kind => configs[kind]?.TagId)
                    .Where(id => id.HasValue && id.Value != Guid.Empty)
                    .Select(id => id.Value));
                if (ids.Count > 0)
                {
                    var updatedCount = 0;
                    using (_api.Database.BufferedUpdate())
                    {
                        foreach (var game in _api.Database.Games.ToList())
                        {
                            try
                            {
                                if (game != null && target.RemoveManaged(game, ids))
                                {
                                    _api.Database.Games.Update(game);
                                    updatedCount++;
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.Error(ex, $"Failed to remove retired {target.Noun}s from game {game?.Name}");
                            }
                        }
                    }

                    foreach (var id in ids)
                    {
                        try
                        {
                            target.Field.Remove(id);
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, $"Failed to delete retired {target.Noun} with ID {id}");
                        }
                    }

                    _logger.Info($"Removed {ids.Count} retired {target.Noun}s from {updatedCount} games");
                }

                foreach (var kind in retired)
                {
                    configs.Remove(kind);
                }

                changed = true;
            }

            return changed;
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
                        SyncAll();
                    }
                    else
                    {
                        RemoveAllTags();
                    }
                }
                else if (e.PropertyName == nameof(TaggingSettings.EnableFeatureSync))
                {
                    if (taggingSettings.EnableFeatureSync)
                    {
                        SyncAll();
                    }
                    else
                    {
                        RemoveAllFeatures();
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
        /// Synchronizes every enabled field and the completion status for all games in the
        /// library, with a progress dialog. Also cleans up orphan items (ones that no longer
        /// match the current config names or enabled kinds).
        /// </summary>
        public void SyncAll()
        {
            SyncAll(silent: false);
        }

        /// <summary>
        /// Synchronizes every enabled field and the completion status for all games in the
        /// library. Also cleans up orphan items.
        /// </summary>
        /// <param name="silent">If true, runs without showing a progress dialog.</param>
        public void SyncAll(bool silent)
        {
            if (!AnySyncEnabled)
            {
                _logger.Info("Library sync skipped: tags, features and completion status are all disabled");
                return;
            }

            if (silent)
            {
                ExecuteSyncAll(null);
            }
            else
            {
                var progressOptions = new GlobalProgressOptions(
                    ResourceProvider.GetString("LOCPlayAch_Tagging_SyncingLibraryProgress"))
                {
                    Cancelable = false,
                    IsIndeterminate = false
                };

                _api.Dialogs.ActivateGlobalProgress((progress) =>
                {
                    ExecuteSyncAll(progress);
                }, progressOptions);
            }
        }

        private void ExecuteSyncAll(GlobalProgressActionArgs progress)
        {
            var active = _targets.Where(target => target.Enabled).ToList();

            // Capture the old ids BEFORE resolving new ones; the enabled kinds then get their
            // items resolved, created or renamed in place, while disabled kinds are left alone.
            // The removal set is the union, which strips the items of kinds the user has since
            // disabled.
            var oldIds = new Dictionary<LibraryFieldSyncTarget, HashSet<Guid>>();
            var removeIds = new Dictionary<LibraryFieldSyncTarget, HashSet<Guid>>();
            foreach (var target in active)
            {
                var old = target.CaptureConfiguredIds();
                target.EnsureConfiguredIds();
                var remove = new HashSet<Guid>(old);
                remove.UnionWith(target.GetAllTrackedIds());
                oldIds[target] = old;
                removeIds[target] = remove;
            }

            var games = _api.Database.Games.ToList();
            if (progress != null)
            {
                progress.ProgressMaxValue = games.Count;
                progress.CurrentProgressValue = 0;
            }

            var updatedCount = 0;
            var targetCompletionStatusId = CompletionStatusEnabled ? GetCompletionStatusId() : null;

            // Buffer database writes so all game updates in this pass raise a single
            // coalesced ItemUpdated event instead of one event per game.
            using (_api.Database.BufferedUpdate())
            {
                foreach (var game in games)
                {
                    if (game == null) continue;

                    if (progress != null)
                    {
                        progress.Text = $"{ResourceProvider.GetString("LOCPlayAch_Tagging_SyncingLibraryProgress")}: {game.Name}";
                        progress.CurrentProgressValue++;
                    }

                    try
                    {
                        if (SyncGame(game, active, removeIds, targetCompletionStatusId))
                        {
                            updatedCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"Failed to sync library fields for game {game.Name}");
                    }
                }
            }

            // Clean up orphan items AFTER games are synced, so no game still references a
            // deleted one. Only the enabled kinds count as current, so a kind the user disabled
            // loses its Playnite item here rather than lingering in the filter panel.
            foreach (var target in active)
            {
                target.CleanupOrphans(oldIds[target], target.GetEnabledIds());
            }

            _logger.Info($"Library sync complete: {updatedCount} games updated");
        }

        /// <summary>
        /// Synchronizes every enabled field and the completion status for specific games (e.g.
        /// after a refresh). Does not show a progress dialog - runs silently in background.
        /// </summary>
        public void SyncTagsForGames(List<Guid> gameIds)
        {
            if (!AnySyncEnabled || gameIds == null || gameIds.Count == 0)
            {
                return;
            }

            try
            {
                var active = _targets.Where(target => target.Enabled).ToList();
                foreach (var target in active)
                {
                    target.EnsureConfiguredIds();
                }

                var targetStatusId = CompletionStatusEnabled ? GetCompletionStatusId() : null;

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
                            SyncGame(game, active, removeIds: null, targetStatusId);
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, $"Failed to sync library fields for game {game.Name}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to sync library fields for games");
            }
        }

        /// <summary>
        /// Evaluates one game once and applies the result to every active field, then the
        /// completion status. Writes the game once if anything moved. Keyed off the game's own
        /// completion, not the Completed tag, so disabling that tag does not disable completion
        /// status syncing.
        /// </summary>
        /// <param name="removeIds">Per field, the ids to strip before adding; a missing or null
        /// entry strips every tracked id.</param>
        private bool SyncGame(
            Game game,
            IReadOnlyList<LibraryFieldSyncTarget> active,
            Dictionary<LibraryFieldSyncTarget, HashSet<Guid>> removeIds,
            Guid? targetCompletionStatusId)
        {
            var evaluation = _evaluator.Evaluate(game);
            var changed = false;
            foreach (var target in active)
            {
                HashSet<Guid> remove = null;
                removeIds?.TryGetValue(target, out remove);
                changed |= target.SyncGame(game, evaluation.TagTypes, remove);
            }

            changed |= ApplyCompletionStatus(game, evaluation.Completion, targetCompletionStatusId);
            if (changed)
            {
                _api.Database.Games.Update(game);
            }

            return changed;
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
        /// Reconciles only the kinds read from custom data (<see cref="GameTagEvaluator.CustomDataTagTypes"/>),
        /// for a change that cannot have moved any other tag or feature.
        /// </summary>
        /// <remarks>
        /// A full sync evaluates every tag, and that means loading the game's hydrated
        /// achievement data -- the whole cost of a sync. Renaming an achievement, writing a note
        /// or overriding an icon moves these kinds and nothing else, so the load is skipped and
        /// the other managed items are left exactly as they are.
        /// </remarks>
        public void SyncCustomizationTagsForGames(List<Guid> gameIds)
        {
            var active = _targets.Where(target => target.Enabled).ToList();
            if (active.Count == 0 || gameIds == null || gameIds.Count == 0)
            {
                return;
            }

            try
            {
                foreach (var target in active)
                {
                    target.EnsureConfiguredIds();
                }

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
                            var applies = new HashSet<TagType>(_evaluator.DetermineCustomDataTagTypes(
                                gameId,
                                () => PlayniteAchievementsPlugin.Instance?.AchievementDataService?.GetGameAchievementData(gameId)));
                            var changed = false;
                            foreach (var target in active)
                            {
                                foreach (var tagType in GameTagEvaluator.CustomDataTagTypes)
                                {
                                    changed |= target.SetManaged(game, tagType, applies.Contains(tagType));
                                }
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
        /// Removes all PA tags from all games and deletes them from the database.
        /// Called when tagging is disabled.
        /// </summary>
        public void RemoveAllTags()
        {
            RemoveAll(_tagsTarget);
        }

        /// <summary>
        /// Removes all PA features from all games and deletes them from the database.
        /// Called when feature sync is disabled.
        /// </summary>
        public void RemoveAllFeatures()
        {
            RemoveAll(_featuresTarget);
        }

        private void RemoveAll(LibraryFieldSyncTarget target)
        {
            var progressText = ResourceProvider.GetString(target.RemovingProgressKey);
            var progressOptions = new GlobalProgressOptions(progressText)
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

                        progress.Text = $"{progressText}: {game.Name}";
                        progress.CurrentProgressValue++;

                        try
                        {
                            if (target.RemoveManaged(game))
                            {
                                _api.Database.Games.Update(game);
                                removedCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, $"Failed to remove {target.Noun}s for game {game.Name}");
                        }
                    }
                }

                var deletedCount = target.DeleteAllTracked();
                _logger.Info($"{target.Noun} removal complete: {removedCount} games updated, {deletedCount} {target.Noun}s deleted");
            }, progressOptions);
        }

        /// <summary>
        /// Reconciles every game's completion status with the current settings: completed games
        /// take the configured status, and statuses this plugin set are restored once they no
        /// longer apply or the switch is off.
        /// </summary>
        public void SyncCompletionStatus()
        {
            if (!CompletionStatusEnabled)
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

            var progressText = ResourceProvider.GetString("LOCPlayAch_Tagging_SyncingLibraryProgress");
            var progressOptions = new GlobalProgressOptions(progressText)
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

                        progress.Text = $"{progressText}: {game.Name}";
                        progress.CurrentProgressValue++;

                        try
                        {
                            if (ApplyCompletionStatus(game, _evaluator.Evaluate(game).Completion, targetStatusId))
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
        /// Restores every completion status this plugin set, for when the switch is turned off.
        /// Only games holding a claim are visited, so this costs nothing per game.
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
    }
}
