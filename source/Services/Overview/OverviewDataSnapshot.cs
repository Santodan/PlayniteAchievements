using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services.Overview
{
    public sealed class OverviewDataSnapshot
    {
        public List<GameSummaryItem> GameSummaries { get; set; } = new List<GameSummaryItem>();

        /// <summary>The unlocks of <see cref="GameSummaries"/>' games per game, day and tally facets.</summary>
        public IReadOnlyList<UnlockFact> Unlocks { get; set; } = Array.Empty<UnlockFact>();

        /// <summary>The unlocks of <see cref="GameSummaries"/>' games under the summary rules, for reading their rows.</summary>
        internal Database.AchievementQuery UnlockQuery() =>
            Database.AchievementQuery.CurrentUser.WithSummaryRules().WhereUnlocked(true).ForGames(
                (GameSummaries ?? new List<GameSummaryItem>()).Where(game => game?.PlayniteGameId != null).Select(game => game.PlayniteGameId.Value));

        /// <summary>
        /// The current user's rows of every achievement pin the build saw, locked ones included,
        /// whatever the summary rules leave out; read with the snapshot, off the UI thread.
        /// </summary>
        internal IReadOnlyList<AchievementDisplayItem> PinnedRows { get; set; } = Array.Empty<AchievementDisplayItem>();

        /// <summary>The pins the build read, as <see cref="PinKeys"/> names them; a pin added since is not among them.</summary>
        internal HashSet<string> PinKeysAtBuild { get; set; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Whether the build read every pin of <paramref name="showcase"/>.</summary>
        internal bool HasSeenAchievementPins(ShowcaseSettings showcase) => PinKeys(showcase).IsSubsetOf(PinKeysAtBuild);

        internal static List<PinnedAchievementReference> Pins(ShowcaseSettings showcase) =>
            (showcase?.AchievementPinCollections ?? new List<PinnedAchievementCollection>())
                .Where(collection => collection?.Pins != null)
                .SelectMany(collection => collection.Pins)
                .Where(pin => pin != null && pin.GameId != Guid.Empty && !string.IsNullOrWhiteSpace(pin.ApiName))
                .ToList();

        internal static HashSet<string> PinKeys(ShowcaseSettings showcase) =>
            new HashSet<string>(Pins(showcase).Select(pin => pin.Key.ToUpperInvariant()), StringComparer.Ordinal);

        /// <summary>
        /// Bounded pool of locked achievements the Unlock Next mosaic draws from. Empty until a
        /// shown widget asks for it (<see cref="RequestUnlockNextPool"/>);
        /// <see cref="UnlockNextPoolBuilt"/> distinguishes "not read" from "nothing found".
        /// </summary>
        public List<AchievementDisplayItem> UnlockNextCandidates { get; set; } =
            new List<AchievementDisplayItem>();

        /// <summary>Whether the Unlock Next pool has been read.</summary>
        public bool UnlockNextPoolBuilt { get; set; }

        /// <summary>Locked rows per game the pool retained from each ranking.</summary>
        public int UnlockNextPoolSlice { get; set; }

        /// <summary>
        /// Reads the Unlock Next pool at a depth (<see cref="UnlockNextCandidateSelector.SliceFor"/>)
        /// off the UI thread; set by the build, null on derived views, which show no pool.
        /// </summary>
        internal Func<int, List<AchievementDisplayItem>> UnlockNextReader { get; set; }

        private int _unlockNextReadingSlice;
        private readonly List<Action> _unlockNextLanded = new List<Action>();

        /// <summary>Whether a widget asking for <paramref name="maxPerGame"/> waits for a pool read.</summary>
        internal bool NeedsUnlockNextRead(int maxPerGame) =>
            UnlockNextReader != null &&
            maxPerGame > 0 &&
            !(UnlockNextPoolBuilt && UnlockNextPoolSlice >= UnlockNextCandidateSelector.SliceFor(maxPerGame));

        /// <summary>
        /// Reads the pool deep enough for <paramref name="maxPerGame"/>, once per depth, and runs
        /// <paramref name="landed"/> on the calling thread's context after the pool is attached.
        /// Disposing the result drops <paramref name="landed"/>, so a closed view is not held by a
        /// kept snapshot. UI thread.
        /// </summary>
        internal IDisposable RequestUnlockNextPool(int maxPerGame, Action landed)
        {
            if (!NeedsUnlockNextRead(maxPerGame) || landed == null)
            {
                return null;
            }

            _unlockNextLanded.Add(landed);
            var request = new LandedRequest(this, landed);
            var slice = UnlockNextCandidateSelector.SliceFor(maxPerGame);
            if (_unlockNextReadingSlice >= slice)
            {
                return request;
            }

            _unlockNextReadingSlice = slice;
            var reader = UnlockNextReader;
            var context = System.Threading.SynchronizationContext.Current;
            System.Threading.Tasks.Task.Run(() => reader(slice)).ContinueWith(read =>
            {
                void Attach()
                {
                    // A deeper read asked for meanwhile attaches and notifies instead.
                    if (slice < _unlockNextReadingSlice)
                    {
                        return;
                    }

                    UnlockNextCandidates = read.Exception == null && read.Result != null
                        ? read.Result
                        : new List<AchievementDisplayItem>();
                    UnlockNextPoolBuilt = true;
                    UnlockNextPoolSlice = slice;
                    var notify = _unlockNextLanded.ToList();
                    _unlockNextLanded.Clear();
                    notify.ForEach(landedAction => landedAction());
                }

                if (context != null)
                {
                    context.Post(_ => Attach(), null);
                }
                else
                {
                    Attach();
                }
            }, System.Threading.Tasks.TaskScheduler.Default);
            return request;
        }

        private sealed class LandedRequest : IDisposable
        {
            private OverviewDataSnapshot _snapshot;
            private readonly Action _landed;

            public LandedRequest(OverviewDataSnapshot snapshot, Action landed)
            {
                _snapshot = snapshot;
                _landed = landed;
            }

            public void Dispose()
            {
                _snapshot?._unlockNextLanded.Remove(_landed);
                _snapshot = null;
            }
        }

        /// <summary>
        /// Whether this snapshot was cut to the unlocks an achievement filter keeps, so its
        /// achievement totals count those unlocks only and it holds no locked remainder.
        /// </summary>
        public bool IsAchievementClip { get; set; }

        /// <summary>
        /// Set when this snapshot's completions count only those finished inside this span
        /// (see <see cref="OverviewLinkedSnapshots.ClipCompletionsToSpan"/>), with no remainder of
        /// completions still possible; null when completions are counted whole.
        /// </summary>
        public UnlockDaySpan? CompletionSpan { get; set; }

        /// <summary>
        /// Each game's completions inside <see cref="CompletionSpan"/>, for the Completed games
        /// pie's per-badge split; null when every game's completions count whole.
        /// </summary>
        public Dictionary<Guid, int> CompletionsByGame { get; set; }

        /// <summary>
        /// The widget achievement filter this snapshot's achievement totals were counted through
        /// (see <see cref="OverviewLinkedSnapshots.FilterAchievements"/>); null when every
        /// achievement counts. A count over the games' tallies applies it too.
        /// </summary>
        public Func<Models.Achievements.AchievementTally, bool> TallyFilter { get; set; }

        /// <summary>
        /// Unlock counts per local calendar day (keys at 00:00, Kind Unspecified; compare by value),
        /// produced by <see cref="UnlockDayCounts.DayOf"/>.
        /// </summary>
        public Dictionary<DateTime, int> GlobalUnlockCountsByDate { get; set; } =
            new Dictionary<DateTime, int>();

        /// <summary>Per-game counts keyed like <see cref="GlobalUnlockCountsByDate"/>.</summary>
        public Dictionary<Guid, Dictionary<DateTime, int>> UnlockCountsByDateByGame { get; set; } =
            new Dictionary<Guid, Dictionary<DateTime, int>>();

        public int TotalGames { get; set; }
        public int TotalAchievements { get; set; }
        public int TotalUnlocked { get; set; }
        public int TotalCommon { get; set; }
        public int TotalUncommon { get; set; }
        public int TotalRare { get; set; }
        public int TotalUltraRare { get; set; }
        public int CompletedGames { get; set; }

        /// <summary>
        /// How many finishes the library holds, which is not the same as how many games are
        /// finished: a game with several capstones contributes one per capstone earned.
        /// </summary>
        public int Completions { get; set; }

        /// <summary>
        /// Every finish the library offers, earned or not, which is what the completions pie
        /// partitions.
        /// </summary>
        public int PossibleCompletions { get; set; }
        public double GlobalProgressionPercent { get; set; }
        public int CollectorScore { get; set; }
        public int CollectorLevel { get; set; }
        public double CollectorLevelProgress { get; set; }
        public string CollectorRank { get; set; } = "Bronze5";
        public int PrestigeScore { get; set; }
        public int PrestigeLevel { get; set; }
        public double PrestigeLevelProgress { get; set; }
        public string PrestigeRank { get; set; } = "Bronze5";

        // Points totals per points family (see PointsFamilies): unlocked provider points earned,
        // and the points of every achievement in the family's games.
        public PlatformScoreTotals PointsTotals { get; private set; } = new PlatformScoreTotals();

        public int GamerscoreScore => PointsTotals.Gamerscore;

        public int EpicXpScore => PointsTotals.EpicXp;

        public int RetroPointsScore => PointsTotals.RetroPoints;

        /// <summary>The raw score a card type shows.</summary>
        public int GetScore(ScoreCardType type)
        {
            if (type.IsPrestige)
            {
                return PrestigeScore;
            }

            return type.IsPoints ? PointsTotals.Get(type) : CollectorScore;
        }

        /// <summary>A points card's possible points: every achievement in its games. 0 for Collection and Prestige.</summary>
        public int GetPossibleScore(ScoreCardType type) => PointsTotals.GetPossible(type);

        /// <summary>Sets the points totals per family.</summary>
        public void ApplyPlatformScores(PlatformScoreTotals totals)
        {
            PointsTotals = totals ?? new PlatformScoreTotals();
        }

        /// <summary>
        /// Unlocked achievements per provider (for provider distribution pie chart).
        /// </summary>
        public Dictionary<string, int> UnlockedByProvider { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Total achievements per provider (including locked, for "unlocked / total" display).
        /// </summary>
        public Dictionary<string, int> TotalByProvider { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Total locked achievements across all providers (for the locked section of provider pie chart).
        /// </summary>
        public int TotalLocked { get; set; }

        /// <summary>
        /// Current-user identities persisted by the friends providers (empty when never scanned).
        /// </summary>
        public List<FriendIdentity> CurrentUserIdentities { get; set; } = new List<FriendIdentity>();

        // Library-wide trophy grade counts, summed from the game summaries. Only the
        // PlayStation-shaped providers populate a trophy type, so these are all zero for a
        // library without them. Set through ApplyTrophyTotals so every snapshot builder sums
        // them the same way.
        public int TotalPlatinum { get; set; }
        public int TotalGold { get; set; }
        public int TotalSilver { get; set; }
        public int TotalBronze { get; set; }
        public int TotalPlatinumPossible { get; set; }
        public int TotalGoldPossible { get; set; }
        public int TotalSilverPossible { get; set; }
        public int TotalBronzePossible { get; set; }

        /// <summary>
        /// Sums the eight trophy totals from the given summaries in one pass. Call once per
        /// snapshot build; there are three builders and they must not each carry their own copy
        /// of these sums.
        /// </summary>
        public void ApplyTrophyTotals(IEnumerable<GameSummaryItem> games)
        {
            TotalPlatinum = 0;
            TotalGold = 0;
            TotalSilver = 0;
            TotalBronze = 0;
            TotalPlatinumPossible = 0;
            TotalGoldPossible = 0;
            TotalSilverPossible = 0;
            TotalBronzePossible = 0;
            if (games == null)
            {
                return;
            }

            foreach (var game in games)
            {
                if (game == null)
                {
                    continue;
                }

                TotalPlatinum += game.TrophyPlatinumCount;
                TotalGold += game.TrophyGoldCount;
                TotalSilver += game.TrophySilverCount;
                TotalBronze += game.TrophyBronzeCount;
                TotalPlatinumPossible += game.TrophyPlatinumTotal;
                TotalGoldPossible += game.TrophyGoldTotal;
                TotalSilverPossible += game.TrophySilverTotal;
                TotalBronzePossible += game.TrophyBronzeTotal;
            }
        }

        /// <summary>
        /// Builds a snapshot holding only the game-summary totals of <paramref name="games"/>, for
        /// pie charts drawn over a filtered game list. The scores are left at zero; no pie reads them.
        /// </summary>
        public static OverviewDataSnapshot FromGameSummaries(IReadOnlyList<GameSummaryItem> games)
        {
            var list = games ?? Array.Empty<GameSummaryItem>();
            var snapshot = new OverviewDataSnapshot
            {
                GameSummaries = new List<GameSummaryItem>(list),
                UnlockedByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
                TotalByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            };
            snapshot.ApplyGameSummaryTotals(list, addClamped: null);
            return snapshot;
        }

        /// <summary>
        /// Applies every total derived from the game summaries in one pass: counts, rarity,
        /// completions, trophies, the two per-provider maps, and the raw collector and prestige
        /// scores.
        /// </summary>
        /// <remarks>
        /// The two snapshot builders used to compute these as roughly twenty separate LINQ
        /// <c>Sum</c>/<c>Count</c> calls plus a loop, so each build walked the library about
        /// twenty times over. The overview rebuilds a snapshot on every delta tick, and the pie
        /// charts build two more, so one custom-data edit paid that many library walks. The
        /// arithmetic is unchanged -- only the number of passes is.
        ///
        /// <paramref name="addClamped"/> is how the caller adds the two scores; both saturate
        /// rather than overflow.
        /// </remarks>
        public void ApplyGameSummaryTotals(
            IReadOnlyList<GameSummaryItem> games,
            Func<int, int, int> addClamped)
        {
            TotalGames = games?.Count ?? 0;
            TotalAchievements = 0;
            TotalUnlocked = 0;
            TotalCommon = 0;
            TotalUncommon = 0;
            TotalRare = 0;
            TotalUltraRare = 0;
            TotalCommonPossible = 0;
            TotalUncommonPossible = 0;
            TotalRarePossible = 0;
            TotalUltraRarePossible = 0;
            CompletedGames = 0;
            Completions = 0;
            PossibleCompletions = 0;
            CollectorScore = 0;
            PrestigeScore = 0;
            var platformScores = addClamped != null ? new PlatformScoreTotals() : null;
            TotalPlatinum = 0;
            TotalGold = 0;
            TotalSilver = 0;
            TotalBronze = 0;
            TotalPlatinumPossible = 0;
            TotalGoldPossible = 0;
            TotalSilverPossible = 0;
            TotalBronzePossible = 0;

            if (UnlockedByProvider == null)
            {
                UnlockedByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            if (TotalByProvider == null)
            {
                TotalByProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            if (games == null)
            {
                return;
            }

            for (var i = 0; i < games.Count; i++)
            {
                var game = games[i];
                if (game == null)
                {
                    continue;
                }

                TotalAchievements += game.TotalAchievements;
                TotalUnlocked += game.UnlockedAchievements;
                TotalCommon += game.CommonCount;
                TotalUncommon += game.UncommonCount;
                TotalRare += game.RareCount;
                TotalUltraRare += game.UltraRareCount;
                TotalCommonPossible += game.TotalCommonPossible;
                TotalUncommonPossible += game.TotalUncommonPossible;
                TotalRarePossible += game.TotalRarePossible;
                TotalUltraRarePossible += game.TotalUltraRarePossible;

                if (game.IsCompleted)
                {
                    CompletedGames++;
                }

                Completions += game.Completions;
                PossibleCompletions += game.PossibleCompletions;

                TotalPlatinum += game.TrophyPlatinumCount;
                TotalGold += game.TrophyGoldCount;
                TotalSilver += game.TrophySilverCount;
                TotalBronze += game.TrophyBronzeCount;
                TotalPlatinumPossible += game.TrophyPlatinumTotal;
                TotalGoldPossible += game.TrophyGoldTotal;
                TotalSilverPossible += game.TrophySilverTotal;
                TotalBronzePossible += game.TrophyBronzeTotal;

                var provider = string.IsNullOrWhiteSpace(game.ProviderKey) ? "Unknown" : game.ProviderKey;
                UnlockedByProvider.TryGetValue(provider, out var providerUnlocked);
                UnlockedByProvider[provider] = providerUnlocked + game.UnlockedAchievements;
                TotalByProvider.TryGetValue(provider, out var providerTotal);
                TotalByProvider[provider] = providerTotal + game.TotalAchievements;

                if (addClamped != null)
                {
                    CollectorScore = addClamped(CollectorScore, game.CollectionScore);
                    PrestigeScore = addClamped(PrestigeScore, game.PrestigeScore);
                    platformScores.AddToFamily(game.PointsFamily, game.PlatformScorePoints, game.PlatformScorePointsTotal);
                }
            }

            if (platformScores != null)
            {
                ApplyPlatformScores(platformScores);
            }

            TotalLocked = Math.Max(0, TotalAchievements - TotalUnlocked);
            GlobalProgressionPercent = TotalAchievements > 0
                ? (double)TotalUnlocked / TotalAchievements * 100
                : 0;
        }

        // Total rarity counts (including locked achievements) for "unlocked / total" display
        public int TotalCommonPossible { get; set; }
        public int TotalUncommonPossible { get; set; }
        public int TotalRarePossible { get; set; }
        public int TotalUltraRarePossible { get; set; }
    }
}
