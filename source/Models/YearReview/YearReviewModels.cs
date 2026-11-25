using System;
using System.Collections.Generic;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Overview;

namespace PlayniteAchievements.Models.YearReview
{
    /// <summary>One of the current user's unlocks, as the Year in Review reads it.</summary>
    public sealed class YearReviewUnlock
    {
        /// <summary>Identifies the achievement across users, as <see cref="YearReviewFriendUnlock.AchievementKey"/> does.</summary>
        public string Key { get; set; }

        public Guid GameId { get; set; }
        public string GameName { get; set; }
        public string AchievementName { get; set; }
        public string IconPath { get; set; }
        public DateTime UnlockUtc { get; set; }
        public double? GlobalPercent { get; set; }
        public RarityTier Rarity { get; set; }
        public string ProviderKey { get; set; }
    }

    /// <summary>A library game with the art the review shows and its completion, when it has one.</summary>
    public sealed class YearReviewGame
    {
        public Guid GameId { get; set; }
        public string Name { get; set; }
        public string CoverPath { get; set; }

        /// <summary>The earliest finish (last capstone, platinum, or the unlock that reached 100%); null when not completed.</summary>
        public DateTime? FinishUtc { get; set; }

        public CompletionBadgeKind Badge { get; set; }
    }

    /// <summary>A friend's unlock, with the current user's unlock of the same achievement.</summary>
    public sealed class YearReviewFriendUnlock
    {
        /// <summary>The friend account: provider key and external user id.</summary>
        public string FriendKey { get; set; }
        public string FriendName { get; set; }
        public Guid? GameId { get; set; }

        /// <summary>Identifies the achievement across users.</summary>
        public string AchievementKey { get; set; }

        public DateTime FriendUnlockUtc { get; set; }
        public DateTime? SelfUnlockUtc { get; set; }
    }

    /// <summary>Everything the builder reads. Built at the boundary from the database and the Overview snapshot.</summary>
    public sealed class YearReviewInputs
    {
        /// <summary>The current user's unlocks with a known unlock time, under the summary rules.</summary>
        public IReadOnlyList<YearReviewUnlock> Unlocks { get; set; } = Array.Empty<YearReviewUnlock>();

        /// <summary>The Overview snapshot's per-game, per-day facts, for scores.</summary>
        public IReadOnlyList<UnlockFact> Facts { get; set; } = Array.Empty<UnlockFact>();

        public IReadOnlyList<YearReviewGame> Games { get; set; } = Array.Empty<YearReviewGame>();

        public IReadOnlyList<YearReviewFriendUnlock> FriendUnlocks { get; set; } = Array.Empty<YearReviewFriendUnlock>();

        /// <summary>Today's local date.</summary>
        public DateTime Today { get; set; }

        /// <summary>The zone unlock times are read in; the local zone when null.</summary>
        public TimeZoneInfo Zone { get; set; }
    }

    public sealed class YearReviewGameCount
    {
        public YearReviewGameCount(YearReviewGame game, int unlocks)
        {
            Game = game;
            Unlocks = unlocks;
        }

        public YearReviewGame Game { get; }
        public int Unlocks { get; }
    }

    public sealed class YearReviewPlatformCount
    {
        public YearReviewPlatformCount(string providerKey, int unlocks)
        {
            ProviderKey = providerKey;
            Unlocks = unlocks;
        }

        public string ProviderKey { get; }
        public int Unlocks { get; }
    }

    /// <summary>One friend's year against the user's.</summary>
    public sealed class YearReviewFriendStats
    {
        public string Key { get; set; }
        public string Name { get; set; }

        /// <summary>Achievements the user unlocked in the year before this friend did.</summary>
        public int UnlockedFirst => UnlockedFirstKeys.Count;

        /// <summary>Achievements the user unlocked in the year that this friend had unlocked earlier.</summary>
        public int TheyFirst => TheyFirstKeys.Count;

        public IReadOnlyCollection<string> UnlockedFirstKeys { get; set; } = Array.Empty<string>();
        public IReadOnlyCollection<string> TheyFirstKeys { get; set; } = Array.Empty<string>();

        /// <summary>Games both unlocked something in this year.</summary>
        public IReadOnlyList<Guid> SharedGameIds { get; set; } = Array.Empty<Guid>();
    }

    public sealed class YearReviewFriendSummary
    {
        /// <summary>Every friend with something to compare, most shared games first.</summary>
        public IReadOnlyList<YearReviewFriendStats> PerFriend { get; set; } = Array.Empty<YearReviewFriendStats>();

