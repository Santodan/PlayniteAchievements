using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Localization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Brings the library's auto capstones in line with the current text templates: every title
    /// or description still reading as a template's text is rewritten, edits are left alone.
    /// </summary>
    /// <remarks>
    /// Runs from the settings' Apply button and at startup when the templates in effect differ
    /// from the ones last applied, which is how a language change reaches existing capstones.
    /// </remarks>
    public sealed class AutoCapstoneTextService
    {
        private const string GameDescriptionId = nameof(AutoCapstoneTextField.GameDescription);
        private const string CategoryDescriptionId = nameof(AutoCapstoneTextField.CategoryDescription);

        private readonly GameCustomDataStore _store;
        private readonly Func<PersistedSettings> _settings;
        private readonly Func<Guid, string> _resolveGameName;
        private readonly ILogger _logger;
        private readonly Lazy<LocalizedDefaultStringCatalog> _catalog;
        private readonly object _applySync = new object();
        private readonly object _pendingSync = new object();
        private readonly HashSet<Guid> _waitingForEditor = new HashSet<Guid>();

        public AutoCapstoneTextService(
            GameCustomDataStore store,
            Func<PersistedSettings> settings,
            Func<Guid, string> resolveGameName,
            string localizationDirectory,
            ILogger logger)
        {
            _store = store;
            _settings = settings;
            _resolveGameName = resolveGameName;
            _logger = logger;
            _catalog = new Lazy<LocalizedDefaultStringCatalog>(() => BuildCatalog(localizationDirectory, logger));
            OpenEditorRegistry.Closed += OnEditorClosed;
        }

        /// <summary>True when the templates in effect are not the ones last applied.</summary>
        public static bool NeedsApply(PersistedSettings settings)
        {
            return settings != null &&
                !string.Equals(
                    AutoCapstoneText.Resolve(settings).Signature,
                    settings.AutoCapstoneAppliedTemplates,
                    StringComparison.Ordinal);
        }

        /// <summary>
        /// Rewrites every auto capstone still on default text. A game whose editor is open is
        /// done when the editor closes, since the editor's next save would overwrite it.
        /// </summary>
        /// <returns>The signature of the templates applied, to store as the last applied.</returns>
        public string Apply(CancellationToken cancel = default(CancellationToken), Action<int, int> progress = null)
        {
            lock (_applySync)
            {
                var settings = _settings?.Invoke();
                var current = AutoCapstoneText.Resolve(settings);
                var known = BuildKnown(settings, current);
                var gameIds = _store?.QueryAll(records => records
                        .Where(record => record?.CustomAchievements?.Any(definition => definition?.IsAutoCapstone == true) == true)
                        .Select(record => record.PlayniteGameId)
                        .Where(id => id != Guid.Empty)
                        .ToList())
                    ?? new List<Guid>();

                var rewritten = 0;
                for (var i = 0; i < gameIds.Count; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (ApplyToGame(gameIds[i], current, known))
                    {
                        rewritten++;
                    }

                    progress?.Invoke(i + 1, gameIds.Count);
                }

                _logger?.Info($"Auto capstone text applied: {rewritten} of {gameIds.Count} games rewritten.");
                return current.Signature;
            }
        }

        private bool ApplyToGame(Guid gameId, AutoCapstoneTemplates current, AutoCapstoneKnownTemplates known)
        {
            if (OpenEditorRegistry.IsOpen(gameId))
            {
                lock (_pendingSync)
                {
                    _waitingForEditor.Add(gameId);
                }

                return false;
            }

            var gameNames = ResolveGameNames(gameId);

            // Looked at before writing: a save reaches every subscriber, and most games need none.
            if (!_store.QueryGame(gameId, data => RewriteAll(data, gameNames, current, known, commit: false), false))
            {
                return false;
            }

            var changed = false;
            try
            {
                _store.Update(gameId, data => changed = RewriteAll(data, gameNames, current, known, commit: true));
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed applying auto capstone text for gameId={gameId}.");
            }

            return changed;
        }

        /// <param name="commit">False to only answer whether anything would change.</param>
        private static bool RewriteAll(
            GameCustomDataFile data,
            IReadOnlyList<string> gameNames,
            AutoCapstoneTemplates current,
            AutoCapstoneKnownTemplates known,
            bool commit)
        {
            var definitions = data?.CustomAchievements;
            if (definitions == null || definitions.Count == 0)
            {
                return false;
            }

            var changed = false;
            var replacement = new List<CustomAchievementDefinition>(definitions.Count);
            foreach (var definition in definitions)
            {
                var copy = definition?.Clone();
                if (copy == null)
                {
                    continue;
                }

                if (copy.IsAutoCapstone &&
                    AutoCapstoneTextRewriter.Rewrite(copy, ResolveCategory(data, copy), gameNames, current, known))
                {
                    changed = true;
                }

                replacement.Add(copy);
            }

            if (commit && changed)
            {
                data.CustomAchievements = replacement;
            }

            return changed;
        }

        /// <summary>
        /// The category the capstone is filed in: the definition's own is only ever the default,
        /// while the one it was filed in lives in the overrides.
        /// </summary>
        private static string ResolveCategory(GameCustomDataFile data, CustomAchievementDefinition definition)
        {
            var apiName = CustomAchievementProjectionService.BuildApiName(definition.Id);
            var overrides = data.AchievementOverrides;
            if (overrides != null && !string.IsNullOrWhiteSpace(apiName))
            {
                var entry = overrides.FirstOrDefault(pair => string.Equals(pair.Key, apiName, StringComparison.OrdinalIgnoreCase)).Value;
                if (!string.IsNullOrWhiteSpace(entry?.Category))
                {
                    return entry.Category;
                }
            }

            return definition.Category;
        }

        /// <summary>
        /// The game's name, which is what gets written, then the fallback authoring uses for a
        /// game with no name.
        /// </summary>
        private IReadOnlyList<string> ResolveGameNames(Guid gameId)
        {
            var fallback = ResourceProvider.GetString(AutoCapstoneText.FallbackGameNameKey);
            string name = null;
            try
            {
                name = _resolveGameName?.Invoke(gameId)?.Trim();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed resolving the game name for gameId={gameId}.");
            }

            return new[] { string.IsNullOrWhiteSpace(name) ? fallback : name, fallback }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private AutoCapstoneKnownTemplates BuildKnown(PersistedSettings settings, AutoCapstoneTemplates current)
        {
            return new AutoCapstoneKnownTemplates(
                field =>
                {
                    switch (field)
                    {
                        case AutoCapstoneTextField.GameDescription:
                            return _catalog.Value.GetKnownDefaults(GameDescriptionId);
                        case AutoCapstoneTextField.CategoryDescription:
                            return _catalog.Value.GetKnownDefaults(CategoryDescriptionId);
                        default:
                            return Enumerable.Empty<string>();
                    }
                },
                settings?.AutoCapstoneTemplateHistory,
                current);
        }

        private static LocalizedDefaultStringCatalog BuildCatalog(string localizationDirectory, ILogger logger)
        {
            var definitions = new List<LocalizedDefaultDefinition>
            {
                new LocalizedDefaultDefinition
                {
                    Id = GameDescriptionId,
                    ResourceKeys = new[] { AutoCapstoneTemplate.DescriptionKey },
                    Compose = values => values[0]
                },
                new LocalizedDefaultDefinition
                {
                    Id = CategoryDescriptionId,
                    ResourceKeys = new[] { AutoCapstoneText.CategoryDescriptionKey },
                    Compose = values => AutoCapstoneText.ToCategoryTemplate(values[0])
                }
            };

            return new LocalizedDefaultStringCatalog(localizationDirectory, definitions, logger);
        }

        private void OnEditorClosed(Guid gameId)
        {
            lock (_pendingSync)
            {
                if (!_waitingForEditor.Remove(gameId))
                {
                    return;
                }
            }

            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var settings = _settings?.Invoke();
                    var current = AutoCapstoneText.Resolve(settings);
                    ApplyToGame(gameId, current, BuildKnown(settings, current));
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"Failed applying auto capstone text after the editor closed for gameId={gameId}.");
                }
            });
        }
    }
}
