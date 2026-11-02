using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Resolves tag keys to their definitions: the global list from settings first, then a game's
    /// own tags for keys the global list does not hold. A key a provider or metadata source put on an achievement that neither list
    /// holds is an implicit tag of that game, named from its key, so deleting a global tag leaves
    /// its achievements tagged under that game. A user tag (<c>u-</c>) whose definition was
    /// deleted is not shown.
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
        private static readonly ConcurrentDictionary<Guid, IReadOnlyList<AchievementTagDefinition>> GameCache =
            new ConcurrentDictionary<Guid, IReadOnlyList<AchievementTagDefinition>>();

        // One definition per implicit key, so per-row display does not allocate.
        private static readonly ConcurrentDictionary<string, AchievementTagDefinition> ImplicitCache =
            new ConcurrentDictionary<string, AchievementTagDefinition>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Raised after a tag list actually changed: the global one (GameId null) or one game's.
        /// </summary>
        public static event EventHandler<AchievementTagsChangedEventArgs> Changed;

        public static IReadOnlyList<AchievementTagDefinition> Global => _global;

        public static void SetGlobal(IEnumerable<AchievementTagDefinition> definitions)
        {
            var updated = (definitions ?? Enumerable.Empty<AchievementTagDefinition>())
                .Where(definition => !string.IsNullOrWhiteSpace(definition?.Key))
                .Select(definition => definition.Clone())
                .ToList();
            var changed = !SameDefinitions(_global, updated);
            _global = updated;
            if (changed)
            {
                Changed?.Invoke(null, new AchievementTagsChangedEventArgs(null));
            }
        }

        public static void SetGameResolver(Func<Guid, IReadOnlyList<AchievementTagDefinition>> resolver)
        {
            _gameResolver = resolver;
            GameCache.Clear();
        }

        /// <summary>
        /// Forgets the cached tags of <paramref name="gameId"/> after its custom data changed, and
        /// raises <see cref="Changed"/> only when its tag list is not what it was: most custom data
        /// edits leave the tags alone.
        /// </summary>
        public static void InvalidateGame(Guid gameId)
        {
            if (!GameCache.TryRemove(gameId, out var before))
            {
                return;
            }

            if (!SameDefinitions(before, GetGameTags(gameId)))
            {
                Changed?.Invoke(null, new AchievementTagsChangedEventArgs(gameId));
            }
        }

        private static bool SameDefinitions(
            IReadOnlyList<AchievementTagDefinition> left,
            IReadOnlyList<AchievementTagDefinition> right)
        {
            if ((left?.Count ?? 0) != (right?.Count ?? 0))
            {
                return false;
            }

            for (var i = 0; i < (left?.Count ?? 0); i++)
            {
                var a = left[i];
                var b = right[i];
                if (!string.Equals(a?.Key, b?.Key, StringComparison.Ordinal) ||
                    !string.Equals(a?.Name, b?.Name, StringComparison.Ordinal) ||
                    !string.Equals(a?.Color, b?.Color, StringComparison.Ordinal) ||
                    !string.Equals(a?.Icon, b?.Icon, StringComparison.Ordinal) ||
                    a?.Hidden != b?.Hidden)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>The tags <paramref name="gameId"/> stores itself, without its implicit tags.</summary>
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

        /// <summary>
        /// The tags available to <paramref name="gameId"/>: global first, then its own, then the
        /// implicit tags among <paramref name="presentKeys"/> (keys its achievements carry).
        /// </summary>
        public static IReadOnlyList<AchievementTagDefinition> GetAvailable(Guid? gameId, IEnumerable<string> presentKeys = null)
        {
            var game = GetGameTags(gameId);
            var implicitTags = (presentKeys ?? Enumerable.Empty<string>())
                .Select(key => ResolveImplicit(key, gameId))
                .Where(definition => definition != null)
                .ToList();
            if (game.Count == 0 && implicitTags.Count == 0)
            {
                return _global;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return _global.Concat(game).Concat(implicitTags)
                .Where(definition => seen.Add(definition.Key))
                .ToList();
        }

        /// <summary>The keys a user can put on an achievement of <paramref name="gameId"/>, in list order.</summary>
        public static IReadOnlyList<string> GetAssignableKeys(Guid? gameId, IEnumerable<string> presentKeys = null) =>
            GetAvailable(gameId, presentKeys)
                .Select(definition => definition.Key)
                .Where(key => !AchievementTagHelper.IsDerivedTag(key))
                .ToList();

        /// <summary>
        /// <see cref="GetAssignableKeys"/> without hidden tags: the tags filters, pies and menus
        /// outside the editor offer.
        /// </summary>
        public static IReadOnlyList<string> GetShownKeys(Guid? gameId, IEnumerable<string> presentKeys = null) =>
            GetAvailable(gameId, presentKeys)
                .Where(definition => !definition.Hidden && !AchievementTagHelper.IsDerivedTag(definition.Key))
                .Select(definition => definition.Key)
                .ToList();

        /// <summary>True when <paramref name="key"/> resolves on <paramref name="gameId"/> to a hidden tag.</summary>
        public static bool IsHidden(string key, Guid? gameId = null) => Resolve(key, gameId)?.Hidden == true;

        /// <summary>The definition of <paramref name="key"/> on <paramref name="gameId"/>; null for a deleted user tag.</summary>
        public static AchievementTagDefinition Resolve(string key, Guid? gameId = null)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var trimmed = key.Trim();
            return _global.FirstOrDefault(definition => KeyEquals(definition, trimmed)) ??
                   GetGameTags(gameId).FirstOrDefault(definition => KeyEquals(definition, trimmed)) ??
                   ResolveImplicit(trimmed, gameId);
        }

        /// <summary>
        /// The implicit tag for <paramref name="key"/>: a key no list defines that is neither a
        /// deleted user tag, a derived unlock mode nor a kind. Null otherwise.
        /// </summary>
        private static AchievementTagDefinition ResolveImplicit(string key, Guid? gameId)
        {
            var trimmed = key?.Trim();
            if (string.IsNullOrEmpty(trimmed) ||
                AchievementTagHelper.IsDerivedTag(trimmed) ||
                trimmed.StartsWith(AchievementTagHelper.UserKeyPrefix, StringComparison.OrdinalIgnoreCase) ||
                CategoryKindHelper.IsKnownKind(trimmed) ||
                _global.Any(definition => KeyEquals(definition, trimmed)) ||
                GetGameTags(gameId).Any(definition => KeyEquals(definition, trimmed)))
            {
                return null;
            }

            return ImplicitCache.GetOrAdd(trimmed, value => new AchievementTagDefinition { Key = value });
        }

        /// <summary>True when the global list or the game's own list defines <paramref name="key"/>.</summary>
        public static bool IsDefined(string key, Guid? gameId = null)
        {
            var trimmed = key?.Trim();
            return !string.IsNullOrEmpty(trimmed) &&
                   (_global.Any(definition => KeyEquals(definition, trimmed)) ||
                    GetGameTags(gameId).Any(definition => KeyEquals(definition, trimmed)));
        }

        /// <summary>True when <paramref name="key"/> is an implicit tag of <paramref name="gameId"/>.</summary>
        public static bool IsImplicit(string key, Guid? gameId) => ResolveImplicit(key, gameId) != null;

        /// <summary>
        /// The tags of <paramref name="value"/> that would be shown, in the order of the available
        /// list: derived keys and deleted user tags are left out, and hidden tags unless
        /// <paramref name="includeHidden"/> (the editor shows them).
        /// </summary>
        public static IReadOnlyList<AchievementTagDefinition> ResolveVisible(string value, Guid? gameId = null, bool includeHidden = false)
        {
            var keys = AchievementTagHelper.GetComponents(value);
            if (keys.Count == 0)
            {
                return Array.Empty<AchievementTagDefinition>();
            }

            var wanted = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
            return GetAvailable(gameId, keys)
                .Where(definition => wanted.Contains(definition.Key) &&
                                     !AchievementTagHelper.IsDerivedTag(definition.Key) &&
                                     (includeHidden || !definition.Hidden))
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
                : AchievementTagHelper.GetDefaultName(definition.Key);
        }

        public static string GetName(string key, Guid? gameId = null)
        {
            var definition = Resolve(key, gameId);
            return definition != null ? GetName(definition) : AchievementTagHelper.GetDefaultName(key);
        }

        private static bool KeyEquals(AchievementTagDefinition definition, string key) =>
            string.Equals(definition?.Key, key, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class AchievementTagsChangedEventArgs : EventArgs
    {
        public AchievementTagsChangedEventArgs(Guid? gameId)
        {
            GameId = gameId;
        }

        /// <summary>The game whose own tags changed; null when the global list changed.</summary>
        public Guid? GameId { get; }
    }
}
