using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.GameCustomData
{
    /// <summary>
    /// The parts of a game's custom data an achievement edit can move, one per member that is
    /// written as a whole.
    /// </summary>
    /// <remarks>
    /// Deliberately not every member of the record. Three kinds are left out:
    ///
    /// The legacy note and icon maps are projections, not state: the normalizer re-derives them
    /// from <see cref="GameCustomDataFile.AchievementOverrides"/> on every save, so recording them
    /// would journal three copies of one change and restoring them would achieve nothing.
    ///
    /// The manual link is excluded for now. Its write is only half the story, because the link is
    /// re-projected onto the achievement cache by the caller, and unlock state held there is the
    /// one thing with no provider to re-fetch it from.
    ///
    /// Everything the editor cannot reach - refresh exclusions, notification appearance, provider
    /// overrides, the game summary category - is excluded so an undo can never reverse a write
    /// that came from another surface.
    /// </remarks>
    public enum GameCustomDataFacet
    {
        Capstones,
        AchievementOrder,
        CategoryOverrides,
        CategoryTypeOverrides,
        CategoryOrder,
        CategoryImageOverrides,
        FilteredApiNames,
        SummaryFilteredApiNames,
        GoalApiNames,
        AchievementOverrides,
        CustomAchievements,
        ManualCapstoneApiName,
        CustomProviderId,
        UseSeparateLockedIcons
    }

    /// <summary>
    /// One facet's value on each side of a write, deep-copied so nothing it holds can be mutated
    /// underneath it afterwards.
    /// </summary>
    public sealed class GameCustomDataFacetPatch
    {
        internal GameCustomDataFacetPatch(GameCustomDataFacet facet, object before, object after)
        {
            Facet = facet;
            Before = before;
            After = after;
        }

        public GameCustomDataFacet Facet { get; }

        internal object Before { get; }

        internal object After { get; }

        /// <summary>How much this patch holds, for bounding a history's total size.</summary>
        public int ElementCount =>
            GameCustomDataFacets.CountElements(Facet, Before) +
            GameCustomDataFacets.CountElements(Facet, After);

        /// <summary>Puts the facet back to the value it had before the write.</summary>
        public void ApplyBefore(GameCustomDataFile target)
        {
            GameCustomDataFacets.Write(Facet, target, Before);
        }

        /// <summary>Re-applies the value the write produced.</summary>
        public void ApplyAfter(GameCustomDataFile target)
        {
            GameCustomDataFacets.Write(Facet, target, After);
        }
    }

    /// <summary>
    /// How each facet is read, written and compared. One table so a facet cannot be diffed one way
    /// and restored another, and so adding a facet is a single entry rather than four edits.
    /// </summary>
    internal static class GameCustomDataFacets
    {
        private sealed class Definition
        {
            public Func<GameCustomDataFile, object> Read;
            public Action<GameCustomDataFile, object> Write;
            public Func<object, object, bool> Equal;
            public Func<object, int> Count;
        }

        /// <summary>
        /// Capstones and the flag that says the list is authoritative move together: the flag is
        /// what separates "this game has no capstones" from "fall back to the provider", so
        /// restoring the list without it would change the meaning of an empty list.
        /// </summary>
        private sealed class CapstoneState
        {
            public bool Materialized;
            public List<CapstoneAssignment> Assignments;
        }

        private static readonly Dictionary<GameCustomDataFacet, Definition> Definitions =
            new Dictionary<GameCustomDataFacet, Definition>
            {
                [GameCustomDataFacet.Capstones] = new Definition
                {
                    Read = data => new CapstoneState
                    {
                        Materialized = data.CapstonesMaterialized,
                        Assignments = CloneList(data.Capstones, item => item?.Clone())
                    },
                    Write = (data, value) =>
                    {
                        var state = (CapstoneState)value;
                        data.CapstonesMaterialized = state?.Materialized ?? false;
                        data.Capstones = CloneList(state?.Assignments, item => item?.Clone());
                    },
                    Equal = (left, right) =>
                    {
                        var a = (CapstoneState)left;
                        var b = (CapstoneState)right;
                        if (a == null || b == null)
                        {
                            return a == null && b == null;
                        }

                        return a.Materialized == b.Materialized &&
                            SequenceEqual(a.Assignments, b.Assignments, (x, y) => JsonEquals(x, y));
                    },
                    Count = value => ((CapstoneState)value)?.Assignments?.Count ?? 0
                },

                [GameCustomDataFacet.AchievementOrder] = StringList(
                    data => data.AchievementOrder,
                    (data, value) => data.AchievementOrder = value),

                [GameCustomDataFacet.CategoryOverrides] = StringMap(
                    data => data.AchievementCategoryOverrides,
                    (data, value) => data.AchievementCategoryOverrides = value),

                [GameCustomDataFacet.CategoryTypeOverrides] = StringMap(
                    data => data.AchievementCategoryTypeOverrides,
                    (data, value) => data.AchievementCategoryTypeOverrides = value),

                [GameCustomDataFacet.CategoryOrder] = StringList(
                    data => data.AchievementCategoryOrder,
                    (data, value) => data.AchievementCategoryOrder = value),

                [GameCustomDataFacet.CategoryImageOverrides] = new Definition
                {
                    Read = data => CloneMap(data.AchievementCategoryImageOverrides, item => item?.Clone()),
                    Write = (data, value) => data.AchievementCategoryImageOverrides =
                        CloneMap((Dictionary<string, CategoryImageOverrideData>)value, item => item?.Clone()),
                    // Per entry, not over the whole map: these are rebuilt from the rows, so two
                    // maps holding the same entries in a different insertion order are the same
                    // state, and serializing the map whole would call that a change.
                    Equal = (left, right) => MapEquals(
                        (Dictionary<string, CategoryImageOverrideData>)left,
                        (Dictionary<string, CategoryImageOverrideData>)right,
                        (x, y) => JsonEquals(x, y)),
                    Count = value => ((Dictionary<string, CategoryImageOverrideData>)value)?.Count ?? 0
                },

                [GameCustomDataFacet.FilteredApiNames] = StringList(
                    data => data.FilteredAchievementApiNames,
                    (data, value) => data.FilteredAchievementApiNames = value),

                [GameCustomDataFacet.SummaryFilteredApiNames] = StringList(
                    data => data.SummaryFilteredAchievementApiNames,
                    (data, value) => data.SummaryFilteredAchievementApiNames = value),

                [GameCustomDataFacet.GoalApiNames] = StringList(
                    data => data.GoalAchievementApiNames,
                    (data, value) => data.GoalAchievementApiNames = value),

                [GameCustomDataFacet.AchievementOverrides] = new Definition
                {
                    Read = data => CloneMap(data.AchievementOverrides, item => item?.Clone()),
                    Write = (data, value) => data.AchievementOverrides =
                        CloneMap((Dictionary<string, AchievementOverride>)value, item => item?.Clone()),
                    // Per entry, for the same reason as the map above.
                    Equal = (left, right) => MapEquals(
                        (Dictionary<string, AchievementOverride>)left,
                        (Dictionary<string, AchievementOverride>)right,
                        (x, y) => JsonEquals(x, y)),
                    Count = value => ((Dictionary<string, AchievementOverride>)value)?.Count ?? 0
                },

                [GameCustomDataFacet.CustomAchievements] = new Definition
                {
                    Read = data => CloneList(data.CustomAchievements, item => item?.Clone()),
                    Write = (data, value) => data.CustomAchievements =
                        CloneList((List<CustomAchievementDefinition>)value, item => item?.Clone()),
                    // A list, so position is part of the state and order is compared with it.
                    Equal = (left, right) => SequenceEqual(
                        (List<CustomAchievementDefinition>)left,
                        (List<CustomAchievementDefinition>)right,
                        (x, y) => JsonEquals(x, y)),
                    Count = value => ((List<CustomAchievementDefinition>)value)?.Count ?? 0
                },

                [GameCustomDataFacet.ManualCapstoneApiName] = Scalar(
                    data => data.ManualCapstoneApiName,
                    (data, value) => data.ManualCapstoneApiName = (string)value),

                [GameCustomDataFacet.CustomProviderId] = Scalar(
                    data => data.CustomProviderId,
                    (data, value) => data.CustomProviderId = (string)value),

                [GameCustomDataFacet.UseSeparateLockedIcons] = Scalar(
                    data => data.UseSeparateLockedIconsOverride,
                    (data, value) => data.UseSeparateLockedIconsOverride = (bool?)value)
            };

        /// <summary>Every facet, in the order they are declared.</summary>
        public static IEnumerable<GameCustomDataFacet> All =>
            Enum.GetValues(typeof(GameCustomDataFacet)).Cast<GameCustomDataFacet>();

        public static object Read(GameCustomDataFacet facet, GameCustomDataFile data)
        {
            return data == null ? null : Definitions[facet].Read(data);
        }

        public static void Write(GameCustomDataFacet facet, GameCustomDataFile data, object value)
        {
            if (data != null)
            {
                Definitions[facet].Write(data, value);
            }
        }

        public static bool AreEqual(GameCustomDataFacet facet, object left, object right)
        {
            return Definitions[facet].Equal(left, right);
        }

        public static int CountElements(GameCustomDataFacet facet, object value)
        {
            return value == null ? 0 : Definitions[facet].Count(value);
        }

        private static Definition StringList(
            Func<GameCustomDataFile, List<string>> read,
            Action<GameCustomDataFile, List<string>> write)
        {
            return new Definition
            {
                Read = data => read(data) != null ? new List<string>(read(data)) : null,
                Write = (data, value) =>
                {
                    var list = (List<string>)value;
                    write(data, list != null ? new List<string>(list) : null);
                },
                Equal = (left, right) => SequenceEqual(
                    (List<string>)left,
                    (List<string>)right,
                    (a, b) => string.Equals(a, b, StringComparison.Ordinal)),
                Count = value => ((List<string>)value)?.Count ?? 0
            };
        }

        private static Definition StringMap(
            Func<GameCustomDataFile, Dictionary<string, string>> read,
            Action<GameCustomDataFile, Dictionary<string, string>> write)
        {
            return new Definition
            {
                Read = data => CopyStringMap(read(data)),
                Write = (data, value) => write(data, CopyStringMap((Dictionary<string, string>)value)),
                Equal = (left, right) => MapEquals(
                    (Dictionary<string, string>)left,
                    (Dictionary<string, string>)right,
                    (a, b) => string.Equals(a, b, StringComparison.Ordinal)),
                Count = value => ((Dictionary<string, string>)value)?.Count ?? 0
            };
        }

        private static Definition Scalar(
            Func<GameCustomDataFile, object> read,
            Action<GameCustomDataFile, object> write)
        {
            return new Definition
            {
                Read = read,
                Write = write,
                Equal = (left, right) => Equals(left, right),
                Count = _ => 1
            };
        }

        private static Dictionary<string, string> CopyStringMap(Dictionary<string, string> value)
        {
            return value != null
                ? new Dictionary<string, string>(value, StringComparer.OrdinalIgnoreCase)
                : null;
        }

        private static Dictionary<string, T> CloneMap<T>(Dictionary<string, T> value, Func<T, T> clone)
        {
            if (value == null)
            {
                return null;
            }

            var copy = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in value)
            {
                copy[pair.Key] = clone(pair.Value);
            }

            return copy;
        }

        private static List<T> CloneList<T>(List<T> value, Func<T, T> clone)
        {
            return value?.ConvertAll(item => clone(item));
        }

        private static bool SequenceEqual<T>(List<T> left, List<T> right, Func<T, T, bool> equal)
        {
            if (left == null || right == null)
            {
                return left == null && right == null;
            }

            if (left.Count != right.Count)
            {
                return false;
            }

            for (var index = 0; index < left.Count; index++)
            {
                if (!equal(left[index], right[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool MapEquals<T>(
            Dictionary<string, T> left,
            Dictionary<string, T> right,
            Func<T, T, bool> equal)
        {
            if (left == null || right == null)
            {
                return left == null && right == null;
            }

            if (left.Count != right.Count)
            {
                return false;
            }

            foreach (var pair in left)
            {
                if (!right.TryGetValue(pair.Key, out var other) || !equal(pair.Value, other))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Whether two facet values are the same, compared as serialized state.
        /// </summary>
        /// <remarks>
        /// Serialized rather than field by field, deliberately. These records gain fields over
        /// time, and a hand-written comparison silently stops noticing the new ones - which for a
        /// journal means an edit that is recorded as "nothing changed" and cannot be undone. The
        /// values are one game's worth of data and this runs once per write, so the cost is not
        /// worth trading that away for.
        /// </remarks>
        private static bool JsonEquals(object left, object right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null)
            {
                return false;
            }

            return string.Equals(
                JsonConvert.SerializeObject(left),
                JsonConvert.SerializeObject(right),
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Works out which facets a write moved.
    /// </summary>
    public static class GameCustomDataFacetDiffer
    {
        /// <summary>
        /// The facets that differ between the record before a write and the record after it.
        /// Empty when the write changed nothing this journal tracks, which is the common case for
        /// writes from elsewhere in the plugin.
        /// </summary>
        /// <remarks>
        /// Both sides are expected to be normalized records, as the store's own save path produces,
        /// so a difference here is a real change rather than a formatting one.
        /// </remarks>
        public static IReadOnlyList<GameCustomDataFacetPatch> Diff(
            GameCustomDataFile previous,
            GameCustomDataFile persisted)
        {
            var patches = new List<GameCustomDataFacetPatch>();
            if (previous == null || persisted == null)
            {
                return patches;
            }

            foreach (var facet in GameCustomDataFacets.All)
            {
                var before = GameCustomDataFacets.Read(facet, previous);
                var after = GameCustomDataFacets.Read(facet, persisted);
                if (!GameCustomDataFacets.AreEqual(facet, before, after))
                {
                    patches.Add(new GameCustomDataFacetPatch(facet, before, after));
                }
            }

            return patches;
        }
    }
}
