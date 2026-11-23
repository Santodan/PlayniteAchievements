using System.Collections.Generic;
using System.Windows.Media;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.YearReview;

namespace PlayniteAchievements.ViewModels.YearReview
{
    // Every slide is laid out on a 1280 x 770 canvas that the window scales to fit, so the
    // positions below are canvas units. Each slide type has its own DataTemplate.

    public abstract class YearReviewSlide
    {
        protected YearReviewSlide(YearReviewSlideKind kind)
        {
            Kind = kind;
        }

        public YearReviewSlideKind Kind { get; }

        /// <summary>The art behind the slide, shown blurred and faded; null for none.</summary>
        public string BackdropPath { get; set; }
    }

    /// <summary>An element placed on the slide canvas, entering after <see cref="Delay"/> milliseconds.</summary>
    public abstract class YearReviewPlaced
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Angle { get; set; }
        public double Delay { get; set; }
    }

    public sealed class YearReviewIntroSlide : YearReviewSlide
    {
        public YearReviewIntroSlide() : base(YearReviewSlideKind.Intro) { }

        public string YearText { get; set; }
        public string RangeText { get; set; }
    }

    public sealed class YearReviewIcon : YearReviewPlaced
    {
        public string IconPath { get; set; }

        /// <summary>The achievement and its game, shown on hover.</summary>
        public string ToolTip { get; set; }
        public double Size { get; set; }
    }

    public sealed class YearReviewNumbersSlide : YearReviewSlide
    {
        public YearReviewNumbersSlide() : base(YearReviewSlideKind.Numbers) { }

        /// <summary>The year's rarest icons, scattered under the figure.</summary>
        public IReadOnlyList<YearReviewIcon> Icons { get; set; }

        public int Unlocked { get; set; }
        public int ScoreGained { get; set; }
        public string LevelText { get; set; }
        public int Games { get; set; }
    }

    public sealed class YearReviewMonthBar
    {
        public string Label { get; set; }
        public int Unlocks { get; set; }

        /// <summary>The bar's height in canvas units.</summary>
        public double Height { get; set; }

        public bool IsPeak { get; set; }
        public double Delay { get; set; }

        /// <summary>The month and its count, shown on hover.</summary>
        public string ToolTip { get; set; }
    }

    public sealed class YearReviewHeatCell : YearReviewPlaced
    {
        /// <summary>0 for no unlocks, then 1 to 4 by share of the busiest day.</summary>
        public int Level { get; set; }

        /// <summary>The day and its count, shown on hover.</summary>
        public string ToolTip { get; set; }
    }

    public sealed class YearReviewMonthsSlide : YearReviewSlide
    {
        public YearReviewMonthsSlide() : base(YearReviewSlideKind.Months) { }

        public IReadOnlyList<YearReviewMonthBar> Months { get; set; }
        public string BusiestDayText { get; set; }
        public int StreakDays { get; set; }
        public string StreakRangeText { get; set; }
        public IReadOnlyList<YearReviewHeatCell> Heat { get; set; }
    }

    public sealed class YearReviewCoverCard : YearReviewPlaced
    {
        public int Rank { get; set; }
        public string Name { get; set; }
        public string CoverPath { get; set; }
        public int Unlocks { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public CompletionBadgeKind Badge { get; set; }
        public double BadgeDelay { get; set; }

        /// <summary>True for a completed game, which wears its completion badge.</summary>
        public bool ShowBadge { get; set; }

        /// <summary>True when the card names its game over the art.</summary>
        public bool ShowCaption { get; set; } = true;

        public bool HasRank => Rank > 0;
    }

    public sealed class YearReviewTopGamesSlide : YearReviewSlide
    {
        public YearReviewTopGamesSlide() : base(YearReviewSlideKind.TopGames) { }

        public int LeadUnlocks { get; set; }
        public string LeadCaption { get; set; }

        /// <summary>The cards from the back of the fan to the front; the leader is last.</summary>
        public IReadOnlyList<YearReviewCoverCard> Cards { get; set; }
    }

    public sealed class YearReviewCompletionsSlide : YearReviewSlide
    {
        public YearReviewCompletionsSlide() : base(YearReviewSlideKind.Completions) { }

        public int Count { get; set; }
        public string BreakdownText { get; set; }
        public IReadOnlyList<YearReviewCoverCard> Cards { get; set; }
    }

    public sealed class YearReviewRarestSlide : YearReviewSlide
    {
        public YearReviewRarestSlide() : base(YearReviewSlideKind.Rarest) { }

        public string Name { get; set; }
        public string IconPath { get; set; }
        public double Percent { get; set; }
        public string DetailText { get; set; }
        public RarityTier Tier { get; set; }
        public string TierText { get; set; }
        public Brush TierBrush { get; set; }
    }

    public sealed class YearReviewBubble : YearReviewPlaced
    {
        public string Label { get; set; }
        public int Unlocks { get; set; }
        public double Diameter { get; set; }
        public double NumberHeight { get; set; }
        public Brush Fill { get; set; }
    }

    public sealed class YearReviewRaritySlide : YearReviewSlide
    {
        public YearReviewRaritySlide() : base(YearReviewSlideKind.Rarity) { }

        public IReadOnlyList<YearReviewBubble> Bubbles { get; set; }
        public string TotalText { get; set; }
    }

    public sealed class YearReviewPlatformEntry : YearReviewPlaced
    {
        public string Name { get; set; }
        public int Unlocks { get; set; }
        public double NumberHeight { get; set; }
        public Brush Fill { get; set; }
    }

    public sealed class YearReviewDonutSegment
    {
        public Geometry Geometry { get; set; }
        public Brush Fill { get; set; }
    }

    public sealed class YearReviewPlatformsSlide : YearReviewSlide
    {
        public YearReviewPlatformsSlide() : base(YearReviewSlideKind.Platforms) { }

        public IReadOnlyList<YearReviewPlatformEntry> Entries { get; set; }
        public IReadOnlyList<YearReviewDonutSegment> Segments { get; set; }
    }

    public sealed class YearReviewFirstAndLatestSlide : YearReviewSlide
    {
        public YearReviewFirstAndLatestSlide() : base(YearReviewSlideKind.FirstAndLatest) { }

        public string FirstLabel { get; set; }
        public string FirstName { get; set; }
        public string FirstIconPath { get; set; }
        public string FirstDetail { get; set; }
        public string LatestName { get; set; }
        public string LatestIconPath { get; set; }
        public string LatestDetail { get; set; }
        public int DaysApart { get; set; }
    }

    public sealed class YearReviewFriendsSlide : YearReviewSlide
    {
        public YearReviewFriendsSlide() : base(YearReviewSlideKind.Friends) { }

        public int UnlockedFirst { get; set; }
        public bool HasTopFriend { get; set; }
        public string FriendName { get; set; }
        public string FriendInitial { get; set; }
        public string AvatarPath { get; set; }
        public string InCommonText { get; set; }
        public IReadOnlyList<YearReviewCoverCard> SharedCards { get; set; }
    }

    public sealed class YearReviewSummarySlide : YearReviewSlide
    {
        public YearReviewSummarySlide() : base(YearReviewSlideKind.Summary) { }

        public string YearText { get; set; }
        public string TopGameName { get; set; }
        public string TopCoverPath { get; set; }

        /// <summary>The card's figures: value over label, two columns.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Figures { get; set; }
    }

    public sealed class YearReviewEmptySlide : YearReviewSlide
    {
        public YearReviewEmptySlide() : base(YearReviewSlideKind.Empty) { }

        public string Text { get; set; }
    }
}
