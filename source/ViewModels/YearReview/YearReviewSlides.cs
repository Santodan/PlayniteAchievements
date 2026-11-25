using System;
using System.Collections.Generic;
using System.Windows.Media;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.YearReview;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels.YearReview
{
    // Every slide is laid out on a 1280 x 770 canvas that the window scales to fit, so the
    // positions below are canvas units. Each slide type has its own DataTemplate.

    /// <summary>One row of an unlock list.</summary>
    public sealed class YearReviewUnlockRow
    {
        public string IconPath { get; set; }
        public string Name { get; set; }
        public string GameName { get; set; }
        public string TimeText { get; set; }
    }

    /// <summary>A set of unlocks, listed when the element showing it is clicked. Rows are made on first open.</summary>
    public sealed class YearReviewUnlockGroup
    {
        private readonly Func<IReadOnlyList<YearReviewUnlockRow>> _rows;
        private IReadOnlyList<YearReviewUnlockRow> _built;

        public YearReviewUnlockGroup(string header, int count, Func<IReadOnlyList<YearReviewUnlockRow>> rows)
        {
            Header = header;
            Count = count;
            _rows = rows;
        }

        public string Header { get; }

        public int Count { get; }

        public IReadOnlyList<YearReviewUnlockRow> Rows => _built ?? (_built = _rows());
    }

    /// <summary>One of a slide's views, picked from the slide's dropdown.</summary>
    public abstract class YearReviewSlideView : ObservableObject
    {
        public string Label { get; set; }

        /// <summary>Art for the backdrop while this view is shown; null keeps the slide's.</summary>
        public string BackdropPath { get; set; }
    }

    public abstract class YearReviewSlide : ObservableObject
    {
        private YearReviewSlideView _currentView;

        protected YearReviewSlide(YearReviewSlideKind kind)
        {
            Kind = kind;
        }

        public YearReviewSlideKind Kind { get; }

        /// <summary>What the slide shows, for the name of a saved image.</summary>
        public string Title { get; set; }

        /// <summary>The art behind the slide, shown blurred and faded; null for none.</summary>
        public string BackdropPath { get; set; }

        /// <summary>The slide's views; the first is the default. Empty for a slide with one view.</summary>
        public IReadOnlyList<YearReviewSlideView> Views { get; set; } = Array.Empty<YearReviewSlideView>();

        public bool HasViews => Views.Count > 1;

        public YearReviewSlideView CurrentView
        {
            get => _currentView;
            set
            {
                if (ReferenceEquals(_currentView, value) || value == null)
                {
                    return;
                }

                SetValue(ref _currentView, value);
                OnPropertyChanged(nameof(ShownBackdropPath));
                OnPropertyChanged(nameof(ViewSuffix));
            }
        }

        /// <summary>The backdrop shown now: the current view's art, else the slide's.</summary>
        public string ShownBackdropPath => CurrentView?.BackdropPath ?? BackdropPath;

        /// <summary>The picked view's label when it is not the default, for the saved image's name.</summary>
        public string ViewSuffix => HasViews && !ReferenceEquals(CurrentView, Views[0]) ? CurrentView?.Label : null;
    }

    /// <summary>An element placed on the slide canvas, entering after <see cref="Delay"/> milliseconds.</summary>
    public abstract class YearReviewPlaced : ObservableObject
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
        public Guid GameId { get; set; }
        public YearReviewUnlockGroup Group { get; set; }

        /// <summary>The achievement and its game, shown on hover.</summary>
        public string ToolTip { get; set; }
        public double Size { get; set; }
    }

    public sealed class YearReviewNumbersSlide : YearReviewSlide
    {
        public YearReviewNumbersSlide() : base(YearReviewSlideKind.Numbers) { }

        /// <summary>Every unlock of the year, opened from the big figure.</summary>
        public YearReviewUnlockGroup AllGroup { get; set; }

        /// <summary>The year's rarest icons, scattered under the figure.</summary>
        public IReadOnlyList<YearReviewIcon> Icons { get; set; }

        public int Unlocked { get; set; }
        public int CollectionGained { get; set; }
        public int PrestigeGained { get; set; }
        public string LevelText { get; set; }
        public string PrestigeLevelText { get; set; }
        public int Games { get; set; }
    }

    public sealed class YearReviewMonthBar
    {
        public string Label { get; set; }

        /// <summary>The month's value in the view's measure: unlocks or score.</summary>
        public int Unlocks { get; set; }

        /// <summary>The bar's height in canvas units.</summary>
        public double Height { get; set; }

        public bool IsPeak { get; set; }
        public double Delay { get; set; }

        /// <summary>The month and its value, shown on hover.</summary>
        public string ToolTip { get; set; }

        public YearReviewUnlockGroup Group { get; set; }
    }

    public sealed class YearReviewHeatCell : YearReviewPlaced
    {
        /// <summary>0 for no unlocks, then 1 to 4 by share of the busiest day.</summary>
        public int Level { get; set; }

        /// <summary>The day and its count, shown on hover.</summary>
        public string ToolTip { get; set; }

        public YearReviewUnlockGroup Group { get; set; }
    }

    /// <summary>The months in one measure: unlocks, collection score or prestige score.</summary>
    public sealed class YearReviewMonthsView : YearReviewSlideView
    {
        public IReadOnlyList<YearReviewMonthBar> Months { get; set; }
        public string BusiestDayText { get; set; }
    }

    public sealed class YearReviewMonthsSlide : YearReviewSlide
    {
        public YearReviewMonthsSlide() : base(YearReviewSlideKind.Months) { }

        public int StreakDays { get; set; }
        public YearReviewUnlockGroup StreakGroup { get; set; }
        public string StreakRangeText { get; set; }
        public IReadOnlyList<YearReviewHeatCell> Heat { get; set; }
    }

    public sealed class YearReviewCoverCard : YearReviewPlaced
    {
        public Guid GameId { get; set; }
        public YearReviewUnlockGroup Group { get; set; }
        public int Rank { get; set; }
        public string Name { get; set; }
        public string CoverPath { get; set; }

        /// <summary>The figure under the name: unlocks, score or average rarity.</summary>
        public string ValueText { get; set; }

        public double Width { get; set; }
        public double Height { get; set; }
        public CompletionBadgeKind Badge { get; set; }
        public double BadgeDelay { get; set; }

        /// <summary>True for a completed game, which wears its completion badge.</summary>
        public bool ShowBadge { get; set; }

        /// <summary>True when the card names its game under the art.</summary>
        public bool ShowCaption { get; set; } = true;

        public bool HasRank => Rank > 0;
    }

    /// <summary>The top games by one measure.</summary>
    public sealed class YearReviewTopGamesView : YearReviewSlideView
    {
        public double LeadValue { get; set; }

        /// <summary>True when the lead figure is an average unlock percent.</summary>
        public bool LeadIsPercent { get; set; }

        public string LeadCaption { get; set; }

        /// <summary>The cards from the back of the row to the front; the leader is last.</summary>
        public IReadOnlyList<YearReviewCoverCard> Cards { get; set; }
    }

    public sealed class YearReviewTopGamesSlide : YearReviewSlide
    {
        public YearReviewTopGamesSlide() : base(YearReviewSlideKind.TopGames) { }
    }

    public sealed class YearReviewCompletionsSlide : YearReviewSlide
    {
        public YearReviewCompletionsSlide() : base(YearReviewSlideKind.Completions) { }

        public int Count { get; set; }
        public string BreakdownText { get; set; }
        public YearReviewUnlockGroup AllGroup { get; set; }
        public IReadOnlyList<YearReviewCoverCard> Cards { get; set; }
    }

    /// <summary>A rare unlock in a row of them: an icon with its percent.</summary>
    public sealed class YearReviewRareEntry : YearReviewPlaced
    {
        public Guid GameId { get; set; }
        public string IconPath { get; set; }
        public string Name { get; set; }
        public string Caption { get; set; }
        public string PercentText { get; set; }
        public Brush TierBrush { get; set; }
        public YearReviewUnlockGroup Group { get; set; }
    }

    /// <summary>The rarest unlock alone, the five rarest, or the rarest of each platform.</summary>
    public sealed class YearReviewRarestView : YearReviewSlideView
    {
        public Guid? GameId { get; set; }
        public bool IsSingle { get; set; }
        public string Name { get; set; }
        public string IconPath { get; set; }
        public YearReviewUnlockGroup Group { get; set; }
        public double Percent { get; set; }
        public string DetailText { get; set; }
        public string TierText { get; set; }
        public Brush TierBrush { get; set; }
        public IReadOnlyList<YearReviewRareEntry> Entries { get; set; } = Array.Empty<YearReviewRareEntry>();
    }

    public sealed class YearReviewRarestSlide : YearReviewSlide
    {
        public YearReviewRarestSlide() : base(YearReviewSlideKind.Rarest) { }
    }

    public sealed class YearReviewBubble : YearReviewPlaced
    {
        public YearReviewUnlockGroup Group { get; set; }
        public string Label { get; set; }
        public int Unlocks { get; set; }
        public double Diameter { get; set; }
        public double NumberHeight { get; set; }
        public Brush Fill { get; set; }

        /// <summary>Dark or light, whichever reads on <see cref="Fill"/>, the user's rarity color.</summary>
        public Brush TextBrush { get; set; }
    }

    /// <summary>The rarity split of every platform or of one.</summary>
    public sealed class YearReviewRarityView : YearReviewSlideView
    {
        /// <summary>The game with the most unlocks among these; null for the every-platform view.</summary>
        public Guid? TopGameId { get; set; }
        public IReadOnlyList<YearReviewBubble> Bubbles { get; set; }
        public string TotalText { get; set; }
    }

    public sealed class YearReviewRaritySlide : YearReviewSlide
    {
        public YearReviewRaritySlide() : base(YearReviewSlideKind.Rarity) { }
    }

    public sealed class YearReviewPlatformEntry : YearReviewPlaced
    {
        private bool _isHighlighted;

        public YearReviewUnlockGroup Group { get; set; }
        public string Name { get; set; }
        public int Unlocks { get; set; }
        public string ShareText { get; set; }
        public double NumberHeight { get; set; }
        public double RowHeight { get; set; }
        public double CoverHeight { get; set; }
        public double CoverWidth { get; set; }
        public Brush Fill { get; set; }

        /// <summary>The platform's top games' covers, most unlocks first.</summary>
        public IReadOnlyList<string> Covers { get; set; }

        /// <summary>True while the row or its donut slice is hovered; both light up.</summary>
        public bool IsHighlighted
        {
            get => _isHighlighted;
            set => SetValue(ref _isHighlighted, value);
        }
    }

    public sealed class YearReviewDonutSegment
    {
        /// <summary>The row this slice pairs with.</summary>
        public YearReviewPlatformEntry Entry { get; set; }

        /// <summary>Where the slice begins, in degrees clockwise from the right.</summary>
        public double StartAngle { get; set; }

        public double Sweep { get; set; }
        public Brush Fill { get; set; }
        public string ToolTip { get; set; }
        public YearReviewUnlockGroup Group { get; set; }
    }

    public sealed class YearReviewPlatformsSlide : YearReviewSlide
    {
        public YearReviewPlatformsSlide() : base(YearReviewSlideKind.Platforms) { }

        public IReadOnlyList<YearReviewPlatformEntry> Entries { get; set; }
        public IReadOnlyList<YearReviewDonutSegment> Segments { get; set; }
        public int Total { get; set; }
        public YearReviewUnlockGroup AllGroup { get; set; }
    }

    public sealed class YearReviewFirstAndLatestSlide : YearReviewSlide
    {
        public YearReviewFirstAndLatestSlide() : base(YearReviewSlideKind.FirstAndLatest) { }

        public string FirstLabel { get; set; }
        public string FirstName { get; set; }
        public string FirstIconPath { get; set; }
        public YearReviewUnlockGroup FirstGroup { get; set; }
        public string FirstDetail { get; set; }
        public string LatestName { get; set; }
        public string LatestIconPath { get; set; }
        public YearReviewUnlockGroup LatestGroup { get; set; }
        public string LatestDetail { get; set; }
        public int DaysApart { get; set; }
    }

    /// <summary>Every friend together, or one friend against the user.</summary>
    public sealed class YearReviewFriendsView : YearReviewSlideView
    {
        private string _avatarPath;

        /// <summary>The friend account; null for the every-friend view.</summary>
        public string FriendKey { get; set; }

        public int BigNumber { get; set; }
        public string BigLabel { get; set; }
        public YearReviewUnlockGroup BigGroup { get; set; }
        public bool HasSecond { get; set; }
        public int SecondNumber { get; set; }
        public string SecondLabel { get; set; }
        public YearReviewUnlockGroup SecondGroup { get; set; }

        /// <summary>The user's unlocks in the games shared with the friend.</summary>
        public YearReviewUnlockGroup SharedGroup { get; set; }

        public bool HasFriend { get; set; }
        public string FriendName { get; set; }
        public string FriendInitial { get; set; }

        /// <summary>Read once the view is first shown.</summary>
        public string AvatarPath
        {
            get => _avatarPath;
            set => SetValue(ref _avatarPath, value);
        }

        public bool AvatarRequested { get; set; }
        public string InCommonText { get; set; }
        public IReadOnlyList<YearReviewCoverCard> SharedCards { get; set; }
    }

    public sealed class YearReviewFriendsSlide : YearReviewSlide
    {
        public YearReviewFriendsSlide() : base(YearReviewSlideKind.Friends) { }
    }

    /// <summary>A cover in the classic card's fan, offset and tilted within the fan's box.</summary>
    public sealed class YearReviewFanCover
    {
        public string Path { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Angle { get; set; }
    }

    /// <summary>A style of the shareable card; its template reads the card's data through <see cref="Card"/>.</summary>
    public abstract class YearReviewCardView : YearReviewSlideView
    {
        public YearReviewSummarySlide Card { get; set; }
    }

    public sealed class YearReviewClassicCard : YearReviewCardView { }

    public sealed class YearReviewPosterCard : YearReviewCardView { }

    public sealed class YearReviewBannerCard : YearReviewCardView { }

    public sealed class YearReviewMosaicCard : YearReviewCardView { }

    public sealed class YearReviewSummarySlide : YearReviewSlide
    {
        private string _cardName;
        private string _cardAvatarPath;

        public YearReviewSummarySlide() : base(YearReviewSlideKind.Summary) { }

        /// <summary>The name on the card; edited on the card.</summary>
        public string CardName
        {
            get => _cardName;
            set
            {
                SetValue(ref _cardName, value);
                OnPropertyChanged(nameof(CardInitial));
            }
        }

        /// <summary>The picture on the card; chosen on the card.</summary>
        public string CardAvatarPath
        {
            get => _cardAvatarPath;
            set => SetValue(ref _cardAvatarPath, value);
        }

        public string CardInitial => string.IsNullOrWhiteSpace(CardName) ? string.Empty : CardName.Trim().Substring(0, 1).ToUpperInvariant();

        public string YearText { get; set; }
        public string TopGameName { get; set; }
        public string TopCoverPath { get; set; }

        /// <summary>The card's figures: value over label.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Figures { get; set; }

        /// <summary>The first four figures, for the styles with room for one row.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> TopFigures { get; set; }

        /// <summary>Covers of the year's top games, for the mosaic style.</summary>
        public IReadOnlyList<string> Covers { get; set; }

        /// <summary>The top three covers, back to front, for the classic style's fan.</summary>
        public IReadOnlyList<YearReviewFanCover> FanCovers { get; set; }
    }

    public sealed class YearReviewEmptySlide : YearReviewSlide
    {
        public YearReviewEmptySlide() : base(YearReviewSlideKind.Empty) { }

        public string Text { get; set; }
    }
}
