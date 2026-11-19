using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Database
{
    /// <summary>The order <see cref="AchievementReadService"/> returns rows in.</summary>
    internal enum AchievementSort
    {
        /// <summary>Friend name for friend rows, then game name, then <see cref="AchievementLayerSql.DisplayOrderSql"/>.</summary>
        Display,

        /// <summary>The resolved unlock time; rows without one come last when descending.</summary>
        UnlockTime,

        /// <summary>The global unlock percent; rows without one come first when ascending.</summary>
        GlobalPercent
    }

    /// <summary>What a totals read groups by, or what a keys read lists.</summary>
    internal enum AchievementGroup
    {
        /// <summary>The Playnite game id.</summary>
        Game,

        /// <summary>
        /// The friend, as <c>FriendsOverviewDerivations.BuildFriendScoreKey</c> spells it
        /// (provider key, unit separator, external user id). Friend queries only.
        /// </summary>
        Friend,

        /// <summary>The provider key of the row's game; NULL for authored achievements.</summary>
        Provider,

        /// <summary>The local calendar day of the resolved unlock time, as <c>yyyy-MM-dd</c>.</summary>
        Day,

        /// <summary>The rarity tier name.</summary>
        Rarity,

        /// <summary>The trophy grade, trimmed and lowercase; NULL when the achievement has none.</summary>
        Trophy,

        /// <summary>Each tag of the row, split as <c>AchievementTagHelper.Parse</c> splits it; NULL for untagged rows.</summary>
        Tag
    }

    /// <summary>
    /// One read of resolved achievement rows: whose rows, which games, which rows of them, in what
    /// order and which page. Immutable; each <c>With</c> method returns a changed copy.
    /// </summary>
    internal sealed class AchievementQuery
    {
        /// <summary>Every row of the current user, authored achievements included.</summary>
        public static readonly AchievementQuery CurrentUser = new AchievementQuery();

        /// <summary>Every row of every active friend, for games the friend's ownership list holds.</summary>
        public static readonly AchievementQuery AllFriends = new AchievementQuery { Friends = true };

        private AchievementQuery()
        {
        }

        public bool Friends { get; private set; }

        /// <summary>With <see cref="Friends"/>: only friends of this provider.</summary>
        public string FriendProviderKey { get; private set; }

        /// <summary>With <see cref="FriendProviderKey"/>: only this friend.</summary>
        public string FriendExternalUserId { get; private set; }

        /// <summary>Only these Playnite games; null for every game.</summary>
        public IReadOnlyList<Guid> GameIds { get; private set; }

        /// <summary>Only the one provider game with this provider key and <see cref="AppId"/> or <see cref="ProviderGameKey"/>.</summary>
        public string GameProviderKey { get; private set; }

        public int AppId { get; private set; }

        public string ProviderGameKey { get; private set; }

        /// <summary>Only unlocked rows (true) or locked rows (false); null for both.</summary>
        public bool? Unlocked { get; private set; }

        /// <summary>Only rows of these rarity tiers; null for every tier.</summary>
        public IReadOnlyList<RarityTier> Rarities { get; private set; }

        /// <summary>Only rows of these trophy grades, compared trimmed and case-insensitively; null for every row.</summary>
        public IReadOnlyList<string> TrophyTypes { get; private set; }

        /// <summary>
        /// Only rows matching any of these kind or tag keys, as
        /// <c>AchievementClassificationFilter.Matches</c> matches them; null for every row.
        /// </summary>
        public IReadOnlyList<string> ClassificationKeys { get; private set; }

        /// <summary>Only unlocked rows whose local unlock day is on or after this day.</summary>
        public DateTime? UnlockedFromDay { get; private set; }

        /// <summary>Only unlocked rows whose local unlock day is on or before this day.</summary>
        public DateTime? UnlockedToDay { get; private set; }

        /// <summary>
        /// Only rows whose search text contains this, as <c>SearchQuery.Matches</c> matches it: game
        /// and achievement name for the current user; friend, game, achievement name, description,
        /// category and provider for friends.
        /// </summary>
        public string SearchText { get; private set; }

        /// <summary>
        /// Leaves out games excluded from summaries and achievements filtered or filtered from
        /// summaries, as the summary reads do.
        /// </summary>
        public bool SummaryRules { get; private set; }

        public AchievementSort Sort { get; private set; }

        public bool Descending { get; private set; }

        /// <summary>The most rows to return; 0 for every row.</summary>
        public int Limit { get; private set; }

        public int Offset { get; private set; }

        public AchievementQuery ForFriend(string providerKey, string externalUserId = null) =>
            With(query =>
            {
                query.Friends = true;
                query.FriendProviderKey = providerKey;
                query.FriendExternalUserId = externalUserId;
            });

        public AchievementQuery ForGames(IEnumerable<Guid> gameIds) =>
            With(query => query.GameIds = gameIds?.Distinct().ToList());

        public AchievementQuery ForProviderGame(string providerKey, int appId, string providerGameKey) =>
            With(query =>
            {
                query.GameProviderKey = providerKey;
                query.AppId = appId;
                query.ProviderGameKey = providerGameKey;
            });

        public AchievementQuery WhereUnlocked(bool? unlocked) => With(query => query.Unlocked = unlocked);

        public AchievementQuery WhereRarity(IEnumerable<RarityTier> rarities) =>
            With(query => query.Rarities = rarities?.Distinct().ToList());

        public AchievementQuery WhereTrophy(IEnumerable<string> trophyTypes) =>
            With(query => query.TrophyTypes = trophyTypes?.ToList());

        public AchievementQuery WhereClassification(IEnumerable<string> keys) =>
            With(query => query.ClassificationKeys = keys?.ToList());

        public AchievementQuery WhereUnlockedBetween(DateTime? fromDay, DateTime? toDay) =>
            With(query =>
            {
                query.UnlockedFromDay = fromDay?.Date;
                query.UnlockedToDay = toDay?.Date;
            });

        public AchievementQuery WhereSearch(string text) => With(query => query.SearchText = text);

        public AchievementQuery WithSummaryRules(bool apply = true) => With(query => query.SummaryRules = apply);

        public AchievementQuery OrderBy(AchievementSort sort, bool descending = false) =>
            With(query =>
            {
                query.Sort = sort;
                query.Descending = descending;
            });

        public AchievementQuery Page(int limit, int offset = 0) =>
            With(query =>
            {
                query.Limit = Math.Max(0, limit);
                query.Offset = Math.Max(0, offset);
            });

        private AchievementQuery With(Action<AchievementQuery> change)
        {
            var copy = (AchievementQuery)MemberwiseClone();
            change(copy);
            return copy;
        }
    }
}
