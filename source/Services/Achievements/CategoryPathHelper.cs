using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// Category labels carry an optional nesting path inside the single label string, so every store
    /// that keys on a label - the definition Category column, the per-game order list, the art
    /// overrides, the game-summary selection - keeps working without a new field or a migration.
    ///
    /// <see cref="Separator"/> is internal: it is never shown to a user and never typed by one.
    /// Nesting is created structurally (indent/outdent, drag) and rendered with
    /// <see cref="DisplaySeparator"/>.
    ///
    /// Every member degenerates to today's flat behavior for a separator-free label:
    /// <see cref="NormalizePath"/> matches AchievementCategoryTypeHelper.NormalizeCategoryOrDefault,
    /// the ancestor walks are empty, and <see cref="IsDescendantOf"/> is false.
    /// </summary>
    internal static class CategoryPathHelper
    {
        public const string Separator = "::";
        public const string DisplaySeparator = " > ";

        /// <summary>
        /// Depth ceiling. Overflow segments are folded into the last kept segment rather than
        /// dropped: dropping them would silently merge distinct nodes and strand their achievements
        /// in a parent. Unreachable in practice - nesting is one level per explicit gesture.
        /// </summary>
        public const int MaxDepth = 8;

        private const int PathCacheCapacity = 512;

        // Pure functions of the input string, so entries never need invalidation. Both are on the
        // per-row path of library-wide rebuilds; Split is the primitive the rest are built on.
        private static readonly ConcurrentDictionary<string, string> NormalizeCache =
            new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        private static readonly ConcurrentDictionary<string, string[]> SplitCache =
            new ConcurrentDictionary<string, string[]>(StringComparer.Ordinal);

        private static readonly string[] SeparatorArray = { Separator };

        /// <summary>
        /// Canonical storage form: segments trimmed, empty segments dropped, depth capped, and a
        /// path rooted at the Default bucket collapsed to that root alone. Idempotent, so the
        /// custom-data normalizer can run it on both read and write and nothing downstream has to
        /// re-normalize.
        /// </summary>
        public static string NormalizePath(string rawValue)
        {
            var key = rawValue ?? string.Empty;
            if (NormalizeCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var result = BuildNormalizedPath(key);
            if (NormalizeCache.Count < PathCacheCapacity)
            {
                NormalizeCache.TryAdd(key, result);
            }

            return result;
        }

        private static string BuildNormalizedPath(string rawValue)
        {
            var segments = rawValue
                .Split(SeparatorArray, StringSplitOptions.None)
                .Select(segment => segment.Trim())
                .Where(segment => !string.IsNullOrWhiteSpace(segment))
                .ToList();

            if (segments.Count == 0)
            {
                return AchievementCategoryTypeHelper.DefaultCategoryLabel;
            }

            // The Default bucket is a root-only sentinel: CategoryDefaultImageResolver treats it as
            // having no provider art, and rename/merge refuse it, so a child under it would be
            // unreachable. Keep the root's own text so existing casing is untouched.
            if (segments.Count > 1 &&
                string.Equals(segments[0], AchievementCategoryTypeHelper.DefaultCategoryLabel, StringComparison.OrdinalIgnoreCase))
            {
                return segments[0];
            }

            if (segments.Count > MaxDepth)
            {
                var folded = string.Join(" - ", segments.Skip(MaxDepth - 1));
                segments = segments.Take(MaxDepth - 1).ToList();
                segments.Add(folded);
            }

            return string.Join(Separator, segments);
        }

        /// <summary>Canonical segments of a path, root first. Always at least one segment.</summary>
        public static IReadOnlyList<string> Split(string rawValue)
        {
            var normalized = NormalizePath(rawValue);
            if (SplitCache.TryGetValue(normalized, out var cached))
            {
                return cached;
            }

            // Already canonical, so a plain split cannot produce empty segments.
            var segments = normalized.Split(SeparatorArray, StringSplitOptions.None);
            if (SplitCache.Count < PathCacheCapacity)
            {
                SplitCache.TryAdd(normalized, segments);
            }

            return segments;
        }

        public static string Join(IEnumerable<string> segments)
        {
            if (segments == null)
            {
                return AchievementCategoryTypeHelper.DefaultCategoryLabel;
            }

            return NormalizePath(string.Join(Separator, segments));
        }

        public static string Join(string parentPath, string childSegment)
        {
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return NormalizePath(childSegment);
            }

            return NormalizePath(string.Concat(parentPath, Separator, childSegment));
        }

        /// <summary>Depth of a path, 1 for a root label.</summary>
        public static int GetDepth(string rawValue)
        {
            return Split(rawValue).Count;
        }

        /// <summary>The last segment - what the user edits and what a drilled view titles itself.</summary>
        public static string GetLeafName(string rawValue)
        {
            var segments = Split(rawValue);
            return segments[segments.Count - 1];
        }

        /// <summary>The parent path, or null for a root label.</summary>
        public static string GetParentPath(string rawValue)
        {
            var segments = Split(rawValue);
            return segments.Count <= 1
                ? null
                : string.Join(Separator, segments.Take(segments.Count - 1));
        }

        /// <summary>Ancestors then self, root first: "A::B::C" gives A, A::B, A::B::C.</summary>
        public static IReadOnlyList<string> EnumerateSelfAndAncestors(string rawValue)
        {
            var segments = Split(rawValue);
            var result = new List<string>(segments.Count);
            for (var i = 1; i <= segments.Count; i++)
            {
                result.Add(string.Join(Separator, segments.Take(i)));
            }

            return result;
        }

        /// <summary>Ancestors only, root first. Empty for a root label.</summary>
        public static IReadOnlyList<string> EnumerateAncestors(string rawValue)
        {
            var selfAndAncestors = EnumerateSelfAndAncestors(rawValue);
            if (selfAndAncestors.Count <= 1)
            {
                return Array.Empty<string>();
            }

            return selfAndAncestors.Take(selfAndAncestors.Count - 1).ToList();
        }

        public static bool IsSame(string a, string b)
        {
            return string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Strict descendant test. Compares on the separator boundary, so "A::BC" is not a
        /// descendant of "A::B" and "AB" is not a descendant of "A".
        /// </summary>
        public static bool IsDescendantOf(string candidate, string ancestor)
        {
            var normalizedCandidate = NormalizePath(candidate);
            var normalizedAncestor = NormalizePath(ancestor);
            var prefix = string.Concat(normalizedAncestor, Separator);

            return normalizedCandidate.Length > prefix.Length &&
                   normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsSelfOrDescendantOf(string candidate, string ancestor)
        {
            return IsSame(candidate, ancestor) || IsDescendantOf(candidate, ancestor);
        }

        /// <summary>Moves a path under a new parent, keeping its leaf name. A null parent makes it a root.</summary>
        public static string Reparent(string path, string newParentPath)
        {
            return Join(newParentPath, GetLeafName(path));
        }

        /// <summary>
        /// Rewrites <paramref name="path"/> when it is <paramref name="sourcePath"/> or sits beneath
        /// it, so one call repoints a whole subtree. Unrelated paths are returned normalized but
        /// otherwise untouched.
        /// </summary>
        public static string RewritePrefix(string path, string sourcePath, string targetPath)
        {
            var normalizedPath = NormalizePath(path);
            var normalizedSource = NormalizePath(sourcePath);
            var normalizedTarget = NormalizePath(targetPath);

            if (string.Equals(normalizedPath, normalizedSource, StringComparison.OrdinalIgnoreCase))
            {
                return normalizedTarget;
            }

            if (!IsDescendantOf(normalizedPath, normalizedSource))
            {
                return normalizedPath;
            }

            var tail = normalizedPath.Substring(normalizedSource.Length + Separator.Length);
            return NormalizePath(string.Concat(normalizedTarget, Separator, tail));
        }

        /// <summary>
        /// The immediate children of <paramref name="parentPath"/> present in
        /// <paramref name="labels"/>, in first-seen order. A null or blank parent yields the roots.
        /// A label deeper than one level contributes its intermediate node, so a node with no
        /// achievements of its own still appears.
        /// </summary>
        public static List<string> GetChildPaths(IEnumerable<string> labels, string parentPath)
        {
            var result = new List<string>();
            if (labels == null)
            {
                return result;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var isRootLevel = string.IsNullOrWhiteSpace(parentPath);
            var parentDepth = isRootLevel ? 0 : GetDepth(parentPath);

            foreach (var label in labels)
            {
                var segments = Split(label);
                if (segments.Count <= parentDepth)
                {
                    continue;
                }

                if (!isRootLevel && !IsSelfOrDescendantOf(label, parentPath))
                {
                    continue;
                }

                var childPath = string.Join(Separator, segments.Take(parentDepth + 1));
                if (seen.Add(childPath))
                {
                    result.Add(childPath);
                }
            }

            return result;
        }

        /// <summary>True when a value carries nesting - used to reject the separator in typed input.</summary>
        public static bool ContainsSeparator(string rawValue)
        {
            return (rawValue ?? string.Empty).IndexOf(Separator, StringComparison.Ordinal) >= 0;
        }

        /// <summary>Full path in display form: "DLC::Season Pass" reads as "DLC &gt; Season Pass".</summary>
        public static string ToDisplayPath(string rawValue)
        {
            return string.Join(DisplaySeparator, Split(rawValue));
        }

        /// <summary>Leaf name only, for surfaces that show ancestry separately (a breadcrumb).</summary>
        public static string ToDisplayLeaf(string rawValue)
        {
            return GetLeafName(rawValue);
        }
    }
}
