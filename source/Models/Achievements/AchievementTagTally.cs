using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Models.Achievements
{
    /// <summary>
    /// One tag's achievements in a game: how many carry it, and how many of those are unlocked.
    /// Filtered achievements are not counted. Keys are stored raw; display resolves them, so a
    /// renamed or deleted tag needs no recount.
    /// </summary>
    public readonly struct AchievementTagTally
    {
        public AchievementTagTally(string key, int unlocked, int total)
        {
            Key = key;
            Unlocked = unlocked;
            Total = total;
        }

        public string Key { get; }

        public int Unlocked { get; }

        public int Total { get; }
    }

    /// <summary>
    /// Which tags a tag filter or the Tag pie offers for the games in view: the global tags with no
    /// game picked; a picked game's tags (the global ones, its own, and the implicit ones its
    /// achievements carry); with several picked, the tags every one of them has.
    /// </summary>
    internal static class AchievementTagScope
    {
        public static IReadOnlyList<string> OptionKeys(
            IReadOnlyCollection<Guid> gameIds,
            Func<Guid, IEnumerable<string>> presentKeys)
        {
            if (gameIds == null || gameIds.Count == 0)
            {
                return AchievementTagRegistry.GetAssignableKeys(null);
            }

            List<string> shared = null;
            foreach (var gameId in gameIds)
            {
                var keys = AchievementTagRegistry.GetAssignableKeys(gameId, presentKeys?.Invoke(gameId));
                shared = shared == null
                    ? keys.ToList()
                    : shared.Where(key => keys.Contains(key, StringComparer.OrdinalIgnoreCase)).ToList();
            }

            return shared;
        }

        /// <summary>The game a tag's name and look resolve on: the only one in view, or none.</summary>
        public static Guid? NamingGame(IReadOnlyCollection<Guid> gameIds) =>
            gameIds != null && gameIds.Count == 1 ? gameIds.First() : (Guid?)null;
    }

    /// <summary>
    /// Builds a game's <see cref="AchievementTagTally"/> list from tag values. An achievement
    /// counts once under each tag it carries; the derived unlock modes (Softcore, Hardcore) describe
    /// how it was earned, not what it is, and are not counted.
    /// </summary>
    internal sealed class AchievementTagTallyBuilder
    {
        private readonly Dictionary<string, (int Unlocked, int Total)> _counts =
            new Dictionary<string, (int Unlocked, int Total)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Whether the user set this achievement's tags, clearing them included.</summary>
        public static bool HasTagEdit(AchievementOverride entry) =>
            entry != null && (entry.ClearTags || entry.Tags != null);

        /// <summary>An achievement's tags once the user's edit in <paramref name="entry"/> (if any) is applied.</summary>
        public static string EffectiveTags(string providerTags, AchievementOverride entry) =>
            AchievementTagHelper.ApplyOverride(
                providerTags,
                HasTagEdit(entry) ? (entry.ClearTags ? string.Empty : entry.Tags) : null);

        /// <summary>
        /// <paramref name="providerTotals"/> with the user's tag edits applied: each edited
        /// achievement found in <paramref name="states"/> (its provider tags and unlock state) moves
        /// from its provider tags to its effective ones.
        /// </summary>
        public static AchievementTagTally[] ApplyEdits(
            AchievementTagTally[] providerTotals,
            IReadOnlyDictionary<string, (string Tags, bool Unlocked)> states,
            IReadOnlyDictionary<string, AchievementOverride> overrides)
        {
            if (states == null || states.Count == 0 || overrides == null || overrides.Count == 0)
            {
                return providerTotals;
            }

            var builder = From(providerTotals);
            foreach (var pair in overrides)
            {
                if (!HasTagEdit(pair.Value) || !states.TryGetValue(pair.Key, out var state))
                {
                    continue;
                }

                builder.Remove(state.Tags, state.Unlocked);
                builder.Add(EffectiveTags(state.Tags, pair.Value), state.Unlocked);
            }

            return builder.Build();
        }

        public static AchievementTagTallyBuilder From(IEnumerable<AchievementTagTally> tallies)
        {
            var builder = new AchievementTagTallyBuilder();
            foreach (var tally in tallies ?? Enumerable.Empty<AchievementTagTally>())
            {
                if (!string.IsNullOrWhiteSpace(tally.Key))
                {
                    builder.Shift(tally.Key, tally.Unlocked, tally.Total);
                }
            }

            return builder;
        }

        public bool IsEmpty => _counts.Count == 0;

        /// <summary>Counts <paramref name="total"/> achievements carrying <paramref name="tags"/>, <paramref name="unlocked"/> of them unlocked.</summary>
        public void Add(string tags, int unlocked, int total)
        {
            foreach (var key in CountedKeys(tags))
            {
                Shift(key, unlocked, total);
            }
        }

        public void Add(string tags, bool unlocked) => Add(tags, unlocked ? 1 : 0, 1);

        /// <summary>Takes back one achievement counted under <paramref name="tags"/>.</summary>
        public void Remove(string tags, bool unlocked)
        {
            foreach (var key in CountedKeys(tags))
            {
                Shift(key, unlocked ? -1 : 0, -1);
            }
        }

        public void AddAll(IEnumerable<AchievementTagTally> tallies)
        {
            foreach (var tally in tallies ?? Enumerable.Empty<AchievementTagTally>())
            {
                if (!string.IsNullOrWhiteSpace(tally.Key))
                {
                    Shift(tally.Key, tally.Unlocked, tally.Total);
                }
            }
        }

        /// <summary>The tallies in key order; null when no achievement carries a tag.</summary>
        public AchievementTagTally[] Build()
        {
            if (_counts.Count == 0)
            {
                return null;
            }

            return _counts
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new AchievementTagTally(string.Intern(pair.Key), pair.Value.Unlocked, pair.Value.Total))
                .ToArray();
        }

        private static IEnumerable<string> CountedKeys(string tags) =>
            AchievementTagHelper.GetComponents(tags).Where(key => !AchievementTagHelper.IsDerivedTag(key));

        private void Shift(string key, int unlocked, int total)
        {
            _counts.TryGetValue(key, out var current);
            var next = (Math.Max(0, current.Unlocked + unlocked), Math.Max(0, current.Total + total));
            if (next.Item2 == 0)
            {
                _counts.Remove(key);
            }
            else
            {
                _counts[key] = next;
            }
        }
    }
}
