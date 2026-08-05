using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Captures
{
    /// <summary>
    /// The kinds of capture the unlock pipeline can produce for a single achievement. The three
    /// screenshot variants mirror <c>ScreenshotVariants</c> (Clean / WithToast / Framed); Video is
    /// the separate unlock-recording clip. Values are ordered for the gallery type selector.
    /// </summary>
    public enum CaptureVariant
    {
        Clean = 0,
        Notification = 1,
        Framed = 2,
        Video = 3
    }

    /// <summary>
    /// A single capture file on disk, already classified back into its variant. Read-only; the
    /// capture pipeline never round-trips these back to disk.
    /// </summary>
    public sealed class CaptureItem
    {
        public CaptureItem(string filePath, CaptureVariant variant, int number, string achievementStem)
        {
            FilePath = filePath;
            Variant = variant;
            Number = number;
            AchievementStem = achievementStem;
        }

        /// <summary>Absolute path to the capture file.</summary>
        public string FilePath { get; }

        public CaptureVariant Variant { get; }

        /// <summary>The 1-based achievement position parsed from the filename's NNN prefix (0 when absent).</summary>
        public int Number { get; }

        /// <summary>Sanitized achievement-name stem parsed from the filename (variant suffix removed).</summary>
        public string AchievementStem { get; }

        public bool IsVideo => Variant == CaptureVariant.Video;
    }

    /// <summary>
    /// All captures that belong to one achievement (identified by its sanitized name stem), grouped
    /// by variant. A variant may hold more than one item when the writer had to disambiguate a
    /// filename collision (the " (2)" suffix).
    /// </summary>
    public sealed class AchievementCaptureGroup
    {
        private readonly IReadOnlyDictionary<CaptureVariant, IReadOnlyList<CaptureItem>> _byVariant;

        public AchievementCaptureGroup(
            int number,
            string achievementStem,
            IReadOnlyList<CaptureItem> items)
        {
            Number = number;
            AchievementStem = achievementStem;
            Items = items ?? Array.Empty<CaptureItem>();
            _byVariant = Items
                .GroupBy(i => i.Variant)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<CaptureItem>)g.ToList());
        }

        public int Number { get; }

        public string AchievementStem { get; }

        /// <summary>All items for this achievement across every variant, in discovery order.</summary>
        public IReadOnlyList<CaptureItem> Items { get; }

        public bool HasVariant(CaptureVariant variant) => _byVariant.ContainsKey(variant);

        public IReadOnlyList<CaptureItem> ForVariant(CaptureVariant variant) =>
            _byVariant.TryGetValue(variant, out var list) ? list : Array.Empty<CaptureItem>();
    }

    /// <summary>
    /// The complete parsed capture library for a single game: an achievement-ordered list of groups
    /// plus lookups used by the grid presence check and the gallery viewer.
    /// </summary>
    public sealed class GameCaptureSet
    {
        public static readonly GameCaptureSet Empty =
            new GameCaptureSet(Array.Empty<AchievementCaptureGroup>());

        private readonly HashSet<string> _stems;

        public GameCaptureSet(IReadOnlyList<AchievementCaptureGroup> groups)
        {
            Groups = groups ?? Array.Empty<AchievementCaptureGroup>();
            _stems = new HashSet<string>(
                Groups.Select(g => g.AchievementStem),
                StringComparer.OrdinalIgnoreCase);
            AvailableVariants = Groups
                .SelectMany(g => g.Items.Select(i => i.Variant))
                .Distinct()
                .OrderBy(v => (int)v)
                .ToList();
        }

        public IReadOnlyList<AchievementCaptureGroup> Groups { get; }

        /// <summary>Variants present anywhere in this game, in selector order.</summary>
        public IReadOnlyList<CaptureVariant> AvailableVariants { get; }

        public bool HasAny => Groups.Count > 0;

        public bool ContainsAchievementStem(string sanitizedStem) =>
            !string.IsNullOrEmpty(sanitizedStem) && _stems.Contains(sanitizedStem);

        /// <summary>Achievement groups that have at least one capture of the given variant, in order.</summary>
        public IReadOnlyList<AchievementCaptureGroup> GroupsWithVariant(CaptureVariant variant) =>
            Groups.Where(g => g.HasVariant(variant)).ToList();
    }
}
