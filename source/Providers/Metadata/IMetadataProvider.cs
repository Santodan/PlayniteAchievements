using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Metadata
{
    /// <summary>The achievement fields a metadata provider can supply to games another provider services.</summary>
    [Flags]
    public enum MetadataField
    {
        None = 0,
        Icons = 1,
        Rarity = 2,
        Categories = 4,
        CategoryArt = 8,
        CapstoneArt = 16
    }

    /// <summary>How a metadata source's categories combine with the servicing provider's.</summary>
    public enum MetadataCategoryMode
    {
        Off = 0,

        /// <summary>Categories the provider named are kept; the source categorizes the rest.</summary>
        Merge = 1,

        /// <summary>The source's categories replace the provider's.</summary>
        Override = 2
    }

    /// <summary>
    /// A secondary source of achievement metadata. It never services a game; the overlay stage asks
    /// it for its rows for a game the servicing provider just refreshed, matches them to that game's
    /// achievements, and copies the fields the user chose. Discovered by reflection in
    /// <see cref="ProviderRegistry"/>, with constructor dependencies resolved the same way as data providers.
    /// </summary>
    internal interface IMetadataProvider
    {
        /// <summary>Stable key, stored in settings preference lists and per-game links.</summary>
        string Key { get; }

        /// <summary>Localization key of the name shown in settings and on the per-game link row.</summary>
        string DisplayNameKey { get; }

        MetadataField SupportedFields { get; }

        /// <summary>
        /// True when this source's data is already the servicing provider's own (SteamHunters for
        /// Steam, PlayStation for PSN and the PlayStation emulators), so the overlay skips it.
        /// </summary>
        bool IsNativeFor(string servicingProviderKey);

        /// <summary>
        /// Opens whatever session the provider needs for one refresh run (auth probe, cookie
        /// session). Called at most once per run, before the first link or fetch.
        /// Returns false when the provider cannot serve this run.
        /// </summary>
        Task<bool> BeginRunAsync(CancellationToken ct);

        /// <summary>Releases what <see cref="BeginRunAsync"/> opened.</summary>
        void EndRun();

        /// <summary>
        /// Finds this source's id for the game, or null when there is no confident match.
        /// Only called when the game has no stored link for this provider.
        /// </summary>
        Task<MetadataSourceLink> ResolveLinkAsync(MetadataLinkRequest request, CancellationToken ct);

        /// <summary>The source's rows for a linked game, or null when the fetch failed.</summary>
        Task<AchievementSourceSet> FetchAsync(MetadataLinkRequest request, string linkId, CancellationToken ct);
    }

    /// <summary>What a metadata provider knows about the game it is asked to link or fetch for.</summary>
    internal sealed class MetadataLinkRequest
    {
        public MetadataLinkRequest(Game game, GameAchievementData data)
        {
            Game = game;
            Data = data;
        }

        public Game Game { get; }

        /// <summary>The servicing provider's refreshed payload, before any overlay.</summary>
        public GameAchievementData Data { get; }

        public string ServicingProviderKey => Data?.ProviderKey;
    }

    /// <summary>A resolved source id with a label for display.</summary>
    internal sealed class MetadataSourceLink
    {
        public MetadataSourceLink(string id, string label = null, bool foundByName = false)
        {
            Id = id;
            Label = label;
            FoundByName = foundByName;
        }

        public string Id { get; }

        public string Label { get; }

        /// <summary>
        /// Found by searching the game's name rather than an id the game carries, so the overlay
        /// keeps it only when enough of the game's achievements match the source's.
        /// </summary>
        public bool FoundByName { get; }
    }

    /// <summary>
    /// One source's achievements for one game, in the plugin's own row shape so the shared matcher
    /// and the overlay read them like provider rows.
    /// </summary>
    internal sealed class AchievementSourceSet
    {
        public AchievementSourceSet(string sourceKey, string linkId, IList<AchievementDetail> achievements)
        {
            SourceKey = sourceKey;
            LinkId = linkId;
            Achievements = achievements ?? new List<AchievementDetail>();
        }

        public string SourceKey { get; }

        public string LinkId { get; }

        public IList<AchievementDetail> Achievements { get; }

        /// <summary>True when the rows carry the servicing provider's ApiNames, so an exact join runs first.</summary>
        public bool SharesApiNames { get; set; }

        /// <summary>
        /// True when the rows use developer-chosen achievement IDs that a port to another platform
        /// may reuse (Steam's), so the overlay checks the overlap and joins by ID when most coincide.
        /// False for generated IDs such as trophy numbers.
        /// </summary>
        public bool ApiNamesMayBeShared { get; set; }

        /// <summary>Category label to image URL, for sources that supply category art.</summary>
        public IDictionary<string, string> CategoryArtUrls { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
