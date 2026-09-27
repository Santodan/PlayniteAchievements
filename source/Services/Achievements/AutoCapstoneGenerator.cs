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
        private readonly object _waitingSync = new object();
        private readonly HashSet<Guid> _waitingForEditor = new HashSet<Guid>();

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
            OpenEditorRegistry.Closed += OnEditorClosed;
        }

        /// <summary>
        /// Handles every game in <paramref name="gameIds"/> that is not handled yet.
        /// </summary>
        /// <param name="progress">Called after each game with how many are done and the total.</param>
        public async Task GenerateAsync(
            IEnumerable<Guid> gameIds,
            CancellationToken cancel = default,
            Action<int, int> progress = null)
        {
            if (_isEnabled?.Invoke() != true || _store == null || _overridesService == null || _authoring == null)
            {
                return;
            }

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
                        await GenerateOneAsync(ids[i]).ConfigureAwait(false);
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

        private async Task GenerateOneAsync(Guid gameId)
        {
            if (_overridesService.IsExcludedFromRefreshes(gameId))
            {
                return;
            }

            // The open editor saves the definitions from its own rows, so an achievement added
            // underneath it would be gone at its next save. The game is looked at when it closes.
            if (OpenEditorRegistry.IsOpen(gameId))
            {
                lock (_waitingSync)
                {
                    _waitingForEditor.Add(gameId);
                }

                return;
            }

            var stored = _store.TryLoad(gameId, out var data) ? data : null;

            // Checked before hydrating: once the library has been handled, every refresh reaches
            // here for games with the marker, and hydrating each one only to skip it adds up.
            if (stored?.AutoCapstoneGenerated == true)
            {
                return;
            }

            var ordered = _authoring.LoadAchievementsInOrder(gameId);
            var decision = AutoCapstoneEligibility.Decide(
                false,
                stored?.CustomAchievements?.Any(definition => definition?.IsAutoCapstone == true) == true,
                ordered);

            switch (decision.Action)
            {
                case AutoCapstoneGenerationAction.MarkHandled:
                    _overridesService.MarkAutoCapstoneGenerated(gameId);
                    break;

                case AutoCapstoneGenerationAction.NominatePlatinum:
                    _authoring.NominatePlatinum(gameId, decision.Platinum);
                    break;

                case AutoCapstoneGenerationAction.Author:
                    await _authoring.AuthorAsync(gameId).ConfigureAwait(false);
                    break;
            }
        }

        private void OnEditorClosed(Guid gameId)
        {
            lock (_waitingSync)
            {
                if (!_waitingForEditor.Remove(gameId))
                {
                    return;
                }
            }

            _ = GenerateAsync(new[] { gameId });
        }
    }
}
