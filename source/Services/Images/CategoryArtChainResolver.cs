using System;
using System.Collections.Generic;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Services.Images
{
    /// <summary>
    /// Per-rebuild-pass memo for category art. Rows in one pass share a single game's image
    /// overrides and default-art directory, so the same labels resolve over and over; an ancestor
    /// in particular would otherwise be probed once per sibling subtree. Disk probing is the cost
    /// being avoided - <see cref="CategoryDefaultImageResolver"/> tries alternate extensions.
    ///
    /// Scope one memo to a single pass over a single game and discard it afterwards.
    /// </summary>
    internal sealed class CategoryArtChainMemo
    {
        private readonly Dictionary<string, string> _entries =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal bool TryGet(string key, out string art) => _entries.TryGetValue(key, out art);

        internal void Set(string key, string art) => _entries[key] = art;
    }

    /// <summary>
    /// Resolves the art for a category label, inheriting from its ancestors when the node itself
    /// has none - so a subcategory shows its parent's art rather than falling straight through to
    /// the game icon.
    ///
    /// For a label with no nesting the ancestor walk is empty, so each caller's chain is exactly
    /// what it was before. The callers do differ: the achievement grid probes the effective label's
    /// own default art before the provider label (so a merged category resolves the target's art)
    /// and routes overrides through the managed-icon service, while the theme runtime states probe
    /// only the provider label and take the stored override value as-is. Those differences are
    /// preserved rather than unified here.
    /// </summary>
    internal static class CategoryArtChainResolver
    {
        // Unit separator: cannot occur in a category label or a file path, so memo keys built by
        // concatenation cannot collide.
        private const string KeySeparator = "\u001f";

        public static string Resolve(
            Guid? gameId,
            string effectiveLabel,
            string providerLabel,
            IReadOnlyDictionary<string, CategoryImageOverrideData> imageOverrides,
            Func<string, string> resolveOverridePath,
            bool probeEffectiveLabelDefault,
            CategoryArtChainMemo memo = null)
        {
            return Resolve(
                gameId,
                effectiveLabel,
                providerLabel,
                imageOverrides,
                resolveOverridePath,
                probeEffectiveLabelDefault,
                memo,
                out _);
        }

        /// <param name="ancestorArtPaths">
        /// Art resolved at each level of the path, root first, null where a level has none. Index
        /// (depth - 1) is that level's own art, which is what lets an aggregate summary row find
        /// the art belonging to its own depth rather than a descendant's.
        /// </param>
        public static string Resolve(
            Guid? gameId,
            string effectiveLabel,
            string providerLabel,
            IReadOnlyDictionary<string, CategoryImageOverrideData> imageOverrides,
            Func<string, string> resolveOverridePath,
            bool probeEffectiveLabelDefault,
            CategoryArtChainMemo memo,
            out IReadOnlyList<string> ancestorArtPaths)
        {
            var normalizedLabel = CategoryPathHelper.NormalizePath(effectiveLabel);
            var normalizedProvider = CategoryPathHelper.NormalizePath(
                string.IsNullOrWhiteSpace(providerLabel) ? effectiveLabel : providerLabel);

            var levels = CategoryPathHelper.EnumerateSelfAndAncestors(normalizedLabel);
            var perLevel = new string[levels.Count];

            for (var i = 0; i < levels.Count; i++)
            {
                var isLeaf = i == levels.Count - 1;
                perLevel[i] = isLeaf
                    ? ResolveLeafArt(
                        gameId, levels[i], normalizedProvider, imageOverrides,
                        resolveOverridePath, probeEffectiveLabelDefault, memo)
                    : ResolveNodeArt(gameId, levels[i], imageOverrides, resolveOverridePath, memo);
            }

            ancestorArtPaths = perLevel;

            // Nearest wins: the node's own art, else the closest ancestor that has any.
            for (var i = perLevel.Length - 1; i >= 0; i--)
            {
                if (!string.IsNullOrWhiteSpace(perLevel[i]))
                {
                    return perLevel[i];
                }
            }

            return null;
        }

        private static string ResolveLeafArt(
            Guid? gameId,
            string label,
            string providerLabel,
            IReadOnlyDictionary<string, CategoryImageOverrideData> imageOverrides,
            Func<string, string> resolveOverridePath,
            bool probeEffectiveLabelDefault,
            CategoryArtChainMemo memo)
        {
            var key = memo == null
                ? null
                : string.Concat("leaf", KeySeparator, label, KeySeparator, providerLabel,
                    KeySeparator, probeEffectiveLabelDefault ? "1" : "0");

            if (key != null && memo.TryGet(key, out var cached))
            {
                return cached;
            }

            var art = ResolveOverrideArt(label, imageOverrides, resolveOverridePath);
            if (art == null && probeEffectiveLabelDefault)
            {
                art = CategoryDefaultImageResolver.Resolve(gameId, label);
            }

            if (art == null)
            {
                art = CategoryDefaultImageResolver.Resolve(gameId, providerLabel);
            }

            memo?.Set(key, art);
            return art;
        }

        private static string ResolveNodeArt(
            Guid? gameId,
            string label,
            IReadOnlyDictionary<string, CategoryImageOverrideData> imageOverrides,
            Func<string, string> resolveOverridePath,
            CategoryArtChainMemo memo)
        {
            var key = memo == null ? null : string.Concat("node", KeySeparator, label);
            if (key != null && memo.TryGet(key, out var cached))
            {
                return cached;
            }

            var art = ResolveOverrideArt(label, imageOverrides, resolveOverridePath)
                ?? CategoryDefaultImageResolver.Resolve(gameId, label);

            memo?.Set(key, art);
            return art;
        }

        private static string ResolveOverrideArt(
            string label,
            IReadOnlyDictionary<string, CategoryImageOverrideData> imageOverrides,
            Func<string, string> resolveOverridePath)
        {
            if (string.IsNullOrWhiteSpace(label) ||
                imageOverrides == null ||
                !imageOverrides.TryGetValue(label, out var imageOverride) ||
                imageOverride == null)
            {
                return null;
            }

            return resolveOverridePath == null
                ? NormalizeStoredValue(imageOverride.Art)
                : resolveOverridePath(imageOverride.Art);
        }

        internal static string NormalizeStoredValue(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }
    }
}
