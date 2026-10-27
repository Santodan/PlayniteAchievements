using Playnite.SDK;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Gives games an auto capstone automatically, once each, when the setting is on.
    /// </summary>
    /// <remarks>
    /// A game is handled once and never again: it gets an authored auto capstone standing for the
    /// whole game, or its own platinum nominated, or, when it already had a capstone, nothing at
    /// all. Whichever it was, the game is marked, so a capstone the user deletes stays deleted.
    /// The one later change: an authored capstone the user left as written gives way to the game's
    /// own achievement once a metadata source's platinum matches one.
    /// A game with no provider data yet is left unmarked and looked at again after a later refresh.
    /// What gets written is <see cref="AutoCapstoneAuthoring"/>'s, the same as the editor's button.
    /// </remarks>
    public sealed class AutoCapstoneGenerator
    {
        private readonly GameCustomDataStore _store;
        private readonly AchievementOverridesService _overridesService;
        private readonly AutoCapstoneAuthoring _authoring;
        private readonly Func<bool> _isEnabled;
        private readonly ILogger _logger;

        // One pass at a time: a retroactive pass and a refresh's pass reaching the same game
        // together would each find it unhandled and author two capstones.
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public AutoCapstoneGenerator(
            GameCustomDataStore store,
            AchievementOverridesService overridesService,
            AutoCapstoneAuthoring authoring,
            Func<bool> isEnabled,
            ILogger logger)
        {
            _store = store;
            _overridesService = overridesService;
            _authoring = authoring;
            _isEnabled = isEnabled;
            _logger = logger;
        }

        /// <summary>
        /// Handles every game in <paramref name="gameIds"/> that is not handled yet, while the
        /// setting is on, and in any case puts a matched platinum in place of an auto capstone
        /// left as written, which the editor's button authors whatever the setting.
        /// </summary>
        /// <param name="progress">Called after each game with how many are done and the total.</param>
        public async Task GenerateAsync(
            IEnumerable<Guid> gameIds,
            CancellationToken cancel = default,
            Action<int, int> progress = null)
        {
            if (_store == null || _overridesService == null || _authoring == null)
            {
                return;
            }

            var enabled = _isEnabled?.Invoke() == true;

            var ids = (gameIds ?? Enumerable.Empty<Guid>())
                .Where(gameId => gameId != Guid.Empty)
                .Distinct()
                .ToList();
            if (ids.Count == 0)
            {
                return;
            }

            await _gate.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                for (var i = 0; i < ids.Count; i++)
                {
                    cancel.ThrowIfCancellationRequested();
                    try
                    {
                        if (await GenerateOneAsync(ids[i], enabled).ConfigureAwait(false))
                        {
                            OpenEditorRegistry.NotifyBackgroundWrite(ids[i]);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn(ex, $"Automatic capstone generation failed for gameId={ids[i]}.");
                    }

                    progress?.Invoke(i + 1, ids.Count);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <returns>True when the game's achievements changed in a way a Manage window shows.</returns>
        private async Task<bool> GenerateOneAsync(Guid gameId, bool enabled)
        {
            if (_overridesService.IsExcludedFromRefreshes(gameId))
            {
                return false;
            }

            // The open editor saves the definitions from its own rows, so an achievement added
            // underneath it would be gone at its next save. The game is looked at when it closes.
            if (OpenEditorRegistry.DeferWhileOpen(gameId, this, () => _ = GenerateAsync(new[] { gameId })))
            {
                return false;
            }

            var stored = _store.TryLoad(gameId, out var data) ? data : null;

            // A collection whose games each have a platinum gets a capstone per game instead of
            // one for the whole game, also once the game was handled before: each game's category
            // is handled once, and an untouched whole-game capstone from before gives way.
            var categoryCapstones = _authoring.CategoryCapstones(gameId);
            if (categoryCapstones.Count > 0)
            {
                if (!enabled)
                {
                    return false;
                }

                var wrote = false;
                if (!categoryCapstones.All(capstone => string.IsNullOrWhiteSpace(capstone?.Category) || _authoring.IsCategoryHandled(gameId, capstone.Category.Trim())))
                {
                    await _authoring.GenerateCategoryCapstonesAsync(gameId, categoryCapstones).ConfigureAwait(false);
                    wrote = true;
                }

                // With the games' own capstones in place, an untouched whole-game one gives way.
                if (stored?.CustomAchievements?.Any(definition => definition?.IsAutoCapstone == true && definition.IsWholeGameAutoCapstone) == true)
                {
                    _authoring.RemoveUntouchedWholeGameCapstone(gameId);
                    wrote = true;
                }

                return wrote;
            }

            // Checked before hydrating: once the library has been handled, every refresh reaches
            // here for games with the marker, and hydrating each one only to skip it adds up.
            var matchedCapstone = _authoring.MatchedCapstoneApiName(gameId);

            // A real capstone matched after the game got an authored one takes its place, however
            // it was authored and whatever the setting.
            var replaced = false;
            if (matchedCapstone != null &&
                stored?.CustomAchievements?.Any(definition => definition?.IsWholeGameAutoCapstone == true && definition.IsAutoCapstone) == true)
            {
                _authoring.ReplaceWithMatchedCapstone(gameId);
                replaced = true;
            }

            if (!enabled || stored?.AutoCapstoneGenerated == true)
            {
                return replaced;
            }

            var ordered = _authoring.LoadAchievementsInOrder(gameId);
            var decision = AutoCapstoneEligibility.Decide(
                false,
                stored?.CustomAchievements?.Any(definition => definition?.IsAutoCapstone == true) == true,
                ordered,
                matchedCapstone);

            switch (decision.Action)
            {
                case AutoCapstoneGenerationAction.MarkHandled:
                    _overridesService.MarkAutoCapstoneGenerated(gameId);
                    return replaced;

                case AutoCapstoneGenerationAction.NominatePlatinum:
                    _authoring.NominatePlatinum(gameId, decision.Platinum);
                    return true;

                case AutoCapstoneGenerationAction.Author:
                    await _authoring.AuthorAsync(gameId).ConfigureAwait(false);
                    return true;
            }

            return replaced;
        }
    }
}
