using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.Linq;
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
        CapstoneArt = 16,
        TrophyTypes = 32,
        Points = 64,
        Tags = 128
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

        /// <summary>
        /// An example of the id a link holds ("exophase-game-id", "123456"), shown in the empty
        /// link box on Manage Achievements. Not localized: it is the id's shape, not a word.
        /// </summary>
        string LinkPlaceholder { get; }

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

    /// <summary>
    /// A source whose one link stands for several pages, which can say where it led for a game
    /// (the TrueGaming page on each site), shown on the game's link row.
    /// </summary>
    internal interface IMetadataLinkDetails
    {
        /// <summary>One line per page the link led to, or null when nothing is known yet.</summary>
        string DescribeLink(Guid gameId, string linkId);

        /// <summary>The link as the user sees and enters it, whatever form it is stored in.</summary>
        string ShownLink(string linkId);
    }

    /// <summary>A source whose link leads to pages a browser can open, shown as buttons on the game's link row.</summary>
    internal interface IMetadataPageLinks
    {
        /// <summary>The addresses of the pages the link uses for the game, in the source's order; empty when none is known.</summary>
        IReadOnlyList<string> PageUrls(Guid gameId, string linkId, string servicingProviderKey);
    }

    /// <summary>
    /// Several metadata sources that share one implementation, such as the Exophase pages of
    /// different platforms. Discovered by reflection like a provider; each of its providers is
    /// registered on its own. A provider type without a public constructor belongs to a family
    /// and is not created by itself.
    /// </summary>
    internal interface IMetadataProviderFamily
    {
        IReadOnlyList<IMetadataProvider> Providers { get; }
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

        /// <summary>
        /// The fields the overlay takes from the provider being asked, by the user's choices; a
        /// source built from several pages loads only the pages these need.
        /// </summary>
        public MetadataField WantedFields { get; internal set; }

        /// <summary>True when the link being fetched is one the user set, so the provider reads only what it names.</summary>
        public bool LinkIsUserSet { get; internal set; }

        /// <summary>A copy for one source, which the overlay asks alongside the others.</summary>
        internal MetadataLinkRequest ForSource(MetadataField wantedFields) =>
            new MetadataLinkRequest(Game, Data) { AlternateNames = AlternateNames, WantedFields = wantedFields };

        /// <summary>
        /// Other names the game is known by: the servicing provider's own, and the labels of its
        /// links to other sources (such as Steam's name from a SteamHunters link). Name searches
        /// try these after the Playnite name.
        /// </summary>
        public IReadOnlyList<string> AlternateNames { get; internal set; } = Array.Empty<string>();

        /// <summary>
        /// The category label for a source's base-game rows: the servicing provider's game name, as
        /// that provider and SteamHunters label the base game; the Playnite name when it has none.
        /// </summary>
        public string BaseCategoryLabel
        {
            get
            {
                var name = PlayniteAchievements.Services.Achievements.CategoryPathHelper.SanitizeSegment(
                    string.IsNullOrWhiteSpace(Data?.GameName) ? Game?.Name : Data.GameName);
                return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            }
        }

        /// <summary>The Playnite name, then each alternate name.</summary>
        public IEnumerable<string> SearchNames =>
            new[] { Game?.Name, Data?.GameName }.Concat(AlternateNames ?? Array.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name));
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

        /// <summary>
        /// Fields the source could not read this time although it answered (a page whose flags did
        /// not decode); the game keeps its stored values for them, as for a source that failed.
        /// </summary>
        public MetadataField UnavailableFields { get; set; }

        /// <summary>
        /// A fuller link the source found while fetching, such as a page on another site the wanted
        /// fields need; the overlay stores it in place of an automatic link. Null keeps the link.
        /// </summary>
        public string ExtendedLinkId { get; set; }

        /// <summary>Category label to image URL, for sources that supply category art.</summary>
        public IDictionary<string, string> CategoryArtUrls { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
