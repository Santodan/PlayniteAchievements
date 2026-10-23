using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Models.Achievements
{
    /// <summary>
    /// A game's achievements that share one tag set, rarity tier, trophy grade and capstone flag:
    /// how many there are, and how many of those are unlocked. <see cref="Key"/> is the tag set
    /// ("Missable|Collectable", empty for untagged), so an achievement with several tags is counted
    /// once, and a count over any choice of tags, tiers, grades and capstones counts each
    /// achievement once, locked ones included. Filtered achievements are not counted. Tag keys are
    /// stored raw; display resolves them, so a renamed or deleted tag needs no recount.
    /// </summary>
    public readonly struct AchievementTally
    {
        public AchievementTally(string key, int unlocked, int total)
            : this(key, RarityTier.Common, null, false, unlocked, total)
        {
        }

        public AchievementTally(
            string key,
            RarityTier? rarity,
            string trophy,
            bool isCapstone,
            int unlocked,
            int total)
        {
            Key = key ?? string.Empty;
            Rarity = rarity;
            Trophy = trophy;
            IsCapstone = isCapstone;
            Unlocked = unlocked;
            Total = total;
        }

        /// <summary>The tag set, its keys joined by "|"; empty for untagged achievements.</summary>
        public string Key { get; }

        /// <summary>The rarity tier; null when the stored tier is unknown, which no tier counts.</summary>
        public RarityTier? Rarity { get; }

        /// <summary>"platinum", "gold", "silver" or "bronze"; null for no trophy grade.</summary>
        public string Trophy { get; }

        public bool IsCapstone { get; }

        public int Unlocked { get; }

        public int Total { get; }

        /// <summary>The tags in <see cref="Key"/>.</summary>
        public IReadOnlyList<string> Tags => AchievementTagHelper.GetComponents(Key);

        /// <summary>A trophy type as tallies hold it: one of the four grades in lower case, else null.</summary>
        public static string NormalizeTrophy(string trophyType)
        {
            switch ((trophyType ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "platinum":
                    return "platinum";
                case "gold":
                    return "gold";
                case "silver":
                    return "silver";
                case "bronze":
                    return "bronze";
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// One achievement as the tallies count it before the user's edits: its provider tags, tier,
    /// trophy grade (with the user's trophy edit, which the store applies), capstone flag and
    /// unlock state.
    /// </summary>
    public readonly struct AchievementTallyState
    {
        public AchievementTallyState(string tags, RarityTier? rarity, string trophy, bool isCapstone, bool unlocked)
        {
            Tags = tags;
            Rarity = rarity;
            Trophy = trophy;
            IsCapstone = isCapstone;
            Unlocked = unlocked;
        }

        public string Tags { get; }

        public RarityTier? Rarity { get; }

        public string Trophy { get; }

        public bool IsCapstone { get; }

        public bool Unlocked { get; }
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
                return AchievementTagRegistry.GetShownKeys(null);
            }

            List<string> shared = null;
            foreach (var gameId in gameIds)
            {
                var keys = AchievementTagRegistry.GetShownKeys(gameId, presentKeys?.Invoke(gameId));
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
    /// Builds a game's <see cref="AchievementTally"/> list: one entry per tag set, tier, trophy
    /// grade and capstone flag its achievements carry. The derived unlock modes (Softcore,
    /// Hardcore) describe how an achievement was earned, not what it is, and are not part of its
    /// tag set.
    /// </summary>
    internal sealed class AchievementTallyBuilder
    {
        private readonly Dictionary<Facet, (int Unlocked, int Total)> _counts =
            new Dictionary<Facet, (int Unlocked, int Total)>();

        /// <summary>Whether the user set this achievement's tags, clearing them included.</summary>
        public static bool HasTagEdit(AchievementOverride entry) =>
            entry != null && (entry.ClearTags || entry.Tags != null);

        /// <summary>An achievement's tags once the user's edit in <paramref name="entry"/> (if any) is applied.</summary>
        public static string EffectiveTags(string providerTags, AchievementOverride entry) =>
            AchievementTagHelper.ApplyOverride(
                providerTags,
                HasTagEdit(entry) ? (entry.ClearTags ? string.Empty : entry.Tags) : null);

        /// <summary>
        /// <paramref name="providerTotals"/> with the user's edits applied: each achievement in
        /// <paramref name="states"/> (as the store counted it) moves to its effective tags from
        /// <paramref name="tagEdits"/>, and, when <paramref name="capstones"/> is given (a game
        /// whose capstones the user edited), to whether that set holds it.
        /// </summary>
        public static AchievementTally[] ApplyEdits(
            AchievementTally[] providerTotals,
            IReadOnlyDictionary<string, AchievementTallyState> states,
            IReadOnlyDictionary<string, AchievementOverride> tagEdits,
            ISet<string> capstones = null)
        {
            if (states == null || states.Count == 0)
            {
                return providerTotals;
            }

            var builder = From(providerTotals);
            var changed = false;
            foreach (var pair in states)
            {
                var state = pair.Value;
                AchievementOverride entry = null;
                tagEdits?.TryGetValue(pair.Key, out entry);
                var tags = HasTagEdit(entry) ? EffectiveTags(state.Tags, entry) : state.Tags;
                var isCapstone = capstones != null ? capstones.Contains(pair.Key) : state.IsCapstone;
                if (!HasTagEdit(entry) && isCapstone == state.IsCapstone)
                {
                    continue;
                }

                builder.Remove(state.Tags, state.Rarity, state.Trophy, state.IsCapstone, state.Unlocked);
                builder.Add(tags, state.Rarity, state.Trophy, isCapstone, state.Unlocked);
                changed = true;
            }

            return changed ? builder.Build() : providerTotals;
        }

        public static AchievementTallyBuilder From(IEnumerable<AchievementTally> tallies)
        {
            var builder = new AchievementTallyBuilder();
            builder.AddAll(tallies);
            return builder;
        }

        public bool IsEmpty => _counts.Count == 0;

        /// <summary>
        /// Counts <paramref name="total"/> achievements carrying <paramref name="tags"/> of this tier,
        /// grade and capstone flag, <paramref name="unlocked"/> of them unlocked.
        /// </summary>
        public void Add(string tags, RarityTier? rarity, string trophy, bool isCapstone, int unlocked, int total) =>
            Shift(new Facet(SetKey(tags), rarity, AchievementTally.NormalizeTrophy(trophy), isCapstone), unlocked, total);

        public void Add(string tags, RarityTier? rarity, string trophy, bool isCapstone, bool unlocked) =>
            Add(tags, rarity, trophy, isCapstone, unlocked ? 1 : 0, 1);

        /// <summary>Counts achievements by their tags alone, as Common, without a trophy grade, and not capstones.</summary>
        public void Add(string tags, int unlocked, int total) =>
            Add(tags, RarityTier.Common, null, false, unlocked, total);

        public void Add(string tags, bool unlocked) => Add(tags, unlocked ? 1 : 0, 1);

        /// <summary>Takes back one achievement counted under these values.</summary>
        public void Remove(string tags, RarityTier? rarity, string trophy, bool isCapstone, bool unlocked) =>
            Shift(new Facet(SetKey(tags), rarity, AchievementTally.NormalizeTrophy(trophy), isCapstone), unlocked ? -1 : 0, -1);

        public void Remove(string tags, bool unlocked) => Remove(tags, RarityTier.Common, null, false, unlocked);

        /// <summary>
        /// Counts over <paramref name="tallies"/> for the tags in <paramref name="shown"/>: each
        /// tag's own achievements, and the achievements carrying any of them, each counted once
        /// however many of the shown tags it has.
        /// </summary>
        public static (Dictionary<string, (int Unlocked, int Total)> PerTag, int Unlocked, int Total) CountShown(
            IEnumerable<AchievementTally> tallies,
            IEnumerable<string> shown)
        {
            var perTag = (shown ?? Enumerable.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(key => key, _ => (Unlocked: 0, Total: 0), StringComparer.OrdinalIgnoreCase);
            var unlocked = 0;
            var total = 0;
            foreach (var tally in tallies ?? Enumerable.Empty<AchievementTally>())
            {
                var any = false;
                foreach (var key in tally.Tags)
                {
                    if (perTag.TryGetValue(key, out var count))
                    {
                        perTag[key] = (count.Unlocked + tally.Unlocked, count.Total + tally.Total);
                        any = true;
                    }
                }

                if (any)
                {
                    unlocked += tally.Unlocked;
                    total += tally.Total;
                }
            }

            return (perTag, unlocked, total);
        }

        public void AddAll(IEnumerable<AchievementTally> tallies)
        {
            foreach (var tally in tallies ?? Enumerable.Empty<AchievementTally>())
            {
                Shift(new Facet(tally.Key, tally.Rarity, tally.Trophy, tally.IsCapstone), tally.Unlocked, tally.Total);
            }
        }

        /// <summary>The tallies in a stable order; null when the game counts no achievement.</summary>
        public AchievementTally[] Build()
        {
            if (_counts.Count == 0)
            {
                return null;
            }

            return _counts
                .OrderBy(pair => pair.Key.Tags, StringComparer.OrdinalIgnoreCase)
                .ThenBy(pair => pair.Key.Rarity.HasValue ? (int)pair.Key.Rarity.Value : -1)
                .ThenBy(pair => pair.Key.Trophy ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(pair => pair.Key.IsCapstone)
                .Select(pair => new AchievementTally(
                    string.Intern(pair.Key.Tags),
                    pair.Key.Rarity,
                    pair.Key.Trophy,
                    pair.Key.IsCapstone,
                    pair.Value.Unlocked,
                    pair.Value.Total))
                .ToArray();
        }

        // The set an achievement is counted under: its tags less the derived ones, in the tag
        // helper's canonical order so the same set always has the same key. Empty for none.
        private static string SetKey(string tags) =>
            AchievementTagHelper.Combine(
                AchievementTagHelper.GetComponents(tags).Where(key => !AchievementTagHelper.IsDerivedTag(key))) ?? string.Empty;

        private void Shift(Facet facet, int unlocked, int total)
        {
            _counts.TryGetValue(facet, out var current);
            var next = (Math.Max(0, current.Unlocked + unlocked), Math.Max(0, current.Total + total));
            if (next.Item2 == 0)
            {
                _counts.Remove(facet);
            }
            else
            {
                _counts[facet] = next;
            }
        }

        private readonly struct Facet : IEquatable<Facet>
        {
            public Facet(string tags, RarityTier? rarity, string trophy, bool isCapstone)
            {
                Tags = tags ?? string.Empty;
                Rarity = rarity;
                Trophy = trophy;
                IsCapstone = isCapstone;
            }

            public string Tags { get; }

            public RarityTier? Rarity { get; }

            public string Trophy { get; }

            public bool IsCapstone { get; }

            public bool Equals(Facet other) =>
                string.Equals(Tags, other.Tags, StringComparison.OrdinalIgnoreCase) &&
                Rarity == other.Rarity &&
                string.Equals(Trophy, other.Trophy, StringComparison.Ordinal) &&
                IsCapstone == other.IsCapstone;

            public override bool Equals(object obj) => obj is Facet other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(Tags);
                    hash = hash * 31 + (Rarity.HasValue ? (int)Rarity.Value + 1 : 0);
                    hash = hash * 31 + (Trophy?.GetHashCode() ?? 0);
                    return hash * 31 + (IsCapstone ? 1 : 0);
                }
            }
        }
    }
}
