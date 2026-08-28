using System;
using System.Collections.Generic;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Services.Images
{
    /// <summary>
    /// How a resolved category art path will be rendered. The only thing that legitimately differs
    /// between the plugin's own surfaces and the theme surface.
    /// </summary>
    internal enum CategoryArtDisplayMode
    {
        /// <summary>
        /// A plain filesystem path. The theme surface is a public contract consumed by arbitrary
        /// theme XAML, which may bind it straight to an Image, so it must never carry the plugin's
        /// cache-bust encoding.
        /// </summary>
        FilePath = 0,

        /// <summary>
        /// A path carrying the plugin's cache-bust token, understood by MemoryImageService and
        /// AnimatedImageHelper. Category art is overwritten in place at a stable managed path, so
        /// without the token a replaced image keeps serving the stale bitmap.
        /// </summary>
        PluginImagePipeline = 1
    }

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
    /// The one path that resolves a category label's art, for every surface.
    ///
    /// Art is looked up as override-then-provider-default at the label's own level, and when that
    /// yields nothing the walk continues up the path, so a subcategory shows its parent's image
    /// rather than dropping straight to the game icon. For a label with no nesting the walk is
    /// empty and the chain is the flat override-then-default lookup it has always been.
    ///
    /// The effective label is probed before the provider label because an achievement moved into
    /// another category by a merge keeps its original provider label while its effective label
    /// points at the target. Probing effective-first is what makes a merged category show the
    /// target's art.
    /// </summary>
    internal static class CategoryArtChainResolver
    {
        // Unit separator: cannot occur in a category label or a file path, so memo keys built by
        // concatenation cannot collide.
        private const string KeySeparator = "\u001f";

        /// <summary>
        /// Turns a stored override value into a display path: managed-path resolution, plus the
        /// cache-bust token when the caller renders through the plugin's image pipeline.
        ///
        /// Installed at startup. An accessor rather than a direct service reference keeps this file
        /// compilable in the test project; when absent the stored value is used as-is.
        /// </summary>
        internal static Func<string, Guid?, CategoryArtDisplayMode, string> OverrideDisplayPathResolver { get; set; }

        public static string Resolve(
            Guid? gameId,
            string effectiveLabel,
            string providerLabel,
            IReadOnlyDictionary<string, CategoryImageOverrideData> imageOverrides,
            CategoryArtDisplayMode displayMode,
            CategoryArtChainMemo memo = null)
        {
            return Resolve(gameId, effectiveLabel, providerLabel, imageOverrides, displayMode, memo, out _);
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
            CategoryArtDisplayMode displayMode,
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
                // Only the deepest level has a provider label to fall back to; an ancestor is a
                // node in the path, not an achievement's own category.
                var providerFallback = i == levels.Count - 1 ? normalizedProvider : null;
                perLevel[i] = ResolveLevel(gameId, levels[i], providerFallback, imageOverrides, displayMode, memo);
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

        private static string ResolveLevel(
            Guid? gameId,
            string label,
            string providerFallback,
            IReadOnlyDictionary<string, CategoryImageOverrideData> imageOverrides,
            CategoryArtDisplayMode displayMode,
            CategoryArtChainMemo memo)
        {
            var key = memo == null
                ? null
                : string.Concat(label, KeySeparator, providerFallback, KeySeparator, (int)displayMode);

            if (key != null && memo.TryGet(key, out var cached))
            {
                return cached;
            }

            var art = ResolveOverrideArt(label, imageOverrides, gameId, displayMode)
                ?? CategoryDefaultImageResolver.Resolve(gameId, label);

            if (art == null && !string.IsNullOrWhiteSpace(providerFallback))
            {
                art = CategoryDefaultImageResolver.Resolve(gameId, providerFallback);
            }

            if (key != null)
            {
                memo.Set(key, art);
            }

            return art;
        }

        private static string ResolveOverrideArt(
            string label,
            IReadOnlyDictionary<string, CategoryImageOverrideData> imageOverrides,
            Guid? gameId,
            CategoryArtDisplayMode displayMode)
        {
            if (string.IsNullOrWhiteSpace(label) ||
                imageOverrides == null ||
                !imageOverrides.TryGetValue(label, out var imageOverride) ||
                imageOverride == null)
            {
                return null;
            }

            var stored = NormalizeStoredValue(imageOverride.Art);
            if (stored == null)
            {
                return null;
            }

            var resolver = OverrideDisplayPathResolver;
            return resolver == null ? stored : resolver(stored, gameId, displayMode);
        }

        internal static string NormalizeStoredValue(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }
    }
}
