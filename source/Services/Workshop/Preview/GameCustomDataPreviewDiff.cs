using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.Services.Workshop.Preview
{
    /// <summary>Which fields of an achievement row a game data package changes.</summary>
    [Flags]
    public enum AchievementPreviewChange
    {
        None = 0,
        Name = 1 << 0,
        Description = 1 << 1,
        UnlockedIcon = 1 << 2,
        LockedIcon = 1 << 3,
        Category = 1 << 4,
        Tags = 1 << 5,
        Capstone = 1 << 6,
        Note = 1 << 7,
        Filter = 1 << 8,
        Goal = 1 << 9,

        /// <summary>The row exists only after the install.</summary>
        Added = 1 << 10,

        /// <summary>The row exists only before the install.</summary>
        Removed = 1 << 11
    }

    /// <summary>
    /// How one achievement looks on one side of a preview. The icons start as absolute file
    /// paths; a later image pass may replace them with decoded images, which raises
    /// <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/>.
    /// </summary>
    public sealed class AchievementPreviewState : ObservableObject
    {
        private object _unlockedIcon;
        private object _lockedIcon;

        /// <summary>The displayed name.</summary>
        public string DisplayName { get; set; }

        /// <summary>The displayed description.</summary>
        public string Description { get; set; }

        /// <summary>The unlocked icon: an absolute path, or an image once one is swapped in.</summary>
        public object UnlockedIcon
        {
            get => _unlockedIcon;
            set => SetValue(ref _unlockedIcon, value);
        }

        /// <summary>The locked icon: an absolute path, or an image once one is swapped in.</summary>
        public object LockedIcon
        {
            get => _lockedIcon;
            set => SetValue(ref _lockedIcon, value);
        }

        /// <summary>The category path the achievement is filed under.</summary>
        public string Category { get; set; }

        /// <summary>The category type.</summary>
        public string Tags { get; set; }

        /// <summary>True when the achievement is a capstone.</summary>
        public bool IsCapstone { get; set; }

        /// <summary>True when the achievement is a goal.</summary>
        public bool IsGoal { get; set; }

        /// <summary>True when the achievement is filtered out.</summary>
        public bool IsFiltered { get; set; }

        /// <summary>The achievement's note, or null.</summary>
        public string Note { get; set; }

        /// <summary>True for a custom achievement.</summary>
        public bool IsCustom { get; set; }
    }

    /// <summary>One achievement a game data package changes.</summary>
    public sealed class AchievementPreviewRow
    {
        /// <summary>Creates a row.</summary>
        public AchievementPreviewRow(
            string apiName,
            AchievementPreviewState before,
            AchievementPreviewState after,
            AchievementPreviewChange changes)
        {
            ApiName = apiName;
            Before = before;
            After = after;
            Changes = changes;
        }

        /// <summary>The achievement's API name.</summary>
        public string ApiName { get; }

        /// <summary>The achievement as it is now; null for an added row or when nothing is compared.</summary>
        public AchievementPreviewState Before { get; }

        /// <summary>The achievement as the install would leave it; null for a removed row.</summary>
        public AchievementPreviewState After { get; }

        /// <summary>The fields that differ, or for a package-only row the fields the package sets.</summary>
        public AchievementPreviewChange Changes { get; }
    }

    /// <summary>
    /// What installing a game data package would change on one library game, or, with no game to
    /// compare against, what the package carries.
    /// </summary>
    public sealed class GameCustomDataPreviewDiff
    {
        /// <summary>True when no game was compared and <see cref="Rows"/> lists the package's own entries.</summary>
        public bool IsPackageOnly { get; internal set; }

        /// <summary>The name of the compared game, or null when <see cref="IsPackageOnly"/>.</summary>
        public string ComparedAgainstGameName { get; internal set; }

        /// <summary>True when an update baseline was merged, so edits made since the last install are kept.</summary>
        public bool HasBaseline { get; internal set; }

        /// <summary>The fields and keys where the user's edit wins over the incoming value.</summary>
        public int KeptEditCount { get; internal set; }

        /// <summary>The changed rows, in the order the game shows them after the install, removed rows last.</summary>
        public IReadOnlyList<AchievementPreviewRow> Rows { get; internal set; } = Array.Empty<AchievementPreviewRow>();

        /// <summary>
        /// The compared achievements the install leaves as they are, in the order the game shows
        /// them after the install; empty when <see cref="IsPackageOnly"/>.
        /// </summary>
        public IReadOnlyList<AchievementPreviewRow> UnchangedRows { get; internal set; } = Array.Empty<AchievementPreviewRow>();

        /// <summary>
        /// The API names of every achievement the package itself touches (overrides, icons,
        /// categories, notes, capstones, filters, custom achievements), whether or not the install
        /// changes them. The published image lists these rows, since a package shared from the
        /// game it was made on changes nothing there.
        /// </summary>
        public IReadOnlyCollection<string> PackageTouchedApiNames { get; internal set; } = Array.Empty<string>();

        /// <summary>How many compared achievements the install leaves as they are.</summary>
        public int UnchangedCount { get; internal set; }

        /// <summary>
        /// The compared game's achievements as the install would leave them: a hydrated copy with
        /// the game-level order and category settings of the predicted record. Null when
        /// <see cref="IsPackageOnly"/>.
        /// </summary>
        public GameAchievementData AfterData { get; internal set; }

        /// <summary>The compared game's achievements as they show now; null when <see cref="IsPackageOnly"/>.</summary>
        public GameAchievementData BeforeData { get; internal set; }

        /// <summary>True when the install changes the game's custom achievement order.</summary>
        public bool OrderChanged { get; internal set; }

        /// <summary>True when the package carries a notification appearance for the game.</summary>
        public bool HasNotificationStyle { get; internal set; }

        /// <summary>How many achievement notes the package carries.</summary>
        public int NotesCount { get; internal set; }

        /// <summary>How many achievement icon files the package carries (unlocked and locked counted separately).</summary>
        public int IconCount { get; internal set; }

        /// <summary>How many achievements the package customizes through per-achievement overrides.</summary>
        public int OverrideCount { get; internal set; }

        /// <summary>How many distinct categories the package names.</summary>
        public int CategoryCount { get; internal set; }

        /// <summary>How many capstones the package defines.</summary>
        public int CapstoneCount { get; internal set; }

        /// <summary>How many custom achievements the package carries.</summary>
        public int CustomAchievementCount { get; internal set; }
    }
}
