using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using System.Windows.Input;
using Playnite.SDK.Data;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels.Items
{
    public class GameSummaryItem : ObservableObject
    {
        [DontSerialize]
        [IgnoreDataMember]
        public ICommand SetDynamicAchievementsGameCommand { get; set; }

        [DontSerialize]
        [IgnoreDataMember]
        public ICommand FilterDynamicGameSummariesByProviderCommand { get; set; }

        [DontSerialize]
        [IgnoreDataMember]
        public ICommand OpenViewAchievementsWindow { get; set; }

        [DontSerialize]
        [IgnoreDataMember]
        public ICommand OpenManageAchievementsWindow { get; set; }

        private string _gameName;
        public string GameName { get => _gameName; set => SetValue(ref _gameName, value); }

        // Session-only: true when this game has any saved unlock captures on disk. Set by the
        // capture presence marker after the summaries are built; gates the Captures column button.
        private bool _hasCaptures;
        private string _nameToolTip;
        [DontSerialize]
        [IgnoreDataMember]
        public bool HasCaptures { get => _hasCaptures; set => SetValue(ref _hasCaptures, value); }

        /// <summary>
        /// Hover text for the name cell, or null for none. A category row shows its leaf, so this
        /// carries the full path - two leaves with the same name under different parents are
        /// otherwise indistinguishable once a column sort breaks the tree order.
        /// </summary>
        public string NameToolTip
        {
            get => _nameToolTip ?? _gameName;
            set => SetValue(ref _nameToolTip, value);
        }

        private string _sortingName;
        public string SortingName { get => _sortingName; set => SetValue(ref _sortingName, value); }

        // Session-only view state, and null on a game row: the connector geometry the name cell
        // draws to place this row in the category tree. Held on the base rather than on
        // CategorySummaryItem so the shared name-column template can bind it without a per-row
        // binding failure on the game surfaces that use the same template.
        private CategoryTreeShape _treeShape;

        [DontSerialize]
        [IgnoreDataMember]
        public CategoryTreeShape TreeShape { get => _treeShape; set => SetValue(ref _treeShape, value); }

        // Session-only view state, false on a game row: whether this category row's subtree is
        // currently collapsed out of the visible list. Stamped by the category list on every filter
        // pass. On the base for the same reason as TreeShape: the shared name-column template binds
        // it, and a per-row binding failure on the game surfaces would be paid on every row.
        private bool _isCollapsed;

        [DontSerialize]
        [IgnoreDataMember]
        public bool IsCollapsed { get => _isCollapsed; set => SetValue(ref _isCollapsed, value); }

        public bool Owned => PlayniteGameId.HasValue;

        private string _gameLogo;
        public string GameLogo { get => _gameLogo; set => SetValue(ref _gameLogo, value); }

        private string _gameCoverPath;
        public string GameCoverPath { get => _gameCoverPath; set => SetValue(ref _gameCoverPath, value); }

        private bool _isFavorite;
        public bool IsFavorite { get => _isFavorite; set => SetValue(ref _isFavorite, value); }

        private string _platformText;
        public string PlatformText
        {
            get => _platformText;
            set => SetValue(ref _platformText, value);
        }

        private string _regionText;
        public string RegionText
        {
            get => _regionText;
            set => SetValue(ref _regionText, value);
        }

        /// <summary>
        /// Distinct Playnite platform names for this game (e.g. "Game Boy", "NES").
        /// Set once at build time; used by the provider/platform filter.
        /// </summary>
        public IReadOnlyList<string> Platforms { get; set; } = Array.Empty<string>();

        private ulong _playtimeSeconds;
        public ulong PlaytimeSeconds
        {
            get => _playtimeSeconds;
            set
            {
                if (SetValueAndReturn(ref _playtimeSeconds, value))
                {
                    OnPropertyChanged(nameof(PlaytimeText));
                }
            }
        }

        public int AppId { get; set; } // Stays as AppId is immutable ID
        public string ProviderGameKey { get; set; }

        private Guid? _playniteGameId;
        public Guid? PlayniteGameId
        {
            get => _playniteGameId;
            set
            {
                if (SetValueAndReturn(ref _playniteGameId, value))
                {
                    OnPropertyChanged(nameof(Owned));
                }
            }
        }

        private int _totalAchievements;
        public int TotalAchievements 
        { 
            get => _totalAchievements; 
            set 
            { 
                if (_totalAchievements != value)
                {
                    SetValue(ref _totalAchievements, value);
                    OnPropertyChanged(nameof(Progression));
                    OnPropertyChanged(nameof(ProgressionText));
                    OnPropertyChanged(nameof(ProgressionCountText));
                }
            } 
        }

        private int _unlockedAchievements;
        public int UnlockedAchievements 
        { 
            get => _unlockedAchievements; 
            set 
            { 
                if (_unlockedAchievements != value)
                {
                    SetValue(ref _unlockedAchievements, value);
                    OnPropertyChanged(nameof(Progression));
                    OnPropertyChanged(nameof(ProgressionText));
                    OnPropertyChanged(nameof(ProgressionCountText));
                }
            } 
        }

        private int _commonCount;
        public int CommonCount { get => _commonCount; set => SetValue(ref _commonCount, value); }

        private int _uncommonCount;
        public int UncommonCount { get => _uncommonCount; set => SetValue(ref _uncommonCount, value); }

        private int _rareCount;
        public int RareCount { get => _rareCount; set => SetValue(ref _rareCount, value); }

        private int _ultraRareCount;
        public int UltraRareCount { get => _ultraRareCount; set => SetValue(ref _ultraRareCount, value); }

        private int _capstoneTotal;

        /// <summary>How many capstones this row has, and how many are earned.</summary>
        public int CapstoneTotal
        {
            get => _capstoneTotal;
            set
            {
                if (SetValueAndReturn(ref _capstoneTotal, value))
                {
                    OnPropertyChanged(nameof(ShowCompletionBadge));
                    OnPropertyChanged(nameof(ShowCompletionCount));
                    OnPropertyChanged(nameof(CompletionCountText));
                    OnPropertyChanged(nameof(Completions));
                }
            }
        }

        private int _capstoneUnlocked;
        public int CapstoneUnlocked
        {
            get => _capstoneUnlocked;
            set
            {
                if (SetValueAndReturn(ref _capstoneUnlocked, value))
                {
                    OnPropertyChanged(nameof(ShowCompletionBadge));
                    OnPropertyChanged(nameof(ShowCompletionCount));
                    OnPropertyChanged(nameof(CompletionCountText));
                    OnPropertyChanged(nameof(Completions));
                }
            }
        }

        private bool _capstonesMatchPlatinums;

        /// <summary>
        /// Whether this row's capstones are exactly its platinum trophies, or it names no capstone
        /// at all. Decided where the achievements are in hand, since one capstone and one platinum
        /// that are different achievements must not read as the same thing.
        /// </summary>
        public bool CapstonesMatchPlatinums
        {
            get => _capstonesMatchPlatinums;
            set
            {
                if (SetValueAndReturn(ref _capstonesMatchPlatinums, value))
                {
                    OnPropertyChanged(nameof(ShowPlatinumInCompletionSpot));
                    OnPropertyChanged(nameof(HasTrophyTypes));
                }
            }
        }

        /// <summary>
        /// Whether the finish badge renders as the platinum trophy, which also takes the platinum
        /// out of the trophy row so it appears once rather than twice. Whether trophy badges are
        /// preferred at all is the host grid's business, so the templates pair this with that.
        /// </summary>
        public bool ShowPlatinumInCompletionSpot =>
            CapstonesMatchPlatinums && TrophyPlatinumTotal > 0;

        /// <summary>
        /// Whether the completion badge carries a number. One finish needs none, the badge itself
        /// being the statement; several are worth counting.
        /// </summary>
        public bool ShowCompletionCount => CapstoneTotal > 1 && Completions > 0;

        /// <summary>How many finishes the badge stands for.</summary>
        public string CompletionCountText =>
            Completions.ToString("N0", PlayniteAchievements.Common.FormattingCulture.Current);

        /// <summary>
        /// How many times this row counts as finished: one per capstone earned, or one for a clean
        /// 100% when it names no capstone at all.
        /// </summary>
        public int Completions =>
            CapstoneTotal > 0 ? CapstoneUnlocked : (IsCompleted ? 1 : 0);

        /// <summary>
        /// How many completions this row could ever yield, which is what keeps the completions pie
        /// a real partition: every capstone is one finish available, and a game that names none
        /// still offers the single finish of a clean 100%.
        /// </summary>
        public int PossibleCompletions =>
            CapstoneTotal > 0 ? CapstoneTotal : (TotalAchievements > 0 ? 1 : 0);

        private int _collectionScore;
        public int CollectionScore
        {
            get => _collectionScore;
            set
            {
                if (SetValueAndReturn(ref _collectionScore, value))
                {
                    OnPropertyChanged(nameof(CollectionScoreFractionText));
                }
            }
        }

        private int _collectionScoreTotal;
        public int CollectionScoreTotal
        {
            get => _collectionScoreTotal;
            set
            {
                if (SetValueAndReturn(ref _collectionScoreTotal, value))
                {
                    OnPropertyChanged(nameof(CollectionScoreFractionText));
                }
            }
        }

        private int _prestigeScore;
        public int PrestigeScore
        {
            get => _prestigeScore;
            set
            {
                if (SetValueAndReturn(ref _prestigeScore, value))
                {
                    OnPropertyChanged(nameof(PrestigeScoreFractionText));
                }
            }
        }

        private int _prestigeScoreTotal;
        public int PrestigeScoreTotal
        {
            get => _prestigeScoreTotal;
            set
            {
                if (SetValueAndReturn(ref _prestigeScoreTotal, value))
                {
                    OnPropertyChanged(nameof(PrestigeScoreFractionText));
                }
            }
        }

        // Sum of raw provider points (gamerscore, RetroAchievements points, etc.) for unlocked achievements.
        private int _points;
        public int Points { get => _points; set => SetValue(ref _points, value); }

        // Total rarity counts (including locked achievements)
        public int TotalCommonPossible { get; set; }
        public int TotalUncommonPossible { get; set; }
        public int TotalRarePossible { get; set; }
        public int TotalUltraRarePossible { get; set; }

        // Earned trophy counts for PlayStation games.
        private int _trophyPlatinumCount;
        public int TrophyPlatinumCount
        {
            get => _trophyPlatinumCount;
            set => SetValue(ref _trophyPlatinumCount, value);
        }

        private int _trophyGoldCount;
        public int TrophyGoldCount
        {
            get => _trophyGoldCount;
            set => SetValue(ref _trophyGoldCount, value);
        }

        private int _trophySilverCount;
        public int TrophySilverCount
        {
            get => _trophySilverCount;
            set => SetValue(ref _trophySilverCount, value);
        }

        private int _trophyBronzeCount;
        public int TrophyBronzeCount
        {
            get => _trophyBronzeCount;
            set => SetValue(ref _trophyBronzeCount, value);
        }

        // Each total raises HasTrophyTypes, which is derived from them: a category row swapping
        // between its own and subtree stat snapshots in place can flip whether the row has trophy
        // data at all.
        private int _trophyPlatinumTotal;
        public int TrophyPlatinumTotal
        {
            get => _trophyPlatinumTotal;
            set
            {
                if (SetValueAndReturn(ref _trophyPlatinumTotal, value))
                {
                    OnPropertyChanged(nameof(HasTrophyTypes));
                    OnPropertyChanged(nameof(ShowPlatinumInCompletionSpot));
                }
            }
        }

        private int _trophyGoldTotal;
        public int TrophyGoldTotal
        {
            get => _trophyGoldTotal;
            set
            {
                if (SetValueAndReturn(ref _trophyGoldTotal, value))
                {
                    OnPropertyChanged(nameof(HasTrophyTypes));
                }
            }
        }

        private int _trophySilverTotal;
        public int TrophySilverTotal
        {
            get => _trophySilverTotal;
            set
            {
                if (SetValueAndReturn(ref _trophySilverTotal, value))
                {
                    OnPropertyChanged(nameof(HasTrophyTypes));
                }
            }
        }

        private int _trophyBronzeTotal;
        public int TrophyBronzeTotal
        {
            get => _trophyBronzeTotal;
            set
            {
                if (SetValueAndReturn(ref _trophyBronzeTotal, value))
                {
                    OnPropertyChanged(nameof(HasTrophyTypes));
                }
            }
        }

        /// <summary>
        /// True if this game has PlayStation trophy type data.
        /// </summary>
        /// <remarks>
        /// The totals, not the earned counts: whether a game has trophies is a fact about the game,
        /// not about progress through it. Reading the earned counts left a PSN game showing rarity
        /// badges until its first unlock and then switching to trophy badges.
        ///
        /// A platinum that is the game's capstone does not count: it moves to the finish badge, so
        /// on its own it would leave the trophy row empty in place of the rarity badges. An auto
        /// capstone is such a platinum, on games that have no other trophies at all.
        /// </remarks>
        public bool HasTrophyTypes =>
            (TrophyPlatinumTotal > 0 && !ShowPlatinumInCompletionSpot) ||
            TrophyGoldTotal > 0 || TrophySilverTotal > 0 || TrophyBronzeTotal > 0;

        public bool HasRarityPieChartData =>
            TotalCommonPossible > 0 ||
            TotalUncommonPossible > 0 ||
            TotalRarePossible > 0 ||
            TotalUltraRarePossible > 0;

        public bool HasTrophyPieChartData =>
            TrophyPlatinumTotal > 0 ||
            TrophyGoldTotal > 0 ||
            TrophySilverTotal > 0 ||
            TrophyBronzeTotal > 0;

        private DateTime? _lastPlayed;
        public DateTime? LastPlayed 
        { 
            get => _lastPlayed; 
            set 
            { 
                if (_lastPlayed != value)
                {
                    SetValue(ref _lastPlayed, value);
                    OnPropertyChanged(nameof(LastPlayedText));
                    OnPropertyChanged(nameof(LastPlayedLocal));
                }
            } 
        }

        private DateTime? _lastUnlockUtc;
        public DateTime? LastUnlockUtc
        {
            get => _lastUnlockUtc;
            set
            {
                if (SetValueAndReturn(ref _lastUnlockUtc, value))
                {
                    OnPropertyChanged(nameof(LastUnlockLocal));
                }
            }
        }

        public DateTime? LastUnlockLocal => LastUnlockUtc?.ToLocalTime();

        private bool _isCompleted;
        public bool IsCompleted
        {
            get => _isCompleted;
            set
            {
                if (SetValueAndReturn(ref _isCompleted, value))
                {
                    OnPropertyChanged(nameof(ShowCompletionBadge));
                    OnPropertyChanged(nameof(ShowCompletionBadge));
                    OnPropertyChanged(nameof(ShowCompletionCount));
                    OnPropertyChanged(nameof(CompletionCountText));
                    OnPropertyChanged(nameof(Completions));
                }
            }
        }

        /// <summary>
        /// Whether the progress column footer renders the completion badge for this row. Category
        /// rows additionally honor the CategoryCompletionBadgeMode display setting.
        /// </summary>
        /// <remarks>
        /// With several capstones the badge counts rather than waits: each one earned is a finish
        /// in its own right, so two of three shows a badge reading 2 instead of nothing until all
        /// three land. A row with one capstone, or none, keeps the plain finished-or-not badge,
        /// which is what makes a rollup over mixed rows read consistently.
        /// </remarks>
        public virtual bool ShowCompletionBadge =>
            CapstoneTotal > 1 ? Completions > 0 : IsCompleted;

        private string _provider;
        public string Provider { get => _provider; set => SetValue(ref _provider, value); }

        private string _providerKey;
        public string ProviderKey
        {
            get => _providerKey;
            set
            {
                if (SetValueAndReturn(ref _providerKey, value))
                {
                    OnPropertyChanged(nameof(ProviderFilterKey));
                    OnPropertyChanged(nameof(ProviderColorHex));
                }
            }
        }

        // Runtime-only display identity for surfaces where ProviderKey stays the raw aggregator key
        // (e.g. friend games routed through Exophase keep ProviderKey = "Exophase" for refresh
        // targeting while displaying the underlying provider such as EA). Never persisted.
        private string _displayProviderKey;
        public string DisplayProviderKey
        {
            get => _displayProviderKey;
            set
            {
                if (SetValueAndReturn(ref _displayProviderKey, value))
                {
                    OnPropertyChanged(nameof(ProviderFilterKey));
                    OnPropertyChanged(nameof(ProviderColorHex));
                }
            }
        }

        /// <summary>
        /// Provider identity used by the provider/platform filter: the display provider when one is
        /// set, otherwise the raw provider key.
        /// </summary>
        public string ProviderFilterKey =>
            string.IsNullOrWhiteSpace(DisplayProviderKey) ? ProviderKey : DisplayProviderKey;

        private string _providerIconKey;
        public string ProviderIconKey { get => _providerIconKey; set => SetValue(ref _providerIconKey, value); }

        private string _providerColorHex;
        public string ProviderColorHex
        {
            get
            {
                return Providers.ProviderRegistry.TryResolveProviderVisuals(
                    ProviderFilterKey,
                    out _,
                    out var colorHex) &&
                    !string.IsNullOrWhiteSpace(colorHex)
                    ? colorHex
                    : _providerColorHex;
            }
            set => SetValue(ref _providerColorHex, value);
        }

        public void RefreshProviderAppearance()
        {
            OnPropertyChanged(nameof(ProviderIconKey));
            OnPropertyChanged(nameof(ProviderColorHex));
        }


        public int Progression => AchievementCompletionPercentCalculator.ComputeRoundedPercent(UnlockedAchievements, TotalAchievements);

        public string ProgressionText => PercentFormatter.FormatWhole(Progression);

        public string ProgressionCountText => FormatScoreFraction(UnlockedAchievements, TotalAchievements);

        public string CollectionScoreFractionText => FormatScoreFraction(CollectionScore, CollectionScoreTotal);

        public string PrestigeScoreFractionText => FormatScoreFraction(PrestigeScore, PrestigeScoreTotal);

        public string PlaytimeText => PlayniteGameMetadataFormatter.FormatPlaytime(PlaytimeSeconds);

        public string LastPlayedText => LastPlayed.HasValue
            ? LastPlayed.Value.ToLocalTime().ToString("g")
            : "";

        // Local-time projection for grid display; formatting is applied by DateDisplayModeConverter.
        public DateTime? LastPlayedLocal => LastPlayed?.ToLocalTime();

        private static string FormatScoreFraction(int earned, int total)
        {
            return string.Format(FormattingCulture.Current, "{0:N0}/{1:N0}", Math.Max(0, earned), Math.Max(0, total));
        }
    }
}