        /// <summary>Achievements the user unlocked in the year before a friend who also has them.</summary>
        public int UnlockedFirst => UnlockedFirstKeys.Count;

        public IReadOnlyCollection<string> UnlockedFirstKeys { get; set; } = Array.Empty<string>();

        /// <summary>The friend with the most games both of them unlocked something in this year; null without one.</summary>
        public string TopFriendKey { get; set; }
        public string TopFriendName { get; set; }
        public IReadOnlyList<Guid> SharedGameIds { get; set; } = Array.Empty<Guid>();

        public bool HasData => UnlockedFirst > 0 || SharedGameIds.Count > 0;
    }

    /// <summary>The slides of a review, in order. Values are stable; never reuse a retired one.</summary>
    public enum YearReviewSlideKind
    {
        Intro = 0,
        Numbers = 1,
        Months = 2,
        TopGames = 3,
        Completions = 4,
        Rarest = 5,
        Rarity = 6,
        Platforms = 7,
        FirstAndLatest = 8,
        Friends = 9,
        Summary = 10,
        Empty = 11
    }

    /// <summary>One year's review.</summary>
    public sealed class YearReviewData
    {
        public int Year { get; set; }
        public DateTime FirstDay { get; set; }

        /// <summary>31 December, or today while the year is still running.</summary>
        public DateTime LastDay { get; set; }

        public int Unlocked { get; set; }
        public int GamesWithUnlocks { get; set; }
        public int CollectionGained { get; set; }
        public int PrestigeGained { get; set; }

        /// <summary>Collection and prestige score earned per local day and per game in the year.</summary>
        public IReadOnlyDictionary<DateTime, int> DayCollection { get; set; } = new Dictionary<DateTime, int>();
        public IReadOnlyDictionary<DateTime, int> DayPrestige { get; set; } = new Dictionary<DateTime, int>();
        public IReadOnlyDictionary<Guid, int> GameCollection { get; set; } = new Dictionary<Guid, int>();
        public IReadOnlyDictionary<Guid, int> GamePrestige { get; set; } = new Dictionary<Guid, int>();

        /// <summary>Every library game the review knows, for art and names.</summary>
        public IReadOnlyDictionary<Guid, YearReviewGame> Games { get; set; } = new Dictionary<Guid, YearReviewGame>();
        public int LevelAtStart { get; set; }
        public int LevelAtEnd { get; set; }
        public int PrestigeLevelAtStart { get; set; }
        public int PrestigeLevelAtEnd { get; set; }

        /// <summary>Unlocks per month, January first.</summary>
        public IReadOnlyList<int> Months { get; set; } = new int[12];

        /// <summary>The month index (0 = January) with the most unlocks; -1 without unlocks.</summary>
        public int BusiestMonth { get; set; } = -1;

        public DateTime? BusiestDay { get; set; }
        public int BusiestDayUnlocks { get; set; }
        public UnlockStreak LongestStreak { get; set; } = UnlockStreak.None;

        /// <summary>Unlocks per local day of the year.</summary>
        public IReadOnlyDictionary<DateTime, int> Days { get; set; } = new Dictionary<DateTime, int>();

        public IReadOnlyList<YearReviewGameCount> TopGames { get; set; } = Array.Empty<YearReviewGameCount>();

        /// <summary>Games finished in the year, earliest first.</summary>
        public IReadOnlyList<YearReviewGame> Completed { get; set; } = Array.Empty<YearReviewGame>();

        /// <summary>Every unlock of the year, earliest first.</summary>
        public IReadOnlyList<YearReviewUnlock> Unlocks { get; set; } = Array.Empty<YearReviewUnlock>();

        /// <summary>The year's rarest unlocks with an icon, rarest first, for decoration.</summary>
        public IReadOnlyList<YearReviewUnlock> Highlights { get; set; } = Array.Empty<YearReviewUnlock>();

        public YearReviewUnlock Rarest { get; set; }
        public YearReviewUnlock First { get; set; }
        public YearReviewUnlock Latest { get; set; }

        /// <summary>Unlocks per rarity tier, indexed by <see cref="RarityTier"/>.</summary>
        public IReadOnlyDictionary<RarityTier, int> Rarities { get; set; } = new Dictionary<RarityTier, int>();

        /// <summary>Unlocks per provider, most first.</summary>
        public IReadOnlyList<YearReviewPlatformCount> Platforms { get; set; } = Array.Empty<YearReviewPlatformCount>();

        public YearReviewFriendSummary Friends { get; set; } = new YearReviewFriendSummary();

        public IReadOnlyList<YearReviewSlideKind> Slides { get; set; } = Array.Empty<YearReviewSlideKind>();
    }
}
