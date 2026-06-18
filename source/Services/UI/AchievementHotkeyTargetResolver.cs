using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace PlayniteAchievements.Services.UI
{
    internal sealed class AchievementHotkeyTargetResolver
    {
        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly object _sync = new object();
        private readonly List<Guid> _runningGamePriority = new List<Guid>();

        public AchievementHotkeyTargetResolver(IPlayniteAPI api, ILogger logger)
        {
            _api = api;
            _logger = logger;
        }

        public void NotifyGameStarted(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return;
            }

            lock (_sync)
            {
                _runningGamePriority.Remove(game.Id);
                _runningGamePriority.Insert(0, game.Id);
            }
        }

        public void NotifyGameStopped(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return;
            }

            lock (_sync)
            {
                _runningGamePriority.Remove(game.Id);
            }
        }

        public AchievementHotkeyTargetResolution Resolve()
        {
            try
            {
                var games = _api?.Database?.Games?
                    .Where(game => game != null && game.Id != Guid.Empty)
                    .ToList() ?? new List<Game>();

                var selectedGames = _api?.MainView?.SelectedGames?
                    .Where(game => game != null && game.Id != Guid.Empty)
                    .ToList() ?? new List<Game>();

                List<Guid> priority;
                lock (_sync)
                {
                    priority = _runningGamePriority.ToList();
                }

                return Resolve(games.Where(game => game.IsRunning), selectedGames, priority);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to resolve achievement hotkey target.");
                return AchievementHotkeyTargetResolution.NoTarget;
            }
        }

        public static AchievementHotkeyTargetResolution Resolve(
            IEnumerable<Game> runningGames,
            IEnumerable<Game> selectedGames,
            IReadOnlyList<Guid> runningGamePriority)
        {
            var running = GetDistinctValidGames(runningGames).ToList();
            if (running.Count == 1)
            {
                return AchievementHotkeyTargetResolution.ForGame(running[0].Id);
            }

            if (running.Count > 1)
            {
                if (runningGamePriority != null)
                {
                    foreach (var gameId in runningGamePriority)
                    {
                        if (gameId == Guid.Empty)
                        {
                            continue;
                        }

                        var prioritized = running.FirstOrDefault(game => game.Id == gameId);
                        if (prioritized != null)
                        {
                            return AchievementHotkeyTargetResolution.ForGame(prioritized.Id);
                        }
                    }
                }

                return AchievementHotkeyTargetResolution.ForGame(running[0].Id);
            }

            var selected = GetDistinctValidGames(selectedGames).ToList();
            return selected.Count == 1
                ? AchievementHotkeyTargetResolution.ForGame(selected[0].Id)
                : AchievementHotkeyTargetResolution.NoTarget;
        }

        private static IEnumerable<Game> GetDistinctValidGames(IEnumerable<Game> games)
        {
            return games?
                .Where(game => game != null && game.Id != Guid.Empty)
                .GroupBy(game => game.Id)
                .Select(group => group.First()) ?? Enumerable.Empty<Game>();
        }
    }

    internal sealed class AchievementHotkeyTargetResolution
    {
        public static readonly AchievementHotkeyTargetResolution NoTarget =
            new AchievementHotkeyTargetResolution(false, Guid.Empty);

        private AchievementHotkeyTargetResolution(bool hasTarget, Guid gameId)
        {
            HasTarget = hasTarget;
            GameId = gameId;
        }

        public bool HasTarget { get; }

        public Guid GameId { get; }

        public static AchievementHotkeyTargetResolution ForGame(Guid gameId)
        {
            return gameId == Guid.Empty
                ? NoTarget
                : new AchievementHotkeyTargetResolution(true, gameId);
        }
    }
}
