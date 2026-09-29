using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace PlayniteAchievements.Models.Settings
{
    public enum ShowcaseWidgetKind
    {
        Profile = 0,
        Scores = 1,
        Pie = 2,
        Timeline = 3,
        Statistics = 4,
        NativePoints = 5,
        // 6 (PinnedAchievements), 7 (FavoriteGames), and 12 (GameMosaic) are retired; they
        // collapsed into RecentAchievements, GameSummaries, and IconMosaic via source options.
        // Do not reuse the numbers.
        IconMosaic = 8,
        ScreenshotSlideshow = 9,
        RecentAchievements = 10,
        GameSummaries = 11,
        ActivityCalendar = 13
    }

    public enum ShowcasePageTemplate
    {
        Blank = 0,
        Showcase = 1,
        Analytics = 2,
        Collection = 3,
        UpNext = 4,
        TrophyCase = 5,
        Library = 6
    }

    public enum ShowcaseScoreMode
    {
        Dual = 0,
        Collection = 1,
        Prestige = 2
    }

    /// <summary>
    /// Which score cards carry the score-over-time line under them. Shares the vocabulary of
    /// <see cref="ShowcaseScoreMode"/> so the two options in the Scores widget editor read as a
    /// pair, with None added because the chart, unlike the cards, can be off entirely.
    /// </summary>
    public enum ShowcaseScoreHistoryMode
    {
        Dual = 0,
        Collection = 1,
        Prestige = 2,
        None = 3
    }

    public enum ShowcasePieMode
    {
        CompletedGames = 0,
        Provider = 1,
        Rarity = 2,
        Trophy = 3
    }

    /// <summary>
    /// Which counts the profile medal row shows. Trophy replaces the rarity tiers and the
    /// completions medal outright rather than adding to them, so a PlayStation-shaped library
    /// reads as trophies and nothing else; Both shows the rarity row followed by the grades.
    /// </summary>
    /// <summary>How the profile card arranges its avatar, text, medals and stat strip.</summary>
    public enum ShowcaseProfileLayout
    {
        /// <summary>Avatar beside the text, everything left-aligned.</summary>
        Left = 0,

        /// <summary>The same blocks, unchanged, centered across the card.</summary>
        Centered = 1,

        /// <summary>Avatar stacked above the text, every line centered.</summary>
        Stacked = 2
    }

    public enum ShowcaseProfileMedalMode
    {
        Rarity = 0,
        Trophy = 1,
        Both = 2
    }

    public enum ShowcasePointsGrouping
    {
        Provider = 0,
        Game = 1
    }

    public enum ShowcaseMosaicSource
    {
        Recent = 0,
        Rarest = 1,
        Pinned = 2,
        Capstones = 3,

        /// <summary>
        /// Locked achievements worth hunting next. Unlike every other source these rows are not in
        /// the overview snapshot's unlocked-only achievement list; they come from the bounded
        /// candidate pool the overview builder hydrates per game.
        /// </summary>
        UnlockNext = 4
    }

    /// <summary>
    /// How the Unlock Next mosaic ranks the locked achievements it offers. The criterion runs
    /// during selection rather than as a re-arrangement afterwards, so it decides which
    /// achievements make the cut, not just the order they appear in.
    /// </summary>
    /// <summary>How Finish Next ranks the unfinished games (mosaic and grid alike).</summary>
    public enum FinishNextCriterion
    {
        /// <summary>Highest completion percentage first.</summary>
        ClosestToCompletion = 0,

        /// <summary>Fewest achievements left first.</summary>
        FewestRemaining = 1,

        /// <summary>
        /// The games whose remaining achievements are the most commonly earned first, by the
        /// rarity tiers still locked.
        /// </summary>
        EasiestRemaining = 2
    }

    // 0 was NextInLine; a stored "NextInLine" no longer parses and falls back to the default.
    public enum UnlockNextCriterion
    {
        /// <summary>Highest global unlock percentage first: what most players already have.</summary>
        Easiest = 1,

        /// <summary>
        /// The games closest to being finished first, each game's most commonly earned locked
        /// achievements first within it.
        /// </summary>
        ClosestToCompletion = 2
    }

    public enum ShowcaseScreenshotVariant
    {
        All = 0,
        Clean = 1,
        Notification = 2,
        Framed = 3
    }

    public enum ShowcaseSlideshowSource
    {
        All = 0,
        GameCollection = 1,
        AchievementCollection = 2
    }

    /// <summary>What the collapsed Mosaic widget renders: achievement icons or game covers.</summary>
    public enum ShowcaseMosaicContent
    {
        Achievements = 0,
        Games = 1
    }

    /// <summary>Row source for the collapsed Achievements Grid widget.</summary>
    public enum ShowcaseAchievementGridSource
    {
        /// <summary>Every unlocked achievement. Locked rows never mix in (not even pinned goals).</summary>
        All = 0,
        Pinned = 1,

        /// <summary>
        /// Locked achievements worth hunting next, from the same bounded candidate pool and options
        /// as the Unlock Next mosaic.
        /// </summary>
        UnlockNext = 2
    }

    /// <summary>Row source for the collapsed Game Summaries Grid widget.</summary>
    public enum ShowcaseGameGridSource
    {
        Library = 0,
        Pinned = 1,
        PlayniteFavorites = 2,

        /// <summary>The unfinished games closest to done, as the Finish Next game mosaic ranks them.</summary>
        FinishNext = 3
    }

    public enum ShowcaseImageFitMode
    {
        Fit = 0,
        Fill = 1
    }

    /// <summary>
    /// Which side of the Screenshot Slideshow carries the achievement info panel, or Off when the
    /// widget shows the image alone.
    /// </summary>
    public enum ShowcaseInfoPanelPosition
    {
        Off = 0,
        Left = 1,
        Right = 2,
        Bottom = 3
    }

    public enum ShowcaseGameMosaicSource
    {
        Completed = 0,
        All = 1,
        Pinned = 2,
        PlayniteFavorites = 3,

        /// <summary>
        /// The unfinished games closest to being finished: the games counterpart of
        /// <see cref="ShowcaseMosaicSource.UnlockNext"/>, and the inverse of Completed.
        /// </summary>
        FinishNext = 4
    }

    public sealed class ShowcaseProfileSettings
    {
        public string DisplayName { get; set; }

        public string Subtitle { get; set; }

        public string AvatarPath { get; set; }

        public string BackgroundPath { get; set; }

        /// <summary>
        /// The platform profile links, in display order. Null means the user never edited them,
        /// and the profile shows a link for every enabled provider that knows the signed-in
        /// user's name; once saved the list is exactly what shows (empty shows none).
        /// </summary>
        public List<ShowcaseProfileLink> Links { get; set; }

        public ShowcaseProfileSettings Clone()
        {
            return new ShowcaseProfileSettings
            {
                DisplayName = DisplayName,
                Subtitle = Subtitle,
                AvatarPath = AvatarPath,
                BackgroundPath = BackgroundPath,
                Links = Links?
                    .Where(link => link != null)
                    .Select(link => link.Clone())
                    .ToList()
            };
        }
    }

    /// <summary>
    /// One profile link on the profile widget: the platform whose icon it shows, and either the
    /// user's name on that platform (turned into the page address by the platform's provider) or
    /// a full link used as-is. A blank value falls back to the signed-in user's stored name.
    /// </summary>
    public sealed class ShowcaseProfileLink
    {
        public string ProviderKey { get; set; }

        public string Value { get; set; }

        public ShowcaseProfileLink Clone()
        {
            return new ShowcaseProfileLink
            {
                ProviderKey = ProviderKey,
                Value = Value
            };
        }
    }

    public sealed class PinnedAchievementReference
    {
        public Guid GameId { get; set; }

        public string ApiName { get; set; }

        public string LastKnownGameName { get; set; }

        public string LastKnownAchievementName { get; set; }

        public DateTime AddedUtc { get; set; } = DateTime.UtcNow;

        public string Key => $"{GameId:D}:{(ApiName ?? string.Empty).Trim()}";

        public PinnedAchievementReference Clone()
        {
            return new PinnedAchievementReference
            {
                GameId = GameId,
                ApiName = ApiName,
                LastKnownGameName = LastKnownGameName,
                LastKnownAchievementName = LastKnownAchievementName,
                AddedUtc = AddedUtc
            };
        }
    }

    public sealed class PinnedAchievementCollection
    {
        public string CollectionId { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = "Default";

        public List<PinnedAchievementReference> Pins { get; set; } =
            new List<PinnedAchievementReference>();

        public PinnedAchievementCollection Clone()
        {
            return new PinnedAchievementCollection
            {
                CollectionId = CollectionId,
                Name = Name,
                Pins = (Pins ?? new List<PinnedAchievementReference>())
                    .Where(pin => pin != null)
                    .Select(pin => pin.Clone())
                    .ToList()
            };
        }
    }

    public sealed class PinnedGameCollection
    {
        public string CollectionId { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = "Default";

        public List<Guid> GameIds { get; set; } = new List<Guid>();

        public PinnedGameCollection Clone()
        {
            return new PinnedGameCollection
            {
                CollectionId = CollectionId,
                Name = Name,
                GameIds = (GameIds ?? new List<Guid>()).ToList()
            };
        }
    }

    public sealed class ShowcaseWidgetInstanceSettings
    {
        public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");

        public ShowcaseWidgetKind Kind { get; set; }

        public string CustomTitle { get; set; }

        public Dictionary<string, string> Options { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The profile card's manual data for a <see cref="ShowcaseWidgetKind.Profile"/> widget:
        /// name, subtitle, avatar, background and links. Per instance, so a duplicated page's
        /// profile card is edited independently of the original. Null for every other kind.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public ShowcaseProfileSettings Profile { get; set; }

        public ShowcaseWidgetInstanceSettings Clone()
        {
            return new ShowcaseWidgetInstanceSettings
            {
                InstanceId = InstanceId,
                Kind = Kind,
                CustomTitle = CustomTitle,
                Options = Options != null
                    ? new Dictionary<string, string>(Options, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                Profile = Profile?.Clone()
            };
        }

        public T GetOption<T>(string key, T fallback)
            where T : struct
        {
            if (Options == null ||
                string.IsNullOrWhiteSpace(key) ||
                !Options.TryGetValue(key, out var raw) ||
                string.IsNullOrWhiteSpace(raw))
            {
                return fallback;
            }

            if (typeof(T).IsEnum && Enum.TryParse(raw, true, out T enumValue))
            {
                return enumValue;
            }

            try
            {
                return (T)Convert.ChangeType(raw, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        public void SetOption<T>(string key, T value)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (Options == null)
            {
                Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            Options[key] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public sealed class ShowcaseBlockSettings
    {
        public string BlockId { get; set; } = Guid.NewGuid().ToString("N");

        public int Row { get; set; }

        public int Column { get; set; }

        public int RowSpan { get; set; } = 1;

        public int ColumnSpan { get; set; } = 1;

        public string WidgetInstanceId { get; set; }

        public ShowcaseBlockSettings Clone()
        {
            return new ShowcaseBlockSettings
            {
                BlockId = BlockId,
                Row = Row,
                Column = Column,
                RowSpan = RowSpan,
                ColumnSpan = ColumnSpan,
                WidgetInstanceId = WidgetInstanceId
            };
        }
    }

    public sealed class ShowcasePageSettings
    {
        public string PageId { get; set; } = Guid.NewGuid().ToString("N");

        public string Name { get; set; } = "Showcase";

        public List<ShowcaseBlockSettings> Blocks { get; set; } =
            new List<ShowcaseBlockSettings>();

        /// <summary>
        /// The page's row count (normalized by ShowcaseLayoutService into its track-count
        /// bounds). 0 means unset: <see cref="GridSize"/> or the default fills it in.
        /// </summary>
        public int RowCount { get; set; }

        /// <summary>The page's column count; 0 means unset, as for <see cref="RowCount"/>.</summary>
        public int ColumnCount { get; set; }

        /// <summary>
        /// Legacy square grid dimension, kept only so older layouts still deserialize.
        /// <c>ShowcaseLayoutService.Normalize</c> seeds unset row and column counts from it and
        /// then clears it.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int? GridSize { get; set; }

        /// <summary>Star weights for the grid's rows; null means equal shares.</summary>
        public List<double> RowWeights { get; set; }

        /// <summary>Star weights for the grid's columns; null means equal shares.</summary>
        public List<double> ColumnWeights { get; set; }

        public ShowcasePageSettings Clone()
        {
            return new ShowcasePageSettings
            {
                PageId = PageId,
                Name = Name,
                RowCount = RowCount,
                ColumnCount = ColumnCount,
                GridSize = GridSize,
                Blocks = (Blocks ?? new List<ShowcaseBlockSettings>())
                    .Where(block => block != null)
                    .Select(block => block.Clone())
                    .ToList(),
                RowWeights = RowWeights?.ToList(),
                ColumnWeights = ColumnWeights?.ToList()
            };
        }
    }

    public sealed class ShowcaseSettings
    {
        public const int CurrentLayoutVersion = 1;

        public const string BuiltInAchievementCollectionId = "default-achievements";

        public const string BuiltInGameCollectionId = "default-games";

        public int LayoutVersion { get; set; } = CurrentLayoutVersion;

        public string LastSelectedPageId { get; set; }

        public List<ShowcasePageSettings> Pages { get; set; } =
            new List<ShowcasePageSettings>();

        public List<ShowcaseWidgetInstanceSettings> WidgetInstances { get; set; } =
            new List<ShowcaseWidgetInstanceSettings>();

        public string DefaultAchievementPinCollectionId { get; set; } =
            BuiltInAchievementCollectionId;

        public string DefaultGamePinCollectionId { get; set; } =
            BuiltInGameCollectionId;

        // Replace on deserialize: these lists are seeded with the built-in Default, and
        // Newtonsoft's default in-place population would append the persisted entries after the
        // seed, duplicating the Default collection on every load.
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<PinnedAchievementCollection> AchievementPinCollections { get; set; } =
            new List<PinnedAchievementCollection>
            {
                new PinnedAchievementCollection
                {
                    CollectionId = BuiltInAchievementCollectionId,
                    Name = "Default"
                }
            };

        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<PinnedGameCollection> GamePinCollections { get; set; } =
            new List<PinnedGameCollection>
            {
                new PinnedGameCollection
                {
                    CollectionId = BuiltInGameCollectionId,
                    Name = "Default"
                }
            };

        /// <summary>
        /// Legacy layout-wide profile data, kept only so older settings still deserialize.
        /// <c>ShowcaseLayoutService.Normalize</c> moves it onto every profile widget that has no
        /// <see cref="ShowcaseWidgetInstanceSettings.Profile"/> of its own and then clears it.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public ShowcaseProfileSettings Profile { get; set; }

        public Dictionary<string, ShowcaseWidgetInstanceSettings> StartPageInstances { get; set; } =
            new Dictionary<string, ShowcaseWidgetInstanceSettings>(StringComparer.OrdinalIgnoreCase);

        public ShowcaseSettings Clone()
        {
            return new ShowcaseSettings
            {
                LayoutVersion = LayoutVersion,
                LastSelectedPageId = LastSelectedPageId,
                Pages = (Pages ?? new List<ShowcasePageSettings>())
                    .Where(page => page != null)
                    .Select(page => page.Clone())
                    .ToList(),
                WidgetInstances = (WidgetInstances ?? new List<ShowcaseWidgetInstanceSettings>())
                    .Where(widget => widget != null)
                    .Select(widget => widget.Clone())
                    .ToList(),
                DefaultAchievementPinCollectionId = DefaultAchievementPinCollectionId,
                DefaultGamePinCollectionId = DefaultGamePinCollectionId,
                AchievementPinCollections = (AchievementPinCollections ??
                    new List<PinnedAchievementCollection>())
                    .Where(collection => collection != null)
                    .Select(collection => collection.Clone())
                    .ToList(),
                GamePinCollections = (GamePinCollections ?? new List<PinnedGameCollection>())
                    .Where(collection => collection != null)
                    .Select(collection => collection.Clone())
                    .ToList(),
                Profile = Profile?.Clone(),
                StartPageInstances = (StartPageInstances ??
                    new Dictionary<string, ShowcaseWidgetInstanceSettings>(StringComparer.OrdinalIgnoreCase))
                    .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null)
                    .ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value.Clone(),
                        StringComparer.OrdinalIgnoreCase)
            };
        }
    }
}
