using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
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
        GlobalPercent,

        /// <summary>The rarity sort value <c>AchievementRarityResolver.GetSortValue</c> gives.</summary>
        Rarity,

        /// <summary>The achievement name, ignoring case.</summary>
        Name,

        /// <summary>The cached game name, ignoring case.</summary>
        Game,

        /// <summary>The friend's name, as <see cref="AchievementQuery.FriendNames"/> gives it, ignoring case; the current user's rows have none.</summary>
        Friend,

        /// <summary>The achievement's points.</summary>
        Points,

        /// <summary>The trophy grade, bronze lowest and platinum highest; rows without one lowest.</summary>
        Trophy,

        /// <summary>The category label, then the unlock time.</summary>
        Category,

        /// <summary>The category kind, then the unlock time.</summary>
        Kind,

        /// <summary>The number of tags, then the unlock time.</summary>
        Tags,

        /// <summary>Rows with a note first when descending, then the name.</summary>
        Note,

        /// <summary>The collection value of the rarity tier.</summary>
        CollectionScore,

        /// <summary>The prestige value of the percent and tier.</summary>
        PrestigeScore,

        /// <summary>The game's provider, as <see cref="AchievementQuery.ProviderKey"/> resolves it, ignoring case.</summary>
        Provider,

        /// <summary>The progress fraction; rows without progress lowest.</summary>
        Progress,

        /// <summary>Unlocked rows above locked ones, then the unlock time.</summary>
        Status,

        /// <summary>Hidden rows above visible ones when descending, then the name.</summary>
        Hidden,

        /// <summary>Capstones above the rest when descending, then the name.</summary>
        Capstone
    }

    /// <summary>What a totals read groups by, or what a keys read lists.</summary>
    internal enum AchievementGroup
    {
        /// <summary>The Playnite game id.</summary>
        Game,

        /// <summary>
        /// The friend: provider key, unit separator, external user id. Friend queries only.
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
        Tag,

        /// <summary>
        /// The provider game, as <see cref="AchievementGroupKeys.ProviderGame"/> spells it:
        /// provider key, provider game key, provider game id and Playnite game id.
        /// </summary>
        ProviderGame,

        /// <summary>The friend and the provider game, as <see cref="AchievementGroupKeys.FriendGame"/> spells it. Friend queries only.</summary>
        FriendGame,

        /// <summary>The stored category text; the reader normalizes it.</summary>
        Category,

        /// <summary>
        /// The Playnite game id, the <see cref="Day"/> (empty when undated), and the
        /// <see cref="Tally"/> facets after the game id, separated by unit separators.
        /// </summary>
        DayTally,

        /// <summary>
        /// One achievement tally: the Playnite game id, stored tags, rarity tier, trophy grade
        /// (empty for none) and capstone flag (1 or 0), separated by unit separators.
        /// </summary>
        Tally
    }

    /// <summary>The fields <see cref="AchievementQuery.SearchText"/> matches for the current user.</summary>
    internal enum AchievementSearchFields
    {
        /// <summary>Game and achievement name, as the recent-achievement lists search.</summary>
        Default,

        /// <summary>Game name, achievement name, description and API name, as the achievement editor searches.</summary>
        Editor
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

        /// <summary>With <see cref="Friends"/>: only these friend accounts; null for every friend.</summary>
        public IReadOnlyList<FriendAccountRef> FriendAccounts { get; private set; }

        /// <summary>
        /// Friend queries: adds the unlock state of the same achievement of the same provider game
        /// for these accounts to each row (<see cref="AchievementRowRecord.CompareUnlocked"/>);
        /// null for none.
        /// </summary>
        public IReadOnlyList<FriendAccountRef> CompareAccounts { get; private set; }

        /// <summary>
        /// Friend queries: the name each of these accounts shows instead of its stored one, which
        /// the friend sort and search read; null for the stored names.
        /// </summary>
        public IReadOnlyList<KeyValuePair<FriendAccountRef, string>> FriendNames { get; private set; }

        /// <summary>Only rows with a resolved unlock time.</summary>
        public bool RequireUnlockTime { get; private set; }

        /// <summary>Leaves out hidden achievements that are locked.</summary>
        public bool HideHiddenLocked { get; private set; }

        /// <summary>Only capstone achievements.</summary>
        public bool CapstonesOnly { get; private set; }

        /// <summary>With <see cref="CapstonesOnly"/> and <see cref="Rarities"/>: rows of those tiers or capstones.</summary>
        public bool CapstoneOrRarity { get; private set; }

        /// <summary>
        /// Only rows whose category label is one of these, as
        /// <c>AchievementCategoryHelper.NormalizeCategoryOrDefault</c> and
        /// <c>CategoryPathHelper.LabelComparer</c> compare them; null for every row.
        /// </summary>
        public IReadOnlyList<string> CategoryLabels { get; private set; }

        /// <summary>Matches no row.</summary>
        public bool MatchesNothing { get; private set; }

        /// <summary>
        /// Totals count each achievement of a provider game once across the matching users, unlocked
        /// when any of them unlocked it, with the latest unlock time.
        /// </summary>
        public bool DistinctAchievements { get; private set; }

        /// <summary>Only these Playnite games; null for every game.</summary>
        public IReadOnlyList<Guid> GameIds { get; private set; }

        /// <summary>Leaves out these Playnite games; null for none.</summary>
        public IReadOnlyList<Guid> ExceptGameIds { get; private set; }

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

        /// <summary>The fields <see cref="SearchText"/> matches for the current user; friend rows always match their own set.</summary>
        public AchievementSearchFields SearchFields { get; private set; }

        /// <summary>
        /// Only rows the user customized (true) or left alone (false), as the achievement editor
        /// marks them: authored, or a stored override that differs from the provider's value, a
        /// cleared field, a note, goal, filter or unlock-time override, or a changed capstone
        /// mark; null for both.
        /// </summary>
        public bool? Customized { get; private set; }

        /// <summary>
        /// For a Playnite game the current user has on several accounts, only the rows of the
        /// most recently updated one, as the per-game cache read picks it; authored rows always.
        /// </summary>
        public bool LatestProgressOnly { get; private set; }

        /// <summary>
        /// Achievements, as Playnite game id and API name, that match whatever the row filters say,
        /// within the query's users and games; null for none. The row filters are every condition
        /// on the row's own values: unlock state and time, rarity, trophy, tags, category, search,
        /// customization, key groups, capstones and hidden.
        /// </summary>
        public IReadOnlyList<KeyValuePair<Guid, string>> KeptAchievements { get; private set; }

        /// <summary>
        /// Leaves out games excluded from summaries and achievements filtered or filtered from
        /// summaries, as the summary reads do.
        /// </summary>
        public bool SummaryRules { get; private set; }

        /// <summary>
        /// Only rows of games of this provider: the game's platform key, else its provider key,
        /// compared ignoring case; null for every provider.
        /// </summary>
        public string ProviderKey { get; private set; }

        /// <summary>
        /// Rows matching one key of every group, as <c>DynamicThemeFilterEvaluator</c> matches
        /// them: Unlocked, Locked, Visible, Hidden, InProgress, NoProgress, HasNotes, NoNotes,
        /// Capstone, a rarity tier, a trophy grade, or else a kind or tag key; null for every row.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<string>> KeyGroups { get; private set; }

        /// <summary>Only these achievements, as Playnite game id and API name; null for every achievement.</summary>
        public IReadOnlyList<KeyValuePair<Guid, string>> Achievements { get; private set; }

        /// <summary>Locked goals first, in goal order, then the sort.</summary>
        public bool GoalsFirst { get; private set; }

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

        /// <summary>Friend rows of these accounts only; an empty list matches no row.</summary>
        public AchievementQuery ForFriendAccounts(IEnumerable<FriendAccountRef> accounts) =>
            With(query =>
            {
                query.Friends = true;
                query.FriendAccounts = Accounts(accounts);
            });

        /// <summary>Adds the unlock state of these accounts to each friend row; null or empty for none.</summary>
        public AchievementQuery CompareWith(IEnumerable<FriendAccountRef> accounts) =>
            With(query =>
            {
                var list = Accounts(accounts);
                query.CompareAccounts = list?.Count > 0 ? list : null;
            });

        /// <summary>Sorts and searches these accounts by the given names; null or empty for the stored names.</summary>
        public AchievementQuery WithFriendNames(IEnumerable<KeyValuePair<FriendAccountRef, string>> names) =>
            With(query =>
            {
                // One name per account, so the join adds no rows.
                var list = names?.Where(name => name.Key != null && name.Value != null)
                    .GroupBy(name => name.Key.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
                query.FriendNames = list?.Count > 0 ? list : null;
            });

        public AchievementQuery WhereUnlockTimeKnown(bool require = true) => With(query => query.RequireUnlockTime = require);

        public AchievementQuery WithoutHiddenLocked(bool hide = true) => With(query => query.HideHiddenLocked = hide);

        public AchievementQuery WhereCapstone(bool only = true, bool orRarity = false) =>
            With(query =>
            {
                query.CapstonesOnly = only;
                query.CapstoneOrRarity = orRarity;
            });

        public AchievementQuery WhereCategory(IEnumerable<string> labels) =>
            With(query => query.CategoryLabels = labels?.ToList());

        public AchievementQuery MatchNothing(bool nothing = true) => With(query => query.MatchesNothing = nothing);

        public AchievementQuery CountDistinctAchievements(bool distinct = true) => With(query => query.DistinctAchievements = distinct);

        private static IReadOnlyList<FriendAccountRef> Accounts(IEnumerable<FriendAccountRef> accounts) =>
            accounts?
                .Where(account => account != null &&
                                  !string.IsNullOrWhiteSpace(account.ProviderKey) &&
                                  !string.IsNullOrWhiteSpace(account.ExternalUserId))
                .Select(account => FriendAccountRef.From(account.ProviderKey, account.ExternalUserId))
                .GroupBy(account => account.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

        public AchievementQuery ForGames(IEnumerable<Guid> gameIds) =>
            With(query => query.GameIds = gameIds?.Distinct().ToList());

        public AchievementQuery ExceptGames(IEnumerable<Guid> gameIds) =>
            With(query =>
            {
                var list = gameIds?.Distinct().ToList();
                query.ExceptGameIds = list?.Count > 0 ? list : null;
            });

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

        public AchievementQuery WhereSearch(string text, AchievementSearchFields fields) =>
            With(query =>
            {
                query.SearchText = text;
                query.SearchFields = fields;
            });

        public AchievementQuery WhereCustomized(bool? customized) => With(query => query.Customized = customized);

        public AchievementQuery WithLatestProgressOnly(bool latest = true) => With(query => query.LatestProgressOnly = latest);

        /// <summary>Rows of these pairs pass the row filters; null or empty keeps none.</summary>
        public AchievementQuery OrKeep(IEnumerable<KeyValuePair<Guid, string>> achievements) =>
            With(query =>
            {
                var list = achievements?
                    .Where(pair => pair.Key != Guid.Empty && !string.IsNullOrWhiteSpace(pair.Value))
                    .Distinct()
                    .ToList();
                query.KeptAchievements = list?.Count > 0 ? list : null;
            });

        public AchievementQuery WithSummaryRules(bool apply = true) => With(query => query.SummaryRules = apply);

        public AchievementQuery WhereProvider(string providerKey) =>
            With(query => query.ProviderKey = string.IsNullOrWhiteSpace(providerKey) ? null : providerKey.Trim());

        public AchievementQuery WhereAnyKey(IEnumerable<string> keys) =>
            With(query => query.KeyGroups = (query.KeyGroups ?? Array.Empty<IReadOnlyList<string>>())
                .Concat(new[] { (IReadOnlyList<string>)(keys?.ToList() ?? new List<string>()) })
                .ToList());

        /// <summary>Only the achievements of these pairs; an empty list matches no row.</summary>
        public AchievementQuery ForAchievements(IEnumerable<KeyValuePair<Guid, string>> achievements) =>
            With(query => query.Achievements = achievements?
                .Where(pair => pair.Key != Guid.Empty && !string.IsNullOrWhiteSpace(pair.Value))
                .Distinct()
                .ToList());

        public AchievementQuery WithGoalsFirst(bool first = true) => With(query => query.GoalsFirst = first);

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

        /// <summary>The order a grid column's sort member path sorts by; null for a column that does not sort.</summary>
        public static AchievementSort? SortOfColumn(string sortMemberPath)
        {
            switch (sortMemberPath)
            {
                case "UnlockTime":
                    return AchievementSort.UnlockTime;
                case "RaritySortValue":
                case "GlobalPercent":
                    return AchievementSort.Rarity;
                case "DisplayName":
                    return AchievementSort.Name;
                case "SortingName":
                case "GameName":
                    return AchievementSort.Game;
                case "FriendName":
                    return AchievementSort.Friend;
                case "Points":
                    return AchievementSort.Points;
                case "TrophyType":
                    return AchievementSort.Trophy;
                case "CategoryLabel":
                    return AchievementSort.Category;
                case "CategoryKind":
                    return AchievementSort.Kind;
                case "CategoryType":
                    return AchievementSort.Tags;
                case "HasAchievementNote":
                    return AchievementSort.Note;
                case "CollectionScore":
                    return AchievementSort.CollectionScore;
                case "PrestigeScore":
                    return AchievementSort.PrestigeScore;
                case "ProviderKey":
                    return AchievementSort.Provider;
                case "ProgressPercent":
                    return AchievementSort.Progress;
                case "Unlocked":
                    return AchievementSort.Status;
                case "Hidden":
                    return AchievementSort.Hidden;
                case "IsCapstone":
                    return AchievementSort.Capstone;
                default:
                    return null;
            }
        }

        private AchievementQuery With(Action<AchievementQuery> change)
        {
            var copy = (AchievementQuery)MemberwiseClone();
            change(copy);
            return copy;
        }
    }
}
