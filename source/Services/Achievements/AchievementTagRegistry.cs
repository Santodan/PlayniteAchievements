using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Resolves tag keys to their definitions: a game's own tags first, then the global list from
    /// settings. A key neither list holds belongs to a deleted tag and is not shown.
    /// </summary>
    /// <remarks>
    /// The plugin sets the global list whenever settings load or save, and the game resolver once
    /// at startup. Until then the built-in defaults stand in, so display code and tests resolve
    /// built-in tags without any setup.
    /// </remarks>
    internal static class AchievementTagRegistry
    {
        private static volatile IReadOnlyList<AchievementTagDefinition> _global =
            AchievementTagHelper.CreateDefaultDefinitions();

        private static volatile Func<Guid, IReadOnlyList<AchievementTagDefinition>> _gameResolver;

        // A game's own tags, read once per game: the resolver reads the stored record, and display code
        // asks per row. Dropped by InvalidateGame when the game's custom data changes.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, IReadOnlyList<AchievementTagDefinition>> GameCache =
            new System.Collections.Concurrent.ConcurrentDictionary<Guid, IReadOnlyList<AchievementTagDefinition>>();

        /// <summary>Raised after the global list or a game's tags change.</summary>
        public static event EventHandler Changed;

        public static IReadOnlyList<AchievementTagDefinition> Global => _global;

        public static void SetGlobal(IEnumerable<AchievementTagDefinition> definitions)
        {
            _global = (definitions ?? Enumerable.Empty<AchievementTagDefinition>())
                .Where(definition => !string.IsNullOrWhiteSpace(definition?.Key))
                .Select(definition => definition.Clone())
                .ToList();
            NotifyChanged();
        }

        public static void SetGameResolver(Func<Guid, IReadOnlyList<AchievementTagDefinition>> resolver)
        {
            _gameResolver = resolver;
            GameCache.Clear();
        }

        /// <summary>Forgets the cached tags of <paramref name="gameId"/> after its custom data changed.</summary>
        public static void InvalidateGame(Guid gameId)
        {
            if (GameCache.TryRemove(gameId, out _))
            {
                NotifyChanged();
            }
        }

        public static void NotifyChanged() => Changed?.Invoke(null, EventArgs.Empty);

        public static IReadOnlyList<AchievementTagDefinition> GetGameTags(Guid? gameId)
        {
            if (!gameId.HasValue || gameId.Value == Guid.Empty)
            {
                return Array.Empty<AchievementTagDefinition>();
            }

            var resolver = _gameResolver;
            if (resolver == null)
            {
                return Array.Empty<AchievementTagDefinition>();
            }

            return GameCache.GetOrAdd(gameId.Value, id =>
            {
                try
                {
                    return resolver(id) ?? Array.Empty<AchievementTagDefinition>();
                }
                catch
                {
                    return Array.Empty<AchievementTagDefinition>();
                }
            });
        }

        /// <summary>The tags available to <paramref name="gameId"/>: global first, then its own.</summary>
        public static IReadOnlyList<AchievementTagDefinition> GetAvailable(Guid? gameId)
        {
            var game = GetGameTags(gameId);
            if (game.Count == 0)
            {
                return _global;
            }

            var globalKeys = new HashSet<string>(_global.Select(definition => definition.Key), StringComparer.OrdinalIgnoreCase);
            return _global.Concat(game.Where(definition => !globalKeys.Contains(definition.Key))).ToList();
        }

        /// <summary>The keys a user can put on an achievement of <paramref name="gameId"/>, in list order.</summary>
        public static IReadOnlyList<string> GetAssignableKeys(Guid? gameId) =>
            GetAvailable(gameId)
                .Select(definition => definition.Key)
                .Where(key => !AchievementTagHelper.IsDerivedTag(key))
                .ToList();

        public static AchievementTagDefinition Resolve(string key, Guid? gameId = null)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var trimmed = key.Trim();
            return GetGameTags(gameId).FirstOrDefault(definition => KeyEquals(definition, trimmed)) ??
                   _global.FirstOrDefault(definition => KeyEquals(definition, trimmed));
        }

        /// <summary>
        /// The tags of <paramref name="value"/> that would be shown, in the order of the available
        /// list: derived keys and keys without a definition are left out.
        /// </summary>
        public static IReadOnlyList<AchievementTagDefinition> ResolveVisible(string value, Guid? gameId = null)
        {
            var keys = AchievementTagHelper.GetComponents(value);
            if (keys.Count == 0)
            {
                return Array.Empty<AchievementTagDefinition>();
            }

            var wanted = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
            return GetAvailable(gameId)
                .Where(definition => wanted.Contains(definition.Key) && !AchievementTagHelper.IsDerivedTag(definition.Key))
                .ToList();
        }

        public static string GetName(AchievementTagDefinition definition)
        {
            if (definition == null)
            {
                return string.Empty;
            }

            return !string.IsNullOrWhiteSpace(definition.Name)
                ? definition.Name
                : AchievementTagHelper.GetBuiltInName(definition.Key) ?? definition.Key;
        }

        public static string GetName(string key, Guid? gameId = null)
        {
            var definition = Resolve(key, gameId);
            return definition != null
                ? GetName(definition)
                : AchievementTagHelper.GetBuiltInName(key) ?? key;
        }

        private static bool KeyEquals(AchievementTagDefinition definition, string key) =>
            string.Equals(definition?.Key, key, StringComparison.OrdinalIgnoreCase);
    }
}
